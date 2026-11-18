using System.Globalization;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

/// <summary>
/// The owner's Discord messages as they're worked through. A message is claimed when it's handed over and finished
/// only once what it asked for is saved, so one cut off by a crash or a close is handed over again later instead of
/// being skipped. Each file on a message is marked as it's dealt with, so a message handed over again only does
/// what's left.
/// </summary>
public sealed partial class WaypointDatabase
{
    /// <summary>The old list of messages already seen, kept in preferences. Those count as finished.</summary>
    private const string OldSeenKey = "discord_seen";

    /// <summary>A message is handed over again at most this many times, so one that always fails can't go on forever.</summary>
    private const int MaxDiscordTries = 5;

    /// <summary>Finished messages are only needed while they could still be read again, so older ones are let go.</summary>
    private static readonly TimeSpan KeepFinishedDiscord = TimeSpan.FromDays(30);

    private void CreateDiscordInboxTables() => Execute("""
        CREATE TABLE IF NOT EXISTS discord_inbox (
            message_id TEXT PRIMARY KEY,
            channel_id TEXT NOT NULL,
            claimed_at TEXT NOT NULL,
            tries INTEGER NOT NULL DEFAULT 1,
            parts TEXT NOT NULL DEFAULT '',
            done_at TEXT
        );
        """);

    /// <summary>
    /// True when the message should be handed over now: the first time it's seen, or again once an earlier claim
    /// has gone <paramref name="unfinishedAfter"/> without being finished. False once it's finished.
    /// </summary>
    public bool ClaimDiscordMessage(string messageId, string channelId, DateTime now, TimeSpan unfinishedAfter)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (OldSeen(connection, messageId))
        {
            return false;
        }

        using (var find = connection.CreateCommand())
        {
            find.CommandText = "SELECT claimed_at, done_at FROM discord_inbox WHERE message_id = $id";
            find.Parameters.AddWithValue("$id", messageId);
            using var reader = find.ExecuteReader();
            if (reader.Read() && (!reader.IsDBNull(1) || DateTime.Parse(reader.GetString(0), CultureInfo.InvariantCulture) > now - unfinishedAfter))
            {
                return false;
            }
        }

        using (var claim = connection.CreateCommand())
        {
            claim.CommandText = """
                INSERT INTO discord_inbox (message_id, channel_id, claimed_at) VALUES ($id, $channel, $at)
                ON CONFLICT (message_id) DO UPDATE SET claimed_at = $at, tries = tries + 1,
                    channel_id = CASE WHEN $channel = '' THEN channel_id ELSE $channel END
                """;
            claim.Parameters.AddWithValue("$id", messageId);
            claim.Parameters.AddWithValue("$channel", channelId);
            claim.Parameters.AddWithValue("$at", FormatReminder(now));
            claim.ExecuteNonQuery();
        }

        transaction.Commit();
        return true;
    }

    /// <summary>Messages handed over that weren't finished in time, oldest first, with the channel each came from.</summary>
    public IReadOnlyList<(string MessageId, string ChannelId)> GetUnfinishedDiscordMessages(DateTime now, TimeSpan unfinishedAfter)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT message_id, channel_id FROM discord_inbox
            WHERE done_at IS NULL AND channel_id <> '' AND claimed_at <= $before AND tries < $tries
            ORDER BY claimed_at
            """;
        command.Parameters.AddWithValue("$before", FormatReminder(now - unfinishedAfter));
        command.Parameters.AddWithValue("$tries", MaxDiscordTries);
        var unfinished = new List<(string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            unfinished.Add((reader.GetString(0), reader.GetString(1)));
        }

        return unfinished;
    }

    /// <summary>Counts a try at a message that couldn't be read back, so one that never can is let go in the end.</summary>
    public void CountDiscordTry(string messageId, DateTime now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE discord_inbox SET tries = tries + 1, claimed_at = $at WHERE message_id = $id AND done_at IS NULL";
        command.Parameters.AddWithValue("$id", messageId);
        command.Parameters.AddWithValue("$at", FormatReminder(now));
        command.ExecuteNonQuery();
    }

    public bool IsDiscordMessageFinished(string messageId)
    {
        using var connection = Open();
        return IsFinished(connection, messageId);
    }

    /// <summary>Marks a message as fully dealt with, so it's never handed over again.</summary>
    public void FinishDiscordMessage(string messageId, DateTime now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        FinishDiscordMessage(connection, messageId, now);
        transaction.Commit();
    }

    /// <summary>The same in the caller's write, so it lands with what the message did. False if it was already finished.</summary>
    internal static bool FinishDiscordMessage(SqliteConnection connection, string messageId, DateTime now)
    {
        if (IsFinished(connection, messageId))
        {
            return false;
        }

        using var finish = connection.CreateCommand();
        finish.CommandText = """
            INSERT INTO discord_inbox (message_id, channel_id, claimed_at, done_at) VALUES ($id, '', $at, $at)
            ON CONFLICT (message_id) DO UPDATE SET done_at = $at;
            DELETE FROM discord_inbox WHERE done_at IS NOT NULL AND done_at < $old;
            """;
        finish.Parameters.AddWithValue("$id", messageId);
        finish.Parameters.AddWithValue("$at", FormatReminder(now));
        finish.Parameters.AddWithValue("$old", FormatReminder(now - KeepFinishedDiscord));
        finish.ExecuteNonQuery();
        return true;
    }

    /// <summary>Whether one part of a message, such as one of its files, was already dealt with.</summary>
    public bool IsDiscordPartDone(string messageId, string part)
    {
        using var connection = Open();
        return IsFinished(connection, messageId) || Parts(connection, messageId).Contains(part);
    }

    /// <summary>
    /// Marks one part of a message as dealt with. Once every part in <paramref name="all"/> is, the message is
    /// finished in the same write. True when it's finished.
    /// </summary>
    public bool FinishDiscordPart(string messageId, string part, IReadOnlyCollection<string> all, DateTime now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var parts = Parts(connection, messageId);
        parts.Add(part);
        using (var save = connection.CreateCommand())
        {
            save.CommandText = """
                INSERT INTO discord_inbox (message_id, channel_id, claimed_at, parts) VALUES ($id, '', $at, $parts)
                ON CONFLICT (message_id) DO UPDATE SET parts = $parts
                """;
            save.Parameters.AddWithValue("$id", messageId);
            save.Parameters.AddWithValue("$at", FormatReminder(now));
            save.Parameters.AddWithValue("$parts", string.Join(' ', parts));
            save.ExecuteNonQuery();
        }

        var finished = all.All(parts.Contains);
        if (finished)
        {
            FinishDiscordMessage(connection, messageId, now);
        }

        transaction.Commit();
        return finished;
    }

    /// <summary>
    /// Saves a capture under an ID of its own, such as the Discord message it came from, so handing the same
    /// message over again saves nothing more. Null when it was already saved.
    /// </summary>
    public long? AddCaptureOnce(string clientId, string text, DateTimeOffset? createdAt, CaptureOrigin origin, string? fileName = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO captures (text, created_at, origin, file_name, client_id)
            VALUES ($text, $createdAt, $origin, $fileName, $clientId) RETURNING id
            """;
        command.Parameters.AddWithValue("$fileName", (object?)fileName ?? DBNull.Value);
        command.Parameters.AddWithValue("$origin", origin.ToString());
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$createdAt", (createdAt?.ToLocalTime() ?? DateTimeOffset.Now).ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$clientId", clientId);
        return command.ExecuteScalar() is long id ? id : null;
    }

    /// <summary>Whether a capture was already saved under this ID.</summary>
    public bool HasCaptureClientId(string clientId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM captures WHERE client_id = $clientId";
        command.Parameters.AddWithValue("$clientId", clientId);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>The ID a capture was saved under, if it had one.</summary>
    public string? GetCaptureClientId(long captureId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT client_id FROM captures WHERE id = $id";
        command.Parameters.AddWithValue("$id", captureId);
        return command.ExecuteScalar() as string;
    }

    private static bool IsFinished(SqliteConnection connection, string messageId)
    {
        if (OldSeen(connection, messageId))
        {
            return true;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM discord_inbox WHERE message_id = $id AND done_at IS NOT NULL";
        command.Parameters.AddWithValue("$id", messageId);
        return command.ExecuteScalar() is not null;
    }

    private static bool OldSeen(SqliteConnection connection, string messageId) =>
        (ReadPreference(connection, OldSeenKey) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(messageId);

    private static HashSet<string> Parts(SqliteConnection connection, string messageId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT parts FROM discord_inbox WHERE message_id = $id";
        command.Parameters.AddWithValue("$id", messageId);
        return ((command.ExecuteScalar() as string) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    }
}
