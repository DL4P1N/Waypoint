using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;
using Waypoint.Core.Remote;

namespace Waypoint.App.Tests;

public sealed class FindViewTests
{
    private static FindSources Sources(WaypointDatabase database) => new(
        new LocalToDoData(database),
        new LocalListsData(database),
        new LocalNotesData(database),
        new LocalHistoryData(database),
        new LocalBudgetData(database));

    private static (FindView View, TextBox Box, ListBox Results, TextBlock Hint) Make(UiTest ui)
    {
        var view = new FindView();
        view.Connect(Sources(ui.Database));
        ui.Host(view);
        ui.Pump();
        return (view, UiTest.Named<TextBox>(view, "SearchBox"), UiTest.Named<ListBox>(view, "Results"), UiTest.Named<TextBlock>(view, "Hint"));
    }

    [Fact]
    public void Typing_finds_things_on_every_page_and_enter_picks_the_first() => Ui.Run(ui =>
    {
        ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, "Call the dentist", DateOnly.FromDateTime(DateTime.Now)), DateTime.Now);
        ui.Database.AddToList("Errands", ["dentist forms"], 0, DateTime.Now);
        var (view, box, results, hint) = Make(ui);
        Assert.Equal(FindView.StartHint, hint.Text);
        Assert.Equal("Type to look through To do, Notes, lists, History, and Budget at once.", hint.Text);

        FoundThing? chosen = null;
        view.Chosen += thing => chosen = thing;
        box.Text = "dentist";
        view.Search();
        ui.Pump();

        Assert.Equal(["Call the dentist", "dentist forms"], results.Items.Cast<FoundThing>().Select(thing => thing.Title));
        Assert.Equal("2 found", hint.Text);
        Assert.Equal(System.Windows.Automation.AutomationLiveSetting.Polite, System.Windows.Automation.AutomationProperties.GetLiveSetting(hint));

        ui.Focus(box);
        Assert.True(ui.Press(box, Key.Enter));
        Assert.Equal("Call the dentist", chosen!.Title);
        Assert.NotNull(chosen.Item);
    });

    [Fact]
    public void Nothing_found_says_so_and_escape_closes() => Ui.Run(ui =>
    {
        var (view, box, results, hint) = Make(ui);
        var closed = false;
        view.Closed += () => closed = true;

        box.Text = "zebra";
        view.Search();
        ui.Pump();
        Assert.Empty(results.Items);
        Assert.Equal("Nothing on any page matches \"zebra\".", hint.Text);

        // This PC's search is made straight away, so Searching... is never on its way to show.
        Assert.False(UiTest.Field<System.Windows.Threading.DispatcherTimer>(view, "_searching")!.IsEnabled);

        ui.Focus(box);
        Assert.True(ui.Press(box, Key.Escape));
        Assert.True(closed);
    });

    [Fact]
    public void A_found_list_entry_opens_its_list_in_notes_with_no_search_hiding_it() => Ui.Run(ui =>
    {
        ui.Database.AddListEntry(WaypointDatabase.ShoppingList, "eggs", DateTime.Now);
        ui.Database.AddToList("Errands", ["dentist forms", "post office"], 0, DateTime.Now);
        var notes = new NotesView();
        notes.Connect(ui.Database);
        ui.Host(notes);
        notes.Refresh(force: true);
        var column = UiTest.Named<NestedColumn>(notes, "Column");
        UiTest.Named<TextBox>(column, "SearchBox").Text = "eggs";
        ui.Pump();

        var entry = ui.Database.GetListEntries("Errands").Single(found => found.Text == "post office");
        notes.RevealListEntry(entry);
        ui.Pump();

        var lists = UiTest.Named<ListsView>(notes, "ListsPanel");
        Assert.Equal(Visibility.Visible, lists.Visibility);
        Assert.Equal("Errands", UiTest.Named<TextBlock>(lists, "ListTitle").Text);
        Assert.Equal("list:Errands", column.SelectedKey);
        Assert.Equal("", column.SearchText);
        var focused = Assert.IsType<CheckBox>(Keyboard.FocusedElement);
        Assert.Equal("post office", UiTest.Prop<string>(focused.DataContext, "Text"));
    });

    [Fact]
    public void A_found_history_row_shows_under_all_searched_for_its_title() => Ui.Run(ui =>
    {
        var item = ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, "Book dentist cleaning", DateOnly.FromDateTime(DateTime.Now)), DateTime.Now).Item!;
        ui.Database.SetDone(item, true);
        var other = ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, "Return books", DateOnly.FromDateTime(DateTime.Now)), DateTime.Now).Item!;
        ui.Database.SetDone(other, true);
        var history = new HistoryView();
        history.Connect(ui.Database, () => { });
        ui.Host(history);
        var column = UiTest.Named<NestedColumn>(history, "Column");
        column.Select(nameof(HistoryFilter.Canceled));
        history.Refresh();
        ui.Pump();

        var entry = ui.Database.SearchHistory("dentist", 10, HistoryFilter.All).Single();
        history.Reveal(entry);
        ui.Pump();

        Assert.Equal(nameof(HistoryFilter.All), column.SelectedKey);
        Assert.Equal("Book dentist cleaning", column.SearchText);
        var rows = UiTest.Named<ItemsControl>(history, "Entries").Items.Cast<object>().Select(row => UiTest.Prop<string>(row, "Title"));
        Assert.Equal(["Book dentist cleaning"], rows);
    });
}
