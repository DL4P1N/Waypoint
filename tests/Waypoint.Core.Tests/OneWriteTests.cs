using Microsoft.Data.Sqlite;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>Many calls run as one write land together or not at all, and a call inside that undoes its own part still does. Made-up data.</summary>
public sealed class OneWriteTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-onewrite-{Guid.NewGuid():N}.db");
    private readonly WaypointDatabase _database;

    public OneWriteTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void Calls_on_a_writes_database_all_land_once_it_returns()
    {
        var seen = _database.InOneWrite(database =>
        {
            database.SetPreference("made_up_one", "1");
            database.SetPreferences(("made_up_two", "2"), ("made_up_three", "3"));
            return database.GetPreference("made_up_two");
        });

        Assert.Equal("2", seen);
        Assert.Equal("1", _database.GetPreference("made_up_one"));
        Assert.Equal("3", _database.GetPreference("made_up_three"));
    }

    [Fact]
    public void Calls_on_a_writes_database_that_throws_keep_none_of_it()
    {
        Assert.Throws<InvalidOperationException>(() => _database.InOneWrite(database =>
        {
            database.SetPreference("made_up_one", "1");
            database.SetPreferences(("made_up_two", "2"));
            throw new InvalidOperationException("made up stop");
        }));

        Assert.Null(_database.GetPreference("made_up_one"));
        Assert.Null(_database.GetPreference("made_up_two"));

        // The next write isn't caught up in the one that failed.
        _database.SetPreference("made_up_after", "yes");
        Assert.Equal("yes", _database.GetPreference("made_up_after"));
    }

    [Fact]
    public void A_call_in_a_write_that_lets_its_own_part_go_undoes_only_that()
    {
        _database.InOneWrite(database =>
        {
            database.SetPreference("made_up_kept", "yes");
            using (database.Connect(out var connection))
            using (database.Begin(connection))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO preferences (key, value) VALUES ('made_up_undone', 'yes')";
                command.ExecuteNonQuery();
            }

            database.SetPreference("made_up_after", "yes");
        });

        Assert.Equal("yes", _database.GetPreference("made_up_kept"));
        Assert.Null(_database.GetPreference("made_up_undone"));
        Assert.Equal("yes", _database.GetPreference("made_up_after"));
    }

    [Fact]
    public void Nothing_from_a_writes_database_is_seen_from_outside_until_it_lands()
    {
        string? outside = "unread";
        string? original = "unread";
        _database.InOneWrite(database =>
        {
            database.SetPreference("made_up_one", "1");
            outside = Task.Run(() =>
            {
                using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM preferences WHERE key = 'made_up_one'";
                return command.ExecuteScalar() as string;
            }).Result;

            // The database it was called on isn't the write's, so it reads what's landed, on a connection of its own.
            original = Task.Factory.StartNew(() => _database.GetPreference("made_up_one"), TaskCreationOptions.LongRunning).GetAwaiter().GetResult();
        });

        Assert.Null(outside);
        Assert.Null(original);
        Assert.Equal("1", _database.GetPreference("made_up_one"));
    }

    [Fact]
    public void A_write_inside_a_write_that_fails_and_is_caught_keeps_none_of_its_own_part()
    {
        _database.InOneWrite(database =>
        {
            database.SetPreference("made_up_outer_before", "yes");
            Assert.Throws<InvalidOperationException>(() => database.InOneWrite(inner =>
            {
                inner.SetPreference("made_up_inner", "yes");
                inner.SetPreferences(("made_up_inner_two", "yes"));
                throw new InvalidOperationException("made up stop");
            }));
            database.SetPreference("made_up_outer_after", "yes");
        });

        Assert.Equal("yes", _database.GetPreference("made_up_outer_before"));
        Assert.Equal("yes", _database.GetPreference("made_up_outer_after"));
        Assert.Null(_database.GetPreference("made_up_inner"));
        Assert.Null(_database.GetPreference("made_up_inner_two"));
    }

    [Fact]
    public void A_write_inside_a_write_undoes_only_its_own_savepoint_and_lands_with_the_outer_one()
    {
        _database.InOneWrite(database =>
        {
            database.SetPreference("made_up_outer", "yes");
            database.InOneWrite(kept => kept.SetPreference("made_up_kept_inner", "yes"));
            try
            {
                database.InOneWrite(inner =>
                {
                    inner.SetPreference("made_up_dropped", "yes");

                    // A write two deep that lands inside the one that fails goes with it.
                    inner.InOneWrite(deeper => deeper.SetPreference("made_up_dropped_deeper", "yes"));
                    throw new InvalidOperationException("made up stop");
                });
            }
            catch (InvalidOperationException)
            {
            }

            Assert.Equal("yes", database.GetPreference("made_up_kept_inner"));
            Assert.Null(database.GetPreference("made_up_dropped"));
            Assert.Null(database.GetPreference("made_up_dropped_deeper"));
        });

        Assert.Equal("yes", _database.GetPreference("made_up_outer"));
        Assert.Equal("yes", _database.GetPreference("made_up_kept_inner"));
        Assert.Null(_database.GetPreference("made_up_dropped"));
        Assert.Null(_database.GetPreference("made_up_dropped_deeper"));
    }

    [Fact]
    public void A_write_inside_a_write_that_fails_undoes_the_whole_write_when_not_caught()
    {
        Assert.Throws<InvalidOperationException>(() => _database.InOneWrite(database =>
        {
            database.SetPreference("made_up_outer", "yes");
            database.InOneWrite(inner =>
            {
                inner.SetPreference("made_up_inner", "yes");
                throw new InvalidOperationException("made up stop");
            });
        }));

        Assert.Null(_database.GetPreference("made_up_outer"));
        Assert.Null(_database.GetPreference("made_up_inner"));
    }

    [Fact]
    public void A_writes_database_handed_to_another_thread_fails_loudly_instead_of_sharing_its_connection()
    {
        Exception? handedOff = null;
        _database.InOneWrite(database =>
        {
            database.SetPreference("made_up_one", "1");
            handedOff = Record.Exception(() => Task.Factory.StartNew(() => database.GetPreference("made_up_one"), TaskCreationOptions.LongRunning).GetAwaiter().GetResult());
        });

        Assert.IsType<InvalidOperationException>(handedOff);
        Assert.Equal("1", _database.GetPreference("made_up_one"));
    }

    [Fact]
    public void A_writes_database_kept_after_the_write_is_over_fails_loudly()
    {
        WaypointDatabase? kept = null;
        _database.InOneWrite(database =>
        {
            database.SetPreference("made_up_one", "1");
            kept = database;
        });

        Assert.Throws<InvalidOperationException>(() => kept!.GetPreference("made_up_one"));
        Assert.Throws<InvalidOperationException>(() => kept!.InOneWrite(database => database.SetPreference("made_up_two", "2")));
        Assert.Null(_database.GetPreference("made_up_two"));
    }

    [Fact]
    public async Task Work_started_in_a_write_on_the_database_itself_opens_a_connection_of_its_own()
    {
        var go = new TaskCompletionSource();
        Task<string?>? later = null;
        _database.InOneWrite(database =>
        {
            database.SetPreference("made_up_one", "1");
            later = Task.Run(async () =>
            {
                await go.Task;
                return _database.GetPreference("made_up_one");
            });
        });

        go.SetResult();
        Assert.Equal("1", await later!);
    }

    [Fact]
    public void A_pcs_note_file_records_are_read_inside_a_write_and_taken_over_with_it()
    {
        var record = new NoteFileRecord(1, "made-up.md", "made-up-hash", 10, 20, "Made up note", null, "made-up-body");
        _database.SetNoteFileRecord(record);

        var stopped = Record.Exception(() => _database.InOneWrite(database =>
        {
            Assert.Equal([record], database.GetNoteFileRecords("made-up-desk"));
            throw new MadeUpStop();
        }));

        // Taking the old records over went with the write that failed, so they still wait for whichever PC asks first.
        Assert.IsType<MadeUpStop>(stopped);
        Assert.Equal([record], _database.GetNoteFileRecords());

        Assert.Equal([record], _database.InOneWrite(database => database.GetNoteFileRecords("made-up-desk")));
        Assert.Empty(_database.GetNoteFileRecords());
        Assert.Equal([record], _database.GetNoteFileRecords("made-up-desk"));
    }

    [Fact]
    public void A_transaction_begun_on_the_database_itself_inside_a_write_on_the_same_thread_fails_straight_away()
    {
        _database.SetPreference("made_up_before", "yes");
        Exception? begun = null;
        Exception? nested = null;
        string? read = null;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        _database.InOneWrite(database =>
        {
            database.SetPreference("made_up_inside", "yes");
            begun = Record.Exception(() => _database.SetPreferences(("made_up_refused", "yes")));
            nested = Record.Exception(() => _database.InOneWrite(other => other.SetPreference("made_up_refused_too", "yes")));

            // A read on the database itself still works, on a connection of its own, as the bedtime shift lookup does.
            read = _database.GetPreference("made_up_before");
        });
        waited.Stop();

        Assert.IsType<InvalidOperationException>(begun);
        Assert.IsType<InvalidOperationException>(nested);
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), $"Took {waited.Elapsed} to refuse.");
        Assert.Equal("yes", read);
        Assert.Equal("yes", _database.GetPreference("made_up_inside"));
        Assert.Null(_database.GetPreference("made_up_refused"));
        Assert.Null(_database.GetPreference("made_up_refused_too"));

        // Once the write is over, the same thread begins transactions on the database itself again.
        _database.SetPreferences(("made_up_after", "yes"));
        _database.InOneWrite(database => database.SetPreference("made_up_after_too", "yes"));
        Assert.Equal("yes", _database.GetPreference("made_up_after"));
        Assert.Equal("yes", _database.GetPreference("made_up_after_too"));
    }

    [Fact]
    public void A_write_open_on_one_database_doesnt_refuse_another_on_the_same_thread()
    {
        var otherPath = Path.Combine(Path.GetTempPath(), $"waypoint-onewrite-other-{Guid.NewGuid():N}.db");
        try
        {
            var other = new WaypointDatabase(otherPath);
            _database.InOneWrite(database =>
            {
                database.SetPreference("made_up_one", "1");
                other.SetPreferences(("made_up_other", "yes"));
                other.InOneWrite(write => write.SetPreference("made_up_other_too", "yes"));
            });

            Assert.Equal("yes", other.GetPreference("made_up_other"));
            Assert.Equal("yes", other.GetPreference("made_up_other_too"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { otherPath, otherPath + "-wal", otherPath + "-shm" })
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public void A_write_that_sqlite_rolls_back_whole_keeps_the_error_that_did_it()
    {
        // A trigger's RAISE(ROLLBACK) undoes the whole transaction, as a full disk would, and takes every savepoint with it.
        using (var connection = _database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER made_up_rollback BEFORE INSERT ON preferences WHEN NEW.key = 'made_up_boom' BEGIN SELECT RAISE(ROLLBACK, 'made up rollback'); END";
            command.ExecuteNonQuery();
        }

        var thrown = Record.Exception(() => _database.InOneWrite(database =>
        {
            database.SetPreference("made_up_before", "yes");
            database.SetPreferences(("made_up_boom", "yes"));
        }));

        Assert.Contains("made up rollback", Assert.IsType<SqliteException>(thrown).Message);
        Assert.Null(_database.GetPreference("made_up_before"));
        _database.SetPreference("made_up_after", "yes");
        Assert.Equal("yes", _database.GetPreference("made_up_after"));
    }

    private sealed class MadeUpStop : Exception;
}
