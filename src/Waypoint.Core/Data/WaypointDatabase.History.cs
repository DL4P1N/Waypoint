using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

public sealed partial class WaypointDatabase
{
    private const string HistoryBackfilledKey = "history_backfilled";
    private const int MaxFinishedMatches = 5;

    private const string HistoryColumns = "id, happened_at, action, source, item_id, kind, title, course, due_date, next_due_date, reminders";

    // An undo adds a row pointing at what it reversed, so the log only ever grows. A deleted item's rows are hidden.
    private const string HistoryIsCurrent = "hidden = 0 AND action <> 'Reopened' AND id NOT IN (SELECT undoes_id FROM item_history WHERE undoes_id IS NOT NULL)";

    /// <summary>Everything closed, newest first. Undone entries and expenses are left out.</summary>
    public IReadOnlyList<HistoryEntry> GetHistory(int? limit = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {HistoryColumns} FROM item_history WHERE {HistoryIsCurrent} ORDER BY happened_at DESC, id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit ?? -1);

        var entries = new List<HistoryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(ReadHistory(reader));
        }

        return entries;
    }

    /// <summary>For the History page: the newest entries, narrowed to ones containing <paramref name="words"/> if given.</summary>
    public IReadOnlyList<HistoryEntry> SearchHistory(string? words, int limit)
    {
        var clean = words?.Trim() ?? "";
        return clean.Length == 0
            ? GetHistory(limit)
            : ReadHistoryHolding($"SELECT {HistoryColumns} FROM item_history WHERE {HistoryIsCurrent} {HistoryHolds(clean)} ORDER BY happened_at DESC, id DESC", clean, limit);
    }

    /// <summary>
    /// Narrows to rows whose title and class, as the page matches them, may hold $words, each still matched exactly
    /// after. Nothing narrows with no words, or words too long for SQLite to take as a pattern, and every row is read.
    /// </summary>
    private static string HistoryHolds(string clean) =>
        Prefilter([clean]) is [_] ? "AND (title || ' ' || COALESCE(course, '')) LIKE $words ESCAPE '\\'" : "";

    /// <summary>
    /// The rows <paramref name="sql"/> finds, in its order, that hold <paramref name="clean"/> in their title and class,
    /// up to <paramref name="limit"/> of them. Reading stops there, so only what's shown is read.
    /// </summary>
    private List<HistoryEntry> ReadHistoryHolding(string sql, string clean, int limit, Action<SqliteCommand>? parameters = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$words", ContainsPattern(clean));
        parameters?.Invoke(command);

        var entries = new List<HistoryEntry>();
        using var reader = command.ExecuteReader();
        while (entries.Count < limit && reader.Read())
        {
            var entry = ReadHistory(reader);
            if ($"{entry.Title} {entry.Course}".Contains(clean, StringComparison.OrdinalIgnoreCase))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>
    /// For the History page's column. Reopened shows the undo rows themselves, each one an item put back on
    /// the list. The other views are closes that are still in force, narrowed by what happened.
    /// </summary>
    public IReadOnlyList<HistoryEntry> SearchHistory(string? words, int limit, HistoryFilter filter)
    {
        if (filter == HistoryFilter.All)
        {
            return SearchHistory(words, limit);
        }

        var clean = words?.Trim() ?? "";
        var holds = clean.Length == 0 ? "" : HistoryHolds(clean);
        var sql = filter == HistoryFilter.Reopened
            ? $"SELECT {HistoryColumns} FROM item_history WHERE hidden = 0 AND action = 'Reopened' {holds} ORDER BY happened_at DESC, id DESC"
            : $"SELECT {HistoryColumns} FROM item_history WHERE {HistoryIsCurrent} AND action = $action {holds} ORDER BY happened_at DESC, id DESC";
        var action = (filter switch
        {
            HistoryFilter.Completed => HistoryAction.Done,
            HistoryFilter.Canceled => HistoryAction.Canceled,
            _ => HistoryAction.Past,
        }).ToString();
        return ReadHistoryHolding(sql, clean, limit, command => command.Parameters.AddWithValue("$action", action));
    }

    /// <summary>Only an item's latest close can be undone, and only while the item still exists.</summary>
    public bool CanReopen(HistoryEntry entry)
    {
        using var connection = Open();
        return CanReopen(connection, entry);
    }

    /// <summary>
    /// The ids of those rows that can be put back, worked out for all of them in one read, by the same rules as
    /// <see cref="CanReopen(HistoryEntry)"/>: an item's latest close that's still in force, while the item is there
    /// and still closed or moved on to its next date, or a list entry's latest check-off while it's still checked.
    /// </summary>
    public IReadOnlySet<long> ReopenableEntries(IEnumerable<HistoryEntry> entries)
    {
        var ids = entries.Select(entry => entry.Id).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new HashSet<long>();
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT h.id FROM item_history h
            WHERE h.id IN ({string.Join(", ", ids.Select(id => id.ToString(CultureInfo.InvariantCulture)))})
              AND CASE h.source
                    WHEN 'List' THEN EXISTS (SELECT 1 FROM list_entries e WHERE e.id = h.item_id AND e.removed_at IS NULL AND e.done <> 0)
                    WHEN 'Calendar' THEN EXISTS (SELECT 1 FROM calendar_events e WHERE e.id = h.item_id AND (e.done <> 0 OR h.next_due_date IS NOT NULL))
                    ELSE EXISTS (SELECT 1 FROM items e WHERE e.id = h.item_id AND (e.done <> 0 OR h.next_due_date IS NOT NULL))
                  END
              AND h.id = (SELECT MAX(id) FROM item_history WHERE source = h.source AND item_id = h.item_id AND {HistoryIsCurrent})
            """;

        var reopenable = new HashSet<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            reopenable.Add(reader.GetInt64(0));
        }

        return reopenable;
    }

    /// <summary>Undoes a History row. What it brings back and the row saying so are one write.</summary>
    public bool ReopenEntry(HistoryEntry entry)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var reopened = entry.Source == ItemSource.List
            ? CanReopen(connection, entry) && ReopenListEntry(connection, entry)
            : CanReopen(connection, entry) && Reopen(connection, FindItem(connection, entry.Source, entry.ItemId)!, DateTime.Now);
        transaction.Commit();
        return reopened;
    }

    private static bool CanReopen(SqliteConnection connection, HistoryEntry entry) => entry.Source == ItemSource.List
        ? IsEntryDone(connection, entry.ItemId) == true
            && LatestClose(connection, new StoredItem(entry.ItemId, entry.Kind, entry.Title, null, null, true, ItemSource.List))?.Id == entry.Id
        : FindItem(connection, entry.Source, entry.ItemId) is { } item
        && (item.Done || entry.NextDueDate is not null)
        && LatestClose(connection, item)?.Id == entry.Id;

    /// <summary>
    /// Closed items named in a note, however long ago, so "undo the laundry" can find them.
    /// Only these get shown to the sorter, which is what lets it reopen them.
    /// </summary>
    public IReadOnlyList<StoredItem> GetFinishedMatching(string note)
    {
        using var connection = Open();
        return GetHistory()
            .Where(entry => SearchIndex.Mentions(entry.Title, note))
            .Select(entry => (Entry: entry, Item: FindItem(connection, entry.Source, entry.ItemId)))
            .Where(match => match.Item is not null && (match.Item.Done || match.Entry.NextDueDate is not null))
            .Select(match => match.Item!)
            .DistinctBy(item => (item.Source, item.Id))
            .Take(MaxFinishedMatches)
            .ToList();
    }

    private void BackfillHistory()
    {
        if (GetPreference(HistoryBackfilledKey) is not null)
        {
            return;
        }

        // The rows and the flag saying they're there are one write, so a crash between can't add them twice.
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
            INSERT INTO item_history (happened_at, action, source, item_id, kind, title, due_date)
            SELECT substr(COALESCE(i.done_at, c.created_at), 1, 19), CASE i.canceled WHEN 1 THEN 'Canceled' ELSE 'Done' END, 'Capture', i.id, i.kind, i.title, i.due_date
            FROM items i JOIN captures c ON c.id = i.capture_id
            WHERE i.done = 1 AND i.kind <> 'Expense';
            INSERT INTO item_history (happened_at, action, source, item_id, kind, title, course, due_date)
            SELECT COALESCE(done_at, due_date || 'T00:00:00'), 'Done', 'Calendar', id, 'Deadline', title, course, due_date
            FROM calendar_events WHERE done = 1;
            """;
            command.ExecuteNonQuery();
        }

        WritePreference(connection, HistoryBackfilledKey, "1");
        transaction.Commit();
    }

    /// <summary>Expenses are closed as soon as they're made, so they'd only bury everything else.</summary>
    private static long? LogHistory(
        SqliteConnection connection,
        HistoryAction action,
        StoredItem item,
        DateTime now,
        DateOnly? nextDueDate = null,
        IReadOnlyList<PlannedReminder>? reminders = null,
        long? undoes = null,
        DateOnly? usualDate = null)
    {
        if (item.Kind == ItemKind.Expense)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO item_history (happened_at, action, source, item_id, kind, title, course, due_date, next_due_date, reminders, undoes_id, usual_date)
            VALUES ($at, $action, $source, $itemId, $kind, $title, $course, $dueDate, $nextDueDate, $reminders, $undoes, $usualDate)
            RETURNING id
            """;
        command.Parameters.AddWithValue("$at", FormatReminder(now));
        command.Parameters.AddWithValue("$action", action.ToString());
        command.Parameters.AddWithValue("$source", item.Source.ToString());
        command.Parameters.AddWithValue("$itemId", item.Id);
        command.Parameters.AddWithValue("$kind", item.Kind.ToString());
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$course", (object?)item.Course ?? DBNull.Value);
        command.Parameters.AddWithValue("$dueDate", (object?)item.DueDate?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$nextDueDate", (object?)nextDueDate?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$reminders", reminders is null ? DBNull.Value : JsonSerializer.Serialize(reminders));
        command.Parameters.AddWithValue("$undoes", (object?)undoes ?? DBNull.Value);
        command.Parameters.AddWithValue("$usualDate", (object?)usualDate?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Undoes the item's latest close. A repeating item gets back the date and reminders it had.</summary>
    private static bool Reopen(SqliteConnection connection, StoredItem item, DateTime now)
    {
        var entry = LatestClose(connection, item);
        if (!item.Done && entry?.NextDueDate is null)
        {
            return false;
        }

        SetDone(connection, item.Source, item.Id, false, now);
        SetCanceled(connection, item, false);
        KeepPastEventListed(connection, item, now);

        if (entry?.NextDueDate is not null)
        {
            RestoreOccurrence(connection, item, entry);

            // Undoing a dose's Took it takes back the time it kept, so the record stays right.
            ForgetDoseTaken(connection, item, entry.DueDate);
        }
        else
        {
            DropMissedReminders(connection, item, now);
        }

        LogHistory(connection, HistoryAction.Reopened, item, now, undoes: entry?.Id);
        return true;
    }

    /// <summary>The list drops past feed events, so one that was just unchecked is marked to stay.</summary>
    private static void KeepPastEventListed(SqliteConnection connection, StoredItem item, DateTime now)
    {
        if (item.Source == ItemSource.Calendar && item.DueDate < DateOnly.FromDateTime(now))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE calendar_events SET reopened = 1 WHERE id = $id";
            command.Parameters.AddWithValue("$id", item.Id);
            command.ExecuteNonQuery();
        }
    }

    private static void RestoreOccurrence(SqliteConnection connection, StoredItem item, HistoryEntry entry)
    {
        using (var command = connection.CreateCommand())
        {
            // Steps closed by that finish carry its exact time. A moved occurrence gets back the date it moved from.
            command.CommandText = """
                UPDATE items SET due_date = $dueDate, usual_date = (SELECT usual_date FROM item_history WHERE id = $entryId) WHERE id = $id;
                DELETE FROM reminders WHERE item_id = $id;
                UPDATE items SET done = 0, done_at = NULL WHERE parent_item_id = $id AND done_at = $closedAt;
                """;
            command.Parameters.AddWithValue("$dueDate", (object?)entry.DueDate?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", item.Id);
            command.Parameters.AddWithValue("$closedAt", FormatReminder(entry.At));
            command.Parameters.AddWithValue("$entryId", entry.Id);
            command.ExecuteNonQuery();
        }

        foreach (var reminder in entry.Reminders)
        {
            AddReminder(connection, item.Id, reminder);
        }
    }

    private static HistoryEntry? LatestClose(SqliteConnection connection, StoredItem item)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {HistoryColumns} FROM item_history
            WHERE source = $source AND item_id = $itemId AND {HistoryIsCurrent}
            ORDER BY id DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$source", item.Source.ToString());
        command.Parameters.AddWithValue("$itemId", item.Id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadHistory(reader) : null;
    }

    /// <summary>Repeating items that moved on since <paramref name="since"/>, with when they did.</summary>
    private static List<(string At, StoredItem Item)> GetRecentRepeats(SqliteConnection connection, DateTime since)
    {
        var entries = new List<(string At, long Id)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT MAX(happened_at), item_id FROM item_history
                WHERE source = 'Capture' AND next_due_date IS NOT NULL AND happened_at >= $since AND {HistoryIsCurrent}
                GROUP BY item_id
                """;
            command.Parameters.AddWithValue("$since", FormatReminder(since));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                entries.Add((reader.GetString(0), reader.GetInt64(1)));
            }
        }

        return entries
            .Select(entry => (entry.At, Item: FindItem(connection, ItemSource.Capture, entry.Id)))
            .Where(entry => entry.Item is { Done: false })
            .Select(entry => (entry.At, entry.Item!))
            .ToList();
    }

    private static List<PlannedReminder> ReadReminders(SqliteConnection connection, long itemId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT remind_at, message FROM reminders WHERE item_id = $itemId";
        command.Parameters.AddWithValue("$itemId", itemId);

        var reminders = new List<PlannedReminder>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            reminders.Add(new PlannedReminder(
                DateTime.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return reminders;
    }

    private static HistoryEntry ReadHistory(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
        Enum.Parse<HistoryAction>(reader.GetString(2)),
        Enum.Parse<ItemSource>(reader.GetString(3)),
        reader.GetInt64(4),
        Enum.Parse<ItemKind>(reader.GetString(5)),
        reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : DateOnly.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
        reader.IsDBNull(9) ? null : DateOnly.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
        reader.IsDBNull(10) ? [] : JsonSerializer.Deserialize<List<PlannedReminder>>(reader.GetString(10))!);
}

/// <summary>The History page's views. All is every close still in force; Reopened is what was put back.</summary>
public enum HistoryFilter
{
    All,
    Completed,
    Canceled,
    Reopened,
    Past,
}
