using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

/// <summary>
/// What deleting an item changed, so undo can put it back: whether it was open (and so was canceled), whether a
/// finished one was taken off the list, and which History entries were hidden.
/// </summary>
public sealed record DeletedItem(StoredItem Item, bool WasOpen, bool WasFinished, IReadOnlyList<long> HiddenHistory);

public sealed partial class WaypointDatabase
{
    private void AddHistoryHiding() => AddColumnIfMissing("item_history", "hidden", "INTEGER NOT NULL DEFAULT 0");

    /// <summary>
    /// Takes an item out of Waypoint altogether. An open one is canceled the usual way, so its reminders and
    /// steps go with it. Then it and its steps leave History too. Nothing is erased, so undo can bring it back.
    /// </summary>
    public DeletedItem? DeleteItem(StoredItem item, DateTime now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var current = FindItem(connection, item.Source, item.Id);
        if (current is null)
        {
            return null;
        }

        var wasOpen = !current.Done;
        var wasFinished = false;
        if (wasOpen)
        {
            ApplyChange(connection, 0, new ItemChange(item.Source, item.Id, ChangeAction.Cancel), now);
        }
        else if (!IsCanceled(connection, current))
        {
            // A finished item stays on the list, crossed out, until its day passes. Canceled ones don't.
            SetCanceled(connection, current, true);
            wasFinished = true;
        }

        var hidden = SetHistoryHidden(connection, current, true, null);
        transaction.Commit();
        return new DeletedItem(current, wasOpen, wasFinished, hidden);
    }

    /// <summary>A History row's item, deleted the same way. One whose item is gone only leaves History.</summary>
    public DeletedItem? DeleteHistoryItem(HistoryEntry entry, DateTime now)
    {
        using (var connection = Open())
        {
            if (FindItem(connection, entry.Source, entry.ItemId) is null)
            {
                var gone = new StoredItem(entry.ItemId, entry.Kind, entry.Title, entry.DueDate, null, true, Source: entry.Source);
                return new DeletedItem(gone, false, false, SetHistoryHidden(connection, gone, true, null));
            }
        }

        using var lookup = Open();
        return DeleteItem(FindItem(lookup, entry.Source, entry.ItemId)!, now);
    }

    public void UndoDelete(DeletedItem deleted, DateTime now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        SetHistoryHidden(connection, deleted.Item, false, deleted.HiddenHistory);
        if (deleted.WasFinished)
        {
            SetCanceled(connection, deleted.Item, false);
        }

        if (deleted.WasOpen && FindItem(connection, deleted.Item.Source, deleted.Item.Id) is { } item)
        {
            Reopen(connection, item, now);
        }

        transaction.Commit();
    }

    private static bool IsCanceled(SqliteConnection connection, StoredItem item)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT canceled FROM {(item.Source == ItemSource.Calendar ? "calendar_events" : "items")} WHERE id = $id";
        command.Parameters.AddWithValue("$id", item.Id);
        return command.ExecuteScalar() is long canceled && canceled == 1;
    }

    /// <summary>Hides the item's History entries and its steps', or shows <paramref name="ids"/> again. Returns the ids changed.</summary>
    private static List<long> SetHistoryHidden(SqliteConnection connection, StoredItem item, bool hidden, IReadOnlyList<long>? ids)
    {
        using var command = connection.CreateCommand();
        if (ids is not null)
        {
            command.CommandText = $"UPDATE item_history SET hidden = $hidden WHERE id IN ({string.Join(',', ids.Prepend(-1))}) RETURNING id";
        }
        else
        {
            var stepColumn = item.Source == ItemSource.Calendar ? "parent_event_id" : "parent_item_id";
            command.CommandText = $"""
                UPDATE item_history SET hidden = $hidden
                WHERE hidden <> $hidden AND ((source = $source AND item_id = $id)
                    OR (source = 'Capture' AND item_id IN (SELECT id FROM items WHERE {stepColumn} = $id)))
                RETURNING id
                """;
            command.Parameters.AddWithValue("$source", item.Source.ToString());
            command.Parameters.AddWithValue("$id", item.Id);
        }

        command.Parameters.AddWithValue("$hidden", hidden ? 1 : 0);
        var changed = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            changed.Add(reader.GetInt64(0));
        }

        return changed;
    }
}
