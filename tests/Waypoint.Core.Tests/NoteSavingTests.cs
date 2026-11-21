using System.Reflection;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;

namespace Waypoint.Core.Tests;

/// <summary>The note editor's save, which only goes over the text it loaded, with this PC's own notes.</summary>
public sealed class NoteSavingTests : IDisposable
{
    private static readonly DateTime Now = new(2030, 5, 2, 18, 0, 0);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public NoteSavingTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    /// <summary>
    /// The notes, with a reply added from Discord landing just before a save reaches them, after anything the editor
    /// read or checked, the same way the Discord worker adds one.
    /// </summary>
    public class AppendBeforeSave : DispatchProxy
    {
        public INotesData Inner { get; set; } = null!;

        public WaypointDatabase Database { get; set; } = null!;

        /// <summary>The text added the next time a save arrives, if any.</summary>
        public string? Reply { get; set; }

        /// <summary>When set, a reply lands before every save, as from someone still adding them from Discord.</summary>
        public bool KeepsReplying { get; set; }

        public List<string> Calls { get; } = [];

        private int _replies;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls.Add(targetMethod!.Name);
            if (targetMethod.Name is nameof(INotesData.SaveNote) or nameof(INotesData.SaveNoteIfUnchanged)
                && (Reply ?? (KeepsReplying ? $"reply {++_replies}" : null)) is { } reply)
            {
                Reply = null;
                var id = (long)args![0]!;
                var note = Database.GetNote(id)!;
                Assert.True(Database.SaveNoteIfUnchanged(id, note.Title, $"{note.Body}\n\n{reply}", note.Body, Now));
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

        public static (INotesData Notes, AppendBeforeSave Line) Over(WaypointDatabase database)
        {
            var notes = Create<INotesData, AppendBeforeSave>();
            var line = (AppendBeforeSave)(object)notes;
            line.Inner = new LocalNotesData(database);
            line.Database = database;
            return (notes, line);
        }
    }

    [Fact]
    public void A_save_goes_over_the_text_it_expected()
    {
        INotesData notes = new LocalNotesData(_database);
        var id = notes.CreateNote("Seed list", "radish", Now);

        Assert.Equal(NoteSaveOutcome.Saved, notes.SaveNoteIfUnchanged(id, "Seed list", "radish, kale", "radish", Now).Outcome);
        Assert.Equal("radish, kale", _database.GetNote(id)!.Body);
    }

    [Fact]
    public void A_save_over_text_that_changed_saves_nothing_and_says_what_it_is_now()
    {
        INotesData notes = new LocalNotesData(_database);
        var id = notes.CreateNote("Seed list", "radish", Now);
        _database.SaveNote(id, "Seed list", "radish\n\nbeets too", Now);

        var result = notes.SaveNoteIfUnchanged(id, "Seed list", "radish, kale", "radish", Now);

        Assert.Equal(NoteSaveOutcome.Changed, result.Outcome);
        Assert.Equal("radish\n\nbeets too", result.Stored!.Body);
        Assert.Equal("radish\n\nbeets too", _database.GetNote(id)!.Body);

        _database.DeleteNote(id, Now);
        Assert.Equal(NoteSaveOutcome.Gone, notes.SaveNoteIfUnchanged(id, "Seed list", "radish, kale", "radish\n\nbeets too", Now).Outcome);
    }

    [Fact]
    public void A_reply_from_Discord_landing_just_before_the_editor_saves_is_kept()
    {
        var (notes, line) = AppendBeforeSave.Over(_database);
        var id = notes.CreateNote("Seed list", "radish", Now);
        line.Reply = "beets too";

        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale", "radish", Now));

        Assert.Equal("radish, kale", _database.GetNote(id)!.Body);
        var kept = Assert.Single(_database.GetNotes(), note => note.Id != id);
        Assert.Equal("Seed list" + NoteDraft.ElsewhereSuffix, kept.Title);
        Assert.Equal("radish\n\nbeets too", kept.Body);
    }

    [Fact]
    public void A_note_changed_again_before_every_try_is_never_saved_over_and_nothing_is_lost()
    {
        var (notes, line) = AppendBeforeSave.Over(_database);
        var id = notes.CreateNote("Seed list", "radish", Now);
        line.KeepsReplying = true;

        // Every try finds another reply landed between its check and its save, so the editor is told to keep what
        // was typed as a new note, as it does for a note that's gone.
        Assert.False(NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale", "radish", Now));
        line.KeepsReplying = false;
        Assert.DoesNotContain(nameof(INotesData.SaveNote), line.Calls);
        Assert.Equal(NoteSaving.Tries, line.Calls.Count(call => call == nameof(INotesData.SaveNoteIfUnchanged)));

        // The note still has every reply, and each version seen along the way was kept as well.
        const string One = "radish\n\nreply 1", Two = One + "\n\nreply 2", Three = Two + "\n\nreply 3";
        Assert.Equal(Three, _database.GetNote(id)!.Body);
        Assert.Equal([One, Two, Three], _database.GetNotes().Where(note => note.Title == "Seed list" + NoteDraft.ElsewhereSuffix).Select(note => note.Body).Order().ToList());

        // What was typed is kept beside it the way the editor does, and both texts are there.
        var typed = notes.CreateNote("Seed list", "radish, kale", Now, _database.GetNote(id)!.FolderId);
        Assert.Equal("radish, kale", _database.GetNote(typed)!.Body);
        Assert.Equal(Three, _database.GetNote(id)!.Body);
        Assert.Equal(5, _database.GetNotes().Count);
    }

    [Fact]
    public void A_save_tried_again_after_the_note_kept_changing_does_not_keep_the_last_version_twice()
    {
        var (notes, line) = AppendBeforeSave.Over(_database);
        var id = notes.CreateNote("Seed list", "radish", Now);
        line.KeepsReplying = true;
        Assert.False(NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale", "radish", Now));
        line.KeepsReplying = false;

        // Making the new note didn't go through, such as on a busy server, so the editor tries again from the text
        // it loaded. The last version seen was already kept, and this time nothing lands in between.
        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale", "radish", Now));

        Assert.Equal("radish, kale", _database.GetNote(id)!.Body);
        Assert.Equal(3, _database.GetNotes().Count(note => note.Title == "Seed list" + NoteDraft.ElsewhereSuffix));
    }

    [Fact]
    public void A_save_with_nothing_changed_elsewhere_makes_no_copy()
    {
        var (notes, line) = AppendBeforeSave.Over(_database);
        var id = notes.CreateNote("Seed list", "radish", Now);

        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale", "radish", Now));
        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale, leeks", "radish, kale", Now));

        Assert.Equal("radish, kale, leeks", Assert.Single(_database.GetNotes()).Body);
        Assert.Equal(["CreateNote", "SaveNoteIfUnchanged", "SaveNoteIfUnchanged"], line.Calls);
    }

    [Fact]
    public void A_change_elsewhere_to_the_same_text_makes_no_copy()
    {
        INotesData notes = new LocalNotesData(_database);
        var id = notes.CreateNote("Seed list", "radish", Now);
        _database.SaveNote(id, "Seed list", "radish, kale", Now);

        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale", "radish", Now));

        Assert.Equal("radish, kale", Assert.Single(_database.GetNotes()).Body);
    }

    [Fact]
    public void A_note_deleted_elsewhere_says_so_so_the_editor_keeps_its_text_as_a_new_note()
    {
        INotesData notes = new LocalNotesData(_database);
        var id = notes.CreateNote("Seed list", "radish", Now);
        _database.DeleteNote(id, Now);

        Assert.False(NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale", "radish", Now));
        Assert.False(NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale", null, Now));
        Assert.Empty(_database.GetNotes());
    }

    /// <summary>The notes, turning down one of the checked saves, as a busy server does.</summary>
    public class RefuseOne : DispatchProxy
    {
        public INotesData Inner { get; set; } = null!;

        /// <summary>Which of the checked saves to turn down, counting from one, or zero for none.</summary>
        public int RefuseCall { get; set; }

        private int _checked;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(INotesData.SaveNoteIfUnchanged) && ++_checked == RefuseCall)
            {
                throw new InvalidOperationException("The server is busy. Try again in a moment.");
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
    public void A_save_turned_down_after_keeping_the_other_version_does_not_keep_it_again_when_tried_again()
    {
        var notes = DispatchProxy.Create<INotesData, RefuseOne>();
        var refuse = (RefuseOne)(object)notes;
        refuse.Inner = new LocalNotesData(_database);
        var id = notes.CreateNote("Seed list", "radish", Now);
        _database.SaveNote(id, "Seed list", "radish\n\nbeets too", Now);

        // The other version is kept, then the save over it is turned down.
        refuse.RefuseCall = 2;
        Assert.Throws<InvalidOperationException>(() => NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale", "radish", Now));
        Assert.Equal(2, _database.GetNotes().Count);

        // The editor tries again with the text it loaded, and the version already kept isn't kept twice.
        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale", "radish", Now));
        Assert.Equal("radish, kale", _database.GetNote(id)!.Body);
        var kept = Assert.Single(_database.GetNotes(), note => note.Id != id);
        Assert.Equal("radish\n\nbeets too", kept.Body);

        // A later change elsewhere is a new version, so it's kept too.
        _database.SaveNote(id, "Seed list", "radish, kale\n\nleeks too", Now);
        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale, chard", "radish, kale", Now));
        Assert.Equal(3, _database.GetNotes().Count);
    }

    [Fact]
    public void With_nothing_loaded_to_compare_with_the_save_goes_ahead_as_before()
    {
        INotesData notes = new LocalNotesData(_database);
        var id = notes.CreateNote("Seed list", "radish", Now);
        _database.SaveNote(id, "Seed list", "radish\n\nbeets too", Now);

        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale", null, Now));

        Assert.Equal("radish, kale", Assert.Single(_database.GetNotes()).Body);
    }

    [Fact]
    public void A_note_renamed_elsewhere_while_its_editor_is_open_keeps_its_new_title_when_the_text_is_saved()
    {
        INotesData notes = new LocalNotesData(_database);
        var id = notes.CreateNote("Seed list", "radish", Now);
        var draft = new NoteDraft();
        draft.Load(_database.GetNote(id));

        // Renamed from its .md file or from Discord, while only the text is typed here.
        _database.SaveNote(id, "Garden seeds", "radish", Now);
        draft.Edit(draft.Title, "radish, kale");

        Assert.True(NoteSaving.SaveKeepingOther(notes, id, draft.Title, draft.Body, draft.ExpectedStoredBody, Now, draft.ExpectedStoredTitle));
        draft.MarkSaved(id);

        var note = Assert.Single(_database.GetNotes());
        Assert.Equal(("Garden seeds", "radish, kale"), (note.Title, note.Body));

        // The next save, from the same editor, still doesn't put the old title back.
        draft.Edit(draft.Title, "radish, kale, leeks");
        Assert.True(NoteSaving.SaveKeepingOther(notes, id, draft.Title, draft.Body, draft.ExpectedStoredBody, Now, draft.ExpectedStoredTitle));
        note = Assert.Single(_database.GetNotes());
        Assert.Equal(("Garden seeds", "radish, kale, leeks"), (note.Title, note.Body));
    }

    [Fact]
    public void A_title_renamed_here_and_text_changed_elsewhere_keeps_both_changes_in_the_one_note()
    {
        INotesData notes = new LocalNotesData(_database);
        var id = notes.CreateNote("Seed list", "radish", Now);
        _database.SaveNote(id, "Seed list", "radish\n\nbeets too", Now);

        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "Spring seeds", "radish", "radish", Now, "Seed list"));

        var note = Assert.Single(_database.GetNotes());
        Assert.Equal(("Spring seeds", "radish\n\nbeets too"), (note.Title, note.Body));
    }

    [Fact]
    public void A_title_taken_from_the_first_line_is_not_taken_for_a_rename()
    {
        INotesData notes = new LocalNotesData(_database);
        var id = notes.CreateNote("", "radish\nkale", Now);
        Assert.Equal("radish", _database.GetNote(id)!.Title);

        // The editor has no title of its own typed, so the title still follows the first line.
        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "", "carrot\nkale", "radish\nkale", Now, ""));

        var note = Assert.Single(_database.GetNotes());
        Assert.Equal(("carrot", "carrot\nkale"), (note.Title, note.Body));
    }

    [Fact]
    public void A_title_and_text_changed_in_both_places_keeps_both_versions()
    {
        INotesData notes = new LocalNotesData(_database);
        var id = notes.CreateNote("Seed list", "radish", Now);
        _database.SaveNote(id, "Garden seeds", "radish\n\nbeets too", Now);

        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "Spring seeds", "radish, kale", "radish", Now, "Seed list"));

        var note = _database.GetNote(id)!;
        Assert.Equal(("Spring seeds", "radish, kale"), (note.Title, note.Body));
        var kept = Assert.Single(_database.GetNotes(), other => other.Id != id);
        Assert.Equal(("Garden seeds" + NoteDraft.ElsewhereSuffix, "radish\n\nbeets too"), (kept.Title, kept.Body));
    }

    [Fact]
    public void A_title_renamed_in_both_places_keeps_both_versions()
    {
        INotesData notes = new LocalNotesData(_database);
        var id = notes.CreateNote("Seed list", "radish", Now);
        _database.SaveNote(id, "Garden seeds", "radish", Now);

        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "Spring seeds", "radish, kale", "radish", Now, "Seed list"));

        var note = _database.GetNote(id)!;
        Assert.Equal(("Spring seeds", "radish, kale"), (note.Title, note.Body));
        var kept = Assert.Single(_database.GetNotes(), other => other.Id != id);
        Assert.Equal(("Garden seeds" + NoteDraft.ElsewhereSuffix, "radish"), (kept.Title, kept.Body));
    }

    [Fact]
    public void Text_added_from_Discord_or_a_file_keeps_a_title_renamed_meanwhile()
    {
        var id = _database.CreateNote("Seed list", "radish", Now);
        _database.SaveNote(id, "Garden seeds", "radish", Now);

        // The save says the title it kept.
        INotesData notes = new LocalNotesData(_database);
        var saved = notes.SaveNoteIfUnchanged(id, "Seed list", "radish", "radish", Now, "Seed list");
        Assert.Equal((NoteSaveOutcome.Saved, "Garden seeds"), (saved.Outcome, saved.Stored!.Title));
        Assert.Null(notes.SaveNoteIfUnchanged(id, "Garden seeds", "radish", "radish", Now, "Garden seeds").Stored);

        // Read before the rename, saved after it, the way a reply from Discord is added.
        Assert.True(_database.SaveNoteIfUnchanged(id, "Seed list", "radish\n\nbeets too", "radish", Now, expectedTitle: "Seed list"));
        Assert.Equal(("Garden seeds", "radish\n\nbeets too"), (_database.GetNote(id)!.Title, _database.GetNote(id)!.Body));

        // One that renames it too finds it renamed, and saves nothing.
        Assert.False(_database.SaveNoteIfUnchanged(id, "Spring seeds", "radish", "radish\n\nbeets too", Now, expectedTitle: "Seed list"));
        Assert.Equal("Garden seeds", _database.GetNote(id)!.Title);
    }
}
