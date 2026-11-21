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
    public void A_restore_never_gives_back_a_stamp_from_before_it()
    {
        _database.CreateNote("Made up note", "Made up body", Now);
        var backup = _database.BackUpNow(Now);
        var before = _database.NotesChangeStamp();

        Assert.True(_database.RestoreBackup(backup, Now.AddMinutes(1)).Restored);

        Assert.NotEqual(before, _database.NotesChangeStamp());
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
