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
    [InlineData("2\\. Stir", "2. Stir")]
    [InlineData("1\\) Mix", "1) Mix")]
    [InlineData("\\> The only way out is through.", "> The only way out is through.")]
    [InlineData("[] call the dentist", "- [ ] call the dentist")]
    [InlineData("[ ] call the dentist", "- [ ] call the dentist")]
    [InlineData("- [] call the dentist", "- [ ] call the dentist")]
    [InlineData("- \\[ ] call the dentist", "- [ ] call the dentist")]
    [InlineData("\\- [] call the dentist", "- [ ] call the dentist")]
    [InlineData("  \\- nested", "  - nested")]
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
    [InlineData("    \\# four spaces in is code")]
    [InlineData("[link](https://example.com) first")]
    [InlineData("\\[1\\] a footnote-looking start")]
    public void Lines_that_werent_typed_marks_stay_exactly_as_they_are(string line)
    {
        Assert.Equal((line, 0), TypedMarkRepair.Repair(line));
    }

    [Fact]
    public void Nothing_inside_a_code_block_changes_and_line_endings_stay()
    {
        var body = "\\# Notes\r\n```\r\n\\# not a heading in code\r\n\\- nor a list\r\n```\r\n\\- after the code\r\n~~~~\r\n\\> still code\r\n~~~\r\n\\> still code, the fence is longer\r\n~~~~\r\n\\> quote";

        var (repaired, lines) = TypedMarkRepair.Repair(body);

        Assert.Equal("# Notes\r\n```\r\n\\# not a heading in code\r\n\\- nor a list\r\n```\r\n- after the code\r\n~~~~\r\n\\> still code\r\n~~~\r\n\\> still code, the fence is longer\r\n~~~~\r\n> quote", repaired);
        Assert.Equal(3, lines);
    }

    [Fact]
    public void Opening_the_database_repairs_the_notes_once_after_a_backup_and_says_what_changed()
    {
        var database = Fresh();
        var week = database.CreateNote("Week 9", "\\# Week 9\n1\\. Read chapter 4\n2\\. Mix \\# not this one\n\\> quote", Now);
        var shopping = database.CreateNote("Shopping", "[] milk\n- eggs", Now);
        var plain = database.CreateNote("Plain", "Costs went up \\# percent", Now);
        Unrepaired(database);

        var opened = new WaypointDatabase(DatabasePath);

        Assert.Equal("# Week 9\n1. Read chapter 4\n2. Mix \\# not this one\n> quote", opened.GetNote(week)!.Body);
        Assert.Equal("- [ ] milk\n- eggs", opened.GetNote(shopping)!.Body);
        Assert.Equal("Costs went up \\# percent", opened.GetNote(plain)!.Body);
        Assert.Equal("Week 9", opened.GetNote(week)!.Title);
        Assert.Equal(
            "Turned 5 lines in 2 notes back into the headings, lists and quotes they were typed as. A copy from before is kept with the other backups.",
            WaypointDatabase.TakeTypedMarksMessage(opened));
        Assert.Null(WaypointDatabase.TakeTypedMarksMessage(opened));

        // The backup holds the notes as they were, and restoring it doesn't repair them again.
        var backup = Assert.Single(opened.GetBackups());
        var copy = new WaypointDatabase(backup.Path);
        Assert.Equal("[] milk\n- eggs", copy.GetNote(shopping)!.Body);
        Assert.Null(WaypointDatabase.TypedMarksMessage(copy.GetPreference(WaypointDatabase.TypedMarksToSayKey)));

        // Once only: a line saved the old way later stays as it's saved.
        opened.SaveNote(plain, "Plain", "\\# typed on purpose after", Now);
        var again = new WaypointDatabase(DatabasePath);
        Assert.Equal("\\# typed on purpose after", again.GetNote(plain)!.Body);
        Assert.Single(again.GetBackups());
    }

    [Fact]
    public void A_new_database_and_one_with_nothing_to_repair_say_nothing_and_make_no_backup()
    {
        var database = Fresh();
        database.CreateNote("Fine", "# Week 9\n- milk", Now);
        Unrepaired(database);

        var opened = new WaypointDatabase(DatabasePath);

        Assert.NotNull(opened.GetPreference(WaypointDatabase.TypedMarksRepairedKey));
        Assert.Null(WaypointDatabase.TakeTypedMarksMessage(opened));
        Assert.Empty(opened.GetBackups());
        Assert.Equal("1 line", WaypointDatabase.TypedMarksMessage("1 1")![7..13]);
    }

    [Fact]
    public void When_the_backup_cant_be_made_nothing_is_repaired_and_it_tries_again_next_time()
    {
        var database = Fresh();
        var id = database.CreateNote("Week 9", "\\# Week 9", Now);
        Unrepaired(database);

        // A file where the backups folder should be, so no copy can be made.
        File.WriteAllText(database.BackupFolder, "not a folder");
        var opened = new WaypointDatabase(DatabasePath);
        Assert.Equal("\\# Week 9", opened.GetNote(id)!.Body);
        Assert.Null(opened.GetPreference(WaypointDatabase.TypedMarksRepairedKey));

        File.Delete(database.BackupFolder);
        var later = new WaypointDatabase(DatabasePath);
        Assert.Equal("# Week 9", later.GetNote(id)!.Body);
    }

    [Fact]
    public void A_deleted_note_is_left_alone()
    {
        var database = Fresh();
        var id = database.CreateNote("Old", "\\# Old", Now);
        database.DeleteNote(id, Now);
        Unrepaired(database);

        var opened = new WaypointDatabase(DatabasePath);

        Assert.Null(opened.GetNote(id));
        Assert.Empty(opened.GetBackups());
    }

    private WaypointDatabase Fresh() => new(DatabasePath);

    /// <summary>As a database from before the repair: no flag, so the next open runs it.</summary>
    private static void Unrepaired(WaypointDatabase database)
    {
        using var connection = new SqliteConnection($"Data Source={database.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM preferences WHERE key IN ($done, $say)";
        command.Parameters.AddWithValue("$done", WaypointDatabase.TypedMarksRepairedKey);
        command.Parameters.AddWithValue("$say", WaypointDatabase.TypedMarksToSayKey);
        command.ExecuteNonQuery();
    }
}
