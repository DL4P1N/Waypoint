using Microsoft.Data.Sqlite;

namespace Waypoint.Core.Data;

/// <summary>
/// Who owns the connection and transaction a call works on. A call on the database itself opens a connection of its
/// own and, when it needs one, a transaction of its own. A call on the database handed to the work of
/// <see cref="InOneWrite{T}(Func{WaypointDatabase, T})"/> works on that write's one connection instead, and a
/// transaction it begins is a savepoint of its own inside the write's transaction.
/// </summary>
public sealed partial class WaypointDatabase
{
    /// <summary>The database itself. A write's database keeps its settings there, so both always agree.</summary>
    private readonly WaypointDatabase _root;

    /// <summary>The write every call on this database runs in, or null for the database itself.</summary>
    private readonly Write? _write;

    /// <summary>The database handed to a write's work. Every call made on it runs on <paramref name="write"/>.</summary>
    private WaypointDatabase(WaypointDatabase root, Write write)
    {
        _root = root;
        _write = write;
        _connectionString = root._connectionString;
    }

    /// <summary>
    /// The one connection and transaction of a single write, owned by the <see cref="InOneWrite{T}(Func{WaypointDatabase, T})"/>
    /// that began them. A connection can't be shared between threads, so its database refuses use from any thread but
    /// the one that began it, and refuses use once the write is over, such as by work handed on that outlived it.
    /// </summary>
    private sealed class Write(SqliteConnection connection, SqliteTransaction transaction)
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private int _savepoints;

        /// <summary>Set once the write has landed or been let go. Read from other threads, so always read fresh.</summary>
        public volatile bool Over;

        /// <summary>The write's connection, for a call on the thread that began it while it's still running.</summary>
        public SqliteConnection Connection
        {
            get
            {
                Check();
                return connection;
            }
        }

        /// <summary>A savepoint of its own inside the write, with a name not used before on its connection.</summary>
        public Step Savepoint()
        {
            Check();
            return Step.Savepoint(transaction, $"one_write_{++_savepoints}");
        }

        private void Check()
        {
            if (Over)
            {
                throw new InvalidOperationException("A single write was used after it was over.");
            }

            if (_thread != Environment.CurrentManagedThreadId)
            {
                throw new InvalidOperationException("A single write was used from a thread other than the one that began it.");
            }
        }
    }

    /// <summary>
    /// The databases with a single write open on this thread, each held by the database itself. A transaction of its
    /// own begun on one of those on the same thread could only wait for the write, which can't land until that call
    /// returns, so it would stall for the whole busy wait and then fail. It's refused straight away instead. Reads
    /// there are fine: they read what's landed on a connection of their own. A write of a single statement with no
    /// transaction can't be told apart from a read here, so it still waits out the busy wait before it fails.
    /// </summary>
    [ThreadStatic]
    private static List<WaypointDatabase>? _writingOnThread;

    private void RefuseIfWritingOnThisThread()
    {
        if (_writingOnThread?.Contains(this) == true)
        {
            throw new InvalidOperationException("A write was begun on the database itself inside a single write on the same thread. Use the database the single write handed its work instead.");
        }
    }

    /// <summary>Closes a connection a call opened for itself, and leaves a write's connection to the write.</summary>
    internal readonly struct Lease(SqliteConnection? owned) : IDisposable
    {
        public void Dispose() => owned?.Dispose();
    }

    /// <summary>
    /// The connection a call works on: this database's write's, or else one of the call's own from <see cref="Open"/>,
    /// closed when the lease is.
    /// </summary>
    internal Lease Connect(out SqliteConnection connection)
    {
        if (_write is { } write)
        {
            connection = write.Connection;
            return default;
        }

        connection = Open();
        return new Lease(connection);
    }

    /// <summary>
    /// A transaction for one call on <paramref name="connection"/>, which came from <see cref="Connect"/>. In a write it's
    /// a savepoint of the call's own, so a call that commits keeps its part and one that doesn't undoes only its own,
    /// just as with a transaction of its own. Otherwise it's a transaction of its own, begun with BEGIN IMMEDIATE.
    /// </summary>
    internal Step Begin(SqliteConnection connection)
    {
        if (_write is not { } write)
        {
            RefuseIfWritingOnThisThread();
            return Step.Own(connection.BeginTransaction());
        }

        if (!ReferenceEquals(connection, write.Connection))
        {
            throw new InvalidOperationException("A transaction was begun inside a single write on a connection that isn't the write's.");
        }

        return write.Savepoint();
    }

    /// <summary>
    /// What a call begins with <see cref="Begin"/>: a transaction of its own, or a savepoint of its own in a write's
    /// transaction. Either way, Commit keeps what the call wrote, and disposing it uncommitted undoes it.
    /// </summary>
    internal sealed class Step : IDisposable
    {
        private readonly SqliteTransaction _transaction;
        private readonly string? _savepoint;
        private bool _done;

        private Step(SqliteTransaction transaction, string? savepoint)
        {
            _transaction = transaction;
            _savepoint = savepoint;
        }

        public static Step Own(SqliteTransaction transaction) => new(transaction, null);

        public static Step Savepoint(SqliteTransaction transaction, string name)
        {
            transaction.Save(name);
            return new(transaction, name);
        }

        public void Commit()
        {
            if (_savepoint is null)
            {
                _transaction.Commit();
                return;
            }

            if (_done)
            {
                throw new InvalidOperationException("This savepoint has already been committed or undone.");
            }

            _done = true;
            _transaction.Release(_savepoint);
        }

        public void Dispose()
        {
            if (_savepoint is null)
            {
                _transaction.Dispose();
                return;
            }

            if (_done)
            {
                return;
            }

            _done = true;
            _transaction.Rollback(_savepoint);
            _transaction.Release(_savepoint);
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> as a single write on one connection and one BEGIN IMMEDIATE transaction, owned here.
    /// The work gets the write's database, and every call it makes on that one runs in the write. It all lands when
    /// the work returns, and none of it when the work throws or the process dies first. Called on a write's database,
    /// it runs as a savepoint inside that write, so a failure inside, even one the caller catches, undoes only its own
    /// part. Calls on any other database, such as the one this was called on, aren't part of the write.
    /// </summary>
    public T InOneWrite<T>(Func<WaypointDatabase, T> work)
    {
        if (_write is { } running)
        {
            using var savepoint = running.Savepoint();
            var inner = work(this);
            savepoint.Commit();
            return inner;
        }

        RefuseIfWritingOnThisThread();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var write = new Write(connection, transaction);
        var writing = _writingOnThread ??= [];
        writing.Add(this);
        try
        {
            var result = work(new WaypointDatabase(this, write));
            write.Over = true;
            transaction.Commit();
            return result;
        }
        finally
        {
            write.Over = true;
            writing.Remove(this);
        }
    }

    /// <inheritdoc cref="InOneWrite{T}(Func{WaypointDatabase, T})"/>
    public void InOneWrite(Action<WaypointDatabase> work) => InOneWrite(database =>
    {
        work(database);
        return 0;
    });
}
