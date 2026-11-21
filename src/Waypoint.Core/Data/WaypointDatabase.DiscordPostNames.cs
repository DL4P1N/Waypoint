namespace Waypoint.Core.Data;

/// <summary>
/// The name a forum post was made with, kept with its send note. Discord takes no nonce for a new post, so a post
/// that went through unheard is looked for by this name, which stays right even if the note's title changes after.
/// </summary>
public sealed partial class WaypointDatabase
{
    private void AddDiscordSendTriedName() => AddColumnIfMissing("discord_sends", "tried_name", "TEXT");

    /// <summary>A post is about to be made with this name.</summary>
    public void SetDiscordSendTriedName(string key, string name)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE discord_sends SET tried_name = $name WHERE send_key = $key";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$name", name);
        command.ExecuteNonQuery();
    }

    /// <summary>The name the last try made its post with, or null when none was noted.</summary>
    public string? GetDiscordSendTriedName(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tried_name FROM discord_sends WHERE send_key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }
}
