using System.Globalization;

namespace Waypoint.Core.Data;

/// <summary>
/// A message Waypoint is about to post, or has posted, whose record isn't saved yet. Key says what it's for, such as
/// one reminder's card, and stays the same however many times it's tried. Nonce goes with each try, so Discord hands
/// back the message already made instead of making another. MessageId is filled in once Discord says it's made.
/// Fresh is true only when this call made the row, so there's been no try before it.
/// </summary>
public sealed record DiscordSend(string Key, string Nonce, string? Channel, string? MessageId, DateTime StartedAt, bool Fresh);

/// <summary>
/// Posts noted before they go out, so a crash between posting and saving what the post is for never leaves a second
/// post or a stray one. A row is made before the post, filled in with the message once it's made, and taken away in
/// the same write that saves the record it was for.
/// </summary>
public sealed partial class WaypointDatabase
{
    private void CreateDiscordSendTables() => Execute("""
        CREATE TABLE IF NOT EXISTS discord_sends (
            send_key TEXT PRIMARY KEY,
            nonce TEXT NOT NULL,
            channel_id TEXT,
            message_id TEXT,
            started_at TEXT NOT NULL
        );
        """);

    /// <summary>Whether a note's post was shown on this PC instead, while Discord couldn't say if it went out.</summary>
    private void AddDiscordSendShownHere() => AddColumnIfMissing("discord_sends", "shown_here", "INTEGER NOT NULL DEFAULT 0");

    /// <summary>
    /// Marks a note's post as shown on this PC instead. True only the first time, so it's shown once however many
    /// rounds or restarts follow. The note itself stays, so a post found in Discord later is never sent again.
    /// </summary>
    public bool MarkDiscordSendShownHere(string key)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE discord_sends SET shown_here = 1 WHERE send_key = $key AND shown_here = 0";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Notes a post before it's made, or hands back the note from a try before. Only one for another channel starts
    /// over with a new nonce. However old a note is, its try may have gone out, so it stays until what it was for is
    /// settled or let go, and its post is looked for rather than made again.
    /// </summary>
    public DiscordSend BeginDiscordSend(string key, string? channel, DateTime now)
    {
        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        if (ReadDiscordSend(connection, key) is { } before
            && (before.MessageId is not null || before.Channel == channel))
        {
            transaction.Commit();
            return before;
        }

        using var write = connection.CreateCommand();
        write.CommandText = "INSERT OR REPLACE INTO discord_sends (send_key, nonce, channel_id, message_id, started_at) VALUES ($key, $nonce, $channel, NULL, $at)";
        write.Parameters.AddWithValue("$key", key);
        write.Parameters.AddWithValue("$nonce", NewNonce());
        write.Parameters.AddWithValue("$channel", (object?)channel ?? DBNull.Value);
        write.Parameters.AddWithValue("$at", now.ToString("O", CultureInfo.InvariantCulture));
        write.ExecuteNonQuery();
        var made = ReadDiscordSend(connection, key)! with { Fresh = true };
        transaction.Commit();
        return made;
    }

    /// <summary>The post went out as this message.</summary>
    public void SetDiscordSendMessage(string key, string messageId)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE discord_sends SET message_id = $message WHERE send_key = $key";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$message", messageId);
        command.ExecuteNonQuery();
    }

    /// <summary>What the post was for is saved, or it's no longer wanted, so the note goes.</summary>
    public void FinishDiscordSend(string key)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM discord_sends WHERE send_key = $key";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Messages already kept as something: a reminder's card, or the post of a note other than <paramref name="exceptKey"/>.
    /// A post looked for by how it looks is never one of these.
    /// </summary>
    public HashSet<string> GetTrackedMessages(string exceptKey)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT message_id FROM sent_reminders WHERE message_id IS NOT NULL
            UNION SELECT message_id FROM discord_sends WHERE message_id IS NOT NULL AND send_key <> $key
            """;
        command.Parameters.AddWithValue("$key", exceptKey);
        using var reader = command.ExecuteReader();
        var messages = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            messages.Add(reader.GetString(0));
        }

        return messages;
    }

    /// <summary>The note for one post, or null.</summary>
    public DiscordSend? GetDiscordSend(string key)
    {
        using var lease = Connect(out var connection);
        return ReadDiscordSend(connection, key);
    }

    /// <summary>The notes whose keys start this way, oldest first.</summary>
    public IReadOnlyList<DiscordSend> GetDiscordSends(string keyPrefix)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT send_key, nonce, channel_id, message_id, started_at FROM discord_sends WHERE substr(send_key, 1, length($prefix)) = $prefix ORDER BY rowid";
        command.Parameters.AddWithValue("$prefix", keyPrefix);
        using var reader = command.ExecuteReader();
        var sends = new List<DiscordSend>();
        while (reader.Read())
        {
            sends.Add(ReadSend(reader));
        }

        return sends;
    }

    private static DiscordSend? ReadDiscordSend(Microsoft.Data.Sqlite.SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT send_key, nonce, channel_id, message_id, started_at FROM discord_sends WHERE send_key = $key";
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSend(reader) : null;
    }

    private static DiscordSend ReadSend(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        false);

    /// <summary>Discord takes up to 25 characters. This is 24, random, so no two posts share one.</summary>
    private static string NewNonce() => Guid.NewGuid().ToString("N")[..24];
}
