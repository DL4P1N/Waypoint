using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

public sealed class DirectEditTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 3, 10, 0, 0);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public DirectEditTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private sealed class CountingSorter : ICaptureSorter
    {
        public int Calls { get; private set; }

        public AssistantContext? Seen { get; private set; }

        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            Seen = context;
            return Task.FromResult<CaptureResult?>(new CaptureResult([], Answer: "ok"));
        }
    }

    private ListEntry Entry(string list, string text) =>
        _database.GetListEntries(list).Single(entry => entry.Text == text);

    private long CapturedNote(string raw, string title)
    {
        _database.CompleteCapture(_database.AddCapture(raw), [new SortedItem(ItemKind.Note, title, null, null)]);
        return _database.GetNotes().Single(note => note.Title == title).Id;
    }

    [Fact]
    public void A_new_list_can_be_made_empty()
    {
        var made = _database.CreateList("my camping list", Now);

        Assert.Equal("camping", made.Name);
        Assert.Null(made.Problem);
        Assert.Contains(_database.GetListOverview(), list => list.Name == "camping");
    }

    [Fact]
    public void Lists_keep_the_order_they_were_put_in_and_new_ones_go_last()
    {
        _database.CreateList("packing", Now);
        _database.CreateList("camping", Now);
        _database.CreateList("errands", Now);
        string[] Names() => _database.GetListOverview(includeEmptyShopping: true).Select(list => list.Name).ToArray();
        Assert.Equal(["Shopping", "camping", "errands", "packing"], Names());

        // Shopping always stays first, even when it's named in the order.
        _database.OrderLists(["packing", "Shopping", "camping", "errands"]);
        Assert.Equal(["Shopping", "packing", "camping", "errands"], Names());
        Assert.Equal(["packing", "camping", "errands"], _database.GetListNames());

        _database.CreateList("books", Now);
        _database.RenameList("camping", "tent", Now);
        Assert.Equal(["Shopping", "packing", "tent", "errands", "books"], Names());

        // A removed list brought back keeps its place.
        _database.DeleteList("packing", Now);
        _database.CreateList("packing", Now);
        Assert.Equal(["Shopping", "packing", "tent", "errands", "books"], Names());
    }

    [Fact]
    public void A_blank_list_name_is_refused()
    {
        Assert.NotNull(_database.CreateList("   ", Now).Problem);
        Assert.Empty(_database.GetListNames());
    }

    [Fact]
    public void Shopping_is_always_there_for_the_lists_page()
    {
        var lists = _database.GetListOverview(includeEmptyShopping: true);

        Assert.Equal(WaypointDatabase.ShoppingList, lists.Single().Name);
    }

    [Fact]
    public void A_list_can_be_renamed_and_keeps_its_entries()
    {
        _database.AddListEntry("packing", "socks", Now);

        var renamed = _database.RenameList("packing", "camping", Now);

        Assert.Equal("camping", renamed.Name);
        Assert.Equal(["socks"], _database.GetListEntries("camping").Select(entry => entry.Text));
        Assert.Empty(_database.GetListEntries("packing"));
    }

    [Fact]
    public void A_rename_can_change_only_the_capitals()
    {
        _database.CreateList("packing", Now);

        Assert.Null(_database.RenameList("packing", "Packing", Now).Problem);
        Assert.Equal(["Packing"], _database.GetListNames());
    }

    [Fact]
    public void A_rename_onto_a_taken_name_changes_nothing()
    {
        _database.CreateList("packing", Now);
        _database.CreateList("camping", Now);

        Assert.NotNull(_database.RenameList("packing", "CAMPING", Now).Problem);
        Assert.Contains("packing", _database.GetListNames());
    }

    [Fact]
    public void Shopping_cannot_be_renamed()
    {
        Assert.NotNull(_database.RenameList(WaypointDatabase.ShoppingList, "Groceries", Now).Problem);
    }

    [Fact]
    public void Nothing_can_be_renamed_to_Shopping()
    {
        _database.CreateList("packing", Now);

        Assert.NotNull(_database.RenameList("packing", "shopping", Now).Problem);
    }

    [Fact]
    public void A_removed_list_comes_back_with_its_entries()
    {
        _database.AddListEntry("packing", "socks", Now);
        _database.DeleteList("packing", Now);
        Assert.DoesNotContain("packing", _database.GetListNames());

        _database.CreateList("packing", Now);

        Assert.Equal(["socks"], _database.GetListEntries("packing").Select(entry => entry.Text));
    }

    [Fact]
    public void Adding_to_shopping_directly_makes_a_shopping_item_and_never_sorts()
    {
        _database.AddListEntry(WaypointDatabase.ShoppingList, "milk", Now);

        Assert.Contains(_database.GetOpenItems(), item => item is { Kind: ItemKind.Shopping, Title: "milk" });
        Assert.Empty(_database.GetPendingCaptures());
    }

    [Fact]
    public void Adding_to_a_list_directly_leaves_no_capture_to_sort()
    {
        _database.AddListEntry("packing", "socks", Now);

        Assert.Empty(_database.GetPendingCaptures());
    }

    [Fact]
    public void A_direct_add_shows_up_when_asked_by_typing()
    {
        _database.AddListEntry(WaypointDatabase.ShoppingList, "milk", Now);
        _database.AddListEntry("packing", "socks", Now);

        Assert.Contains("milk", _database.DescribeList("shopping"));
        Assert.Contains("socks", _database.DescribeList("packing"));
    }

    [Fact]
    public void Adding_something_already_checked_off_puts_it_back()
    {
        _database.AddListEntry("packing", "socks", Now);
        _database.SetEntryDone(Entry("packing", "socks"), true, Now);

        _database.AddListEntry("packing", "Socks", Now);

        Assert.False(Assert.Single(_database.GetListEntries("packing")).Done);
    }

    [Fact]
    public void A_blank_entry_is_ignored()
    {
        _database.AddListEntry(WaypointDatabase.ShoppingList, "  ", Now);

        Assert.Empty(_database.GetListEntries(WaypointDatabase.ShoppingList));
    }

    [Fact]
    public void Entries_can_be_edited_on_any_list()
    {
        _database.AddListEntry("packing", "sock", Now);
        _database.AddListEntry(WaypointDatabase.ShoppingList, "mlik", Now);

        _database.EditEntry(Entry("packing", "sock"), "socks");
        _database.EditEntry(Entry(WaypointDatabase.ShoppingList, "mlik"), "milk");

        Assert.Equal("socks", Assert.Single(_database.GetListEntries("packing")).Text);
        Assert.Equal("milk", Assert.Single(_database.GetListEntries(WaypointDatabase.ShoppingList)).Text);
    }

    [Fact]
    public void Editing_to_nothing_keeps_the_entry()
    {
        _database.AddListEntry("packing", "socks", Now);

        _database.EditEntry(Entry("packing", "socks"), "  ");

        Assert.Equal("socks", Assert.Single(_database.GetListEntries("packing")).Text);
    }

    [Fact]
    public void A_deleted_entry_can_be_brought_back_where_it_was()
    {
        _database.AddListEntry("packing", "socks", Now);
        _database.AddListEntry("packing", "charger", Now);
        var entry = Entry("packing", "socks");

        _database.DeleteEntry(entry, Now);
        Assert.Equal(["charger"], _database.GetListEntries("packing").Select(item => item.Text));

        _database.RestoreEntry(entry, Now);
        Assert.Equal(["socks", "charger"], _database.GetListEntries("packing").Select(item => item.Text));
    }

    [Fact]
    public void A_deleted_shopping_entry_is_canceled_in_history_and_comes_back()
    {
        _database.AddListEntry(WaypointDatabase.ShoppingList, "milk", Now);
        var entry = Entry(WaypointDatabase.ShoppingList, "milk");

        _database.DeleteEntry(entry, Now);

        Assert.Empty(_database.GetListEntries(WaypointDatabase.ShoppingList));
        Assert.Equal("Canceled", _database.GetHistory().Single(history => history.Title == "milk").Label);

        _database.RestoreEntry(entry, Now);

        Assert.Single(_database.GetListEntries(WaypointDatabase.ShoppingList));
        Assert.DoesNotContain(_database.GetHistory(), history => history.Title == "milk");
    }

    [Fact]
    public void A_checked_shopping_entry_can_be_unchecked()
    {
        _database.AddListEntry(WaypointDatabase.ShoppingList, "milk", Now);
        var entry = Entry(WaypointDatabase.ShoppingList, "milk");
        _database.SetEntryDone(entry, true, Now);

        _database.SetEntryDone(entry, false, Now);

        Assert.Single(_database.GetListEntries(WaypointDatabase.ShoppingList));
    }

    [Fact]
    public void Typing_to_remove_an_entry_can_also_be_undone_and_searching_skips_it()
    {
        _database.AddListEntry("packing", "socks", Now);
        var entry = Entry("packing", "socks");

        _database.RemoveEntry("packing", "socks", Now);

        Assert.Empty(_database.SearchEverything(new SearchQuery("socks"), Now));
        _database.RestoreEntry(entry, Now);
        Assert.Single(_database.GetListEntries("packing"));
    }

    [Fact]
    public void A_new_note_is_stored_without_going_to_the_sorter()
    {
        var id = _database.CreateNote("Bank fee", "They close at 5", Now);

        var note = _database.GetNote(id)!;
        Assert.Equal("Bank fee", note.Title);
        Assert.Equal("They close at 5", note.Body);
        Assert.Empty(_database.GetPendingCaptures());
    }

    [Fact]
    public void A_note_with_no_title_takes_its_first_line()
    {
        var id = _database.CreateNote("", "\nCall the bank\nabout the fee", Now);

        Assert.Equal("Call the bank", _database.GetNote(id)!.Title);
    }

    [Fact]
    public void A_captured_note_shows_what_was_written()
    {
        var id = CapturedNote("Call the bank about the fee\nThey close at 5", "Bank fee");

        Assert.Equal("Call the bank about the fee\nThey close at 5", _database.GetNote(id)!.Body);
    }

    [Fact]
    public void Editing_a_captured_note_keeps_the_original_capture_text()
    {
        var id = CapturedNote("original words", "Bank fee");

        Assert.True(_database.SaveNote(id, "Bank", "edited words", Now));

        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.text FROM captures c JOIN items i ON i.capture_id = c.id WHERE i.id = $id";
        command.Parameters.AddWithValue("$id", id);
        Assert.Equal("original words", command.ExecuteScalar());

        var note = _database.GetNote(id)!;
        Assert.Equal("Bank", note.Title);
        Assert.Equal("edited words", note.Body);
    }

    [Fact]
    public void Search_finds_the_edited_words_and_not_the_replaced_ones()
    {
        var id = CapturedNote("original words", "Bank fee");
        _database.SaveNote(id, "Bank fee", "platypus sighting", Now);

        var query = new SearchQuery("platypus", SearchIndex.Note);
        Assert.Single(_database.SearchEverything(query, Now));
        Assert.Empty(_database.SearchEverything(new SearchQuery("original", SearchIndex.Note), Now));
    }

    [Fact]
    public async Task The_sorter_sees_an_edited_note_it_is_asked_about()
    {
        var id = CapturedNote("original words", "Bank fee");
        _database.SaveNote(id, "Bank fee", "platypus sighting", Now);
        var sorter = new CountingSorter();
        var service = new CaptureService(_database, sorter);

        service.Capture("what did I write about the platypus");
        await service.ProcessPendingAsync();

        var shown = Assert.Single(sorter.Seen!.EditedNotes!);
        Assert.Equal("platypus sighting", shown.Body);
        Assert.Contains("platypus sighting", ClaudeCliSorter.BuildPrompt("note", Now, sorter.Seen));
    }

    [Fact]
    public async Task Notes_nobody_asked_about_stay_out_of_the_prompt()
    {
        var id = CapturedNote("original words", "Bank fee");
        _database.SaveNote(id, "Bank fee", "platypus sighting", Now);
        var sorter = new CountingSorter();
        var service = new CaptureService(_database, sorter);

        service.Capture("laundry tomorrow");
        await service.ProcessPendingAsync();

        Assert.Empty(sorter.Seen!.EditedNotes!);
    }

    [Fact]
    public void Direct_edits_do_not_call_the_sorter_at_all()
    {
        var id = _database.CreateNote("Bank", "body", Now);
        _database.SaveNote(id, "Bank", "more", Now);
        _database.AddListEntry("packing", "socks", Now);

        Assert.Empty(_database.GetPendingCaptures());
    }

    [Fact]
    public void Notes_filter_on_title_and_body_whatever_the_case()
    {
        _database.CreateNote("Bank fee", "they close at 5", Now);
        _database.CreateNote("Groceries", "CHEAP eggs", Now);

        Assert.Equal(["Groceries"], _database.GetNotes("cheap").Select(note => note.Title));
        Assert.Equal(["Bank fee"], _database.GetNotes("bank").Select(note => note.Title));
        Assert.Equal(2, _database.GetNotes("  ").Count);
    }

    [Fact]
    public void Notes_stay_newest_first_even_after_an_edit_so_the_list_does_not_jump()
    {
        var first = _database.CreateNote("First", "", Now);
        _database.CreateNote("Second", "", Now);

        _database.SaveNote(first, "First!", "", Now);

        Assert.Equal(["Second", "First!"], _database.GetNotes().Select(note => note.Title));
    }

    [Fact]
    public void A_deleted_note_goes_to_history_and_comes_back()
    {
        var id = _database.CreateNote("Bank fee", "body", Now);

        _database.DeleteNote(id, Now);

        Assert.Empty(_database.GetNotes());
        Assert.Null(_database.GetNote(id));
        Assert.Equal("Canceled", _database.GetHistory().Single(history => history.Title == "Bank fee").Label);

        _database.RestoreNote(id, Now);

        Assert.Equal("body", Assert.Single(_database.GetNotes()).Body);
        Assert.DoesNotContain(_database.GetHistory(), history => history.Title == "Bank fee");
    }

    [Fact]
    public void Saving_a_note_that_is_gone_reports_it_so_the_text_is_not_lost()
    {
        var id = _database.CreateNote("Bank fee", "body", Now);
        _database.DeleteNote(id, Now);

        Assert.False(_database.SaveNote(id, "Bank fee", "newer", Now));
    }

    [Fact]
    public void History_can_be_searched()
    {
        _database.AddListEntry(WaypointDatabase.ShoppingList, "milk", Now);
        _database.AddListEntry(WaypointDatabase.ShoppingList, "eggs", Now);
        _database.SetEntryDone(Entry(WaypointDatabase.ShoppingList, "milk"), true, Now);
        _database.SetEntryDone(Entry(WaypointDatabase.ShoppingList, "eggs"), true, Now);

        Assert.Equal(["milk"], _database.SearchHistory("MIL", 30).Select(entry => entry.Title));
        Assert.Equal(2, _database.SearchHistory("", 30).Count);
        Assert.Single(_database.SearchHistory(null, 1));
    }
}

public sealed class RefreshGateTests
{
    [Fact]
    public void The_same_data_never_rebuilds()
    {
        var gate = new RefreshGate();

        Assert.True(gate.ShouldRebuild("a", busy: false));
        Assert.False(gate.ShouldRebuild("a", busy: false));
    }

    [Fact]
    public void A_change_waits_while_someone_is_working_in_the_list()
    {
        var gate = new RefreshGate();
        gate.ShouldRebuild("a", busy: false);

        Assert.False(gate.ShouldRebuild("b", busy: true));
        Assert.True(gate.ShouldRebuild("b", busy: false));
        Assert.False(gate.ShouldRebuild("b", busy: false));
    }

    [Fact]
    public void Invalidate_forces_the_next_rebuild()
    {
        var gate = new RefreshGate();
        gate.ShouldRebuild("a", busy: false);

        gate.Invalidate();

        Assert.True(gate.ShouldRebuild("a", busy: false));
    }
}

public sealed class NoteDraftTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 10, 0, 0);

    private static NoteDetail Stored(string title = "Title", string body = "Body") => new(1, title, body, null, Now);

    [Fact]
    public void Loading_a_note_is_not_an_edit()
    {
        var draft = new NoteDraft();

        draft.Load(Stored());

        Assert.False(draft.IsDirty);
        Assert.Equal(1, draft.Id);
    }

    [Fact]
    public void Typing_makes_it_dirty_and_saving_clears_that()
    {
        var draft = new NoteDraft();
        draft.Load(Stored());

        draft.Edit("Title", "Body and more");
        Assert.True(draft.IsDirty);

        draft.MarkSaved(1);
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void Typing_back_to_what_was_saved_needs_no_save()
    {
        var draft = new NoteDraft();
        draft.Load(Stored());

        draft.Edit("Title", "Body!");
        draft.Edit("Title", "Body");

        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void A_new_note_with_nothing_in_it_is_not_saved()
    {
        var draft = new NoteDraft();
        draft.Load(null);

        draft.Edit("  ", "\n");

        Assert.False(draft.ShouldSave);
    }

    [Fact]
    public void A_new_note_with_text_gets_saved_and_takes_its_id()
    {
        var draft = new NoteDraft();
        draft.Load(null);

        draft.Edit("", "something");
        Assert.True(draft.ShouldSave);

        draft.MarkSaved(9);
        Assert.Equal(9, draft.Id);
        Assert.False(draft.ShouldSave);
    }

    [Fact]
    public void A_refresh_never_replaces_text_being_typed()
    {
        var draft = new NoteDraft();
        draft.Load(Stored());
        draft.Edit("Title", "half a sentence");

        Assert.False(draft.ShouldReload(Stored(body: "changed elsewhere"), editing: false));
        Assert.False(draft.ShouldReload(null, editing: false));
    }

    [Fact]
    public void A_refresh_never_replaces_the_editor_while_it_has_focus()
    {
        var draft = new NoteDraft();
        draft.Load(Stored());

        Assert.False(draft.ShouldReload(Stored(body: "changed elsewhere"), editing: true));
    }

    [Fact]
    public void A_change_from_elsewhere_loads_once_nobody_is_in_the_editor()
    {
        var draft = new NoteDraft();
        draft.Load(Stored());

        Assert.True(draft.ShouldReload(Stored(body: "changed elsewhere"), editing: false));
        Assert.False(draft.ShouldReload(Stored(), editing: false));
    }

    [Fact]
    public void A_note_removed_elsewhere_clears_only_a_clean_idle_editor()
    {
        var draft = new NoteDraft();
        draft.Load(Stored());

        Assert.True(draft.ShouldReload(null, editing: false));
    }

    [Fact]
    public void A_new_note_never_reloads()
    {
        var draft = new NoteDraft();
        draft.Load(null);

        Assert.False(draft.ShouldReload(null, editing: false));
    }
}
