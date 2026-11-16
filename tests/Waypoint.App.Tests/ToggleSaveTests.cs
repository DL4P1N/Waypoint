using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.App.Tests;

/// <summary>
/// A box toggled through UI Automation, the way a screen reader or voice control does it, never raises Click,
/// so each of these must save on the change itself.
/// </summary>
public sealed class ToggleSaveTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private static void Toggle(CheckBox box)
    {
        var peer = (IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(box).GetPattern(PatternInterface.Toggle);
        peer.Toggle();
    }

    private static CheckBox Box(System.Windows.FrameworkElement view, string name) =>
        UiTest.Descendants<CheckBox>(view).First(box => System.Windows.Automation.AutomationProperties.GetName(box) == name);

    [Fact]
    public void A_to_do_row_toggled_by_automation_is_saved() => Ui.Run(ui =>
    {
        ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, "Email advisor", Today.AddDays(1)), DateTime.Now);
        var view = new TodayView();
        view.Connect(ui.Database, () => { }, _ => { }, _ => { }, () => { });
        ui.Host(view);
        view.Refresh(null);
        ui.Pump();

        // Building the rows fills each box from what's saved, which mustn't count as a change.
        Assert.Contains(ui.Database.GetOpenItems(), item => item.Title == "Email advisor" && !item.Done);

        Toggle(Box(view, "Email advisor"));
        ui.Pump();

        Assert.DoesNotContain(ui.Database.GetOpenItems(), item => item.Title == "Email advisor" && !item.Done);
    });

    [Fact]
    public void Hide_finished_toggled_by_automation_is_saved() => Ui.Run(ui =>
    {
        var view = new TodayView();
        view.Connect(ui.Database, () => { }, _ => { }, _ => { }, () => { });
        ui.Host(view);
        view.Refresh(null);
        ui.Pump();

        Toggle(UiTest.Named<CheckBox>(view, "HideFinished"));

        Assert.Equal("1", ui.Database.GetPreference("todo_hide_finished"));
    });

    [Fact]
    public void A_list_entry_toggled_by_automation_is_saved() => Ui.Run(ui =>
    {
        ui.Database.AddListEntry(WaypointDatabase.ShoppingList, "eggs", DateTime.Now);
        var view = new ListsView();
        view.Connect(ui.Database);
        ui.Host(view);
        view.Refresh(force: true);
        ui.Pump();

        Toggle(Box(view, "eggs"));
        ui.Pump();

        Assert.DoesNotContain(ui.Database.GetListEntries(WaypointDatabase.ShoppingList), entry => entry.Text == "eggs" && !entry.Done);
    });

    [Fact]
    public void A_history_row_untoggled_by_automation_is_reopened() => Ui.Run(ui =>
    {
        var added = ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, "return books", Today), DateTime.Now).Item!;
        ui.Database.SetDone(added, true);
        var view = new HistoryView();
        view.Connect(ui.Database, () => { });
        ui.Host(view);
        view.Refresh();
        ui.Pump();
        UiTest.Named<ItemsControl>(view, "Entries").UpdateLayout();

        var box = UiTest.Descendants<CheckBox>(view).First(box => box.IsChecked == true && box.DataContext is { } row && UiTest.Prop<string>(row, "Title") == "return books");
        Toggle(box);
        ui.Pump();

        Assert.Contains(ui.Database.GetOpenItems(), item => item.Title == "return books" && !item.Done);
    });
}
