using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Remote;

namespace Waypoint.App.Tests;

/// <summary>The side menu, now that the lists are in Notes, and everything that used to open the Lists page.</summary>
public sealed class SideMenuTests
{
    private static ItemsWindow Shown(UiTest ui, bool lastPage = false)
    {
        var window = new ItemsWindow(ui.Database, _ => { }, () => { }, _ => { })
        {
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
        };
        if (lastPage)
        {
            window.ShowLastPage();
        }
        else
        {
            window.ShowList();
        }

        ui.Pump();
        return window;
    }

    private static ListsView Lists(ItemsWindow window) => UiTest.Named<ListsView>(window.NotesPage, "ListsPanel");

    [Fact]
    public void The_menu_has_no_lists_page_and_ctrl_numbers_follow_it() => Ui.Run(ui =>
    {
        var window = Shown(ui);
        var menu = UiTest.Named<ListBox>(window, "Menu");
        var items = menu.Items.Cast<ListBoxItem>().ToList();

        Assert.Equal(["To do", "Notes", "Budget", "History", "Settings"], items.Select(item => (string)item.Content));
        Assert.Equal(["Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4", "Ctrl+5"], items.Select(AutomationProperties.GetAcceleratorKey));
        Assert.All(items, item => Assert.EndsWith($"({AutomationProperties.GetAcceleratorKey(item)})", (string)item.ToolTip, StringComparison.Ordinal));

        // The second entry is Notes, which Ctrl+2 picks.
        menu.SelectedIndex = 1;
        ui.Pump();
        Assert.Equal(Visibility.Visible, window.NotesPage.Visibility);
        Assert.Equal("Notes", ui.Database.GetPreference("last_page"));
        window.Hide();
    });

    [Fact]
    public void A_found_list_entry_opens_notes_on_that_list() => Ui.Run(ui =>
    {
        ui.Database.AddToList("Errands", ["dentist forms", "post office"], 0, DateTime.Now);
        var window = Shown(ui);
        var entry = ui.Database.GetListEntries("Errands").Single(found => found.Text == "post office");

        UiTest.Call(window, "ShowFound", new FoundThing(FoundPage.Lists, entry.Text, "Notes, Errands list") { Entry = entry });
        ui.Pump();

        Assert.Equal(Visibility.Visible, window.NotesPage.Visibility);
        Assert.Equal(Visibility.Visible, Lists(window).Visibility);
        Assert.Equal("Errands", UiTest.Named<TextBlock>(Lists(window), "ListTitle").Text);
        Assert.Equal("list:Errands", UiTest.Named<NestedColumn>(window.NotesPage, "Column").SelectedKey);
        window.Hide();
    });

    [Fact]
    public void A_replys_shopping_link_opens_notes_on_shopping() => Ui.Run(ui =>
    {
        ui.Database.AddListEntry(WaypointDatabase.ShoppingList, "milk", DateTime.Now);
        var window = Shown(ui);

        UiTest.Call(window, "Reveal", new StoredItem(1, ItemKind.Shopping, "milk", null, null, false));
        ui.Pump();

        Assert.Equal(Visibility.Visible, window.NotesPage.Visibility);
        Assert.Equal(Visibility.Visible, Lists(window).Visibility);
        Assert.Equal(WaypointDatabase.ShoppingList, UiTest.Named<TextBlock>(Lists(window), "ListTitle").Text);
        window.Hide();
    });

    [Fact]
    public void Left_on_the_old_lists_page_it_opens_notes_on_the_list_that_was_open() => Ui.Run(ui =>
    {
        ui.Database.AddToList("Packing", ["charger"], 0, DateTime.Now);
        ui.Database.SetPreference("last_page", "Lists");
        ui.Database.SetPreference("lists_open", "Packing");

        var window = Shown(ui, lastPage: true);

        Assert.Equal(Visibility.Visible, window.NotesPage.Visibility);
        Assert.Equal(Visibility.Visible, Lists(window).Visibility);
        Assert.Equal("Packing", UiTest.Named<TextBlock>(Lists(window), "ListTitle").Text);
        Assert.Equal("Notes", ui.Database.GetPreference("last_page"));
        window.Hide();
    });
}
