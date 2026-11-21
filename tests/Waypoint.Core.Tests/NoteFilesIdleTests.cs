using System.Reflection;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;

namespace Waypoint.Core.Tests;

/// <summary>
/// A notes folder sync with nothing changed since the last one reads no notes and no files. Any change on either side,
/// or anything left to try again, still gets a full pass.
/// </summary>
public sealed class NoteFilesIdleTests : IDisposable
{
    private static readonly DateTime Now = new(2030, 7, 8, 9, 0, 0);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-idle-{Guid.NewGuid():N}.db");
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"waypoint-idle-notes-{Guid.NewGuid():N}");
    private readonly WaypointDatabase _database;
    private readonly NoteFiles _files;
    private readonly Counting _store;

    public NoteFilesIdleTests()
    {
        _database = new WaypointDatabase(_path);
        var store = DispatchProxy.Create<INoteFileStore, Counting>();
        _store = (Counting)(object)store;
        _store.Inner = _database;
        _files = new NoteFiles(store, _root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Counts each time the sync reads every note.</summary>
    public class Counting : DispatchProxy
    {
        public WaypointDatabase Inner { get; set; } = null!;

        public int Reads { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(INoteFileStore.GetNoteFileStates))
            {
                Reads++;
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is { } inner)
            {
                throw inner;
            }
        }
    }

    private string Full(string relative) => Path.Combine(_root, relative);

    /// <summary>Syncs until a pass changes nothing, so the next one with nothing new can be skipped.</summary>
    private void Settle()
    {
        _files.Sync(Now);
        _files.Sync(Now);
    }

    [Fact]
    public void A_sync_with_nothing_changed_reads_no_notes()
    {
        _database.CreateNote("Made up lecture", "Made up body", Now);
        Settle();
        var reads = _store.Reads;

        for (var round = 0; round < 3; round++)
        {
            Assert.Equal(new NoteSyncResult(0, 0, 0, false), _files.Sync(Now));
        }

        Assert.Equal(reads, _store.Reads);
    }

    [Fact]
    public void An_edit_in_Waypoint_after_an_idle_sync_still_reaches_its_file()
    {
        var id = _database.CreateNote("Made up lecture", "Made up body", Now);
        Settle();
        _files.Sync(Now);

        _database.SaveNote(id, "Made up lecture", "Made up body, edited", Now);
        _files.Sync(Now);

        Assert.Equal("Made up body, edited", File.ReadAllText(Full("Made up lecture.md")));
    }

    [Fact]
    public void An_edit_to_a_file_after_an_idle_sync_still_comes_in()
    {
        var id = _database.CreateNote("Made up lecture", "Made up body", Now);
        Settle();
        _files.Sync(Now);

        File.WriteAllText(Full("Made up lecture.md"), "Made up body, from outside");
        File.SetLastWriteTimeUtc(Full("Made up lecture.md"), DateTime.UtcNow.AddMinutes(1));
        var result = _files.Sync(Now);

        Assert.True(result.NotesChanged);
        Assert.Equal("Made up body, from outside", _database.GetNote(id)!.Body);
    }

    [Fact]
    public void A_new_file_after_an_idle_sync_still_becomes_a_note()
    {
        _database.CreateNote("Made up lecture", "Made up body", Now);
        Settle();
        _files.Sync(Now);

        File.WriteAllText(Full("Made up new.md"), "Made up new body");
        _files.Sync(Now);

        Assert.Contains(_database.GetNotes(), note => note.Title == "Made up new" && note.Body == "Made up new body");
    }

    [Fact]
    public void A_file_another_app_holds_is_tried_again_even_with_nothing_else_changed()
    {
        var id = _database.CreateNote("Made up lecture", "Made up body", Now);
        Settle();
        File.WriteAllText(Full("Made up lecture.md"), "Made up body, from outside");
        File.SetLastWriteTimeUtc(Full("Made up lecture.md"), DateTime.UtcNow.AddMinutes(1));

        using (new FileStream(Full("Made up lecture.md"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            _files.Sync(Now);
            _files.Sync(Now);
        }

        Assert.Equal("Made up body", _database.GetNote(id)!.Body);
        var reads = _store.Reads;
        _files.Sync(Now);

        Assert.Equal(reads + 1, _store.Reads);
        Assert.Equal("Made up body, from outside", _database.GetNote(id)!.Body);
    }

    [Fact]
    public void A_missing_file_keeps_being_checked_until_its_grace_runs_out()
    {
        var kept = _database.CreateNote("Made up kept", "Made up body", Now);
        var gone = _database.CreateNote("Made up gone", "Made up other body", Now);
        _database.CreateNote("Made up third", "Made up third body", Now);
        Settle();

        File.Delete(Full("Made up gone.md"));
        _files.Sync(Now);
        _files.Sync(Now.AddSeconds(3));
        Assert.NotNull(_database.GetNote(gone));

        _files.Sync(Now + NoteFiles.MissingGrace + TimeSpan.FromSeconds(1));

        Assert.Null(_database.GetNote(gone));
        Assert.NotNull(_database.GetNote(kept));
    }
}
