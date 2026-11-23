using Microsoft.Data.Sqlite;

namespace Waypoint.Core.Data;

/// <summary>
/// Work made of many of this class's own calls saved as one write, so a crash partway keeps all of it or none. While
/// <see cref="InOneWrite{T}"/> runs, every connection opened on the same flow shares one real connection and its
/// transaction. A transaction begun on one of those becomes a savepoint inside it, so a call that rolls its own
/// write back still does, and nothing is saved until the whole of the work is.
/// </summary>
public sealed partial class WaypointDatabase
{
    private readonly AsyncLocal<OneWrite?> _oneWrite = new();

    /// <summary>
    /// The write running on this flow, and the thread that began it. Work handed to another thread carries it along,
    /// so <see cref="Open"/> refuses that loudly while the write runs instead of sharing its connection. Once it's
    /// over, anything still carrying it opens a connection of its own as usual.
    /// </summary>
    private sealed class OneWrite(SqliteConnection connection, SqliteTransaction transaction)
    {
        public (SqliteConnection Connection, SqliteTransaction Transaction) Shared { get; } = (connection, transaction);

        public int Thread { get; } = Environment.CurrentManagedThreadId;

        /// <summary>Set once the write has landed or been let go. Read from other threads, so always read fresh.</summary>
        public volatile bool Over;

        public (SqliteConnection Connection, SqliteTransaction Transaction) Join() =>
            Thread != Environment.CurrentManagedThreadId
                ? throw new InvalidOperationException("A single write was used from a thread other than the one that began it.")
                : Shared;
    }

    /// <summary>
    /// Runs <paramref name="work"/> as a single write. It all lands when the work returns, and none of it lands when
    /// the work throws or the process dies first. Work already inside one just joins it. The work must stay on its
    /// own flow and not hand database calls to other threads, since they would share the one connection.
    /// </summary>
    public T InOneWrite<T>(Func<T> work)
    {
        if (_oneWrite.Value is { Over: false } running)
        {
            running.Join();
            return work();
        }

        if (_write is not null)
        {
            throw new InvalidOperationException("A single write of the older kind was begun inside one of the newer kind.");
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var write = new OneWrite(connection, transaction);
        _oneWrite.Value = write;
        try
        {
            var result = work();
            _oneWrite.Value = null;
            write.Over = true;
            transaction.Commit();
            return result;
        }
        finally
        {
            _oneWrite.Value = null;
            write.Over = true;
        }
    }

    /// <summary>
    /// A stand-in for the shared connection. It's open on an empty in-memory database of its own, so the library's
    /// checks on an open connection and its transaction pass, but every command it makes runs on the shared one.
    /// The BEGIN, COMMIT, and ROLLBACK its transactions send become a savepoint of their own on the shared one.
    /// </summary>
    private sealed class SharedConnection : SqliteConnection
    {
        private static int _savepoints;

        private readonly (SqliteConnection Connection, SqliteTransaction Transaction) _shared;
        private string? _savepoint;

        public SharedConnection((SqliteConnection Connection, SqliteTransaction Transaction) shared)
            : base("Data Source=:memory:")
        {
            _shared = shared;
            Open();
        }

        public override SqliteCommand CreateCommand() => new SharedCommand(this) { Connection = _shared.Connection, Transaction = _shared.Transaction };

        /// <summary>The SQL a transaction of this stand-in sends, as it runs on the shared connection instead.</summary>
        internal string Translate(string sql)
        {
            switch (sql.Trim().TrimEnd(';').ToUpperInvariant())
            {
                case "BEGIN":
                case "BEGIN IMMEDIATE":
                    _savepoint = $"one_write_{Interlocked.Increment(ref _savepoints)}";
                    return $"SAVEPOINT {_savepoint};";
                case "COMMIT":
                    return $"RELEASE {Take()};";
                case "ROLLBACK":
                    var savepoint = Take();
                    return $"ROLLBACK TO {savepoint}; RELEASE {savepoint};";
                default:
                    return sql;
            }
        }

        private string Take()
        {
            var savepoint = _savepoint ?? throw new InvalidOperationException("No transaction is running on this connection.");
            _savepoint = null;
            return savepoint;
        }
    }

    private sealed class SharedCommand(SharedConnection owner) : SqliteCommand
    {
        public override int ExecuteNonQuery()
        {
            CommandText = owner.Translate(CommandText);
            return base.ExecuteNonQuery();
        }
    }
}
