using System.Globalization;

namespace Waypoint.Core.Data;

/// <summary>
/// Files from Discord already added to a note, by Discord's attachment ID, so a message read again, such as after a
/// failure partway, never adds the same file twice.
/// </summary>
public sealed partial class WaypointDatabase
{
    /// <summary>How long an added file is remembered. A message is read again long before this, if at all.</summary>
    private static readonly TimeSpan AttachedKept = TimeSpan.FromDays(30);

    private void CreateDiscordAttachedTables() => Execute("""
        CREATE TABLE IF NOT EXISTS discord_attached_files (
            attachment_id TEXT PRIMARY KEY,
            note_id INTEGER NOT NULL,
            attached_at TEXT NOT NULL
        );
        """);

    public bool WasDiscordFileAttached(string attachmentId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM discord_attached_files WHERE attachment_id = $id";
        command.Parameters.AddWithValue("$id", attachmentId);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>Remembers an added file, and forgets ones added long ago so the list doesn't grow forever.</summary>
    public void MarkDiscordFileAttached(string attachmentId, long noteId, DateTime now)
    {
        using var connection = Open();
        using (var prune = connection.CreateCommand())
        {
            prune.CommandText = "DELETE FROM discord_attached_files WHERE attached_at < $before";
            prune.Parameters.AddWithValue("$before", (now - AttachedKept).ToString("O", CultureInfo.InvariantCulture));
            prune.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO discord_attached_files (attachment_id, note_id, attached_at) VALUES ($id, $note, $at)";
        command.Parameters.AddWithValue("$id", attachmentId);
        command.Parameters.AddWithValue("$note", noteId);
        command.Parameters.AddWithValue("$at", now.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }
}
