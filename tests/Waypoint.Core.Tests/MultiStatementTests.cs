using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>
/// A write made of several statements reports a failure in any of them, not only the first. Each test makes a later
/// statement fail with a trigger and checks that the call throws and nothing from the earlier statements was kept.
/// </summary>
public sealed class MultiStatementTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-multi-{Guid.NewGuid():N}.db");
    private readonly WaypointDatabase _database;

    public MultiStatementTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void Taking_a_hold_whose_clearing_of_older_holds_fails_keeps_every_hold()
    {
        var older = AddTask("Water the made up plants");
        var newer = AddTask("Return the made up book");
        Run($"""
            INSERT INTO after_this (item_id, source, held_at) VALUES ({older.Id}, 'Capture', '{DateTime.Now.AddHours(-2):yyyy-MM-ddTHH:mm:ss}');
            INSERT INTO after_this (item_id, source, held_at) VALUES ({newer.Id}, 'Capture', '{DateTime.Now.AddHours(-1):yyyy-MM-ddTHH:mm:ss}');
            """);
        FailWhen($"BEFORE DELETE ON after_this WHEN OLD.item_id = {older.Id}");

        Assert.ThrowsAny<SqliteException>(() => _database.TakeAfterThis(DateTime.Now));

        Assert.Equal(2, Count("after_this"));
    }

    [Fact]
    public void A_reply_whose_trimming_of_old_talk_fails_isnt_kept()
    {
        // As many exchanges as are kept, so saving one more trims the oldest, which the trigger refuses.
        Run("""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 200)
            INSERT INTO conversation (origin, at, note, reply, refs) SELECT 'Discord', '2030-01-01T09:00:00', 'made up note', 'made up reply', '[]' FROM n
            """);
        FailWhen("BEFORE DELETE ON conversation");

        Assert.ThrowsAny<SqliteException>(() => _database.AddExchange(CaptureOrigin.Discord, "made up last note", "made up reply", [], DateTime.Now));

        Assert.Equal(200, Count("conversation"));
    }

    [Fact]
    public void Feedback_whose_saving_fails_throws_instead_of_returning_an_id()
    {
        FailWhen("BEFORE INSERT ON feedback");

        Assert.ThrowsAny<SqliteException>(() => _database.AddFeedback("made-up-feature", null, null, DateTime.Now));

        Assert.Equal(0, Count("feedback"));
    }

    private StoredItem AddTask(string title)
    {
        _database.CompleteCapture(_database.AddCapture(title), [new SortedItem(ItemKind.Task, title, null, null)]);
        return _database.GetOpenItems().Single(item => item.Title == title);
    }

    private void Run(string sql)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Makes any write the trigger names fail, the way a constraint on that statement would.</summary>
    private void FailWhen(string when)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TRIGGER fail_partway {when} BEGIN SELECT RAISE(ABORT, 'made to fail partway'); END";
        command.ExecuteNonQuery();
    }

    private long Count(string table)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)command.ExecuteScalar()!;
    }
}
