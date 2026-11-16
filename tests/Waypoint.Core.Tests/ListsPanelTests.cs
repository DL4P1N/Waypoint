using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Tests;

public sealed class ListsPanelTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 3, 10, 0, 0);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public ListsPanelTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private sealed class FakeSorter(CaptureResult result) : ICaptureSorter
    {
        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<CaptureResult?>(result);
    }

    private void Add(string list, params string[] entries)
    {
        var capture = _database.AddCapture("add");
        _database.AddToList(list, entries, capture, Now);
        _database.CompleteCapture(capture, []);
    }

    private void Note(string raw, string title, string? about = null) =>
        _database.CompleteCapture(_database.AddCapture(raw), [new SortedItem(ItemKind.Note, title, null, null) { About = about }]);

    [Fact]
    public void Nothing_to_show_gives_an_empty_overview()
    {
        Assert.Empty(_database.GetListOverview());
        Assert.Empty(_database.GetRecentNotes(10));
    }

    [Fact]
    public void Shopping_comes_first_with_open_counts()
    {
        Add("packing", "socks", "charger");
        Add("Aardvark", "feed it");
        Add("shopping", "milk", "eggs");
        _database.SetEntryDone("packing", "socks", true, Now);

        var lists = _database.GetListOverview();

        Assert.Equal(["Shopping", "Aardvark", "packing"], lists.Select(list => list.Name));
        Assert.Equal(2, lists[0].OpenCount);
        Assert.Equal(1, lists[2].OpenCount);
    }

    [Fact]
    public void An_empty_shopping_list_is_left_out()
    {
        Add("packing", "socks");

        Assert.Equal(["packing"], _database.GetListOverview().Select(list => list.Name));
    }

    [Fact]
    public void Checked_entries_sit_at_the_bottom()
    {
        Add("packing", "socks", "charger");
        _database.SetEntryDone("packing", "socks", true, Now);

        var entries = _database.GetListOverview().Single().Entries;

        Assert.Equal(["charger", "socks"], entries.Select(entry => entry.Text));
    }

    [Fact]
    public void Clicking_an_entry_checks_that_exact_one()
    {
        Add("packing", "milk crate");
        Add("camping", "milk crate");
        var entry = _database.GetListOverview().Single(list => list.Name == "packing").Entries.Single();

        _database.SetEntryDone(entry, true, Now);

        var lists = _database.GetListOverview();
        Assert.True(lists.Single(list => list.Name == "packing").Entries.Single().Done);
        Assert.False(lists.Single(list => list.Name == "camping").Entries.Single().Done);
    }

    [Fact]
    public void Clicking_again_unchecks_a_list_entry()
    {
        Add("packing", "socks");
        var entry = _database.GetListOverview().Single().Entries.Single();
        _database.SetEntryDone(entry, true, Now);

        _database.SetEntryDone(entry with { Done = true }, false, Now);

        Assert.False(_database.GetListOverview().Single().Entries.Single().Done);
    }

    [Fact]
    public void Clicking_a_shopping_entry_finishes_it_like_typing_does()
    {
        Add("shopping", "milk");
        var entry = _database.GetListOverview().Single().Entries.Single();

        _database.SetEntryDone(entry, true, Now);

        Assert.Empty(_database.GetListOverview());
        Assert.Contains(_database.GetHistory(10), history => history.Title == "milk");
    }

    [Fact]
    public void Clicking_a_shopping_entry_that_is_already_closed_does_nothing()
    {
        Add("shopping", "milk");
        var entry = _database.GetListOverview().Single().Entries.Single();
        _database.SetEntryDone(entry, true, Now);

        _database.SetEntryDone(entry, true, Now);

        Assert.Single(_database.GetHistory(10), history => history.Title == "milk");
    }

    [Fact]
    public void Checking_off_a_list_entry_shows_in_history_once_with_its_list()
    {
        Add("packing", "socks");
        var entry = _database.GetListOverview().Single().Entries.Single();

        _database.SetEntryDone(entry, true, Now);
        _database.SetEntryDone("packing", "socks", true, Now);

        var history = Assert.Single(_database.GetHistory(10));
        Assert.Equal("socks", history.Title);
        Assert.Equal("packing", history.Course);
        Assert.Equal(ItemSource.List, history.Source);
        Assert.Equal("Finished", history.Label);
        Assert.Single(_database.SearchHistory("packing", 10, HistoryFilter.Completed));
    }

    [Fact]
    public void Unchecking_a_list_entry_takes_it_back_out_of_history()
    {
        Add("packing", "socks");
        var entry = _database.GetListOverview().Single().Entries.Single();
        _database.SetEntryDone(entry, true, Now);

        _database.SetEntryDone(entry with { Done = true }, false, Now);

        Assert.Empty(_database.GetHistory(10));
        Assert.Single(_database.SearchHistory(null, 10, HistoryFilter.Reopened));
    }

    [Fact]
    public void A_list_check_off_can_be_reopened_from_history()
    {
        Add("packing", "socks");
        _database.SetEntryDone("packing", "socks", true, Now);
        var history = _database.GetHistory(10).Single();

        Assert.True(_database.CanReopen(history));
        Assert.True(_database.ReopenEntry(history));

        Assert.False(_database.GetListOverview().Single().Entries.Single().Done);
        Assert.Empty(_database.GetHistory(10));
        Assert.False(_database.CanReopen(history));
    }

    [Fact]
    public void Deleting_a_list_check_off_from_history_leaves_the_list_alone()
    {
        Add("packing", "socks");
        _database.SetEntryDone("packing", "socks", true, Now);

        var deleted = _database.DeleteHistoryItem(_database.GetHistory(10).Single(), Now)!;

        Assert.Empty(_database.GetHistory(10));
        Assert.True(_database.GetListOverview().Single().Entries.Single().Done);

        _database.UndoDelete(deleted, Now);
        Assert.Single(_database.GetHistory(10));
    }

    [Fact]
    public void Notes_come_newest_first_and_stop_at_the_limit()
    {
        Note("first", "First note");
        Note("second", "Second note");
        Note("third", "Third note");

        Assert.Equal(["Third note", "Second note"], _database.GetRecentNotes(2).Select(note => note.Title));
    }

    [Fact]
    public void A_note_shows_the_first_line_of_what_was_written()
    {
        Note("Call the bank about the fee\nThey close at 5", "Bank fee");

        Assert.Equal("Call the bank about the fee", _database.GetRecentNotes(10).Single().FirstLine);
    }

    [Fact]
    public void A_note_that_is_just_its_title_repeats_nothing()
    {
        Note("Bank fee", "Bank fee");

        Assert.Null(_database.GetRecentNotes(10).Single().FirstLine);
    }

    [Fact]
    public void A_long_first_line_is_cut_short()
    {
        Note(new string('a', 500), "Long");

        Assert.True(_database.GetRecentNotes(10).Single().FirstLine!.Length <= 121);
    }

    [Fact]
    public void Other_kinds_are_not_notes()
    {
        _database.CompleteCapture(_database.AddCapture("task"), [new SortedItem(ItemKind.Task, "A task", null, null)]);

        Assert.Empty(_database.GetRecentNotes(10));
    }

    [Fact]
    public async Task A_list_asked_for_in_Discord_answers_there_in_full()
    {
        Add("shopping", "milk", "eggs");
        CaptureOutcome? outcome = null;
        var service = new CaptureService(_database, new FakeSorter(new CaptureResult([], Ops: [new AssistantOp(AssistantOpKind.ListShow, "shopping")])));
        service.Sorted += sorted => outcome = sorted;

        service.Capture("what's on my shopping list", null, CaptureOrigin.Discord);
        await service.ProcessPendingAsync();

        Assert.Equal(CaptureOrigin.Discord, outcome!.Origin);
        var card = DiscordText.Notice("Waypoint", outcome.Answer!);
        Assert.Contains("milk", card.Description);
        Assert.Contains("eggs", card.Description);
    }

    [Fact]
    public void A_very_long_list_fits_a_Discord_card_and_says_how_much_is_left_out()
    {
        var lines = Enumerable.Range(1, 400).Select(number => $"- something to buy number {number}");

        var card = DiscordText.Notice("Waypoint", string.Join("\n", lines));

        Assert.True(card.Description!.Length <= 4096);
        Assert.Contains("- something to buy number 1\n", card.Description);
        Assert.EndsWith("more", card.Description);
        Assert.DoesNotContain("number 400", card.Description);
    }

    [Fact]
    public void Replies_that_already_end_a_sentence_do_not_get_a_second_period()
    {
        var outcome = new CaptureOutcome([], [], null, OpReplies: ["Work saved: Monday, 6:00 PM.", "Work saved: Saturday, 2:00 PM."]);

        Assert.Equal("Work saved: Monday, 6:00 PM. Work saved: Saturday, 2:00 PM.", ItemGroups.OutcomeSummary(outcome));
    }

    [Fact]
    public void Replies_without_a_period_are_still_joined_with_one()
    {
        var outcome = new CaptureOutcome([], [], null, OpReplies: ["Checked off milk", "Which list?", "Added eggs"]);

        Assert.Equal("Checked off milk. Which list? Added eggs", ItemGroups.OutcomeSummary(outcome));
    }
}
