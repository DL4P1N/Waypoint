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
    public void Calls_run_as_one_write_all_land_once_it_returns()
    {
        var seen = _database.InOneWrite(() =>
        {
            _database.SetPreference("made_up_one", "1");
            _database.SetPreferences(("made_up_two", "2"), ("made_up_three", "3"));
            return _database.GetPreference("made_up_two");
        });

        Assert.Equal("2", seen);
        Assert.Equal("1", _database.GetPreference("made_up_one"));
        Assert.Equal("3", _database.GetPreference("made_up_three"));
    }

    [Fact]
    public void Calls_run_as_one_write_that_throws_keep_none_of_it()
    {
        Assert.Throws<InvalidOperationException>(() => _database.InOneWrite<int>(() =>
        {
            _database.SetPreference("made_up_one", "1");
            _database.SetPreferences(("made_up_two", "2"));
            throw new InvalidOperationException("made up stop");
        }));

        Assert.Null(_database.GetPreference("made_up_one"));
        Assert.Null(_database.GetPreference("made_up_two"));

        // The next write isn't caught up in the one that failed.
        _database.SetPreference("made_up_after", "yes");
        Assert.Equal("yes", _database.GetPreference("made_up_after"));
    }

    [Fact]
    public void A_call_that_rolls_its_own_part_back_still_does_and_the_rest_lands()
    {
        _database.InOneWrite(() =>
        {
            _database.SetPreference("made_up_kept", "yes");
            using (var connection = _database.Open())
            using (var transaction = connection.BeginTransaction())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO preferences (key, value) VALUES ('made_up_undone', 'yes')";
                command.ExecuteNonQuery();
            }

            return 0;
        });

        Assert.Equal("yes", _database.GetPreference("made_up_kept"));
        Assert.Null(_database.GetPreference("made_up_undone"));
    }

    [Fact]
    public void Nothing_is_seen_from_outside_until_the_one_write_lands()
    {
        string? outside = "unread";
        _database.InOneWrite(() =>
        {
            _database.SetPreference("made_up_one", "1");
            outside = Task.Run(() =>
            {
                using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM preferences WHERE key = 'made_up_one'";
                return command.ExecuteScalar() as string;
            }).Result;
            return 0;
        });

        Assert.Null(outside);
        Assert.Equal("1", _database.GetPreference("made_up_one"));
    }

    [Fact]
    public void A_database_call_handed_to_another_thread_inside_one_write_fails_loudly_instead_of_sharing_it()
    {
        Exception? handedOff = null;
        _database.InOneWrite(() =>
        {
            _database.SetPreference("made_up_one", "1");
            handedOff = Record.Exception(() => Task.Factory.StartNew(() => _database.GetPreference("made_up_one"), TaskCreationOptions.LongRunning).GetAwaiter().GetResult());
            return 0;
        });

        Assert.IsType<InvalidOperationException>(handedOff);
        Assert.Equal("1", _database.GetPreference("made_up_one"));
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
    public async Task Work_still_running_after_one_write_is_over_opens_a_connection_of_its_own()
    {
        var go = new TaskCompletionSource();
        Task<string?>? later = null;
        _database.InOneWrite(() =>
        {
            _database.SetPreference("made_up_one", "1");
            later = Task.Run(async () =>
            {
                await go.Task;
                return _database.GetPreference("made_up_one");
            });
            return 0;
        });

        go.SetResult();
        Assert.Equal("1", await later!);
    }
}
