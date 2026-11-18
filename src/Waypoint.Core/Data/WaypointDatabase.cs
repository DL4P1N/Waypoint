using System.Globalization;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

public sealed partial class WaypointDatabase
{
    private const string ItemColumns = "id, kind, title, due_date, amount_cents, done, due_time, parent_item_id, parent_event_id, recurrence, dose";
    // The whole text kept under a shortened title, read after ItemColumns for To do. A note's body is the note itself, so it stays behind.
    private const string ItemBody = "CASE WHEN kind = 'Note' THEN NULL ELSE body END";
    // A date the user set wins over the feed's, which keeps updating underneath it.
    private const string EventDueDate = "COALESCE(due_override, due_date)";
    private const string EventDueAt = "CASE WHEN due_override IS NULL THEN due_at ELSE due_override_at END";
    private const string EventColumns = $"id, title, course, {EventDueDate}, {EventDueAt}, done";
    private const string NudgesPlanned = "planned";
    private const string NudgesSkipped = "skipped";
    private const string MonthlyBudgetKey = "monthly_budget_cents";
    private const int MaxRecentlyFinished = 15;

    // A step stays out of sight once its parent is closed or past, and comes back if the parent reopens.
    private const string StepIsLive = $"""
        (parent_item_id IS NULL OR parent_item_id IN (SELECT id FROM items WHERE done = 0))
        AND (parent_event_id IS NULL OR parent_event_id IN (SELECT id FROM calendar_events WHERE done = 0 AND {EventDueDate} >= $today))
        """;

    private static readonly TimeOnly EndOfDay = new(23, 59);

    public void SetMonthlyBudget(long cents) => SetPreference(MonthlyBudgetKey, cents.ToString(CultureInfo.InvariantCulture));

    /// <summary>Today's spending room, or null until a budget has been set.</summary>
    public long? GetSafeToSpend(DateOnly today) =>
        SpendingSoFar(today) is { } spent ? Briefing.SafeToSpendToday(spent.Budget, spent.Before, spent.Today, today) : null;

    /// <summary>Why safe to spend is $0.00 today, or null when there's room or no budget.</summary>
    public string? GetSafeToSpendReason(DateOnly today) =>
        SpendingSoFar(today) is { } spent ? Briefing.SafeToSpendReason(spent.Budget, spent.Before, spent.Today, today) : null;

    private (long Budget, long Before, long Today)? SpendingSoFar(DateOnly today)
    {
        if (GetPreference(MonthlyBudgetKey) is not { } budget)
        {
            return null;
        }

        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var spentToday = GetSpentCents(today, today);
        var spentBefore = today > monthStart ? GetSpentCents(monthStart, today.AddDays(-1)) : 0;
        return (long.Parse(budget, CultureInfo.InvariantCulture), spentBefore, spentToday);
    }

    /// <summary>Total expenses in a date range. Checked-off expenses count, and canceled ones don't.</summary>
    private long GetSpentCents(DateOnly from, DateOnly through)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COALESCE(SUM(i.amount_cents - i.reimbursed_cents), 0) FROM items i JOIN captures c ON c.id = i.capture_id
            WHERE i.kind = 'Expense' AND i.canceled = 0 AND i.one_off = 0 AND {CountedSql("i")}
              AND COALESCE(i.due_date, substr(c.created_at, 1, 10)) BETWEEN $from AND $through
            """;
        command.Parameters.AddWithValue("$from", from.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$through", through.ToString("O", CultureInfo.InvariantCulture));
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Open items, soonest first. Calendar events from past days are left out.</summary>
    public IReadOnlyList<StoredItem> GetOpenItems()
    {
        using var connection = Open();
        var items = new List<StoredItem>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {ItemColumns}, {ItemBody} FROM items WHERE done = 0 AND {StepIsLive} ORDER BY id";
            command.Parameters.AddWithValue("$today", DateOnly.FromDateTime(DateTime.Now).ToString("O", CultureInfo.InvariantCulture));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadItemWithBody(reader));
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT {EventColumns} FROM calendar_events
                WHERE done = 0 AND {EventDueDate} >= $today
                """;
            command.Parameters.AddWithValue("$today", DateOnly.FromDateTime(DateTime.Now).ToString("O", CultureInfo.InvariantCulture));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadEvent(reader));
            }
        }

        return items
            .OrderBy(item => item.DueDate is null)
            .ThenBy(item => item.DueDate)
            .ThenBy(item => item.DueAt ?? DateTimeOffset.MaxValue)
            .ToList();
    }

    /// <summary>Items finished since <paramref name="since"/>, newest first, so they can be undone.</summary>
    public IReadOnlyList<StoredItem> GetRecentlyFinished(DateTime since)
    {
        using var connection = Open();
        var finished = new List<(string At, StoredItem Item)>();

        using (var command = connection.CreateCommand())
        {
            // An expense carries the card it came in on, so the sorter can tell same-named charges apart.
            command.CommandText = $"SELECT {ItemColumns}, done_at, (SELECT text FROM captures WHERE id = items.capture_id) FROM items WHERE done = 1 AND done_at >= $since AND {CountedSql("items")}";
            command.Parameters.AddWithValue("$since", FormatReminder(since));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var item = ReadItem(reader);
                finished.Add((reader.GetString(11), item.Kind == ItemKind.Expense && !reader.IsDBNull(12) && reader.GetString(12).StartsWith(SpendingRow.ImportedPrefix, StringComparison.Ordinal)
                    ? item with { Account = reader.GetString(12)[SpendingRow.ImportedPrefix.Length..] }
                    : item));
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {EventColumns}, done_at FROM calendar_events WHERE done = 1 AND done_at >= $since";
            command.Parameters.AddWithValue("$since", FormatReminder(since));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                finished.Add((reader.GetString(6), ReadEvent(reader)));
            }
        }

        finished.AddRange(GetRecentRepeats(connection, since));

        return finished
            .OrderByDescending(entry => entry.At, StringComparer.Ordinal)
            .Take(MaxRecentlyFinished)
            .Select(entry => entry.Item)
            .ToList();
    }

    /// <summary>
    /// Finished items that stay on the list a while, so they can be unchecked. One stays while its
    /// date is ahead, or otherwise through the day after it was finished. Canceled ones go to history.
    /// A past feed event that was unchecked comes along open, since nothing else lists it until it's rechecked.
    /// </summary>
    public IReadOnlyList<StoredItem> GetFinishedForList(DateTime now)
    {
        using var connection = Open();
        var today = DateOnly.FromDateTime(now);
        var items = new List<StoredItem>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT {ItemColumns}, {ItemBody} FROM items
                WHERE done = 1 AND canceled = 0 AND kind NOT IN ('Expense', 'Note', 'Shopping') AND {StepIsLive}
                  AND (due_date >= $today OR done_at >= $since)
                ORDER BY id
                """;
            command.Parameters.AddWithValue("$today", today.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$since", FormatReminder(today.AddDays(-1).ToDateTime(TimeOnly.MinValue)));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadItemWithBody(reader));
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT {EventColumns} FROM calendar_events
                WHERE (done = 1 AND canceled = 0 AND ({EventDueDate} >= $today OR done_at >= $since))
                   OR (done = 0 AND reopened = 1 AND {EventDueDate} < $today)
                ORDER BY id
                """;
            command.Parameters.AddWithValue("$today", today.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$since", FormatReminder(today.AddDays(-1).ToDateTime(TimeOnly.MinValue)));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadEvent(reader));
            }
        }

        return items;
    }

    /// <summary>Checks an item off or back on. The change and its History entry are one write.</summary>
    public void SetDone(StoredItem item, bool done)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (done)
        {
            // A dose is marked taken instead, and only once for its day.
            if (TakeDose(connection, item, null, DateTime.Now) is null)
            {
                Finish(connection, item, DateTime.Now);
            }
        }
        else
        {
            Reopen(connection, FindItem(connection, item.Source, item.Id) ?? item, DateTime.Now);
        }

        transaction.Commit();
    }

    /// <summary>True if the date on this feed item is one the user gave.</summary>
    public bool IsMovedFromFeed(StoredItem item)
    {
        if (item.Source != ItemSource.Calendar)
        {
            return false;
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT due_override IS NOT NULL FROM calendar_events WHERE id = $id";
        command.Parameters.AddWithValue("$id", item.Id);
        return command.ExecuteScalar() is 1L;
    }

    private static DateOnly? ReadUsualDate(SqliteConnection connection, long itemId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT usual_date FROM items WHERE id = $id";
        command.Parameters.AddWithValue("$id", itemId);
        return command.ExecuteScalar() is string date ? DateOnly.Parse(date, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// Closes an item, or sends a recurring one on to its next date. Past means it moved on without
    /// anyone saying it was done.
    /// </summary>
    /// <param name="keepToday">Moves an earlier day's on to today's, if today has one, even once today's time has passed.</param>
    private static void Finish(SqliteConnection connection, StoredItem item, DateTime now, HistoryAction action = HistoryAction.Done, bool keepToday = false)
    {
        ForgetAfterThis(connection, item.Source, item.Id);
        if (item.Recurs is not { } recurs)
        {
            SetDone(connection, item.Source, item.Id, true, now);
            LogHistory(connection, action, item, now);
            return;
        }

        var today = DateOnly.FromDateTime(now);
        var time = item.DueAt is { } dueAt ? TimeOnly.FromDateTime(dueAt.DateTime) : (TimeOnly?)null;

        // Finishing yesterday's late doesn't use up today's, if today's hasn't come yet.
        var isOverdue = item.DueDate < today;
        var todayIsAhead = isOverdue && now < ReminderPlan.DueMoment(today, time);
        var usual = ReadUsualDate(connection, item.Id);
        var next = recurs.Next(usual ?? item.DueDate ?? today, todayIsAhead || (keepToday && isOverdue) ? today.AddDays(-1) : today);

        CloseSteps(connection, item.Id, null, now);
        LogHistory(connection, action, item, now, next, ReadReminders(connection, item.Id), usualDate: usual);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE items SET due_date = $dueDate, usual_date = NULL WHERE id = $id;
                DELETE FROM reminders WHERE item_id = $id;
                """;
            command.Parameters.AddWithValue("$dueDate", next.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$id", item.Id);
            command.ExecuteNonQuery();
        }

        foreach (var reminder in ReminderPlan.Build(new SortedItem(item.Kind, item.Title, next, null, time), now))
        {
            AddReminder(connection, item.Id, reminder);
        }
    }

    private static void SetCanceled(SqliteConnection connection, StoredItem item, bool canceled)
    {
        var table = item.Source == ItemSource.Calendar ? "calendar_events" : "items";

        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {table} SET canceled = $canceled WHERE id = $id";
        command.Parameters.AddWithValue("$canceled", canceled ? 1 : 0);
        command.Parameters.AddWithValue("$id", item.Id);
        command.ExecuteNonQuery();
    }

    private static void SetDone(SqliteConnection connection, ItemSource source, long id, bool done, DateTime now)
    {
        var table = source == ItemSource.Calendar ? "calendar_events" : "items";

        using var command = connection.CreateCommand();
        var clearReopened = source == ItemSource.Calendar && done ? ", reopened = 0" : "";
        command.CommandText = $"UPDATE {table} SET done = $done, done_at = $doneAt{clearReopened} WHERE id = $id";
        command.Parameters.AddWithValue("$done", done ? 1 : 0);
        command.Parameters.AddWithValue("$doneAt", done ? FormatReminder(now) : DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static StoredItem? FindItem(SqliteConnection connection, ItemSource source, long id)
    {
        // A list entry's id belongs to list_entries, so it must never be looked up as an item.
        if (source == ItemSource.List)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = source == ItemSource.Calendar
            ? $"SELECT {EventColumns} FROM calendar_events WHERE id = $id"
            : $"SELECT {ItemColumns} FROM items WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return !reader.Read() ? null : source == ItemSource.Calendar ? ReadEvent(reader) : ReadItem(reader);
    }

    private static string ReminderTable(StoredItem item) =>
        item.Source == ItemSource.Calendar ? "calendar_reminders" : "reminders";

    private static StoredItem ReadEvent(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        ItemKind.Deadline,
        reader.GetString(1),
        DateOnly.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
        null,
        reader.GetInt64(5) != 0,
        ItemSource.Calendar,
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture));

    private static StoredItem ReadItem(SqliteDataReader reader)
    {
        DateOnly? date = reader.IsDBNull(3) ? null : DateOnly.Parse(reader.GetString(3), CultureInfo.InvariantCulture);
        TimeOnly? time = reader.IsDBNull(6) ? null : TimeOnly.Parse(reader.GetString(6), CultureInfo.InvariantCulture);

        return new StoredItem(
            reader.GetInt64(0),
            Enum.Parse<ItemKind>(reader.GetString(1)),
            reader.GetString(2),
            date,
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.GetInt64(5) != 0,
            DueAt: date is { } day && time is { } clock ? new DateTimeOffset(day.ToDateTime(clock)) : null,
            ParentRef: reader.IsDBNull(7)
                ? reader.IsDBNull(8) ? null : ItemRef.Format(ItemSource.Calendar, reader.GetInt64(8))
                : ItemRef.Format(ItemSource.Capture, reader.GetInt64(7)),
            Recurs: reader.IsDBNull(9) ? null : Recurrence.Parse(reader.GetString(9)),
            Dose: reader.GetInt64(10) != 0);
    }

    /// <summary>An item read with <see cref="ItemBody"/> after its columns. A blank body is the same as none.</summary>
    private static StoredItem ReadItemWithBody(SqliteDataReader reader)
    {
        var item = ReadItem(reader);
        return reader.IsDBNull(11) || string.IsNullOrWhiteSpace(reader.GetString(11)) ? item : item with { Body = reader.GetString(11) };
    }

    private static string FormatReminder(DateTime time) => time.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
}
