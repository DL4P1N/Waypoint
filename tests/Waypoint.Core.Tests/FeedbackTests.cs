using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>"This didn't help": the click and an optional line are kept, read back on Settings, cleared, and exported as text.</summary>
public sealed class FeedbackTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 11, 15, 5, 0);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-feedback-{Guid.NewGuid()}");
    private readonly WaypointDatabase _database;

    public FeedbackTests()
    {
        Directory.CreateDirectory(_folder);
        _database = new WaypointDatabase(Path.Combine(_folder, "waypoint.db"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_click_is_kept_with_where_it_was_the_item_and_when()
    {
        var item = _database.AddByHand(new HandAddedItem(ItemKind.Task, "Call the bank", null), Now).Item!;

        var id = _database.AddFeedback(Feedback.Reminder, ItemRef.Format(item), null, Now);

        var entry = Assert.Single(_database.GetFeedback());
        Assert.Equal((id, Now, Feedback.Reminder, ItemRef.Format(item), "Call the bank", (string?)null), (entry.Id, entry.At, entry.Feature, entry.ItemRef, entry.ItemTitle, entry.Note));
    }

    [Fact]
    public void The_note_is_optional_and_kept_to_one_short_line()
    {
        var id = _database.AddFeedback(Feedback.Suggestion, null, null, Now);

        Assert.True(_database.NoteFeedback(id, "  picked something\nfrom weeks ago  "));
        Assert.Equal("picked something from weeks ago", _database.GetFeedback().Single().Note);

        _database.NoteFeedback(id, new string('x', 500));
        Assert.Equal(Feedback.MaxNote, _database.GetFeedback().Single().Note!.Length);

        _database.NoteFeedback(id, "   ");
        Assert.Null(_database.GetFeedback().Single().Note);
    }

    [Fact]
    public void The_list_reads_newest_first_and_clears()
    {
        _database.AddFeedback(Feedback.Session, null, null, Now);
        _database.AddFeedback(Feedback.Row, null, "wrong one", Now.AddMinutes(1));

        Assert.Equal([Feedback.Row, Feedback.Session], _database.GetFeedback().Select(entry => entry.Feature));
        Assert.Equal(2, _database.ClearFeedback());
        Assert.Empty(_database.GetFeedback());
    }

    [Fact]
    public void Each_entry_reads_plainly_on_settings()
    {
        var entry = new FeedbackEntry(1, Now, Feedback.Reminder, "item:4", "Call the bank", "too early");

        Assert.Equal("Nov 11, 3:05 PM  ·  Reminder card  ·  Call the bank: too early", Feedback.Describe(entry));
        Assert.Equal("Nov 11, 3:05 PM  ·  Suggestion on To do", Feedback.Describe(entry with { Feature = Feedback.Suggestion, ItemTitle = null, Note = null }));
    }

    [Fact]
    public void The_notes_export_carries_them_as_text_and_leaves_them_out_when_there_are_none()
    {
        var empty = Path.Combine(_folder, "empty");
        _database.ExportNotes(empty);
        Assert.False(File.Exists(Path.Combine(empty, WaypointDatabase.FeedbackFileName)));

        _database.AddFeedback(Feedback.AfterThisCard, "item:9", "already did it", Now);
        var export = Path.Combine(_folder, "export");
        Assert.Equal(0, _database.ExportNotes(export));

        var text = File.ReadAllText(Path.Combine(export, WaypointDatabase.FeedbackFileName));
        Assert.Contains("2026-11-11 15:05  Earlier you meant to (item:9): already did it", text);
    }

    [Fact]
    public void A_backup_keeps_them()
    {
        _database.AddFeedback(Feedback.Session, null, "too small", Now);

        var backup = _database.BackUpNow(Now);
        SqliteConnection.ClearAllPools();

        Assert.Equal("too small", new WaypointDatabase(backup).GetFeedback().Single().Note);
    }

    [Fact]
    public void The_button_reads_the_same_everywhere_with_no_em_dash()
    {
        Assert.Equal("This didn't help", CardLabels.DidntHelp);
        Assert.DoesNotContain("—", Feedback.AsText([new FeedbackEntry(1, Now, Feedback.Row, null, null, null)]));
    }
}
