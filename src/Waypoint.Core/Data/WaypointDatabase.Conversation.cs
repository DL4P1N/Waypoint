using System.Globalization;
using System.Text.Json;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

public sealed partial class WaypointDatabase
{
    private const int MaxExchangesShown = 6;
    private const int MaxExchangesKept = 200;
    private const int MaxExchangeLength = 400;

    private static readonly TimeSpan ConversationWindow = TimeSpan.FromHours(2);

    /// <summary>Saves a note and its reply, and drops the oldest once there are plenty.</summary>
    public void AddExchange(CaptureOrigin origin, string note, string reply, IReadOnlyList<string> refs, DateTime now)
    {
        // Saving and trimming land together, so a trim that fails doesn't leave one more exchange than is kept.
        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversation (origin, at, note, reply, refs) VALUES ($origin, $at, $note, $reply, $refs);
            DELETE FROM conversation WHERE id NOT IN (SELECT id FROM conversation ORDER BY id DESC LIMIT $kept);
            """;
        command.Parameters.AddWithValue("$origin", origin.ToString());
        command.Parameters.AddWithValue("$at", FormatReminder(now));
        command.Parameters.AddWithValue("$note", Shorten(note));
        command.Parameters.AddWithValue("$reply", Shorten(reply));
        command.Parameters.AddWithValue("$refs", JsonSerializer.Serialize(refs));
        command.Parameters.AddWithValue("$kept", MaxExchangesKept);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>The last few exchanges typed in one place, oldest first. Older talk has gone cold, so it's left out.</summary>
    public IReadOnlyList<Exchange> GetRecentExchanges(CaptureOrigin origin, DateTime now)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT at, note, reply, refs FROM conversation
            WHERE origin = $origin AND at >= $since
            ORDER BY id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$origin", origin.ToString());
        command.Parameters.AddWithValue("$since", FormatReminder(now - ConversationWindow));
        command.Parameters.AddWithValue("$limit", MaxExchangesShown);

        var exchanges = new List<Exchange>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            exchanges.Add(new Exchange(
                DateTime.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                reader.GetString(1),
                reader.GetString(2),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? []));
        }

        exchanges.Reverse();
        return exchanges;
    }

    /// <summary>The items behind a set of refs. Any that are gone are skipped.</summary>
    /// <summary>
    /// Everything a capture added that's still open, in the order it was added, so each line of its reply can link
    /// to it. Steps are left out, since their parent's link shows them.
    /// </summary>
    public IReadOnlyList<StoredItem> GetItemsFromCapture(long captureId)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ItemColumns} FROM items WHERE capture_id = $id AND done = 0 AND canceled = 0 AND parent_item_id IS NULL AND parent_event_id IS NULL ORDER BY id";
        command.Parameters.AddWithValue("$id", captureId);
        var items = new List<StoredItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    public IReadOnlyList<StoredItem> FindItems(IEnumerable<string> refs)
    {
        using var lease = Connect(out var connection);
        var items = new List<StoredItem>();
        foreach (var text in refs)
        {
            if (ItemRef.TryParse(text, out var source, out var id) && FindItem(connection, source, id) is { } item)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private static string Shorten(string text) => text.Length > MaxExchangeLength ? text[..MaxExchangeLength] : text;
}
