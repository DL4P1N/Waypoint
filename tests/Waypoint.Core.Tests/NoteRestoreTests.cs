using Waypoint.Core.Data;
using Waypoint.Core.Notes;

namespace Waypoint.Core.Tests;

/// <summary>After a restore the restored notes win over the notes folder, and nothing in the folder is lost.</summary>
public sealed class NoteRestoreTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 4, 9, 0, 0);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-restore-{Guid.NewGuid():N}");
    private readonly string _root;
    private readonly WaypointDatabase _database;
    private readonly NoteFiles _files;

    public NoteRestoreTests()
    {
        Directory.CreateDirectory(_folder);
        _root = Path.Combine(_folder, "Notes");
        _database = new WaypointDatabase(Path.Combine(_folder, "waypoint.db"));
        _files = new NoteFiles(_database, _root);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private string File_(string relative) => Path.Combine(_root, relative);

    private void WriteOutside(string relative, string text)
    {
        var full = File_(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddMinutes(1));
    }

    /// <summary>Every file put aside by the restore, by its path under the folder it was put in.</summary>
    private Dictionary<string, string> PutAside()
    {
        var aside = Directory.GetDirectories(_root, ".*").Single();
        return Directory.GetFiles(aside, "*.md", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(Directory.GetDirectories(aside).Single(), path), File.ReadAllText);
    }

    [Fact]
    public void After_a_restore_the_folder_is_rewritten_from_the_restored_notes_and_what_was_there_is_kept_aside()
    {
        var lecture = _database.CreateNote("Lecture 1", "Graphs", Now);
        var groceries = _database.CreateNote("Groceries", "milk", Now);
        _database.AddTranscriptSource(groceries, "Transcript of a.m4a", "milk please", null, Now);
        _files.Sync(Now);
        var backup = _database.BackUpNow(Now);

        // After the backup: the lecture is edited in another app, the groceries file is deleted, and a note is added.
        WriteOutside("Lecture 1.md", "Graphs and trees");
        File.Delete(File_("Groceries.md"));
        _database.CreateNote("Later", "made after the backup", Now);
        _files.Sync(Now);
        _files.Sync(Now.Add(NoteFiles.MissingGrace));
        Assert.Null(_database.GetNote(groceries));
        Assert.Equal("Graphs and trees", _database.GetNote(lecture)!.Body);

        Assert.True(_database.RestoreBackup(backup, Now.AddHours(1)).Restored);
        _files.Sync(Now.AddHours(1));

        Assert.Equal("Graphs", _database.GetNote(lecture)!.Body);
        Assert.Equal("milk", _database.GetNote(groceries)!.Body);
        Assert.Single(_database.GetNoteSources(groceries));
        Assert.Equal(2, _database.GetNotes().Count);
        Assert.Equal("Graphs", File.ReadAllText(File_("Lecture 1.md")));
        Assert.Equal("milk", File.ReadAllText(File_("Groceries.md")));
        Assert.Equal(2, Directory.GetFiles(_root, "*.md").Length);

        var aside = PutAside();
        Assert.Equal("Graphs and trees", aside["Lecture 1.md"]);
        Assert.Equal("made after the backup", aside["Later.md"]);

        // Settled: later passes, the usual way, change nothing.
        var again = _files.Sync(Now.AddHours(2));
        var later = _files.Sync(Now.AddHours(3));
        Assert.False(again.NotesChanged || later.NotesChanged);
        Assert.Equal(2, _database.GetNotes().Count);
        Assert.Equal("Graphs", File.ReadAllText(File_("Lecture 1.md")));
    }

    [Fact]
    public void A_sync_held_while_restoring_runs_after_and_does_not_undo_it()
    {
        var id = _database.CreateNote("Groceries", "milk", Now);
        _files.Sync(Now);
        var backup = _database.BackUpNow(Now);
        _database.SaveNote(id, "Groceries", "milk and bread", Now);
        _files.Sync(Now);

        var during = Task.CompletedTask;
        _files.WhileHeld(() =>
        {
            during = Task.Run(() => _files.Sync(Now));
            Assert.False(during.Wait(200));
            Assert.True(_database.RestoreBackup(backup, Now.AddHours(1)).Restored);
        });
        during.Wait();

        Assert.Equal("milk", _database.GetNote(id)!.Body);
        Assert.Equal("milk", File.ReadAllText(File_("Groceries.md")));
    }
}
