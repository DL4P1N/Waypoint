using System.Reflection;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;

namespace Waypoint.Core.Tests;

public sealed class NoteFilesTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 4, 9, 0, 0);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-{Guid.NewGuid():N}.db");
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"waypoint-notes-{Guid.NewGuid():N}");
    private readonly WaypointDatabase _database;
    private readonly NoteFiles _files;

    public NoteFilesTests()
    {
        _database = new WaypointDatabase(_path);
        _files = new NoteFiles(_database, _root);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string File_(string relative) => Path.Combine(_root, relative);

    private string Text(string relative) => File.ReadAllText(File_(relative));

    /// <summary>Writes a file as another app would, with a later time so a sync can tell it changed.</summary>
    private void WriteOutside(string relative, string text)
    {
        var full = File_(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public void Existing_notes_are_written_as_files_in_their_folders()
    {
        var folder = _database.CreateNoteFolder("CS 220", null, Now).Id;
        _database.CreateNote("Lecture 1", "# Graphs\r\nNodes and edges", Now, folder);
        _database.CreateNote("Groceries", "milk\r\neggs", Now);

        _files.Sync(Now);

        Assert.Equal("# Graphs\r\nNodes and edges", Text(@"CS 220\Lecture 1.md"));
        Assert.Equal("milk\r\neggs", Text("Groceries.md"));
    }

    [Fact]
    public void An_edit_in_waypoint_is_written_to_the_file()
    {
        var id = _database.CreateNote("Groceries", "milk", Now);
        _files.Sync(Now);

        _database.SaveNote(id, "Groceries", "milk\r\nbread", Now);
        var result = _files.Sync(Now);

        Assert.Equal("milk\r\nbread", Text("Groceries.md"));
        Assert.False(result.NotesChanged);
    }

    [Fact]
    public void An_edit_to_the_file_comes_into_waypoint()
    {
        var id = _database.CreateNote("Groceries", "milk", Now);
        _files.Sync(Now);

        WriteOutside("Groceries.md", "milk\nbread from obsidian");
        var result = _files.Sync(Now);

        Assert.True(result.NotesChanged);
        Assert.Equal("milk\nbread from obsidian", _database.GetNote(id)!.Body);
        Assert.False(_files.Sync(Now).NotesChanged);
    }

    [Fact]
    public void Renaming_a_note_in_waypoint_renames_its_file()
    {
        var id = _database.CreateNote("Groceries", "milk", Now);
        _files.Sync(Now);

        _database.SaveNote(id, "Shopping", "milk", Now);
        _files.Sync(Now);

        Assert.False(File.Exists(File_("Groceries.md")));
        Assert.Equal("milk", Text("Shopping.md"));
    }

    [Fact]
    public void A_file_renamed_or_moved_outside_keeps_the_same_note()
    {
        var id = _database.CreateNote("Groceries", "milk", Now);
        _files.Sync(Now);

        Directory.CreateDirectory(File_("Home"));
        File.Move(File_("Groceries.md"), File_(@"Home\Shopping.md"));
        var result = _files.Sync(Now);

        Assert.True(result.NotesChanged);
        var note = _database.GetNote(id)!;
        Assert.Equal("Shopping", note.Title);
        Assert.Equal("Home", _database.GetNoteFolder(note.FolderId!.Value)!.Name);
        Assert.Single(_database.GetNotes());
    }

    [Fact]
    public void A_file_renamed_and_edited_outside_at_once_keeps_the_same_note_and_its_sources()
    {
        var body = "# Mitosis\nProphase\nMetaphase\nAnaphase\nTelophase";
        var id = _database.CreateNote("Lecture 4", body, Now);
        _database.AddTranscriptSource(id, "Transcript of lecture 4.m4a", "Today we cover mitosis.", null, Now);
        _files.Sync(Now);

        File.Delete(File_("Lecture 4.md"));
        WriteOutside(@"Biology\Mitosis.md", body + "\nCytokinesis");
        var result = _files.Sync(Now);

        Assert.True(result.NotesChanged);
        var note = Assert.Single(_database.GetNotes());
        Assert.Equal(id, note.Id);
        Assert.Equal("Mitosis", note.Title);
        Assert.Equal(body + "\nCytokinesis", note.Body);
        Assert.Equal("Biology", _database.GetNoteFolder(note.FolderId!.Value)!.Name);
        Assert.Single(_database.GetNoteSources(id));

        // Settled: the next passes change nothing.
        Assert.False(_files.Sync(Now.Add(NoteFiles.MissingGrace)).NotesChanged);
        Assert.Equal(body + "\nCytokinesis", Text(@"Biology\Mitosis.md"));
    }

    [Fact]
    public void A_different_file_appearing_as_one_goes_is_not_taken_for_it()
    {
        var id = _database.CreateNote("Groceries", "milk\neggs\nbread", Now);
        _database.CreateNote("Chores", "laundry", Now);
        _files.Sync(Now);

        File.Delete(File_("Groceries.md"));
        WriteOutside("Ideas.md", "a tray clock that shows the next class");
        _files.Sync(Now);
        _files.Sync(Now.Add(NoteFiles.MissingGrace));

        Assert.Null(_database.GetNote(id));
        Assert.Equal(["a tray clock that shows the next class", "laundry"], _database.GetNotes().Select(note => note.Body).Order());
    }

    [Fact]
    public void Files_a_sync_app_hides_for_a_moment_keep_their_notes()
    {
        var ids = new List<long>();
        for (var n = 1; n <= 6; n++)
        {
            ids.Add(_database.CreateNote($"Note {n}", $"text {n}", Now));
        }

        _files.Sync(Now);
        var hidden = Path.Combine(Path.GetTempPath(), $"waypoint-hidden-{Guid.NewGuid():N}");
        Directory.CreateDirectory(hidden);
        try
        {
            foreach (var n in new[] { 1, 2, 3 })
            {
                File.Move(File_($"Note {n}.md"), Path.Combine(hidden, $"Note {n}.md"));
            }

            _files.Sync(Now);
            _files.Sync(Now.AddSeconds(3));
            foreach (var n in new[] { 1, 2, 3 })
            {
                File.Move(Path.Combine(hidden, $"Note {n}.md"), File_($"Note {n}.md"));
            }

            _files.Sync(Now.Add(NoteFiles.MissingGrace));
        }
        finally
        {
            Directory.Delete(hidden, recursive: true);
        }

        Assert.Equal(ids, _database.GetNotes().Select(note => note.Id).Order());
        Assert.Equal(6, Directory.GetFiles(_root, "*.md").Length);
    }

    [Fact]
    public void A_file_deleted_outside_deletes_the_note_so_history_can_bring_it_back()
    {
        var id = _database.CreateNote("Groceries", "milk", Now);
        _database.CreateNote("Chores", "laundry", Now);
        _database.CreateNote("Ideas", "a tray clock", Now);
        _files.Sync(Now);

        File.Delete(File_("Groceries.md"));
        _files.Sync(Now);

        // Gone for one pass could be a sync app moving it, so the note waits for a later pass.
        Assert.NotNull(_database.GetNote(id));
        _files.Sync(Now.Add(NoteFiles.MissingGrace));

        Assert.Null(_database.GetNote(id));
        Assert.Equal(2, _database.GetNotes().Count);

        _database.RestoreNote(id, Now);
        _files.Sync(Now);
        Assert.Equal("milk", Text("Groceries.md"));
    }

    [Fact]
    public void A_note_deleted_in_waypoint_takes_its_file_and_empty_folder_with_it()
    {
        var folder = _database.CreateNoteFolder("Home", null, Now).Id;
        var id = _database.CreateNote("Groceries", "milk", Now, folder);
        _files.Sync(Now);

        _database.DeleteNote(id, Now);
        _files.Sync(Now);

        Assert.False(File.Exists(File_(@"Home\Groceries.md")));
        Assert.False(Directory.Exists(File_("Home")));
    }

    [Fact]
    public void A_new_file_outside_becomes_a_note_in_a_matching_folder()
    {
        _files.Sync(Now);

        WriteOutside(@"School\Math\Limits.md", "- epsilon\n- delta");
        var result = _files.Sync(Now);

        Assert.True(result.NotesChanged);
        var note = Assert.Single(_database.GetNotes());
        Assert.Equal("Limits", note.Title);
        Assert.Equal("- epsilon\n- delta", note.Body);
        var math = _database.GetNoteFolder(note.FolderId!.Value)!;
        Assert.Equal("Math", math.Name);
        Assert.Equal("School", _database.GetNoteFolder(math.ParentId!.Value)!.Name);
        Assert.False(_files.Sync(Now).NotesChanged);
    }

    [Fact]
    public void Other_apps_dot_folders_are_left_alone()
    {
        WriteOutside(@".obsidian\workspace.md", "settings");
        WriteOutside(@".trash\Old.md", "gone");

        _files.Sync(Now);

        Assert.Empty(_database.GetNotes());
    }

    [Fact]
    public void Changed_on_both_sides_keeps_both_versions()
    {
        var id = _database.CreateNote("Groceries", "milk", Now);
        _files.Sync(Now);

        _database.SaveNote(id, "Groceries", "milk from waypoint", Now);
        WriteOutside("Groceries.md", "milk from obsidian");
        _files.Sync(Now);

        Assert.Equal("milk from waypoint", _database.GetNote(id)!.Body);
        Assert.Equal("milk from waypoint", Text("Groceries.md"));
        Assert.Contains(_database.GetNotes(), note => note.Title == "Groceries (edited elsewhere)" && note.Body == "milk from obsidian");
    }

    /// <summary>The database as the sync sees it, failing to remember a written file once, as a dropped server would.</summary>
    public class FailsToRemember : DispatchProxy
    {
        public WaypointDatabase Inner { get; set; } = null!;

        public bool Failed { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(INoteFileStore.SetNoteFileRecord) && !Failed)
            {
                Failed = true;
                throw new InvalidOperationException("Made-up failure to remember the file.");
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

    [Fact]
    public void A_file_written_whose_record_was_not_saved_is_remembered_not_kept_as_a_copy()
    {
        var id = _database.CreateNote("Groceries", "milk", Now);
        _files.Sync(Now);
        _database.SaveNote(id, "Groceries", "milk and bread", Now);

        var store = DispatchProxy.Create<INoteFileStore, FailsToRemember>();
        ((FailsToRemember)(object)store).Inner = _database;
        Assert.Throws<InvalidOperationException>(() => new NoteFiles(store, _root).Sync(Now));
        Assert.Equal("milk and bread", Text("Groceries.md"));

        var result = _files.Sync(Now);

        Assert.False(result.NotesChanged);
        Assert.Single(_database.GetNotes());
        Assert.Equal("milk and bread", _database.GetNote(id)!.Body);
        Assert.Equal(["Groceries.md"], Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Select(Path.GetFileName));
        Assert.Equal(NoteFiles.Hash("milk and bread"), _database.GetNoteFileRecords().Single().FileHash);
    }

    [Fact]
    public void Writes_a_crash_left_unfinished_are_swept_on_the_first_sync_but_other_apps_files_stay()
    {
        string Made(string relative, DateTime writtenUtc)
        {
            var full = File_(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "half a note");
            File.SetLastWriteTimeUtc(full, writtenUtc);
            return full;
        }

        var old = DateTime.UtcNow.AddHours(-2);
        var stale = Made(".Groceries.md.tmp", old);
        var staleInFolder = Made(@"CS 220\.Lecture 1.md.tmp", old);
        var writing = Made(".Packing.md.tmp", DateTime.UtcNow);
        var otherApp = Made(@".obsidian\.workspace.md.tmp", old);
        var notOurs = Made("Draft.tmp", old);

        _files.Sync(Now);

        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(staleInFolder));
        Assert.True(File.Exists(writing));
        Assert.True(File.Exists(otherApp));
        Assert.True(File.Exists(notOurs));
        Assert.Empty(_database.GetNotes());
    }

    [Fact]
    public void Writing_a_note_file_keeps_when_it_was_first_made()
    {
        var id = _database.CreateNote("Groceries", "milk", Now);
        _files.Sync(Now);
        var made = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetCreationTimeUtc(File_("Groceries.md"), made);

        _database.SaveNote(id, "Groceries", "milk and eggs", Now);
        _files.Sync(Now);

        Assert.Equal("milk and eggs", Text("Groceries.md"));
        Assert.Equal(made, File.GetCreationTimeUtc(File_("Groceries.md")));
    }

    [Fact]
    public void Writing_a_note_file_leaves_no_file_beside_it()
    {
        var id = _database.CreateNote("Groceries", "milk", Now);
        _files.Sync(Now);
        _database.SaveNote(id, "Groceries", "milk and eggs", Now);
        _files.Sync(Now);

        Assert.Equal("milk and eggs", Text("Groceries.md"));
        Assert.Equal(["Groceries.md"], Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Select(Path.GetFileName));
    }

    /// <summary>
    /// The database as the sync sees it, with the editor saving a note right after the pass has read the notes, so
    /// the pass holds the text from before that save.
    /// </summary>
    public class EditorSavesDuringPass : DispatchProxy
    {
        public WaypointDatabase Inner { get; set; } = null!;

        /// <summary>The note and the text the editor saves, once, just after the next pass reads the notes.</summary>
        public (long Id, string Body)? Save { get; set; }

        /// <summary>When set, the editor's save gives the note this title too.</summary>
        public string? Rename { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            object? result;
            try
            {
                result = targetMethod!.Invoke(Inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is { } inner)
            {
                throw inner;
            }

            if (targetMethod.Name == nameof(INoteFileStore.GetNoteFileStates) && Save is { } save)
            {
                Save = null;
                var note = Inner.GetNote(save.Id)!;
                Assert.True(Inner.SaveNoteIfUnchanged(save.Id, Rename ?? note.Title, save.Body, note.Body, Now));
            }

            return result;
        }
    }

    private (NoteFiles Files, EditorSavesDuringPass Editor) WithEditor()
    {
        var store = DispatchProxy.Create<INoteFileStore, EditorSavesDuringPass>();
        var editor = (EditorSavesDuringPass)(object)store;
        editor.Inner = _database;
        return (new NoteFiles(store, _root), editor);
    }

    [Fact]
    public void An_editor_save_during_a_pass_that_takes_in_the_file_is_kept_and_the_file_too()
    {
        var (files, editor) = WithEditor();
        var id = _database.CreateNote("Groceries", "milk", Now);
        files.Sync(Now);

        WriteOutside("Groceries.md", "milk from obsidian");
        editor.Save = (id, "milk from the editor");
        files.Sync(Now);

        Assert.Equal("milk from the editor", _database.GetNote(id)!.Body);
        Assert.Equal("milk from the editor", Text("Groceries.md"));
        Assert.Contains(_database.GetNotes(), note => note.Title == "Groceries (edited elsewhere)" && note.Body == "milk from obsidian");
        Assert.Equal(2, _database.GetNotes().Count);
    }

    [Fact]
    public void An_editor_save_during_a_pass_that_takes_a_renamed_file_keeps_the_editors_text()
    {
        var (files, editor) = WithEditor();
        var id = _database.CreateNote("Groceries", "milk", Now);
        files.Sync(Now);

        File.Move(File_("Groceries.md"), File_("Shopping.md"));
        editor.Save = (id, "milk from the editor");
        files.Sync(Now);

        var note = Assert.Single(_database.GetNotes());
        Assert.Equal("Shopping", note.Title);
        Assert.Equal("milk from the editor", note.Body);
        Assert.Equal("milk from the editor", Text("Shopping.md"));
        Assert.False(files.Sync(Now).NotesChanged);
    }

    [Fact]
    public void A_rename_in_the_editor_during_a_pass_that_takes_a_renamed_file_keeps_the_editors_title()
    {
        var (files, editor) = WithEditor();
        var id = _database.CreateNote("Groceries", "milk", Now);
        files.Sync(Now);

        File.Move(File_("Groceries.md"), File_("Shopping.md"));
        editor.Save = (id, "milk");
        editor.Rename = "Errands";
        files.Sync(Now);
        files.Sync(Now);

        var note = Assert.Single(_database.GetNotes());
        Assert.Equal(("Errands", "milk"), (note.Title, note.Body));
        Assert.Equal("milk", Text("Errands.md"));
        Assert.False(files.Sync(Now).NotesChanged);
    }

    [Fact]
    public void An_editor_save_during_a_pass_that_takes_a_renamed_and_edited_file_keeps_both()
    {
        var (files, editor) = WithEditor();
        var body = "# Mitosis\nProphase\nMetaphase\nAnaphase\nTelophase";
        var id = _database.CreateNote("Lecture 4", body, Now);
        files.Sync(Now);

        File.Delete(File_("Lecture 4.md"));
        WriteOutside(@"Biology\Mitosis.md", body + "\nCytokinesis");
        editor.Save = (id, body + "\nInterphase");
        files.Sync(Now);

        var note = _database.GetNote(id)!;
        Assert.Equal("Mitosis", note.Title);
        Assert.Equal(body + "\nInterphase", note.Body);
        Assert.Equal(body + "\nInterphase", Text(@"Biology\Mitosis.md"));
        Assert.Contains(_database.GetNotes(), other => other.Title == "Mitosis (edited elsewhere)" && other.Body == body + "\nCytokinesis");
        Assert.Equal(2, _database.GetNotes().Count);
    }

    [Fact]
    public void Notes_with_the_same_title_get_their_own_files()
    {
        _database.CreateNote("Meeting", "first", Now);
        _database.CreateNote("Meeting", "second", Now);

        _files.Sync(Now);
        _files.Sync(Now);

        Assert.Equal("first", Text("Meeting.md"));
        Assert.Equal("second", Text("Meeting (2).md"));
        Assert.Equal(2, _database.GetNotes().Count);
    }

    [Fact]
    public void Titles_that_cant_be_file_names_are_cleaned_up()
    {
        _database.CreateNote("Q&A: what/why?", "text", Now);

        _files.Sync(Now);

        Assert.Equal("text", Text("Q&A- what-why-.md"));
        Assert.Equal("Q&A: what/why?", Assert.Single(_database.GetNotes()).Title);
    }

    [Fact]
    public void Renaming_a_folder_in_waypoint_moves_its_notes()
    {
        var folder = _database.CreateNoteFolder("Math", null, Now).Id;
        _database.CreateNote("Limits", "text", Now, folder);
        _files.Sync(Now);

        _database.RenameNoteFolder(folder, "Calculus");
        _files.Sync(Now);

        Assert.Equal("text", Text(@"Calculus\Limits.md"));
        Assert.False(Directory.Exists(File_("Math")));
    }

    [Fact]
    public void A_missing_notes_folder_is_written_again_instead_of_deleting_every_note()
    {
        _database.CreateNote("One", "1", Now);
        _database.CreateNote("Two", "2", Now);
        _files.Sync(Now);

        Directory.Delete(_root, recursive: true);
        _files.Sync(Now);

        Assert.Equal(2, _database.GetNotes().Count);
        Assert.Equal("1", Text("One.md"));
    }

    [Fact]
    public void A_fresh_folder_after_moving_it_gets_every_note()
    {
        _database.CreateNote("One", "1", Now);
        _files.Sync(Now);

        var elsewhere = new NoteFiles(_database, _root + "-moved");
        _database.ClearNoteFileRecords();
        try
        {
            elsewhere.Sync(Now);
            Assert.Equal("1", File.ReadAllText(Path.Combine(_root + "-moved", "One.md")));
            Assert.Single(_database.GetNotes());
        }
        finally
        {
            Directory.Delete(_root + "-moved", recursive: true);
        }
    }

    /// <summary>Syncs the way the app does over a few minutes, past the wait before a missing file counts as deleted.</summary>
    private static NoteSyncResult[] SyncPastGrace(NoteFiles files) =>
        [files.Sync(Now), files.Sync(Now.AddSeconds(3)), files.Sync(Now.Add(NoteFiles.MissingGrace)), files.Sync(Now.Add(NoteFiles.MissingGrace * 3))];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void An_emptied_notes_folder_gets_every_note_written_back_instead_of_deleting_them(int count)
    {
        var ids = Enumerable.Range(1, count).Select(n => _database.CreateNote($"Note {n}", $"text {n}", Now)).ToList();
        _files.Sync(Now);

        foreach (var file in Directory.GetFiles(_root, "*.md"))
        {
            File.Delete(file);
        }

        var results = SyncPastGrace(_files);

        Assert.Equal(ids, _database.GetNotes().Select(note => note.Id).Order());
        Assert.All(results, result => Assert.Equal(0, result.Removed));
        Assert.Equal(count, Directory.GetFiles(_root, "*.md").Length);
        Assert.Equal("text 1", Text("Note 1.md"));
        Assert.NotNull(results[0].Notice);
        Assert.Null(results[^1].Notice);
    }

    [Fact]
    public void A_missing_notes_folder_never_deletes_notes_even_after_the_wait()
    {
        _database.CreateNote("One", "1", Now);
        _database.CreateNote("Two", "2", Now);
        _database.CreateNote("Three", "3", Now);
        _files.Sync(Now);

        Directory.Delete(_root, recursive: true);
        SyncPastGrace(_files);

        Assert.Equal(3, _database.GetNotes().Count);
        Assert.Equal("3", Text("Three.md"));
    }

    [Fact]
    public void Losing_most_of_a_few_files_at_once_writes_them_back_instead_of_deleting()
    {
        var ids = Enumerable.Range(1, 3).Select(n => _database.CreateNote($"Note {n}", $"text {n}", Now)).ToList();
        _files.Sync(Now);

        File.Delete(File_("Note 1.md"));
        File.Delete(File_("Note 2.md"));
        var results = SyncPastGrace(_files);

        Assert.Equal(ids, _database.GetNotes().Select(note => note.Id).Order());
        Assert.Equal("text 2", Text("Note 2.md"));
        Assert.NotNull(results[0].Notice);
    }

    [Fact]
    public void One_file_deleted_out_of_two_still_deletes_its_note_after_the_wait()
    {
        var gone = _database.CreateNote("Groceries", "milk", Now);
        var kept = _database.CreateNote("Chores", "laundry", Now);
        _files.Sync(Now);

        File.Delete(File_("Groceries.md"));
        var results = SyncPastGrace(_files);

        Assert.Null(_database.GetNote(gone));
        Assert.Equal(kept, Assert.Single(_database.GetNotes()).Id);
        Assert.All(results, result => Assert.Null(result.Notice));
    }

    [Fact]
    public void A_note_whose_only_file_is_renamed_outside_keeps_the_same_note()
    {
        var id = _database.CreateNote("Groceries", "milk\neggs", Now);
        _files.Sync(Now);

        File.Move(File_("Groceries.md"), File_("Shopping.md"));
        SyncPastGrace(_files);

        var note = Assert.Single(_database.GetNotes());
        Assert.Equal(id, note.Id);
        Assert.Equal("Shopping", note.Title);
        Assert.Single(Directory.GetFiles(_root, "*.md"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Notes_synced_with_another_folder_are_written_to_a_new_one_instead_of_deleted(bool folderExists)
    {
        // A trial copy of the data, or a folder pointed somewhere else, still has the records made against the old one.
        var ids = Enumerable.Range(1, 4).Select(n => _database.CreateNote($"Note {n}", $"text {n}", Now)).ToList();
        _files.Sync(Now);

        var other = _root + "-other";
        if (folderExists)
        {
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "Note 1.md"), "text 1");
            File.WriteAllText(Path.Combine(other, "Unrelated.md"), "something already there");
        }

        var notes = folderExists ? 5 : 4;
        try
        {
            var results = SyncPastGrace(new NoteFiles(_database, other));

            Assert.Equal(notes, _database.GetNotes().Count);
            Assert.Subset(_database.GetNotes().Select(note => note.Id).ToHashSet(), ids.ToHashSet());
            Assert.All(results, result => Assert.Equal(0, result.Removed));
            Assert.Equal("text 2", File.ReadAllText(Path.Combine(other, "Note 2.md")));
            Assert.Equal(notes, Directory.GetFiles(other, "*.md").Length);
            Assert.Equal(Path.GetFullPath(other), _database.NoteFilesRoot());
            Assert.All(_database.GetNoteFileRecords(), record => Assert.True(File.Exists(Path.Combine(other, record.Path))));

            // Back in the first folder, which still has its files, nothing is lost either.
            SyncPastGrace(_files);
            Assert.Equal(notes, _database.GetNotes().Count);
            Assert.Equal(notes, Directory.GetFiles(_root, "*.md").Length);
        }
        finally
        {
            if (Directory.Exists(other))
            {
                Directory.Delete(other, recursive: true);
            }
        }
    }
}
