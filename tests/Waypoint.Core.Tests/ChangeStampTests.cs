using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>The notes' change stamp moves with anything a note's file or forum post shows, and stays put otherwise.</summary>
public sealed class ChangeStampTests : IDisposable
{
    private static readonly DateTime Now = new(2030, 8, 1, 9, 0, 0);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-stamp-{Guid.NewGuid():N}");
    private readonly WaypointDatabase _database;

    public ChangeStampTests()
    {
        Directory.CreateDirectory(_folder);
        _database = new WaypointDatabase(Path.Combine(_folder, "waypoint.db"));
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
    public void The_stamp_moves_with_each_note_change_and_stays_put_otherwise()
    {
        var stamp = _database.NotesChangeStamp();
        Assert.NotNull(stamp);
        Assert.Equal(stamp, _database.NotesChangeStamp());

        // Things no note file or post shows leave it alone.
        _database.CompleteCapture(_database.AddCapture("made up errand"), [new SortedItem(ItemKind.Task, "Made up errand", null, null)]);
        _database.SetPreference("made_up_setting", "1");
        Assert.Equal(stamp, _database.NotesChangeStamp());

        var id = Changes(() => _database.CreateNote("Made up note", "Made up body", Now));
        Changes(() => _database.SaveNote(id, "Made up note", "Made up body, edited", Now));
        var folder = Changes(() => _database.CreateNoteFolder("Made up folder", null, Now).Id);
        Changes(() => _database.MoveNoteToFolder(id, folder));
        Changes(() => _database.AddNoteSource(id, "pdf", "made-up.pdf", "Made up slide", Now));
        Changes(() => _database.SetNoteFilesRoot(Path.Combine(_folder, "notes")));
        Changes(() => _database.DeleteNote(id, Now));
    }

    [Fact]
    public void A_notes_folder_kept_for_one_pc_in_server_mode_moves_the_stamp()
    {
        Changes(() => _database.SetPreference("note_files_synced_root:made-up-pc", Path.Combine(_folder, "pc notes")));
        Changes(() => _database.SetPreference("note_files_synced_root:made-up-pc", Path.Combine(_folder, "other notes")));

        var stamp = _database.NotesChangeStamp();
        _database.SetPreference("note_files_synced_root_made_up", "1");
        Assert.Equal(stamp, _database.NotesChangeStamp());
    }

    [Fact]
    public void A_restore_never_gives_back_a_stamp_from_before_it()
    {
        _database.CreateNote("Made up note", "Made up body", Now);
        var backup = _database.BackUpNow(Now);
        var before = _database.NotesChangeStamp();

        Assert.True(_database.RestoreBackup(backup, Now.AddMinutes(1)).Restored);

        Assert.NotEqual(before, _database.NotesChangeStamp());
    }

    [Fact]
    public void An_older_database_gets_the_current_stamp_triggers_and_loses_the_old_ones()
    {
        var path = Path.Combine(_folder, "waypoint.db");
        var current = StampTriggers(path);
        Assert.NotEmpty(current);
        Assert.All(current, name => Assert.DoesNotContain("stamp_notes_", name));

        // An older Waypoint's triggers: unversioned names, and one watching notes with a body that no longer moves the count.
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = string.Concat(current.Select(name => $"DROP TRIGGER \"{name}\";")) + """
                CREATE TRIGGER stamp_notes_items_insert AFTER INSERT ON items BEGIN SELECT 1; END;
                CREATE TRIGGER stamp_0badc0de_notes_captures_update AFTER UPDATE OF text ON captures BEGIN SELECT 1; END;
                CREATE TRIGGER made_up_other_trigger AFTER INSERT ON items BEGIN SELECT 1; END;
                """;
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var reopened = new WaypointDatabase(path);
        Assert.Equal(current, StampTriggers(path));
        Assert.Contains("made_up_other_trigger", AllTriggers(path));

        var stamp = reopened.NotesChangeStamp();
        reopened.CreateNote("Made up note", "Made up body", Now);
        Assert.NotEqual(stamp, reopened.NotesChangeStamp());

        // Once they're current, opening again writes nothing to the schema.
        var version = SchemaVersion(path);
        SqliteConnection.ClearAllPools();
        _ = new WaypointDatabase(path);
        Assert.Equal(version, SchemaVersion(path));
    }

    private static List<string> StampTriggers(string path) => AllTriggers(path).Where(name => name.StartsWith("stamp_", StringComparison.Ordinal)).ToList();

    private static List<string> AllTriggers(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'trigger' ORDER BY name";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static long SchemaVersion(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA schema_version";
        return (long)command.ExecuteScalar()!;
    }

    private T Changes<T>(Func<T> change)
    {
        var before = _database.NotesChangeStamp();
        var result = change();
        Assert.NotEqual(before, _database.NotesChangeStamp());
        return result;
    }

    private void Changes(Action change) => Changes(() =>
    {
        change();
        return 0;
    });
}
