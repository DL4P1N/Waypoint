using System.Globalization;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

public sealed partial class WaypointDatabase
{
    /// <summary>The shopping list is the open Shopping items, so those keep their undo and history.</summary>
    public const string ShoppingList = "Shopping";

    private const int MaxNoteLine = 120;

    /// <summary>In the order they were put in on the Lists page. Lists never moved, such as new ones, come after, by name.</summary>
    public IReadOnlyList<string> GetListNames()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM lists WHERE archived_at IS NULL ORDER BY position IS NULL, position, name";

        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    public IReadOnlyList<ListEntry> GetListEntries(string name)
    {
        using var connection = Open();
        return IsShopping(name) ? ReadShopping(connection) : ReadEntries(connection, CleanName(name));
    }

    /// <summary>Every list, Shopping first, then in their order. Shopping is left out when it's empty, unless asked for.</summary>
    public IReadOnlyList<ListOverview> GetListOverview(bool includeEmptyShopping = false)
    {
        var shopping = GetListEntries(ShoppingList);
        var others = GetListNames()
            .Select(name => new ListOverview(name, GetListEntries(name).OrderBy(entry => entry.Done).ToList()));

        return shopping.Count > 0 || includeEmptyShopping ? others.Prepend(new ListOverview(ShoppingList, shopping)).ToList() : others.ToList();
    }

    /// <summary>The newest notes. The rest are found by searching.</summary>
    public IReadOnlyList<RecentNote> GetRecentNotes(int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.title, i.about, COALESCE(i.body, c.text), c.created_at FROM items i JOIN captures c ON c.id = i.capture_id
            WHERE i.kind = 'Note' AND i.canceled = 0
            ORDER BY i.id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var notes = new List<RecentNote>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var title = reader.GetString(0);
            var line = reader.IsDBNull(1) ? FirstLine(reader.GetString(2)) : reader.GetString(1);
            notes.Add(new RecentNote(
                title,
                Same(line, title) ? null : line,
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture).LocalDateTime));
        }

        return notes;
    }

    /// <summary>Adds to a list, making it first if it's new. Anything already on it is left alone.</summary>
    public string AddToList(string? name, IReadOnlyList<string> entries, long captureId, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Which list?";
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var reply = AddToList(connection, name, entries, captureId, now);
        transaction.Commit();
        return reply;
    }

    /// <summary>
    /// The same, in the caller's write. A shopping entry is an item, and an item needs a capture, so with no
    /// capture given one is made here for what's added, and only when something is.
    /// </summary>
    private static string AddToList(SqliteConnection connection, string name, IReadOnlyList<string> entries, long? captureId, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Which list?";
        }

        var clean = CleanName(name);
        var shopping = IsShopping(clean);
        var existing = (shopping ? ReadShopping(connection) : ReadEntries(connection, clean)).Select(entry => entry.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = entries.Select(entry => entry.Trim()).Where(entry => entry.Length > 0 && existing.Add(entry)).ToList();

        var listId = shopping ? 0 : EnsureList(connection, clean, now);
        var shoppingCapture = shopping && added.Count > 0 ? captureId ?? InsertHandTyped(connection, string.Join(", ", added), now) : 0;
        foreach (var entry in added)
        {
            if (shopping)
            {
                InsertItem(connection, shoppingCapture, new SortedItem(ItemKind.Shopping, entry, null, null), null, null);
            }
            else
            {
                InsertEntry(connection, listId, entry, now);
            }
        }

        var label = shopping ? ShoppingList : clean;
        return added.Count > 0 ? $"Added {string.Join(", ", added)} to {label}" : $"{label} is ready";
    }

    public string DescribeList(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            var names = GetListNames().Prepend(ShoppingList);
            return $"Your lists: {string.Join(", ", names)}";
        }

        var clean = CleanName(name);
        var entries = GetListEntries(clean);
        if (entries.Count == 0)
        {
            return IsShopping(clean) || GetListNames().Contains(clean, StringComparer.OrdinalIgnoreCase)
                ? $"{clean} is empty."
                : $"There's no list called {clean}.";
        }

        var left = entries.Where(entry => !entry.Done).Select(entry => $"- {entry.Text}");
        var done = entries.Where(entry => entry.Done).Select(entry => entry.Text).ToList();
        var lines = new[] { entries[0].List }.Concat(left);
        return string.Join("\n", done.Count > 0 ? lines.Append($"Checked off: {string.Join(", ", done)}") : lines);
    }

    /// <summary>Checks or unchecks one entry. Without a list name it looks everywhere, and asks if that finds more than one.</summary>
    public string SetEntryDone(string? name, string text, bool done, DateTime now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var candidates = FindEntries(connection, name, text).Where(entry => entry.Done != done).ToList();
        if (Pick(candidates, text) is not { } entry)
        {
            return NoSingleMatch(candidates, text);
        }

        var reply = SetEntryDone(connection, entry, done, now);
        transaction.Commit();
        return reply;
    }

    /// <summary>Checks or unchecks the exact entry that was clicked, so there's nothing to ask about.</summary>
    public void SetEntryDone(ListEntry entry, bool done, DateTime now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        SetEntryDone(connection, entry, done, now);
        transaction.Commit();
    }

    /// <summary>The check and its History entry, in the caller's write, so neither is ever kept without the other.</summary>
    private static string SetEntryDone(SqliteConnection connection, ListEntry entry, bool done, DateTime now)
    {
        if (entry.List == ShoppingList)
        {
            // The window can be a moment behind, so the item may already be closed.
            if (FindItem(connection, ItemSource.Capture, entry.Id) is { } item && item.Done != done)
            {
                if (done)
                {
                    Finish(connection, item, now);
                }
                else
                {
                    Reopen(connection, item, now);
                }
            }

            return done ? $"Checked off {entry.Text}" : $"{entry.Text} is back on {entry.List}";
        }

        var wasDone = IsEntryDone(connection, entry.Id);
        WriteEntryDone(connection, entry.Id, done, now);

        // A check-off goes in History, and unchecking takes it back out, the same as an item's undo.
        if (done && wasDone == false)
        {
            LogHistory(connection, HistoryAction.Done, AsHistoryItem(entry), now);
        }
        else if (!done && wasDone == true)
        {
            LogHistory(connection, HistoryAction.Reopened, AsHistoryItem(entry), now, undoes: LatestClose(connection, AsHistoryItem(entry))?.Id);
        }

        return done ? $"Checked off {entry.Text}" : $"{entry.Text} is back on {entry.List}";
    }

    /// <summary>Null when the entry is gone or taken off its list.</summary>
    private static bool? IsEntryDone(SqliteConnection connection, long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT done FROM list_entries WHERE id = $id AND removed_at IS NULL";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is long done ? done != 0 : null;
    }

    private static void WriteEntryDone(SqliteConnection connection, long id, bool done, DateTime now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE list_entries SET done = $done, done_at = $doneAt WHERE id = $id";
        command.Parameters.AddWithValue("$done", done ? 1 : 0);
        command.Parameters.AddWithValue("$doneAt", done ? FormatReminder(now) : DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>How a list entry is kept in History. The list's name stands in for a course, so searching finds it.</summary>
    private static StoredItem AsHistoryItem(ListEntry entry) =>
        new(entry.Id, ItemKind.Task, entry.Text, null, null, entry.Done, ItemSource.List, entry.List);

    /// <summary>Unchecks a list entry from its History row.</summary>
    private static bool ReopenListEntry(SqliteConnection connection, HistoryEntry entry)
    {
        WriteEntryDone(connection, entry.ItemId, false, DateTime.Now);
        var item = new StoredItem(entry.ItemId, entry.Kind, entry.Title, null, null, false, ItemSource.List, entry.Course);
        LogHistory(connection, HistoryAction.Reopened, item, DateTime.Now, undoes: entry.Id);
        return true;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return line.Length > MaxNoteLine ? $"{line[..MaxNoteLine]}…" : line;
    }

    public string RemoveEntry(string? name, string text, DateTime now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var candidates = FindEntries(connection, name, text);
        if (Pick(candidates, text) is not { } entry)
        {
            return NoSingleMatch(candidates, text);
        }

        RemoveEntry(connection, entry, now);
        transaction.Commit();
        return $"Took {entry.Text} off {entry.List}";
    }

    /// <summary>Takes off the exact entry shown. It can come back with <see cref="RestoreEntry"/>.</summary>
    public void DeleteEntry(ListEntry entry, DateTime now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RemoveEntry(connection, entry, now);
        transaction.Commit();
    }

    public void RestoreEntry(ListEntry entry, DateTime now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (entry.List == ShoppingList)
        {
            RestoreItem(connection, entry.Id, now);
        }
        else
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE list_entries SET removed_at = NULL WHERE id = $id";
            command.Parameters.AddWithValue("$id", entry.Id);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Adds one thing from the Lists page. Anything already checked off goes back on, and nothing goes to the sorter.</summary>
    public void AddListEntry(string name, string text, DateTime now)
    {
        var clean = text.Trim();
        if (clean.Length == 0)
        {
            return;
        }

        if (GetListEntries(name).FirstOrDefault(entry => Same(entry.Text, clean)) is { Done: true } finished)
        {
            SetEntryDone(finished, false, now);
            return;
        }

        // A shopping entry is an item, and an item has to belong to a capture, which is made in the same write.
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        AddToList(connection, name, [clean], null, now);
        transaction.Commit();
    }

    public void EditEntry(ListEntry entry, string text)
    {
        var clean = text.Trim();
        if (clean.Length == 0)
        {
            return;
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = entry.List == ShoppingList
            ? "UPDATE items SET title = $text WHERE id = $id AND kind = 'Shopping'"
            : "UPDATE list_entries SET text = $text WHERE id = $id";
        command.Parameters.AddWithValue("$text", clean);
        command.Parameters.AddWithValue("$id", entry.Id);
        command.ExecuteNonQuery();
    }

    /// <summary>Saves the order the lists were dragged or moved into. Shopping always stays first, so it's skipped.</summary>
    public void OrderLists(IReadOnlyList<string> names)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var position = 0;
        foreach (var name in names.Where(name => !IsShopping(name)))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE lists SET position = $position WHERE name = $name COLLATE NOCASE";
            command.Parameters.AddWithValue("$position", position++);
            command.Parameters.AddWithValue("$name", name);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Makes an empty list, or brings back a removed one with that name.</summary>
    public ListEdit CreateList(string name, DateTime now)
    {
        var clean = CleanName(name);
        if (clean.Length == 0)
        {
            return new ListEdit(clean, "A list needs a name.");
        }

        if (IsShopping(clean))
        {
            return new ListEdit(ShoppingList);
        }

        using var connection = Open();
        EnsureList(connection, clean, now);
        return new ListEdit(clean);
    }

    public ListEdit RenameList(string name, string newName, DateTime now)
    {
        var clean = CleanName(newName);
        if (clean.Length == 0)
        {
            return new ListEdit(name, "A list needs a name.");
        }

        if (IsShopping(name) || IsShopping(clean))
        {
            return new ListEdit(name, "Shopping keeps its name, and nothing else can use it.");
        }

        // The list and the Discord posts that show it are renamed together.
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var taken = connection.CreateCommand();
        taken.CommandText = "SELECT COUNT(*) FROM lists WHERE name = $name COLLATE NOCASE AND id <> (SELECT id FROM lists WHERE name = $old COLLATE NOCASE)";
        taken.Parameters.AddWithValue("$name", clean);
        taken.Parameters.AddWithValue("$old", CleanName(name));
        if ((long)taken.ExecuteScalar()! > 0)
        {
            return new ListEdit(name, $"The name {clean} is already used.");
        }

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE lists SET name = $name WHERE name = $old COLLATE NOCASE";
        command.Parameters.AddWithValue("$name", clean);
        command.Parameters.AddWithValue("$old", CleanName(name));
        command.ExecuteNonQuery();
        RenameListPosts(connection, CleanName(name), clean);
        transaction.Commit();
        return new ListEdit(clean);
    }

    private static void RemoveEntry(SqliteConnection connection, ListEntry entry, DateTime now)
    {
        if (entry.List == ShoppingList)
        {
            if (FindItem(connection, ItemSource.Capture, entry.Id) is { Done: false } item)
            {
                CancelItem(connection, item, now);
            }

            return;
        }

        // Kept out of sight rather than deleted, so it can come back where it was.
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE list_entries SET removed_at = $now WHERE id = $id";
        command.Parameters.AddWithValue("$now", FormatReminder(now));
        command.Parameters.AddWithValue("$id", entry.Id);
        command.ExecuteNonQuery();
    }

    private static void CancelItem(SqliteConnection connection, StoredItem item, DateTime now)
    {
        SetDone(connection, item.Source, item.Id, true, now);
        SetCanceled(connection, item, true);
        LogHistory(connection, HistoryAction.Canceled, item, now);
    }

    private static void RestoreItem(SqliteConnection connection, long itemId, DateTime now)
    {
        if (FindItem(connection, ItemSource.Capture, itemId) is { } item)
        {
            Reopen(connection, item, now);
        }
    }

    /// <summary>Items need a capture, so things typed straight into a page get one that's already sorted.</summary>
    private long AddHandTyped(string text, DateTime now)
    {
        using var connection = Open();
        return InsertHandTyped(connection, text, now);
    }

    /// <summary>The same capture, inside the caller's write, so it's never left behind without what it was made for.</summary>
    private static long InsertHandTyped(SqliteConnection connection, string text, DateTime now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO captures (text, created_at, sorted_at) VALUES ($text, $at, $at) RETURNING id";
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$at", new DateTimeOffset(now).ToString("O", CultureInfo.InvariantCulture));
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Unchecks everything, so a routine or checklist can be run again, and gives the first thing to do.</summary>
    public string StartList(string? name)
    {
        var clean = CleanName(name ?? "");
        var found = GetListNames().FirstOrDefault(list => Same(list, clean));
        if (found is null)
        {
            return $"There's no list called {clean}.";
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE list_entries SET done = 0, done_at = NULL WHERE list_id = (SELECT id FROM lists WHERE name = $name COLLATE NOCASE)";
        command.Parameters.AddWithValue("$name", found);
        command.ExecuteNonQuery();

        var entries = ReadEntries(connection, found);
        return entries.Count > 0 ? $"{found} is ready. Start with: {entries[0].Text}" : $"{found} is empty.";
    }

    /// <summary>
    /// Undo for Start over: checks these entries on the list again. Nothing goes in History, since Start over took
    /// nothing out of it. An entry deleted since is left alone.
    /// </summary>
    public void UndoStartList(string name, IReadOnlyList<long> checkedIds, DateTime now)
    {
        if (checkedIds.Count == 0)
        {
            return;
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var id in checkedIds)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE list_entries SET done = 1, done_at = $now WHERE id = $id AND removed_at IS NULL AND list_id = (SELECT id FROM lists WHERE name = $name COLLATE NOCASE)";
            command.Parameters.AddWithValue("$now", FormatReminder(now));
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", CleanName(name));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Hides a list. Its entries stay stored, and naming it again brings it back.</summary>
    public string DeleteList(string? name, DateTime now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE lists SET archived_at = $now WHERE name = $name COLLATE NOCASE AND archived_at IS NULL";
        command.Parameters.AddWithValue("$now", FormatReminder(now));
        command.Parameters.AddWithValue("$name", CleanName(name ?? ""));
        return command.ExecuteNonQuery() > 0 ? $"Removed {CleanName(name!)}" : $"There's no list called {CleanName(name ?? "")}.";
    }

    /// <summary>An exact match wins. Anything else has to be the only one.</summary>
    private static ListEntry? Pick(IReadOnlyList<ListEntry> candidates, string text)
    {
        var exact = candidates.Where(entry => Same(entry.Text, text.Trim())).ToList();
        return exact.Count == 1 ? exact[0] : exact.Count == 0 && candidates.Count == 1 ? candidates[0] : null;
    }

    private static string NoSingleMatch(IReadOnlyList<ListEntry> candidates, string text) =>
        candidates.Count == 0
            ? $"Couldn't find {text} on a list"
            : $"Which one: {string.Join(", ", candidates.Select(entry => $"{entry.Text} ({entry.List})"))}?";

    private static List<ListEntry> FindEntries(SqliteConnection connection, string? name, string text)
    {
        var clean = string.IsNullOrWhiteSpace(name) ? null : CleanName(name);
        var entries = clean is null
            ? ReadEntries(connection, null).Concat(ReadShopping(connection))
            : IsShopping(clean) ? ReadShopping(connection) : ReadEntries(connection, clean);
        return entries.Where(entry => entry.Text.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static long EnsureList(SqliteConnection connection, string name, DateTime now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO lists (name, created_at) VALUES ($name, $now)
            ON CONFLICT (name) DO UPDATE SET archived_at = NULL
            RETURNING id
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$now", FormatReminder(now));
        return (long)command.ExecuteScalar()!;
    }

    private static void InsertEntry(SqliteConnection connection, long listId, string text, DateTime now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO list_entries (list_id, text, added_at) VALUES ($listId, $text, $now)";
        command.Parameters.AddWithValue("$listId", listId);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$now", FormatReminder(now));
        command.ExecuteNonQuery();
    }

    /// <summary>Entries in the order they were added, from one list or all of them.</summary>
    private static List<ListEntry> ReadEntries(SqliteConnection connection, string? list)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.id, l.name, e.text, e.done, e.added_at FROM list_entries e JOIN lists l ON l.id = e.list_id
            WHERE l.archived_at IS NULL AND e.removed_at IS NULL AND ($list IS NULL OR l.name = $list COLLATE NOCASE)
            ORDER BY l.name, e.id
            """;
        command.Parameters.AddWithValue("$list", (object?)list ?? DBNull.Value);

        var entries = new List<ListEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new ListEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3) != 0,
                DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
        }

        return entries;
    }

    private static List<ListEntry> ReadShopping(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.id, i.title, c.created_at FROM items i JOIN captures c ON c.id = i.capture_id
            WHERE i.kind = 'Shopping' AND i.done = 0 ORDER BY i.id
            """;

        var entries = new List<ListEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new ListEntry(reader.GetInt64(0), ShoppingList, reader.GetString(1), false, DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture).LocalDateTime));
        }

        return entries;
    }

    /// <summary>"my packing list" and "packing" are the same list.</summary>
    private static string CleanName(string name)
    {
        var clean = name.Trim();
        foreach (var prefix in new[] { "my ", "the " })
        {
            if (clean.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                clean = clean[prefix.Length..];
            }
        }

        return clean.EndsWith(" list", StringComparison.OrdinalIgnoreCase) ? clean[..^5].TrimEnd() : clean;
    }

    private static bool IsShopping(string name) => Same(CleanName(name), ShoppingList);
}
