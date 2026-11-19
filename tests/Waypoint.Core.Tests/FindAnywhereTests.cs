using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;
using Waypoint.Core.Remote;

namespace Waypoint.Core.Tests;

public sealed class FindAnywhereTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public FindAnywhereTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private FindSources Local() => new(
        new LocalToDoData(_database),
        new LocalListsData(_database),
        new LocalNotesData(_database),
        new LocalHistoryData(_database),
        new LocalBudgetData(_database));

    private StoredItem Add(ItemKind kind, string title) =>
        _database.AddByHand(new HandAddedItem(kind, title, Today), DateTime.Now).Item!;

    [Fact]
    public void One_search_finds_to_dos_list_entries_notes_history_and_purchases_in_page_order()
    {
        Add(ItemKind.Task, "Call the dentist");
        _database.AddToList("Errands", ["dentist forms"], 0, DateTime.Now);
        _database.CreateNote("Dentist questions", "ask about the cleaning", DateTime.Now);
        _database.SetDone(Add(ItemKind.Task, "Book dentist cleaning"), true);
        _database.AddExpense("Smile Dentist", 4500, "Health", Today, DateTime.Now);
        Add(ItemKind.Task, "Return library books");

        var found = FindAnywhere.Search("dentist", Local());

        Assert.Equal(
            [FoundPage.ToDo, FoundPage.Lists, FoundPage.Notes, FoundPage.History, FoundPage.Budget],
            found.Select(thing => thing.Page));
        Assert.Equal("Call the dentist", found[0].Title);
        Assert.NotNull(found[0].Item);
        Assert.Equal("Errands", found[1].Entry!.List);
        Assert.Equal("Notes, Errands list", found[1].Where);
        Assert.NotNull(found[2].NoteId);
        Assert.Equal("Book dentist cleaning", found[3].History!.Title);
        Assert.StartsWith("History, finished ", found[3].Where);
        Assert.Equal(4500, found[4].Purchase!.AmountCents);
        Assert.EndsWith("$45.00", found[4].Where);
    }

    [Fact]
    public void Shopping_shows_once_under_lists_and_notes_only_under_notes()
    {
        _database.AddListEntry(WaypointDatabase.ShoppingList, "oat milk", DateTime.Now);
        _database.CreateNote("Milk alternatives", "oat, soy", DateTime.Now);

        var found = FindAnywhere.Search("milk", Local());

        Assert.Equal([FoundPage.Lists, FoundPage.Notes], found.Select(thing => thing.Page));
        Assert.Equal(WaypointDatabase.ShoppingList, found[0].Entry!.List);
        Assert.Equal("Notes, Shopping list", found[0].Where);
    }

    [Fact]
    public void A_checked_entry_says_which_list_and_that_it_is_checked()
    {
        _database.AddToList("Packing", ["socks"], 0, DateTime.Now);
        _database.SetEntryDone(_database.GetListEntries("Packing").Single(), true, DateTime.Now);

        var found = FindAnywhere.Search("socks", Local());

        Assert.Equal("Notes, Packing list, checked", found.Single(thing => thing.Page == FoundPage.Lists).Where);
    }

    [Fact]
    public void Every_word_has_to_match_and_a_single_letter_finds_nothing()
    {
        Add(ItemKind.Task, "Pay rent");
        Add(ItemKind.Task, "Pay phone bill");

        Assert.Equal(["Pay phone bill"], FindAnywhere.Search("pay phone", Local()).Select(thing => thing.Title));
        Assert.Empty(FindAnywhere.Search("p", Local()));
        Assert.Empty(FindAnywhere.Search("  ", Local()));
    }

    [Fact]
    public void Each_page_shows_a_few_so_one_busy_page_cant_crowd_out_the_rest()
    {
        for (var i = 0; i < 10; i++)
        {
            Add(ItemKind.Task, $"Study chapter {i}");
        }

        _database.CreateNote("Study plan", "chapters", DateTime.Now);

        var found = FindAnywhere.Search("study", Local());

        Assert.Equal(FindAnywhere.PerPage, found.Count(thing => thing.Page == FoundPage.ToDo));
        Assert.Contains(found, thing => thing.Page == FoundPage.Notes);
    }

    [Fact]
    public void Purchases_are_found_in_any_month_by_merchant_or_category_newest_first()
    {
        _database.AddExpense("Chipotle", 1200, "Food", Today.AddMonths(-3), DateTime.Now);
        _database.AddExpense("Chipotle", 1400, "Food", Today, DateTime.Now);
        _database.AddExpense("Shell", 4000, "Gas", Today, DateTime.Now);

        Assert.Equal([1400, 1200], _database.SearchPurchases("chipotle", 10).Select(row => row.AmountCents));
        Assert.Equal(["Shell"], _database.SearchPurchases("shell", 10).Select(row => row.Title));
        Assert.Equal(2, _database.SearchPurchases(_database.SearchPurchases("chipotle", 1)[0].Category, 10).Count(row => row.Title == "Chipotle"));
        Assert.Single(_database.SearchPurchases("chipotle", 1));
        Assert.Empty(_database.SearchPurchases("", 10));
    }

    [Fact]
    public void A_page_that_cant_answer_is_left_out_and_the_rest_still_show()
    {
        Add(ItemKind.Task, "Renew passport");
        var sources = Local() with { Notes = null, Lists = new AwayLists() };

        var found = FindAnywhere.Search("passport", sources);

        Assert.Equal(["Renew passport"], found.Select(thing => thing.Title));
    }

    /// <summary>Stands in for the server being away with nothing read yet.</summary>
    private sealed class AwayLists : IListsData
    {
        public string? GetPreference(string key) => null;

        public void SetPreference(string key, string value) => throw new ServerUnavailableException("away");

        public IReadOnlyList<ListOverview> GetListOverview() => throw new ServerUnavailableException("away");

        public ListEdit CreateList(string name, DateTime now) => throw new NotSupportedException();

        public ListEdit RenameList(string name, string newName, DateTime now) => throw new NotSupportedException();

        public void OrderLists(IReadOnlyList<string> names) => throw new NotSupportedException();

        public string DeleteList(string name, DateTime now) => throw new NotSupportedException();

        public void AddListEntry(string list, string text, DateTime now) => throw new NotSupportedException();

        public void SetEntryDone(ListEntry entry, bool done, DateTime now) => throw new NotSupportedException();

        public void EditEntry(ListEntry entry, string text) => throw new NotSupportedException();

        public void DeleteEntry(ListEntry entry, DateTime now) => throw new NotSupportedException();

        public void RestoreEntry(ListEntry entry, DateTime now) => throw new NotSupportedException();

        public string StartList(string name) => throw new NotSupportedException();

        public void UndoStartList(string name, IReadOnlyList<long> checkedIds) => throw new NotSupportedException();
    }
}
