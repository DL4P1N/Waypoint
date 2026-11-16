using Microsoft.Data.Sqlite;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>What the Notes page reads besides notes: saved spots, classes for a class folder, and moving notes between folders.</summary>
public sealed class NotesPageTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 3, 10, 0, 0);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public NotesPageTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void Saved_spots_list_newest_first()
    {
        _database.SaveHandoff("Waypoint", "Notes column", "Drag notes", [], _database.AddCapture("one"), Now);
        _database.SaveHandoff("Essay", "Second paragraph", null, [], _database.AddCapture("two"), Now.AddMinutes(5));

        var spots = _database.GetSavedSpots();

        Assert.Equal(new[] { "Essay", "Waypoint" }, spots.Select(spot => spot.Project));
    }

    [Fact]
    public void Saved_spot_search_needs_every_word_somewhere_in_the_spot()
    {
        _database.SaveHandoff("Waypoint", "Notes column", "Drag notes onto folders", [], _database.AddCapture("one"), Now);
        _database.SaveHandoff("Essay", "Second paragraph", null, [], _database.AddCapture("two"), Now);

        Assert.Equal("Waypoint", Assert.Single(_database.GetSavedSpots("waypoint folders")).Project);
        Assert.Equal("Essay", Assert.Single(_database.GetSavedSpots("PARAGRAPH")).Project);
        Assert.Empty(_database.GetSavedSpots("waypoint paragraph"));
        Assert.Equal(2, _database.GetSavedSpots("  ").Count);
    }

    [Fact]
    public void Class_names_come_from_the_schedule_and_are_sorted()
    {
        _database.ScheduleCourses = ["MATH 220", "CS 101", " ", "cs 101"];

        var classes = _database.GetClassNames();

        Assert.Contains(classes, name => name.Equals("CS 101", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(classes, name => name.Equals("MATH 220", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(classes, string.IsNullOrWhiteSpace);
        Assert.Equal(classes.Order(StringComparer.OrdinalIgnoreCase), classes);
    }

    [Fact]
    public void A_class_folder_for_a_listed_class_is_made_once()
    {
        _database.ScheduleCourses = ["CS 101"];
        var course = Assert.Single(_database.GetClassNames());

        var first = _database.CreateClassFolder(course, Now);
        var second = _database.CreateClassFolder(course, Now);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Id, _database.FindClassFolder(course));
        Assert.Single(_database.GetNoteFolders());
    }

    [Fact]
    public void A_new_note_in_a_folder_shows_there_and_in_all_but_not_unfiled()
    {
        var folder = _database.CreateNoteFolder("School", null, Now).Id;
        var note = _database.CreateNote("Lecture", "# Recursion\n\n- base case", Now, folder);

        Assert.Equal(note, Assert.Single(_database.GetNotesInFolder(folder)).Id);
        Assert.Empty(_database.GetNotesInFolder(null));
        Assert.Contains(_database.GetNotes(), found => found.Id == note);
    }

    [Fact]
    public void Moving_a_note_and_moving_it_back_restores_where_it_was()
    {
        var school = _database.CreateNoteFolder("School", null, Now).Id;
        var home = _database.CreateNoteFolder("Home", null, Now).Id;
        var note = _database.CreateNote("Lecture", "body", Now, school);

        Assert.True(_database.MoveNoteToFolder(note, home));
        Assert.Equal(home, _database.GetNote(note)!.FolderId);

        Assert.True(_database.MoveNoteToFolder(note, school));
        Assert.Equal(school, _database.GetNote(note)!.FolderId);
    }

    [Fact]
    public void Folder_search_reaches_note_bodies_inside_subfolders()
    {
        var school = _database.CreateNoteFolder("School", null, Now).Id;
        var cs = _database.CreateNoteFolder("CS 101", school, Now).Id;
        _database.CreateNote("Week 3", "Recursion needs a base case", Now, cs);
        _database.CreateNote("Groceries", "Recursion soup", Now);

        Assert.Equal("Week 3", Assert.Single(_database.SearchNotesInFolder("recursion", school)).Title);
        Assert.Equal(2, _database.SearchNotes("recursion").Count);
    }

    [Fact]
    public void Deleting_a_folder_with_notes_keeps_them_in_the_parent()
    {
        var school = _database.CreateNoteFolder("School", null, Now).Id;
        var cs = _database.CreateNoteFolder("CS 101", school, Now).Id;
        var note = _database.CreateNote("Week 3", "body", Now, cs);

        Assert.Equal(FolderDeleteResult.HasNotes, _database.DeleteNoteFolder(cs, NotesInFolder.Refuse));
        Assert.Equal(FolderDeleteResult.Deleted, _database.DeleteNoteFolder(cs, NotesInFolder.MoveToParent));

        Assert.Equal(school, _database.GetNote(note)!.FolderId);
    }
}
