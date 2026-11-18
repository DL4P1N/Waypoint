using Microsoft.Data.Sqlite;
using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Recording;

namespace Waypoint.Core.Tests;

/// <summary>
/// Every connection Waypoint opens checks its foreign keys, so a row can't point at one that isn't there and a row
/// still pointed at can't be deleted out from under it. None of the keys cascade.
/// </summary>
public sealed class ForeignKeyTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-{Guid.NewGuid():N}.db");
    private readonly WaypointDatabase _database;

    public ForeignKeyTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private const long Missing = 9999;
    private static DateTime Now => DateTime.Now;

    /// <summary>Child table, the table it points at, and an insert of one child row pointing at $parent.</summary>
    public static TheoryData<string, string, string> Relationships => new()
    {
        { "items", "captures", "INSERT INTO items (capture_id, kind, title) VALUES ($parent, 'Task', 'made up')" },
        { "reminders", "items", "INSERT INTO reminders (item_id, remind_at) VALUES ($parent, '2030-01-01T09:00:00')" },
        { "calendar_reminders", "calendar_events", "INSERT INTO calendar_reminders (event_id, remind_at) VALUES ($parent, '2030-01-01T09:00:00')" },
        { "list_entries", "lists", "INSERT INTO list_entries (list_id, text, added_at) VALUES ($parent, 'made up', '2030-01-01T09:00:00')" },
        { "note_folders", "note_folders", "INSERT INTO note_folders (name, parent_id, created_at) VALUES ('Inner', $parent, '2030-01-01T09:00:00')" },
        { "note_sources", "items", "INSERT INTO note_sources (note_id, kind, name, text, created_at) VALUES ($parent, 'Text', 'made up', 'made up', '2030-01-01T09:00:00')" },
        { "note_suggestions", "items", "INSERT INTO note_suggestions (note_id, kind, title, created_at) VALUES ($parent, 'Task', 'made up', '2030-01-01T09:00:00')" },
        { "transcription_jobs", "items", "INSERT INTO transcription_jobs (note_id, original_path, status, created_at, updated_at) VALUES ($parent, 'made-up.wav', 'Queued', '2030-01-01T09:00:00', '2030-01-01T09:00:00')" },
        { "recording_marks", "recordings", "INSERT INTO recording_marks (recording_id, offset_ms, created_at) VALUES ($parent, 0, '2030-01-01T09:00:00')" },
        { "waiting_on", "items", "INSERT INTO waiting_on (item_id, who, since, follow_up_on) VALUES ($parent, 'Someone', '2030-01-01', '2030-01-03')" },
        { "dose_taken", "items", "INSERT INTO dose_taken (item_id, due_on, taken_at) VALUES ($parent, '2030-01-01', '2030-01-01T08:12:00')" },
    };

    [Fact]
    public void Every_connection_checks_foreign_keys_even_after_one_turned_it_off()
    {
        using (var connection = _database.Open())
        {
            Run(connection, "PRAGMA foreign_keys = 0");
        }

        // The pool hands the same connection back, so it only checks again if Waypoint asks it to on every open.
        using var reopened = _database.Open();
        Assert.Equal(1L, Scalar(reopened, "PRAGMA foreign_keys"));
    }

    [Fact]
    public void The_declared_relationships_are_the_ones_these_tests_cover_and_none_cascade()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.name, f."table", f.on_delete FROM sqlite_master m, pragma_foreign_key_list(m.name) f
            WHERE m.type = 'table' ORDER BY m.name, f."table"
            """;
        var declared = new List<(string Child, string Parent, string OnDelete)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                declared.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        var covered = Relationships.Select(row => ((string)row[0], (string)row[1], "NO ACTION")).OrderBy(row => row.Item1).ThenBy(row => row.Item2);
        Assert.Equal(covered, declared);
    }

    [Theory]
    [MemberData(nameof(Relationships))]
    public void A_row_pointing_at_one_that_is_not_there_is_refused(string child, string parent, string insert)
    {
        using var connection = _database.Open();
        Assert.Equal(0L, Scalar(connection, $"SELECT COUNT(*) FROM {parent} WHERE rowid = $parent", Missing));
        var before = Scalar(connection, $"SELECT COUNT(*) FROM {child}");

        var refused = Assert.Throws<SqliteException>(() => Run(connection, insert, Missing));

        Assert.Contains("FOREIGN KEY", refused.Message);
        Assert.Equal(before, Scalar(connection, $"SELECT COUNT(*) FROM {child}"));
    }

    [Theory]
    [MemberData(nameof(Relationships))]
    public void A_row_still_pointed_at_cannot_be_deleted_until_what_points_at_it_goes(string child, string parent, string insert)
    {
        using var connection = _database.Open();
        var parentId = AddParent(connection, parent);
        Run(connection, insert, parentId);
        var childId = Scalar(connection, "SELECT last_insert_rowid()");

        Assert.Throws<SqliteException>(() => Run(connection, $"DELETE FROM {parent} WHERE rowid = $parent", parentId));
        Assert.Equal(1L, Scalar(connection, $"SELECT COUNT(*) FROM {parent} WHERE rowid = $parent", parentId));

        Run(connection, $"DELETE FROM {child} WHERE rowid = $parent", childId);
        Run(connection, $"DELETE FROM {parent} WHERE rowid = $parent", parentId);
        Assert.Equal(0L, Scalar(connection, $"SELECT COUNT(*) FROM {parent} WHERE rowid = $parent", parentId));
    }

    [Fact]
    public void An_item_for_a_capture_that_is_not_there_is_refused_and_nothing_is_kept()
    {
        Assert.Throws<SqliteException>(() => _database.CompleteCapture(Missing, [new SortedItem(ItemKind.Task, "made up chore", null, null)]));

        Assert.Empty(_database.GetOpenItems());
    }

    [Fact]
    public void An_event_with_nudges_that_leaves_the_feed_goes_with_its_nudges()
    {
        var due = DateOnly.FromDateTime(Now).AddDays(3);
        _database.ReplaceCalendarEvents([new CalendarEvent("made-up-uid", "Made up quiz", null, due, null)]);
        var quiz = _database.GetOpenItems().Single(item => item.Source == ItemSource.Calendar);
        _database.SaveNudges([new EventNudges(quiz.Id, [new PlannedReminder(Now.AddDays(1), "start")])]);

        _database.ReplaceCalendarEvents([]);

        Assert.DoesNotContain(_database.GetOpenItems(), item => item.Source == ItemSource.Calendar);
        Assert.Empty(_database.GetDueReminders(Now.AddDays(2)));
        AssertNoOrphans();
    }

    [Fact]
    public void Deleting_a_folder_moves_what_is_inside_it_up_first()
    {
        var outer = _database.CreateNoteFolder("Outer", null, Now);
        var inner = _database.CreateNoteFolder("Inner", outer.Id, Now);
        var note = _database.CreateNote("Made up note", "made up body", Now, outer.Id);

        Assert.Equal(FolderDeleteResult.Deleted, _database.DeleteNoteFolder(outer.Id, NotesInFolder.MoveToParent));

        Assert.Equal(inner.Id, Assert.Single(_database.GetNoteFolders()).Id);
        Assert.Null(_database.GetNote(note)!.FolderId);
        AssertNoOrphans();
    }

    [Fact]
    public void Deleting_items_notes_and_lists_keeps_what_points_at_them_valid()
    {
        var captureId = _database.AddCapture("made up chore");
        _database.CompleteCapture(captureId, [new SortedItem(ItemKind.Task, "made up chore", null, null,
            Reminders: [new PlannedReminder(Now.AddHours(1), null)],
            Steps: [new PlannedStep("first made up step", Now.AddMinutes(30))])]);
        var chore = _database.GetOpenItems().Single(item => item.Title == "made up chore");
        _database.MarkWaiting("Someone", null, ItemRef.Format(chore), null, null, null, Now);
        var note = _database.CreateNote("Made up note", "made up body", Now);
        _database.AddNoteSource(note, "Text", "made up source", "made up text", Now);
        var recording = _database.AddRecording(AudioInput.Microphone, false, "Made up recording", Now);
        _database.AddRecordingMark(recording, TimeSpan.FromSeconds(5), "made up mark", Now);
        _database.AddListEntry("Made up list", "made up thing", Now);

        Assert.NotNull(_database.DeleteItem(chore, Now));
        _database.DeleteNote(note, Now);
        Assert.StartsWith("Removed", _database.DeleteList("Made up list", Now));
        _database.StopWaiting(chore.Id);

        Assert.DoesNotContain(_database.GetOpenItems(), item => item.Title == "made up chore");
        AssertNoOrphans();
    }

    [Fact]
    public void A_database_with_rows_already_pointing_at_nothing_still_opens_and_works()
    {
        SqliteConnection.ClearAllPools();
        using (var unchecked_ = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, ForeignKeys = false, Pooling = false }.ToString()))
        {
            unchecked_.Open();
            Run(unchecked_, "INSERT INTO reminders (item_id, remind_at) VALUES ($parent, '2000-01-01T09:00:00')", Missing);
            Run(unchecked_, "INSERT INTO list_entries (list_id, text, added_at) VALUES ($parent, 'made up', '2000-01-01T09:00:00')", Missing);
            Run(unchecked_, "INSERT INTO note_folders (name, parent_id, created_at) VALUES ('Stray', $parent, '2000-01-01T09:00:00')", Missing);
        }

        var reopened = new WaypointDatabase(_path);

        // What points at nothing is left where it is, out of sight, and the rest carries on.
        Assert.Empty(reopened.GetDueReminders(Now));
        Assert.DoesNotContain(reopened.GetListOverview(), list => list.Entries.Any(entry => entry.Text == "made up"));
        var stray = Assert.Single(reopened.GetNoteFolders());
        Assert.Equal("Stray", stray.Name);
        Assert.Null(reopened.RenameNoteFolder(stray.Id, "Renamed").Problem);
        Assert.True(reopened.MoveNoteFolder(stray.Id, null, 0));

        reopened.CompleteCapture(reopened.AddCapture("made up chore"), [new SortedItem(ItemKind.Task, "made up chore", null, null)]);
        reopened.AddListEntry("Made up list", "made up thing", Now);
        Assert.Contains(reopened.GetOpenItems(), item => item.Title == "made up chore");

        using var connection = reopened.Open();
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
    }

    private void AssertNoOrphans()
    {
        using var connection = _database.Open();
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
    }

    private static long AddParent(SqliteConnection connection, string table)
    {
        var capture = Insert(connection, "INSERT INTO captures (text, created_at) VALUES ('made up', '2030-01-01T09:00:00') RETURNING id");
        return table switch
        {
            "captures" => capture,
            "items" => Insert(connection, $"INSERT INTO items (capture_id, kind, title) VALUES ({capture}, 'Note', 'made up') RETURNING id"),
            "calendar_events" => Insert(connection, "INSERT INTO calendar_events (uid, title, due_date) VALUES ('made-up-uid', 'made up', '2030-01-01') RETURNING id"),
            "lists" => Insert(connection, "INSERT INTO lists (name, created_at) VALUES ('Made up list', '2030-01-01T09:00:00') RETURNING id"),
            "note_folders" => Insert(connection, "INSERT INTO note_folders (name, created_at) VALUES ('Outer', '2030-01-01T09:00:00') RETURNING id"),
            "recordings" => Insert(connection, "INSERT INTO recordings (path, input, title, status, started_at) VALUES ('made-up.wav', 'Microphone', 'made up', 'Done', '2030-01-01T09:00:00') RETURNING id"),
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, null),
        };
    }

    private static long Insert(SqliteConnection connection, string sql) => Scalar(connection, sql);

    private static void Run(SqliteConnection connection, string sql, long parent = 0)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$parent", parent);
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql, long parent = 0)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (sql.Contains("$parent"))
        {
            command.Parameters.AddWithValue("$parent", parent);
        }

        return (long)command.ExecuteScalar()!;
    }
}
