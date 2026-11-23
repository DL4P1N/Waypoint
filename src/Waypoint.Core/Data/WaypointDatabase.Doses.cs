using System.Globalization;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

/// <summary>
/// Doses: repeating reminders marked with items.dose, answered with Took it. Each Took it keeps one row in dose_taken
/// for the day of the reminder it answered, so a second Took it for that day, from any card or surface, is turned
/// down instead of kept twice. Rows are pruned after <see cref="Doses.KeepDays"/> days, and nothing counts missed days.
/// </summary>
public sealed partial class WaypointDatabase
{
    private void CreateDoseTables()
    {
        AddColumnIfMissing("items", "dose", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("sent_reminders", "due_on", "TEXT");
        Execute("""
            CREATE TABLE IF NOT EXISTS dose_taken (
                item_id INTEGER NOT NULL REFERENCES items(id),
                due_on TEXT NOT NULL,
                taken_at TEXT NOT NULL,
                PRIMARY KEY (item_id, due_on)
            );
            """);
    }

    /// <summary>Took it from a To do row, /took, or typing: it counts for the dose's day, and only once.</summary>
    public DoseReply TakeDose(StoredItem item, DateTime now)
    {
        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        var reply = TakeDose(connection, item, null, now) ?? new DoseReply($"{item.Title} isn't a dose.", false);
        transaction.Commit();
        return reply;
    }

    /// <summary>
    /// An answer on a dose's card, for the day the card was for. <paramref name="reminder"/> is the PC card's own
    /// reminder, which is cleared so it doesn't come straight back. Discord's was already cleared when it was sent.
    /// </summary>
    public string AnswerDose(long itemId, DateOnly dueOn, DoseAnswer answer, DateTime now, DueReminder? reminder = null)
    {
        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        if (reminder is not null)
        {
            using var clear = connection.CreateCommand();
            clear.CommandText = "DELETE FROM reminders WHERE id = $id AND item_id = $item";
            clear.Parameters.AddWithValue("$id", reminder.Id);
            clear.Parameters.AddWithValue("$item", itemId);
            clear.ExecuteNonQuery();
        }

        var reply = FindItem(connection, ItemSource.Capture, itemId) is { } item
            ? AnswerDose(connection, item, dueOn, answer, now, reminder)
            : "That reminder is gone.";
        transaction.Commit();
        return reply;
    }

    /// <summary>Marks a repeating reminder as a dose, or not. Returns false when it isn't a repeating reminder.</summary>
    public bool SetDose(StoredItem item, bool dose)
    {
        if (item.Source != ItemSource.Capture)
        {
            return false;
        }

        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE items SET dose = $dose, repeat_minutes = CASE WHEN $dose = 1 THEN NULL ELSE repeat_minutes END WHERE id = $id AND recurrence IS NOT NULL";
        command.Parameters.AddWithValue("$dose", dose ? 1 : 0);
        command.Parameters.AddWithValue("$id", item.Id);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>Every dose reminder still kept, with its taken times from the last month, newest first.</summary>
    public IReadOnlyList<DoseRecord> GetDoses()
    {
        using var lease = Connect(out var connection);
        var items = new List<StoredItem>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {ItemColumns} FROM items WHERE dose = 1 AND canceled = 0 AND recurrence IS NOT NULL ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadItem(reader));
            }
        }

        return items.Select(item => new DoseRecord(item, ReadTaken(connection, item.Id))).ToList();
    }

    /// <summary>
    /// A dose left unanswered moves on once its day is over, the same as a repeating reminder that went past, so a
    /// missed day is simply gone. Today's still comes if today has one. Old taken times are pruned here too.
    /// </summary>
    public void MoveOnPastDoses(DateTime now)
    {
        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        var past = new List<StoredItem>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {ItemColumns} FROM items WHERE dose = 1 AND done = 0 AND canceled = 0 AND recurrence IS NOT NULL AND due_date < $today";
            command.Parameters.AddWithValue("$today", DateOnly.FromDateTime(now).ToString("O", CultureInfo.InvariantCulture));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                past.Add(ReadItem(reader));
            }
        }

        foreach (var item in past)
        {
            Finish(connection, item, now, HistoryAction.Past, keepToday: true);
        }

        PruneDoses(connection, now);
        transaction.Commit();
    }

    /// <summary>Took it, kept once for <paramref name="dueOn"/>. Null when the item isn't a dose, so the caller finishes it as usual.</summary>
    /// <param name="dueOn">The day the card was for. Without one it's the dose's own day, or today once it has moved past today.</param>
    private static DoseReply? TakeDose(SqliteConnection connection, StoredItem given, DateOnly? dueOn, DateTime now)
    {
        if (given.Source != ItemSource.Capture || FindItem(connection, ItemSource.Capture, given.Id) is not { Dose: true, Recurs: not null, DueDate: { } current } item)
        {
            return null;
        }

        var today = DateOnly.FromDateTime(now);
        var day = dueOn ?? (current <= today ? current : today);
        if (dueOn is null && current > today && ReadTaken(connection, item.Id).FirstOrDefault() is { } latest && DateOnly.FromDateTime(latest.TakenAt) == today)
        {
            // Already taken today, and the reminder has moved on to the next one.
            return new DoseReply(Doses.AlreadyTaken(latest.TakenAt, now), false);
        }

        if (TakenOn(connection, item.Id, day) is { } at)
        {
            return new DoseReply(Doses.AlreadyTaken(at, now), false);
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO dose_taken (item_id, due_on, taken_at) VALUES ($item, $dueOn, $at)";
            command.Parameters.AddWithValue("$item", item.Id);
            command.Parameters.AddWithValue("$dueOn", day.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$at", FormatReminder(now));
            command.ExecuteNonQuery();
        }

        if (current == day)
        {
            // The same as Done on a repeating reminder: on to the next one, and Finished in History.
            Finish(connection, item, now);
        }
        else
        {
            // That day already moved on unanswered, so its History entry now says it was finished.
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE item_history SET action = 'Done', happened_at = $at
                WHERE id = (SELECT id FROM item_history WHERE source = 'Capture' AND item_id = $item AND action = 'Past' AND due_date = $dueOn ORDER BY id DESC LIMIT 1)
                """;
            command.Parameters.AddWithValue("$item", item.Id);
            command.Parameters.AddWithValue("$dueOn", day.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$at", FormatReminder(now));
            command.ExecuteNonQuery();
        }

        PruneDoses(connection, now);
        return new DoseReply(Doses.Marked(item.Title, now), true);
    }

    private static string AnswerDose(SqliteConnection connection, StoredItem item, DateOnly dueOn, DoseAnswer answer, DateTime now, DueReminder? reminder)
    {
        if (answer == DoseAnswer.Took)
        {
            if (TakeDose(connection, item, dueOn, now) is { } reply)
            {
                return reply.Text;
            }

            // No longer a dose, so Took it is plain Done.
            if (item.DueDate == dueOn)
            {
                Finish(connection, item, now);
            }

            return $"Done: {item.Title}";
        }

        if (TakenOn(connection, item.Id, dueOn) is { } at)
        {
            return Doses.AlreadyTaken(at, now);
        }

        // Answered some other way already, such as Skip on another card.
        if (item.DueDate != dueOn)
        {
            return $"{item.Title} has already moved on to its next time.";
        }

        if (answer == DoseAnswer.Skip)
        {
            Finish(connection, item, now, HistoryAction.Past);
            return $"Skipped: {item.Title}";
        }

        // Once only: the card that comes back has no Not now.
        if (reminder?.IsFollowUp != true)
        {
            AddFollowUp(connection, item, TransitionAssist.FollowUpAt(now));
        }

        return $"Back once in {TransitionAssist.FollowUpMinutes} minutes: {item.Title}";
    }

    /// <summary>Takes back a day's taken time, when its Took it is undone.</summary>
    private static void ForgetDoseTaken(SqliteConnection connection, StoredItem item, DateOnly? dueOn)
    {
        if (item.Source != ItemSource.Capture || dueOn is not { } day)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dose_taken WHERE item_id = $item AND due_on = $dueOn";
        command.Parameters.AddWithValue("$item", item.Id);
        command.Parameters.AddWithValue("$dueOn", day.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static DateTime? TakenOn(SqliteConnection connection, long itemId, DateOnly dueOn)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT taken_at FROM dose_taken WHERE item_id = $item AND due_on = $dueOn";
        command.Parameters.AddWithValue("$item", itemId);
        command.Parameters.AddWithValue("$dueOn", dueOn.ToString("O", CultureInfo.InvariantCulture));
        return command.ExecuteScalar() is string at ? DateTime.Parse(at, CultureInfo.InvariantCulture) : null;
    }

    private static List<DoseTaken> ReadTaken(SqliteConnection connection, long itemId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT due_on, taken_at FROM dose_taken WHERE item_id = $item ORDER BY taken_at DESC";
        command.Parameters.AddWithValue("$item", itemId);
        var taken = new List<DoseTaken>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            taken.Add(new DoseTaken(
                DateOnly.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture)));
        }

        return taken;
    }

    /// <summary>Only about a month of taken times is kept, enough to answer "did I take it?".</summary>
    private static void PruneDoses(SqliteConnection connection, DateTime now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dose_taken WHERE taken_at < $cutoff";
        command.Parameters.AddWithValue("$cutoff", FormatReminder(now.AddDays(-Doses.KeepDays)));
        command.ExecuteNonQuery();
    }
}
