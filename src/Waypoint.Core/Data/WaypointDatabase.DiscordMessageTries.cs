using System.Globalization;

namespace Waypoint.Core.Data;

/// <summary>
/// How many times each message typed in a post has been tried, kept across restarts, so one that keeps failing is
/// left as it is after a few tries instead of being tried again forever.
/// </summary>
public sealed partial class WaypointDatabase
{
    /// <summary>How long a count is kept. A message is dealt with or left long before this, if it's read at all.</summary>
    private static readonly TimeSpan MessageTriesKept = TimeSpan.FromDays(30);

    private void CreateDiscordMessageTryTables() => Execute("""
        CREATE TABLE IF NOT EXISTS discord_message_tries (
            thread_id TEXT NOT NULL,
            message_id TEXT NOT NULL,
            tries INTEGER NOT NULL,
            tried_at TEXT NOT NULL,
            PRIMARY KEY (thread_id, message_id)
        );
        """);

    /// <summary>
    /// Counts one more try at a message in a post, before it's tried, so a try a crash cuts off still counts, and gives
    /// back how many there have been with this one. Counts kept from long ago are forgotten.
    /// </summary>
    public int AddDiscordMessageTry(string threadId, string messageId, DateTime now)
    {
        using var lease = Connect(out var connection);
        using (var prune = connection.CreateCommand())
        {
            prune.CommandText = "DELETE FROM discord_message_tries WHERE tried_at < $before";
            prune.Parameters.AddWithValue("$before", (now - MessageTriesKept).ToString("O", CultureInfo.InvariantCulture));
            prune.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO discord_message_tries (thread_id, message_id, tries, tried_at) VALUES ($thread, $message, 1, $at)
            ON CONFLICT (thread_id, message_id) DO UPDATE SET tries = tries + 1, tried_at = $at
            RETURNING tries
            """;
        command.Parameters.AddWithValue("$thread", threadId);
        command.Parameters.AddWithValue("$message", messageId);
        command.Parameters.AddWithValue("$at", now.ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>How many times a message in a post has been tried so far, or 0.</summary>
    public int DiscordMessageTries(string threadId, string messageId)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tries FROM discord_message_tries WHERE thread_id = $thread AND message_id = $message";
        command.Parameters.AddWithValue("$thread", threadId);
        command.Parameters.AddWithValue("$message", messageId);
        return command.ExecuteScalar() is { } tries ? Convert.ToInt32(tries, CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>The message was dealt with or left as it is, so its count goes.</summary>
    public void ForgetDiscordMessageTries(string threadId, string messageId)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM discord_message_tries WHERE thread_id = $thread AND message_id = $message";
        command.Parameters.AddWithValue("$thread", threadId);
        command.Parameters.AddWithValue("$message", messageId);
        command.ExecuteNonQuery();
    }
}
