using Microsoft.Data.Sqlite;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>
/// The database is kept in write-ahead mode, switched over the first time an older one is opened. Backups stay single
/// files, and a restore leaves nothing from before it in the log.
/// </summary>
public sealed class WriteAheadLogTests : IDisposable
{
    private static readonly DateTime Now = new(2030, 4, 2, 9, 30, 0);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-wal-{Guid.NewGuid():N}");
    private readonly string _path;

    public WriteAheadLogTests()
    {
        Directory.CreateDirectory(_folder);
        _path = Path.Combine(_folder, "waypoint.db");
    }

    public void Dispose()
    {
        for (var attempt = 0; ; attempt++)
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_folder, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(100);
            }
        }
    }

    [Fact]
    public void An_older_database_with_a_plain_journal_is_switched_to_the_log_when_opened_and_keeps_its_data()
    {
        new WaypointDatabase(_path).CreateNote("Made up lecture", "Made up body", Now);
        SqliteConnection.ClearAllPools();
        Assert.Equal("delete", Scalar(_path, "PRAGMA journal_mode = DELETE"));
        Assert.Equal(1, JournalByte(_path));

        var database = new WaypointDatabase(_path);

        Assert.Equal("wal", Scalar(_path, "PRAGMA journal_mode"));
        Assert.Equal(2, JournalByte(_path));
        Assert.Equal("Made up lecture", Assert.Single(database.GetNotes()).Title);
        Assert.Equal("ok", Scalar(_path, "PRAGMA integrity_check"));
    }

    [Fact]
    public void Every_connection_syncs_only_at_checkpoints()
    {
        var database = new WaypointDatabase(_path);
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous";

        // 1 is NORMAL.
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public void The_log_shrinks_back_after_growing_past_its_cap()
    {
        var database = new WaypointDatabase(_path);
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_size_limit";
            Assert.Equal(16L * 1024 * 1024, command.ExecuteScalar());
        }

        // A read held open the whole time keeps the log from starting over, so it grows past the cap.
        using (var reader = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            reader.Open();
            using var read = reader.BeginTransaction(deferred: true);
            using (var command = reader.CreateCommand())
            {
                command.Transaction = read;
                command.CommandText = "SELECT COUNT(*) FROM items";
                command.ExecuteScalar();
            }

            for (var index = 0; index < 50; index++)
            {
                database.CreateNote($"Made up long note {index}", new string((char)('a' + (index % 26)), 400_000), Now);
            }

            Assert.True(new FileInfo(_path + "-wal").Length > WaypointDatabase.LogKeptBytes);
        }

        // Once the read ends, the next fold in lets the log start over, and the write after that cuts it back.
        Assert.Equal(0L, Scalar(_path, "PRAGMA wal_checkpoint(PASSIVE)"));
        database.CreateNote("Made up short note", "Made up body", Now);

        // Another test clearing the connection pools meanwhile can close the last connection, which folds the log in
        // and deletes it, and a log that's gone is within the cap too.
        var log = new FileInfo(_path + "-wal");
        Assert.True(!log.Exists || log.Length <= WaypointDatabase.LogKeptBytes);
        Assert.Equal(51, database.GetNotes().Count);
    }

    [Fact]
    public void A_backup_is_one_file_with_a_plain_journal()
    {
        var database = new WaypointDatabase(_path);
        database.CreateNote("Made up lecture", "Made up body", Now);

        var backup = database.BackUpNow(Now);

        Assert.Equal(1, JournalByte(backup));
        Assert.Null(database.RestoreProblem(backup));
        Assert.False(File.Exists(backup + "-wal"));
        Assert.False(File.Exists(backup + "-shm"));
        Assert.Equal(["waypoint-2030-04-02-093000.db"], Directory.GetFiles(database.BackupFolder).Select(Path.GetFileName));
    }

    [Fact]
    public void A_restore_leaves_nothing_from_before_it_in_the_log()
    {
        var database = new WaypointDatabase(_path);
        database.CreateNote("Made up kept note", "Made up body", Now);
        var backup = database.BackUpNow(Now);

        // Held open the whole time, like the app's own connections, so the log is never folded in by a close.
        using var held = database.Open();
        for (var index = 0; index < 20; index++)
        {
            database.CreateNote($"Made up later note {index}", new string('x', 4000), Now);
        }

        Assert.True(new FileInfo(_path + "-wal").Length > 0);

        Assert.True(database.RestoreBackup(backup, Now.AddMinutes(5)).Restored);

        // The log is emptied, and the file on its own is the restored copy.
        Assert.Equal(0, new FileInfo(_path + "-wal").Length);
        var alone = Path.Combine(_folder, "alone.db");
        File.Copy(_path, alone);
        Assert.Equal(["Made up kept note"], Titles(alone));
        Assert.Equal("ok", Scalar(alone, "PRAGMA integrity_check"));
        Assert.Equal(2, JournalByte(_path));
        Assert.Equal("Made up kept note", Assert.Single(database.GetNotes()).Title);
    }

    [Fact]
    public void A_backup_taken_before_the_switch_can_still_be_restored()
    {
        var database = new WaypointDatabase(_path);
        database.CreateNote("Made up old note", "Made up body", Now);
        var backup = database.BackUpNow(Now);
        database.CreateNote("Made up newer note", "Made up body", Now);

        Assert.True(database.RestoreBackup(backup, Now.AddMinutes(1)).Restored);

        Assert.Equal("Made up old note", Assert.Single(database.GetNotes()).Title);
        Assert.Equal("wal", Scalar(_path, "PRAGMA journal_mode"));
    }

    /// <summary>Byte 18 of the header: 1 for a plain journal, 2 for write-ahead.</summary>
    private static int JournalByte(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        file.Position = 18;
        return file.ReadByte();
    }

    private static object? Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static List<string> Titles(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT title FROM items WHERE kind = 'Note' ORDER BY id";
        using var reader = command.ExecuteReader();
        var titles = new List<string>();
        while (reader.Read())
        {
            titles.Add(reader.GetString(0));
        }

        return titles;
    }
}
