using System.Globalization;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

/// <summary>
/// Waiting on someone. The wait is a row beside an ordinary task, so the task keeps its search, reminders,
/// and history, and finishing it is the same Done as anything else. The one nudge is an ordinary reminder.
/// </summary>
public sealed partial class WaypointDatabase
{
    private const string WaitingColumns = """
        w.item_id, i.title, w.who, w.topic, w.since, w.follow_up_on, w.email, w.replied_at, w.reply_from, w.reply_subject
        """;

    private void CreateWaitingTables() => Execute("""
        CREATE TABLE IF NOT EXISTS waiting_on (
            item_id INTEGER PRIMARY KEY REFERENCES items(id),
            who TEXT NOT NULL,
            topic TEXT,
            since TEXT NOT NULL,
            follow_up_on TEXT NOT NULL,
            email TEXT,
            reminder_id INTEGER,
            replied_at TEXT,
            reply_from TEXT,
            reply_subject TEXT
        );
        """);

    /// <summary>
    /// Marks a listed item as waiting on someone, or adds a new one for it. The item is a task, so it can be
    /// searched and finished like any other. With no date given, it checks back in a few days.
    /// </summary>
    public OpReply MarkWaiting(string? who, string? title, string? itemRef, DateOnly? followUp, string? email, long? captureId, DateTime now)
    {
        var name = who?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return new OpReply("Who are you waiting on?", true);
        }

        // The task, the wait, and its nudge are one write, so a failure partway can't leave a task that waits on no one.
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var item = ItemRef.TryParse(itemRef, out var source, out var id) && source == ItemSource.Capture
            ? FindItem(connection, ItemSource.Capture, id)
            : null;
        var listed = item is { Done: false };
        if (!listed)
        {
            var newTitle = string.IsNullOrWhiteSpace(title) ? WaitingOn.TitleFor(name, null) : title.Trim();
            var itemId = InsertItem(connection, captureId ?? InsertHandTyped(connection, newTitle, now), new SortedItem(ItemKind.Task, newTitle, null, null), null, null);
            item = FindItem(connection, ItemSource.Capture, itemId)!;
        }

        var today = DateOnly.FromDateTime(now);
        var day = WaitingOn.FollowUpOr(followUp, today);
        var address = string.IsNullOrWhiteSpace(email) ? null : email.Trim();

        // A listed task is what the wait is about when its title doesn't say, as in "Refund for the headphones".
        var topic = WaitingOn.TopicOf(item!.Title) ?? (listed ? item.Title : null);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO waiting_on (item_id, who, topic, since, follow_up_on, email)
                VALUES ($id, $who, $topic, $since, $day, $email)
                ON CONFLICT (item_id) DO UPDATE SET who = excluded.who, follow_up_on = excluded.follow_up_on,
                    email = COALESCE(excluded.email, waiting_on.email), replied_at = NULL, reply_from = NULL, reply_subject = NULL
                """;
            command.Parameters.AddWithValue("$id", item.Id);
            command.Parameters.AddWithValue("$who", name);
            command.Parameters.AddWithValue("$topic", (object?)topic ?? DBNull.Value);
            command.Parameters.AddWithValue("$since", FormatReminder(now));
            command.Parameters.AddWithValue("$day", Day(day));
            command.Parameters.AddWithValue("$email", (object?)address ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        var waiting = ReadWaiting(connection, item.Id)!;
        PlanNudge(connection, waiting, new PlannedReminder(day.ToDateTime(WaitingOn.NudgeTime), WaitingOn.NudgeMessage(waiting)));
        transaction.Commit();
        return new OpReply($"Waiting on {name}: {item.Title}. I'll check in {WaitingOn.When(day, today)}.");
    }

    /// <summary>The To do form's way in, for a new wait. It's stored the same way the sorter's op is.</summary>
    public HandAddResult AddWaitingByHand(string who, string? topic, DateOnly? followUp, string? email, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(who))
        {
            return new HandAddResult(null, "Say who you're waiting on.");
        }

        var title = WaitingOn.TitleFor(who, topic);
        MarkWaiting(who, title, null, followUp, email, null, now);
        return new HandAddResult(GetOpenItems().LastOrDefault(item => item.Title == title));
    }

    /// <summary>The row menu's way in, for a task that's already listed. Only the user's own open items can wait.</summary>
    public string? MarkWaitingByHand(StoredItem item, string who, DateOnly? followUp, string? email, DateTime now)
    {
        if (item.Source != ItemSource.Capture || item.Done)
        {
            return "Only your own open items can wait on someone.";
        }

        var reply = MarkWaiting(who, null, ItemRef.Format(item), followUp, email, null, now);
        return reply.IsAnswer ? reply.Text : null;
    }

    /// <summary>"Still waiting": checks back again later, with one new nudge then. A reply that didn't settle it is let go.</summary>
    public OpReply StillWaiting(long? itemId, DateOnly? until, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        if ((itemId is { } id ? GetWaiting().FirstOrDefault(found => found.ItemId == id) : LatestDue(today)) is not { } waiting)
        {
            return new OpReply("You're not waiting on anyone right now.", true);
        }

        var day = WaitingOn.FollowUpOr(until, today);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE waiting_on SET follow_up_on = $day, replied_at = NULL, reply_from = NULL, reply_subject = NULL WHERE item_id = $id";
            command.Parameters.AddWithValue("$day", Day(day));
            command.Parameters.AddWithValue("$id", waiting.ItemId);
            command.ExecuteNonQuery();
        }

        PlanNudge(connection, waiting, new PlannedReminder(day.ToDateTime(WaitingOn.NudgeTime), WaitingOn.NudgeMessage(waiting)));
        transaction.Commit();
        return new OpReply($"Still waiting on {waiting.Who}. I'll check in {WaitingOn.When(day, today)}.");
    }

    /// <summary>No longer waiting, but still something to do. The task stays and its nudge goes.</summary>
    public bool StopWaiting(long itemId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (ReadReminderId(connection, itemId) is { } reminder)
        {
            DeleteReminder(connection, itemId, reminder);
        }

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM waiting_on WHERE item_id = $id";
        command.Parameters.AddWithValue("$id", itemId);
        var stopped = command.ExecuteNonQuery() > 0;
        transaction.Commit();
        return stopped;
    }

    /// <summary>Every open item that's waiting, soonest follow-up first. Finished ones are left in the table for a reopen.</summary>
    public IReadOnlyList<WaitingItem> GetWaiting()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {WaitingColumns} FROM waiting_on w JOIN items i ON i.id = w.item_id
            WHERE i.done = 0 ORDER BY w.follow_up_on, w.item_id
            """;
        var waiting = new List<WaitingItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            waiting.Add(ReadWaitingRow(reader));
        }

        return waiting;
    }

    /// <summary>Finds a wait by its ref, or by a word of who it's on.</summary>
    public WaitingItem? FindWaiting(string? text, DateOnly today)
    {
        var all = GetWaiting();
        if (ItemRef.TryParse(text, out var source, out var id) && source == ItemSource.Capture)
        {
            return all.FirstOrDefault(found => found.ItemId == id);
        }

        return string.IsNullOrWhiteSpace(text)
            ? LatestDue(today) ?? (all.Count == 1 ? all[0] : null)
            : all.FirstOrDefault(found => found.Who.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase) || found.Title.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A follow-up to copy, for the wait named or the one most recently due. Null when there's nothing waiting.</summary>
    public string? DraftFollowUp(string? text, DateOnly today) =>
        FindWaiting(text, today) is { } waiting ? WaitingOn.Draft(waiting) : null;

    public string DescribeWaiting(DateOnly today) => WaitingOn.Describe(GetWaiting(), today);

    /// <summary>
    /// An email came in. Each wait it answers is marked replied, and its planned nudge becomes one saying so,
    /// due now. Nothing is finished for the user, since only they know if the reply settles it.
    /// </summary>
    public IReadOnlyList<WaitingItem> NoticeReply(string address, string? name, string subject, DateTime received)
    {
        var answered = GetWaiting().Where(waiting => waiting.RepliedAt is null && waiting.Since < received && WaitingOn.IsReplyFrom(waiting, address, name)).ToList();
        if (answered.Count == 0)
        {
            return [];
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var who = string.IsNullOrWhiteSpace(name) ? address : name.Trim();
        foreach (var waiting in answered)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE waiting_on SET replied_at = $at, reply_from = $from, reply_subject = $subject WHERE item_id = $id";
                command.Parameters.AddWithValue("$at", FormatReminder(received));
                command.Parameters.AddWithValue("$from", who);
                command.Parameters.AddWithValue("$subject", subject);
                command.Parameters.AddWithValue("$id", waiting.ItemId);
                command.ExecuteNonQuery();
            }

            PlanNudge(connection, waiting, new PlannedReminder(received, WaitingOn.ReplyMessage(waiting.Who, subject)));
        }

        transaction.Commit();

        return answered.Select(waiting => waiting with { RepliedAt = received, ReplyFrom = who, ReplySubject = subject }).ToList();
    }

    /// <summary>
    /// Takes out waits whose follow-up day hasn't come, for anything that picks what to do next.
    /// They still show on the Waiting on filter and in search.
    /// </summary>
    public IReadOnlyList<StoredItem> WithoutQuietWaiting(IEnumerable<StoredItem> items, DateOnly today)
    {
        var quiet = GetWaiting().Where(waiting => !waiting.IsDue(today)).Select(waiting => waiting.ItemId).ToHashSet();
        return items.Where(item => item.Source != ItemSource.Capture || !quiet.Contains(item.Id)).ToList();
    }

    /// <summary>The short replies to a nudge. Null when the word isn't one, or there's nothing waiting for it to answer.</summary>
    public OpReply? ApplyWaitingWord(WaitingWord word, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        if (LatestDue(today) is not { } waiting)
        {
            return null;
        }

        return word == WaitingWord.StillWaiting
            ? StillWaiting(waiting.ItemId, null, now)
            : new OpReply(WaitingOn.DraftReply(waiting), true);
    }

    /// <summary>The wait a reminder is the nudge for, so its card offers Follow up, Still waiting, and Done. Null for any other reminder.</summary>
    public WaitingItem? WaitingFor(DueReminder reminder) =>
        reminder.Item.Source == ItemSource.Capture ? GetWaiting().FirstOrDefault(waiting => waiting.ItemId == reminder.Item.Id) : null;

    /// <summary>The wait whose nudge came most recently, which is the one a short reply is about.</summary>
    private WaitingItem? LatestDue(DateOnly today) =>
        GetWaiting().Where(waiting => waiting.IsDue(today))
            .OrderByDescending(waiting => waiting.RepliedAt ?? waiting.FollowUpOn.ToDateTime(WaitingOn.NudgeTime))
            .FirstOrDefault();

    /// <summary>Only ever one nudge per wait, so a new one replaces the last.</summary>
    private static void PlanNudge(SqliteConnection connection, WaitingItem waiting, PlannedReminder nudge)
    {
        if (ReadReminderId(connection, waiting.ItemId) is { } old)
        {
            DeleteReminder(connection, waiting.ItemId, old);
        }

        AddReminder(connection, waiting.ItemId, nudge);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE waiting_on SET reminder_id = last_insert_rowid() WHERE item_id = $id";
        command.Parameters.AddWithValue("$id", waiting.ItemId);
        command.ExecuteNonQuery();
    }

    private static long? ReadReminderId(SqliteConnection connection, long itemId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT reminder_id FROM waiting_on WHERE item_id = $id";
        command.Parameters.AddWithValue("$id", itemId);
        return command.ExecuteScalar() is long id ? id : null;
    }

    /// <summary>Reminder ids are reused once freed, so the nudge is only taken if it's still this item's.</summary>
    private static void DeleteReminder(SqliteConnection connection, long itemId, long reminderId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM reminders WHERE id = $id AND item_id = $item";
        command.Parameters.AddWithValue("$id", reminderId);
        command.Parameters.AddWithValue("$item", itemId);
        command.ExecuteNonQuery();
    }

    private static WaitingItem? ReadWaiting(SqliteConnection connection, long itemId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {WaitingColumns} FROM waiting_on w JOIN items i ON i.id = w.item_id WHERE w.item_id = $id";
        command.Parameters.AddWithValue("$id", itemId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadWaitingRow(reader) : null;
    }

    private static WaitingItem ReadWaitingRow(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
        DateOnly.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9));
}
