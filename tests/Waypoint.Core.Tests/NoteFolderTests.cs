using Microsoft.Data.Sqlite;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

public sealed class NoteFolderTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 3, 10, 0, 0);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public NoteFolderTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private long Folder(string name, long? parent = null) => _database.CreateNoteFolder(name, parent, Now).Id;

    [Fact]
    public void ExistingNotesStartUnfiled()
    {
        var id = _database.CreateNote("Old note", "body", Now);

        Assert.Null(_database.GetNote(id)!.FolderId);
        Assert.Single(_database.GetNotesInFolder(null));
        Assert.Equal(1, _database.CountNotesInFolder(null));
    }

    [Fact]
    public void ReopeningAnOlderDatabaseAddsFoldersWithoutLosingNotes()
    {
        _database.CreateNote("Before folders", "body", Now);
        SqliteConnection.ClearAllPools();

        var reopened = new WaypointDatabase(_path);

        Assert.Equal("Before folders", Assert.Single(reopened.GetNotesInFolder(null)).Title);
        Assert.Empty(reopened.GetNoteFolders());
    }

    [Fact]
    public void FoldersListInOrderWithTheirSubfolders()
    {
        var school = Folder("School");
        Folder("Home");
        Folder("CS 101", school);
        Folder("Math", school);

        var folders = _database.GetNoteFolders();

        Assert.Equal(["School", "Home"], folders.Select(folder => folder.Name));
        Assert.Equal(["CS 101", "Math"], folders[0].Children.Select(folder => folder.Name));
        Assert.Equal([0, 1], folders[0].Children.Select(folder => folder.Position));
        Assert.All(folders[0].Children, child => Assert.Equal(school, child.ParentId));
    }

    [Fact]
    public void AFolderNeedsANameThatIsNotTakenAtItsLevel()
    {
        var school = Folder("School");

        Assert.NotNull(_database.CreateNoteFolder("  ", null, Now).Problem);
        Assert.NotNull(_database.CreateNoteFolder("school", null, Now).Problem);
        Assert.Null(_database.CreateNoteFolder("School", school, Now).Problem);
        Assert.NotNull(_database.CreateNoteFolder("Inside", 999, Now).Problem);
    }

    [Fact]
    public void RenamingKeepsTheFolderAndItsNotes()
    {
        var id = Folder("Shcool");
        _database.CreateNote("Lecture", "body", Now, id);
        Folder("Home");

        Assert.Equal("School", _database.RenameNoteFolder(id, " School ").Name);
        Assert.NotNull(_database.RenameNoteFolder(id, "Home").Problem);
        Assert.NotNull(_database.RenameNoteFolder(999, "Gone").Problem);
        Assert.Equal("School", _database.GetNoteFolder(id)!.Name);
        Assert.Equal(1, _database.GetNoteFolder(id)!.NoteCount);
    }

    [Fact]
    public void ReorderingKeepsPositionsInSequence()
    {
        var a = Folder("A");
        var b = Folder("B");
        var c = Folder("C");

        Assert.True(_database.MoveNoteFolder(c, null, 0));
        Assert.Equal(["C", "A", "B"], _database.GetNoteFolders().Select(folder => folder.Name));
        Assert.Equal([0, 1, 2], _database.GetNoteFolders().Select(folder => folder.Position));

        Assert.True(_database.MoveNoteFolder(c, null, 99));
        Assert.Equal(["A", "B", "C"], _database.GetNoteFolders().Select(folder => folder.Name));
        Assert.True(_database.MoveNoteFolder(a, b, 0));
        Assert.Equal(["B", "C"], _database.GetNoteFolders().Select(folder => folder.Name));
        Assert.Equal([0, 1], _database.GetNoteFolders().Select(folder => folder.Position));
        Assert.Equal("A", Assert.Single(_database.GetNoteFolder(b)!.Children).Name);
    }

    [Fact]
    public void AFolderCannotMoveInsideItself()
    {
        var outer = Folder("Outer");
        var inner = Folder("Inner", outer);

        Assert.False(_database.MoveNoteFolder(outer, inner, 0));
        Assert.False(_database.MoveNoteFolder(outer, outer, 0));
        Assert.False(_database.MoveNoteFolder(outer, 999, 0));
        Assert.Equal(outer, Assert.Single(_database.GetNoteFolders()).Id);
    }

    [Fact]
    public void ANewOrderIsSavedWholeWithOtherFoldersAfterIt()
    {
        var a = Folder("A");
        var b = Folder("B");
        var c = Folder("C");
        var d = Folder("D");
        var inner = Folder("Inner", a);

        Assert.True(_database.ReorderNoteFolders(null, [c, a, b]));
        Assert.Equal(["C", "A", "B", "D"], _database.GetNoteFolders().Select(folder => folder.Name));
        Assert.Equal([0, 1, 2, 3], _database.GetNoteFolders().Select(folder => folder.Position));

        // A folder dragged in from another level moves there, and the level it left is renumbered.
        Assert.True(_database.ReorderNoteFolders(null, [inner, d]));
        Assert.Equal(["Inner", "D", "C", "A", "B"], _database.GetNoteFolders().Select(folder => folder.Name));
        Assert.Empty(_database.GetNoteFolder(a)!.Children);
        Assert.Equal([0, 1, 2, 3, 4], _database.GetNoteFolders().Select(folder => folder.Position));
    }

    [Fact]
    public void ANewOrderThatCantAllBeSavedChangesNothing()
    {
        var a = Folder("A");
        var b = Folder("B");
        var c = Folder("C");
        var inner = Folder("Inner", a);
        var before = _database.GetNoteFolders();

        // One folder gone, one listed twice, a folder put inside itself, or a parent that's gone: none of it is kept.
        Assert.False(_database.ReorderNoteFolders(null, [c, b, 999, a]));
        Assert.False(_database.ReorderNoteFolders(null, [c, b, c, a]));
        Assert.False(_database.ReorderNoteFolders(inner, [b, a]));
        Assert.False(_database.ReorderNoteFolders(999, [c, b]));

        Assert.Equivalent(before, _database.GetNoteFolders(), strict: true);
        Assert.Equal(["A", "B", "C"], _database.GetNoteFolders().Select(folder => folder.Name));
        Assert.Equal("Inner", Assert.Single(_database.GetNoteFolder(a)!.Children).Name);
    }

    [Fact]
    public void DeletingAFolderWithNotesAsksFirst()
    {
        var id = Folder("School");
        var note = _database.CreateNote("Lecture", "body", Now, id);

        Assert.Equal(FolderDeleteResult.HasNotes, _database.DeleteNoteFolder(id, NotesInFolder.Refuse));
        Assert.NotNull(_database.GetNoteFolder(id));
        Assert.Equal(id, _database.GetNote(note)!.FolderId);
    }

    [Fact]
    public void AnEmptyFolderDeletesWithoutAsking()
    {
        var id = Folder("Empty");

        Assert.Equal(FolderDeleteResult.Deleted, _database.DeleteNoteFolder(id, NotesInFolder.Refuse));
        Assert.Equal(FolderDeleteResult.NotFound, _database.DeleteNoteFolder(id, NotesInFolder.Refuse));
    }

    [Fact]
    public void DeletingAFolderMovesItsNotesToUnfiledAndKeepsThem()
    {
        var id = Folder("School");
        var note = _database.CreateNote("Lecture", "body", Now, id);
        var deleted = _database.CreateNote("Old", "body", Now, id);
        _database.DeleteNote(deleted, Now);

        Assert.Equal(FolderDeleteResult.Deleted, _database.DeleteNoteFolder(id, NotesInFolder.MoveToUnfiled));

        Assert.Null(_database.GetNote(note)!.FolderId);
        Assert.Single(_database.GetNotesInFolder(null));
        _database.RestoreNote(deleted, Now);
        Assert.Null(_database.GetNote(deleted)!.FolderId);
    }

    [Fact]
    public void DeletingAFolderCanMoveItsNotesAndSubfoldersUpALevel()
    {
        var school = Folder("School");
        Folder("Before", school);
        var cs = Folder("CS", school);
        Folder("After", school);
        var lab = Folder("Lab", cs);
        var note = _database.CreateNote("Lecture", "body", Now, cs);

        Assert.Equal(FolderDeleteResult.Deleted, _database.DeleteNoteFolder(cs, NotesInFolder.MoveToParent));

        Assert.Equal(school, _database.GetNote(note)!.FolderId);
        var children = _database.GetNoteFolder(school)!.Children;
        Assert.Equal(["Before", "Lab", "After"], children.Select(folder => folder.Name));
        Assert.Equal([0, 1, 2], children.Select(folder => folder.Position));
        Assert.Equal(school, _database.GetNoteFolder(lab)!.ParentId);
    }

    [Fact]
    public void NotesMoveBetweenFoldersAndBackToUnfiled()
    {
        var a = Folder("A");
        var b = Folder("B");
        var note = _database.CreateNote("Lecture", "body", Now, a);

        Assert.True(_database.MoveNoteToFolder(note, b));
        Assert.Empty(_database.GetNotesInFolder(a));
        Assert.Equal(note, Assert.Single(_database.GetNotesInFolder(b)).Id);

        Assert.False(_database.MoveNoteToFolder(note, 999));
        Assert.Equal(b, _database.GetNote(note)!.FolderId);

        Assert.True(_database.MoveNoteToFolder(note, null));
        Assert.Null(_database.GetNote(note)!.FolderId);
        Assert.False(_database.MoveNoteToFolder(12345, a));
    }

    [Fact]
    public void AFolderCanListTheNotesInItsSubfoldersToo()
    {
        var school = Folder("School");
        var cs = Folder("CS", school);
        _database.CreateNote("Top", "body", Now, school);
        _database.CreateNote("Nested", "body", Now, cs);

        Assert.Single(_database.GetNotesInFolder(school));
        Assert.Equal(2, _database.GetNotesInFolder(school, includeSubfolders: true).Count);
    }

    [Fact]
    public void AClassFolderIsMadeOnlyWhenAskedAndOnlyOnce()
    {
        Assert.Empty(_database.GetNoteFolders());
        Assert.Null(_database.FindClassFolder("CS 101"));

        var made = _database.CreateClassFolder(" CS 101 ", Now);
        var again = _database.CreateClassFolder("cs 101", Now);

        Assert.Null(made.Problem);
        Assert.Equal("CS 101", made.Name);
        Assert.Equal(made.Id, again.Id);
        Assert.Equal(made.Id, _database.FindClassFolder("CS 101"));
        Assert.Equal("CS 101", Assert.Single(_database.GetNoteFolders()).Course);
        Assert.NotNull(_database.CreateClassFolder(" ", Now).Problem);
    }

    [Fact]
    public void AClassFolderUsesAFolderThatAlreadyHasItsName()
    {
        var existing = Folder("Biology");

        Assert.Equal(existing, _database.CreateClassFolder("Biology", Now).Id);
        Assert.Equal(existing, _database.FindClassFolder("Biology"));
        Assert.Single(_database.GetNoteFolders());
    }

    [Fact]
    public void SearchFindsTitlesAndTextAcrossFolders()
    {
        var cs = Folder("CS");
        _database.CreateNote("Recursion", "base case first", Now, cs);
        _database.CreateNote("Shopping", "eggs and recursion jokes", Now);
        _database.CreateNote("Other", "nothing here", Now);

        Assert.Equal(2, _database.SearchNotes("recursion").Count);
        Assert.Equal("Recursion", Assert.Single(_database.SearchNotes("base case")).Title);
        Assert.Equal(3, _database.SearchNotes("").Count);
    }

    [Fact]
    public void SearchCanStayInsideOneFolder()
    {
        var school = Folder("School");
        var cs = Folder("CS", school);
        _database.CreateNote("Recursion", "base case", Now, cs);
        _database.CreateNote("Recursion again", "unfiled", Now);

        Assert.Equal("Recursion", Assert.Single(_database.SearchNotesInFolder("recursion", school)).Title);
        Assert.Empty(_database.SearchNotesInFolder("recursion", school, includeSubfolders: false));
        Assert.Equal("Recursion again", Assert.Single(_database.SearchNotesInFolder("recursion", null)).Title);
    }

    [Fact]
    public void ShortSearchesStillMatchText()
    {
        _database.CreateNote("CS notes", "body", Now);

        Assert.Single(_database.SearchNotes("CS"));
    }
}
