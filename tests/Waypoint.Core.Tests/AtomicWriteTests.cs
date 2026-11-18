using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Email;

namespace Waypoint.Core.Tests;

/// <summary>
/// Writes made of several steps land whole or not at all. Each test makes a later step fail with a trigger, the way a
/// crash or a broken row would stop it partway, and checks that nothing from the earlier steps was kept.
/// </summary>
public sealed class AtomicWriteTests : IDisposable
{
    private static readonly DateOnly Day = new(2030, 3, 12);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-atomic-{Guid.NewGuid():N}.db");
    private readonly WaypointDatabase _database;

    public AtomicWriteTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void Checking_off_an_item_whose_history_fails_leaves_it_open()
    {
        var item = AddTask("Return the made up form");
        FailWhen("BEFORE INSERT ON item_history");

        Assert.ThrowsAny<SqliteException>(() => _database.SetDone(item, true));

        Assert.Equal(0, Scalar($"SELECT done FROM items WHERE id = {item.Id}"));
    }

    [Fact]
    public void An_undo_whose_history_fails_leaves_the_item_finished()
    {
        var item = AddTask("Water the made up plant");
        _database.SetDone(item, true);
        FailWhen("BEFORE INSERT ON item_history");

        Assert.ThrowsAny<SqliteException>(() => _database.SetDone(item, false));

        Assert.Equal(1, Scalar($"SELECT done FROM items WHERE id = {item.Id}"));
    }

    [Fact]
    public void A_reminder_card_whose_action_fails_can_still_be_pressed()
    {
        var item = AddTask("Send the made up email");
        _database.TrackSentReminder("made-up-card", item.Id);
        FailWhen("BEFORE INSERT ON item_history");

        Assert.ThrowsAny<SqliteException>(() => _database.ApplyAlertAction(new AlertAction("made-up-card", AlertActionKind.Done), DateTime.Now, 10));

        Assert.Equal(0, Scalar("SELECT handled FROM sent_reminders WHERE message_id = 'made-up-card'"));
        Assert.Equal(0, Scalar($"SELECT done FROM items WHERE id = {item.Id}"));
    }

    private StoredItem AddTask(string title)
    {
        _database.CompleteCapture(_database.AddCapture(title), [new SortedItem(ItemKind.Task, title, null, null)]);
        return _database.GetOpenItems().Single(item => item.Title == title);
    }

    private long Scalar(string sql)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private void Run(string sql)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Makes any write the trigger names fail, the way a constraint on that step would.</summary>
    private void FailWhen(string when)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TRIGGER fail_partway {when} BEGIN SELECT RAISE(ABORT, 'made to fail partway'); END";
        command.ExecuteNonQuery();
    }

    private long Count(string table, string where = "1 = 1")
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {where}";
        return (long)command.ExecuteScalar()!;
    }
}
