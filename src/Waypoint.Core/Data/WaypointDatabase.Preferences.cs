using Microsoft.Data.Sqlite;
using Waypoint.Core.Remote;

namespace Waypoint.Core.Data;

/// <summary>Preferences are read through <see cref="IPreferences"/>, so a page can take them from the server instead.</summary>
public sealed partial class WaypointDatabase : IPreferences
{
    public string? GetPreference(string key)
    {
        using var connection = Open();
        return ReadPreference(connection, key);
    }

    public void SetPreference(string key, string value)
    {
        using var connection = Open();
        WritePreference(connection, key, value);
    }

    /// <summary>Sets several preferences in one write, for values that only make sense together.</summary>
    public void SetPreferences(params (string Key, string Value)[] values)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var (key, value) in values)
        {
            WritePreference(connection, key, value);
        }

        transaction.Commit();
    }

    private static string? ReadPreference(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM preferences WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void WritePreference(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO preferences (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }
}
