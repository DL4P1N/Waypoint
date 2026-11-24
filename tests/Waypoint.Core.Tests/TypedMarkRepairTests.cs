using Microsoft.Data.Sqlite;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;

namespace Waypoint.Core.Tests;

/// <summary>
/// The one-time repair of notes whose typed "# ", "- ", "1. ", "&gt; ", and "[] " were saved as escaped text while the
/// editor missed the space that should have formatted them.
/// </summary>
public sealed class TypedMarkRepairTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 23, 9, 0, 0);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-repair-{Guid.NewGuid()}");

    public TypedMarkRepairTests() => Directory.CreateDirectory(_folder);

    private string DatabasePath => Path.Combine(_folder, "waypoint.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    [Theory]
    [InlineData("\\# Week 9", "# Week 9")]
    [InlineData("\\## Part two", "## Part two")]
    [InlineData("\\### Small", "### Small")]
    [InlineData("\\- milk", "- milk")]
    [InlineData("\\* eggs", "* eggs")]
    [InlineData("\\+ bread", "+ bread")]
    [InlineData("1\\. Mix", "1. Mix")]
    [InlineData("1\\) Mix", "1) Mix")]
    [InlineData("\\> The only way out is through.", "> The only way out is through.")]
    [InlineData("[] call the dentist", "- [ ] call the dentist")]
    [InlineData("[ ] call the dentist", "- [ ] call the dentist")]
    [InlineData("- [] call the dentist", "- [ ] call the dentist")]
    [InlineData("- \\[ ] call the dentist", "- [ ] call the dentist")]
    [InlineData("  - [] nested", "  - [ ] nested")]
    [InlineData("\\- [] call the dentist", "- [ ] call the dentist")]
    public void Each_typed_line_start_is_made_a_mark_again(string saved, string repaired)
    {
        Assert.Equal((repaired, 1), TypedMarkRepair.Repair(saved));
    }

    [Theory]
    [InlineData("Costs went up \\# percent")]
    [InlineData("2 \\* 3 \\* 4 is 24")]
    [InlineData("Ends with a mark \\-")]
    [InlineData("# Already a heading")]
    [InlineData("- already a list")]
    [InlineData("1. already numbered")]
    [InlineData("> already a quote")]
    [InlineData("- [ ] already a checklist item")]
    [InlineData("\\#hashtag")]
    [InlineData("\\-5 degrees")]
    [InlineData("1\\.5 times")]
    [InlineData("\\#### four marks never made a heading")]
    [InlineData("2\\. typing never started a list at 2")]
    [InlineData("12\\. nor at 12")]
    [InlineData("  \\- an indented line is left alone")]
    [InlineData(" \\# so is one space in")]
    [InlineData("    \\# four spaces in is code")]
    [InlineData("\\- ")]
    [InlineData("\\# ")]
    [InlineData("[link](https://example.com) first")]
    [InlineData("\\[1\\] a footnote-looking start")]
    public void Lines_typing_wouldnt_have_formatted_stay_exactly_as_they_are(string line)
    {
        Assert.Equal((line, 0), TypedMarkRepair.Repair(line));
    }

    [Theory]
    [InlineData("\\- milk\nbuy before noon", "- milk\n\nbuy before noon")]
    [InlineData("1\\. Mix\nthen wait", "1. Mix\n\nthen wait")]
    [InlineData("\\> The only way out is through.\nsaid someone", "> The only way out is through.\n\nsaid someone")]
    [InlineData("[] call the dentist\nbefore Friday", "- [ ] call the dentist\n\nbefore Friday")]
    [InlineData("\\- milk\r\nbuy before noon", "- milk\r\n\r\nbuy before noon")]
    [InlineData("\\- milk\n2\\. not a list", "- milk\n\n2\\. not a list")]
    public void A_plain_line_right_after_a_repaired_list_item_or_quote_gets_a_blank_line_so_it_doesnt_join_it(string saved, string repaired)
    {
        Assert.Equal(repaired, TypedMarkRepair.Repair(saved).Body);
    }

    [Theory]
    [InlineData("\\- milk\n\\- eggs", "- milk\n- eggs")]
    [InlineData("\\> one\n\\> two", "> one\n> two")]
    [InlineData("\\- milk\n\nlater", "- milk\n\nlater")]
    [InlineData("\\- milk\n# Next", "- milk\n# Next")]
    [InlineData("\\- milk\n1. step", "- milk\n1. step")]
    [InlineData("\\# Week 9\nplain under a heading", "# Week 9\nplain under a heading")]
    [InlineData("\\- milk", "- milk")]
    public void No_blank_line_where_the_next_line_starts_its_own_block_or_there_is_none(string saved, string repaired)
    {
        Assert.Equal(repaired, TypedMarkRepair.Repair(saved).Body);
    }

    [Fact]
    public void Nothing_inside_a_code_block_changes_and_line_endings_stay()
    {
        var body = "\\# Notes\r\n```\r\n\\# not a heading in code\r\n\\- nor a list\r\n```\r\n\\# after the code\r\n~~~~\r\n\\> still code\r\n~~~\r\n\\> still code, the fence is longer\r\n~~~~\r\n\\> quote";

        var (repaired, lines) = TypedMarkRepair.Repair(body);

        Assert.Equal("# Notes\r\n```\r\n\\# not a heading in code\r\n\\- nor a list\r\n```\r\n# after the code\r\n~~~~\r\n\\> still code\r\n~~~\r\n\\> still code, the fence is longer\r\n~~~~\r\n> quote", repaired);
        Assert.Equal(3, lines);
    }

    [Fact]
    public void The_repair_changes_the_notes_once_after_a_backup_of_its_own_and_says_so_once()
    {
        var database = Fresh();
        var week = database.CreateNote("Week 9", "\\# Week 9\n1\\. Read chapter 4\nMix \\# not this one\n\\> quote", Now);
        var shopping = database.CreateNote("Shopping", "[] milk\n- eggs", Now);
        var plain = database.CreateNote("Plain", "Costs went up \\# percent", Now);

        var done = database.RepairTypedNoteMarks(Now)!;

        Assert.Equal((4, 2), (done.Lines, done.Notes));
        Assert.Equal("# Week 9\n1. Read chapter 4\n\nMix \\# not this one\n> quote", database.GetNote(week)!.Body);
        Assert.Equal("- [ ] milk\n- eggs", database.GetNote(shopping)!.Body);
        Assert.Equal("Costs went up \\# percent", database.GetNote(plain)!.Body);
        Assert.Equal("Week 9", database.GetNote(week)!.Title);

        // The backup is named for what it's for and isn't the day's daily copy, so the daily one is still made.
        var backup = Assert.Single(database.GetBackups());
        Assert.EndsWith("-before-note-repair.db", backup.Path, StringComparison.Ordinal);
        Assert.True(backup.KeptApart);
        Assert.NotNull(database.BackUpIfDue(Now));
        var name = Path.GetFileName(backup.Path);

        Assert.Equal(
            $"Turned 4 lines in 2 notes back into the headings, lists and quotes they were typed as. The notes as they were are kept in the backups folder as {name}.",
            WaypointDatabase.TakeTypedMarksMessage(database.TakePreference));
        Assert.Null(WaypointDatabase.TakeTypedMarksMessage(database.TakePreference));

        // The backup holds the notes as they were, and is marked so it's never repaired itself.
        var copy = new WaypointDatabase(backup.Path);
        Assert.Equal("[] milk\n- eggs", copy.GetNote(shopping)!.Body);
        Assert.Null(copy.RepairTypedNoteMarks(Now));

        // Once only: a line saved the old way later stays as it's saved, and opening the database runs nothing.
        database.SaveNote(plain, "Plain", "\\# typed on purpose after", Now);
        Assert.Null(database.RepairTypedNoteMarks(Now));
        Assert.Null(new WaypointDatabase(DatabasePath).RepairTypedNoteMarks(Now.AddDays(1)));
        Assert.Equal("\\# typed on purpose after", database.GetNote(plain)!.Body);
    }

    [Fact]
    public void Opening_the_database_doesnt_repair_anything_on_its_own()
    {
        var database = Fresh();
        var id = database.CreateNote("Week 9", "\\# Week 9", Now);

        var opened = new WaypointDatabase(DatabasePath);

        Assert.Equal("\\# Week 9", opened.GetNote(id)!.Body);
        Assert.Null(opened.GetPreference(WaypointDatabase.TypedMarksRepairedKey));
    }

    [Fact]
    public void A_note_still_as_written_from_its_sources_keeps_its_link_to_them_and_is_left_alone()
    {
        var database = Fresh();
        var generated = database.CreateNote("Lecture 4", "", Now);
        Assert.Equal(GeneratedSaveResult.Saved, database.SaveGeneratedNotes(generated, "\\# not typed, written", "", Now).Result);
        var edited = database.CreateNote("Lecture 5", "", Now);
        database.SaveGeneratedNotes(edited, "Written", "", Now);
        database.SaveNote(edited, "Lecture 5", "\\# typed after", Now);

        var done = database.RepairTypedNoteMarks(Now)!;

        Assert.Equal(1, done.Notes);
        Assert.Equal("\\# not typed, written", database.GetNote(generated)!.Body);
        Assert.Equal("# typed after", database.GetNote(edited)!.Body);
    }

    [Fact]
    public void Nothing_to_repair_says_nothing_and_makes_no_backup()
    {
        var database = Fresh();
        database.CreateNote("Fine", "# Week 9\n- milk", Now);

        Assert.Equal(0, database.RepairTypedNoteMarks(Now)!.Notes);

        Assert.NotNull(database.GetPreference(WaypointDatabase.TypedMarksRepairedKey));
        Assert.Null(WaypointDatabase.TakeTypedMarksMessage(database.TakePreference));
        Assert.Empty(database.GetBackups());
    }

    [Fact]
    public void When_no_backup_can_be_made_nothing_is_repaired_and_it_stops_trying_after_three_times()
    {
        var database = Fresh();
        var id = database.CreateNote("Week 9", "\\# Week 9", Now);

        // A file where the backups folder should be, so no copy can be made.
        File.WriteAllText(database.BackupFolder, "not a folder");
        var logged = new List<(string Message, Exception? Problem)>();
        for (var i = 0; i < WaypointDatabase.TypedMarksTries; i++)
        {
            TypedMarkRepair.RunOnce(database, (message, problem) => logged.Add((message, problem)));
            Assert.Equal("\\# Week 9", database.GetNote(id)!.Body);
        }

        Assert.Equal(WaypointDatabase.TypedMarksTries, logged.Count);
        Assert.All(logged, entry => Assert.NotNull(entry.Problem));
        Assert.Contains("won't be tried again", logged[^1].Message, StringComparison.Ordinal);

        File.Delete(database.BackupFolder);
        Assert.Null(database.RepairTypedNoteMarks(Now));
        Assert.Equal("\\# Week 9", database.GetNote(id)!.Body);
    }

    [Fact]
    public void A_backup_that_works_on_a_later_try_repairs_as_usual()
    {
        var database = Fresh();
        var id = database.CreateNote("Week 9", "\\# Week 9", Now);
        File.WriteAllText(database.BackupFolder, "not a folder");
        Assert.NotNull(database.RepairTypedNoteMarks(Now)!.Problem);

        File.Delete(database.BackupFolder);
        Assert.Equal(1, database.RepairTypedNoteMarks(Now)!.Notes);
        Assert.Equal("# Week 9", database.GetNote(id)!.Body);
    }

    [Fact]
    public void Restoring_an_older_backup_keeps_the_repair_done_so_it_isnt_done_or_said_again()
    {
        var database = Fresh();
        var id = database.CreateNote("Week 9", "\\# Week 9", Now);
        var older = database.BackUpNow(Now.AddDays(-1));
        database.RepairTypedNoteMarks(Now);
        database.TakePreference(WaypointDatabase.TypedMarksToSayKey);

        Assert.True(database.RestoreBackup(older, Now).Restored);

        Assert.Equal("\\# Week 9", database.GetNote(id)!.Body);
        Assert.Null(database.RepairTypedNoteMarks(Now));
        Assert.Null(WaypointDatabase.TakeTypedMarksMessage(database.TakePreference));
    }

    [Fact]
    public void A_deleted_note_is_left_alone()
    {
        var database = Fresh();
        var id = database.CreateNote("Old", "\\# Old", Now);
        database.DeleteNote(id, Now);

        Assert.Equal(0, database.RepairTypedNoteMarks(Now)!.Notes);
        Assert.Empty(database.GetBackups());
    }

    [Fact]
    public void A_problem_anywhere_is_logged_and_never_thrown()
    {
        var database = Fresh();
        database.CreateNote("Week 9", "\\# Week 9", Now);
        SqliteConnection.ClearAllPools();
        File.Delete(DatabasePath);
        Directory.CreateDirectory(DatabasePath);
        var logged = new List<Exception?>();

        TypedMarkRepair.RunOnce(database, (_, problem) => logged.Add(problem));

        Assert.NotNull(Assert.Single(logged));
    }

    private WaypointDatabase Fresh() => new(DatabasePath);
}
