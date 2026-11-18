using Microsoft.Data.Sqlite;

namespace Waypoint.Core.Data;

/// <summary>Opening a connection, with SQLite's busy wait set, or joining the write already running on this flow.</summary>
public sealed partial class WaypointDatabase
{
    private readonly string _connectionString;

    /// <summary>
    /// SQLite's own busy wait is set on every connection. The library only retries a locked database while a statement
    /// steps, but an INSERT ... RETURNING commits when its reader closes, so a read running at the same moment (a
    /// Discord view refresh, say) would fail it straight away with "database is locked" instead of waiting.
    /// Foreign keys are checked on every connection too (see the constructor), so a row can't point at one that
    /// isn't there. Rows from older copies that already do are left alone: SQLite doesn't recheck them, and the
    /// reads join to what they point at, so they stay out of sight. Inside <see cref="InOneWrite{T}"/>, it's a
    /// stand-in for that write's connection instead.
    /// </summary>
    internal SqliteConnection Open()
    {
        if (_oneWrite.Value is { Over: false } shared)
        {
            return new SharedConnection(shared.Join());
        }

        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var wait = connection.CreateCommand();
        wait.CommandText = $"PRAGMA busy_timeout = {(int)BusyWait.TotalMilliseconds}";
        wait.ExecuteNonQuery();
        return connection;
    }

    private static readonly TimeSpan BusyWait = TimeSpan.FromSeconds(30);

    private void Execute(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
