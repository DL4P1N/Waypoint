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

    [Fact]
    public void A_list_check_whose_history_fails_stays_unchecked()
    {
        _database.AddToList("Made up packing", ["Spare socks"], 0, DateTime.Now);
        var entry = _database.GetListEntries("Made up packing").Single();
        FailWhen("BEFORE INSERT ON item_history");

        Assert.ThrowsAny<SqliteException>(() => _database.SetEntryDone(entry, true, DateTime.Now));

        Assert.False(_database.GetListEntries("Made up packing").Single().Done);
    }

    [Fact]
    public void A_shopping_entry_whose_item_fails_leaves_no_capture()
    {
        FailWhen("BEFORE INSERT ON items WHEN NEW.kind = 'Shopping'");

        Assert.ThrowsAny<SqliteException>(() => _database.AddListEntry("Shopping", "Made up oats", DateTime.Now));

        Assert.Equal(0, Count("captures"));
    }

    [Fact]
    public void A_note_sent_once_whose_mark_fails_isnt_made()
    {
        FailWhen("BEFORE UPDATE OF client_id ON items WHEN NEW.client_id IS NOT NULL");

        Assert.ThrowsAny<SqliteException>(() => _database.CreateNoteOnce("made-up-client", "Made up title", "Made up body", DateTime.Now, null));

        Assert.Equal(0, Count("items"));
        Assert.Equal(0, Count("captures"));
    }

    [Fact]
    public void A_transcript_sent_once_whose_mark_fails_isnt_kept()
    {
        var note = _database.CreateNote("Made up lecture", "", DateTime.Now);
        FailWhen("BEFORE UPDATE OF client_id ON note_sources WHEN NEW.client_id IS NOT NULL");

        Assert.ThrowsAny<SqliteException>(() => _database.AddTranscriptSource(note, "Made up recording", "Made up words", "[]", DateTime.Now, null, "made-up-client"));

        Assert.Equal(0, Count("note_sources"));
    }

    [Fact]
    public void A_wait_whose_record_fails_leaves_no_task_behind()
    {
        FailWhen("BEFORE INSERT ON waiting_on");

        Assert.ThrowsAny<SqliteException>(() => _database.MarkWaiting("A Made Up Office", "Hear back about the form", null, null, null, null, DateTime.Now));

        Assert.Equal(0, Count("items"));
        Assert.Equal(0, Count("captures"));
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
