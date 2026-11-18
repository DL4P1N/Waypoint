using System.Globalization;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

/// <summary>
/// Items held by <see cref="AfterThis"/>. A hold is only ever taken once, and taking it is what drops it, so it can
/// never come back a second time or turn into something to catch up on.
/// </summary>
public sealed partial class WaypointDatabase
{
    private void CreateAfterThisTables()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS after_this (
                id INTEGER PRIMARY KEY,
                item_id INTEGER NOT NULL,
                source TEXT NOT NULL,
                message_id TEXT,
                held_at TEXT NOT NULL
            );
            """);
        AddColumnIfMissing("sent_reminders", "sent_at", "TEXT");
        AddColumnIfMissing("sent_reminders", "held", "INTEGER NOT NULL DEFAULT 0");
    }

    /// <summary>Holds a startable, open item once. Holding it again only moves it to now. Returns whether it was held.</summary>
    public bool HoldAfterThis(StoredItem item, DateTime at, string? messageId = null)
    {
        if (item.Done || !Activation.CanStart(item))
        {
            return false;
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Hold(connection, item.Source, item.Id, at, messageId);
        transaction.Commit();
        return true;
    }

    private static void Hold(SqliteConnection connection, ItemSource source, long itemId, DateTime at, string? messageId)
    {
        using var command = connection.CreateCommand();
        // Only the newest is ever kept. A newer one replaces it, and an older one never pushes out a newer one.
        command.CommandText = """
            DELETE FROM after_this WHERE held_at <= $at OR (item_id = $item AND source = $source);
            INSERT INTO after_this (item_id, source, message_id, held_at)
                SELECT $item, $source, $message, $at WHERE NOT EXISTS (SELECT 1 FROM after_this WHERE held_at > $at);
            """;
        command.Parameters.AddWithValue("$item", itemId);
        command.Parameters.AddWithValue("$source", source.ToString());
        command.Parameters.AddWithValue("$message", (object?)messageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", FormatReminder(at));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Discord reminders with a Start that got no answer for <see cref="AfterThis.Unanswered"/>. Each is looked at
    /// once. Ones too old to matter are passed over, so this never brings back a backlog.
    /// </summary>
    public int HoldUnansweredReminders(DateTime now)
    {
        // Marking a card looked at and holding its item are one write, so a failure between can't lose the hold.
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var unanswered = new List<(string Message, long Item, ItemSource Source, DateTime SentAt)>();
        using (var find = connection.CreateCommand())
        {
            find.CommandText = "SELECT message_id, item_id, source, sent_at FROM sent_reminders WHERE handled = 0 AND held = 0 AND sent_at IS NOT NULL AND sent_at <= $cutoff ORDER BY sent_at";
            find.Parameters.AddWithValue("$cutoff", FormatReminder(now - AfterThis.Unanswered));
            using var reader = find.ExecuteReader();
            while (reader.Read())
            {
                unanswered.Add((reader.GetString(0), reader.GetInt64(1), Enum.Parse<ItemSource>(reader.GetString(2)), DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
            }
        }

        var held = 0;
        foreach (var (message, itemId, source, sentAt) in unanswered)
        {
            using (var mark = connection.CreateCommand())
            {
                mark.CommandText = "UPDATE sent_reminders SET held = 1 WHERE message_id = $message AND handled = 0 AND held = 0";
                mark.Parameters.AddWithValue("$message", message);
                if (mark.ExecuteNonQuery() != 1)
                {
                    continue;
                }
            }

            // Started some other way since the card went out, such as from To do or /next, means it wasn't put off.
            if (sentAt < now - AfterThis.Kept || FindItem(connection, source, itemId) is not { Done: false } item || !Activation.CanStart(item)
                || WorkedOnSince(connection, ItemRef.Format(item), sentAt))
            {
                continue;
            }

            Hold(connection, source, itemId, sentAt, message);
            held++;
        }

        transaction.Commit();
        return held;
    }

    /// <summary>
    /// The most recent hold, taken off so it's shown once. Holds past <see cref="AfterThis.Kept"/> are dropped, and
    /// so is one whose item was finished or is already being worked on.
    /// </summary>
    public StoredItem? TakeAfterThis(DateTime now)
    {
        var working = GetActiveSession(now)?.TaskRef;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var expire = connection.CreateCommand())
        {
            expire.CommandText = "DELETE FROM after_this WHERE held_at < $expired";
            expire.Parameters.AddWithValue("$expired", FormatReminder(now - AfterThis.Kept));
            expire.ExecuteNonQuery();
        }

        while (true)
        {
            long id;
            long itemId;
            ItemSource source;
            DateTime heldAt;
            using (var find = connection.CreateCommand())
            {
                find.CommandText = "SELECT id, item_id, source, held_at FROM after_this ORDER BY held_at DESC, id DESC LIMIT 1";
                using var reader = find.ExecuteReader();
                if (!reader.Read())
                {
                    reader.Close();
                    transaction.Commit();
                    return null;
                }

                id = reader.GetInt64(0);
                itemId = reader.GetInt64(1);
                source = Enum.Parse<ItemSource>(reader.GetString(2));
                heldAt = DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture);
            }

            // Taking the newest clears everything, so nothing older ever comes back after it.
            using (var take = connection.CreateCommand())
            {
                take.CommandText = "DELETE FROM after_this WHERE id = $id; SELECT changes(); DELETE FROM after_this;";
                take.Parameters.AddWithValue("$id", id);
                if ((long)take.ExecuteScalar()! != 1)
                {
                    continue;
                }
            }

            var taken = FindItem(connection, source, itemId) is { Done: false } item && Activation.CanStart(item) && ItemRef.Format(item) != working
                && !WorkedOnSince(connection, ItemRef.Format(item), heldAt)
                ? item
                : null;
            transaction.Commit();
            return taken;
        }
    }

    /// <summary>Forgets any hold on an item, once it was answered some other way.</summary>
    public void DropAfterThis(StoredItem item)
    {
        using var connection = Open();
        ForgetAfterThis(connection, item.Source, item.Id);
    }

    /// <summary>
    /// The item was dealt with some other way: started, finished, moved, snoozed, deleted, or marked didn't help.
    /// Its hold goes, and its cards still waiting for an answer are marked looked at, so none of them holds it later.
    /// </summary>
    private static void ForgetAfterThis(SqliteConnection connection, ItemSource source, long itemId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM after_this WHERE item_id = $item AND source = $source;
            UPDATE sent_reminders SET held = 1 WHERE item_id = $item AND source = $source AND handled = 0;
            """;
        command.Parameters.AddWithValue("$item", itemId);
        command.Parameters.AddWithValue("$source", source.ToString());
        command.ExecuteNonQuery();
    }

    /// <summary>Forgets the hold on the item a reference names, such as a reminder card's This didn't help.</summary>
    public void DropAfterThis(string? itemRef)
    {
        if (ItemRef.TryParse(itemRef, out var source, out var id))
        {
            using var connection = Open();
            ForgetAfterThis(connection, source, id);
        }
    }

    /// <summary>Whether a session on this item was started or touched since then, so it was being worked on.</summary>
    private static bool WorkedOnSince(SqliteConnection connection, string itemRef, DateTime since)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM activation_sessions WHERE task_ref = $ref AND (started_at >= $since OR touched_at >= $since) LIMIT 1";
        command.Parameters.AddWithValue("$ref", itemRef);
        command.Parameters.AddWithValue("$since", FormatReminder(since));
        return command.ExecuteScalar() is not null;
    }

    /// <summary>A button on the card was used after all, so its hold goes.</summary>
    private static void DropAfterThis(SqliteConnection connection, string messageId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM after_this WHERE message_id = $message";
        command.Parameters.AddWithValue("$message", messageId);
        command.ExecuteNonQuery();
    }
}
