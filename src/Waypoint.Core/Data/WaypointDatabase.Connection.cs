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
    /// Each connection also syncs to disk only at checkpoints rather than on every commit, which the write-ahead log
    /// (see <see cref="UseWriteAheadLog"/>) keeps safe if Waypoint is killed: a commit is lost only if the PC itself
    /// loses power just then, and the database is never left damaged. The log is cut back to <see cref="LogKeptBytes"/>
    /// each time it starts over after a checkpoint, so one big write, or a read holding it open a while, doesn't leave
    /// it that size on disk for good. It's set here because whichever connection commits next does the cutting.
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
        wait.CommandText = $"PRAGMA busy_timeout = {(int)BusyWait.TotalMilliseconds}; PRAGMA synchronous = NORMAL; PRAGMA journal_size_limit = {LogKeptBytes}";
        wait.ExecuteNonQuery();
        return connection;
    }

    private static readonly TimeSpan BusyWait = TimeSpan.FromSeconds(30);

    /// <summary>The most the write-ahead log keeps on disk once it's been folded in: 16 MB.</summary>
    internal const long LogKeptBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Puts the database in write-ahead mode, once as it's opened. It stays that way in the file, so an older database
    /// is switched over the first time this copy opens it. Reads then never wait on a write, or a write on reads, so a
    /// page refreshing while Discord or the notes folder saves something doesn't hold either up. A database that can't
    /// switch, such as one kept only in memory, carries on as it was.
    /// </summary>
    private void UseWriteAheadLog()
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL";
        command.ExecuteScalar();
    }

    private void Execute(string sql)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
