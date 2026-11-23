using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.App.Tests;

public sealed class HistoryViewTests
{
    private static HistoryView Make(UiTest ui)
    {
        var view = new HistoryView();
        view.Connect(ui.Database, () => { });
        ui.Host(view);
        view.Refresh();
        ui.Pump();
        return view;
    }

    private static void Pick(HistoryView view, UiTest ui, string key)
    {
        var column = UiTest.Named<ListBox>(UiTest.Named<NestedColumn>(view, "Column"), "Entries");
        column.SelectedItem = column.Items.Cast<ColumnEntry>().Single(entry => entry.Key == key);
        ui.Pump();
    }

    [Fact]
    public void A_rows_box_says_unchecking_puts_it_back_on_to_do() => Ui.Run(ui =>
    {
        var item = ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, "return books", DateOnly.FromDateTime(DateTime.Now)), DateTime.Now).Item!;
        ui.Database.SetDone(item, true);
        var view = Make(ui);
        var entries = UiTest.Named<ItemsControl>(view, "Entries");
        entries.UpdateLayout();

        var box = UiTest.Descendants<CheckBox>(entries).Single();
        Assert.Equal("Uncheck to put it back on To do", box.ToolTip);
    });

    [Fact]
    public void Empty_views_say_what_would_show_there_and_a_search_says_where_else_to_look() => Ui.Run(ui =>
    {
        var view = Make(ui);
        var empty = UiTest.Named<TextBlock>(view, "Empty");
        Assert.Equal(Visibility.Visible, empty.Visibility);
        Assert.Equal("Nothing here yet. Things you finish or cancel show up here.", empty.Text);

        Pick(view, ui, "Canceled");
        Assert.Equal("Things you cancel show up here.", empty.Text);

        var search = UiTest.Named<TextBox>(UiTest.Named<NestedColumn>(view, "Column"), "SearchBox");
        search.Text = "dent";
        ui.Pump();
        search.Text = "dentist";
        ui.Pump();
        Assert.Equal("Nothing in History matches \"dentist\". Open things are on To do, and Ctrl+K looks through every page.", empty.Text);
    });

    [Fact]
    public void A_row_that_cant_be_put_back_still_has_a_named_keyboard_stop_whose_menu_and_delete_key_work() => Ui.Run(ui =>
    {
        // Finished, then put back: the Reopened row can't itself be put back, so it has no box.
        var item = ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, "return books", DateOnly.FromDateTime(DateTime.Now)), DateTime.Now).Item!;
        ui.Database.SetDone(item, true);
        ui.Database.SetDone(item, false);
        var view = Make(ui);
        Pick(view, ui, "Reopened");
        var entries = UiTest.Named<ItemsControl>(view, "Entries");
        entries.UpdateLayout();
        Assert.DoesNotContain(UiTest.Descendants<CheckBox>(entries), box => box.IsVisible);

        var more = UiTest.Descendants<Button>(entries).Single();
        Assert.True(more.Focusable && more.IsTabStop);
        Assert.StartsWith("More for return books, ", AutomationProperties.GetName(more), StringComparison.Ordinal);

        more.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, more));
        ui.Pump();
        Assert.Equal(["Delete", "Copy title"], UiTest.Labels(ui.Menus[^1]));
        ui.Close(ui.Menus[^1]);

        ui.Focus(more);
        Assert.True(ui.Press(more, Key.Delete));
        Assert.Empty(ui.Database.SearchHistory("return books", 10, HistoryFilter.Reopened));
        Assert.Contains("Deleted return books", UiTest.Descendants<TextBlock>(UiTest.Named<UndoBar>(view, "Bar")).Select(text => text.Text));
    });

    [Fact]
    public void A_row_that_can_be_put_back_offers_reopen_on_its_more_button() => Ui.Run(ui =>
    {
        var item = ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, "return books", DateOnly.FromDateTime(DateTime.Now)), DateTime.Now).Item!;
        ui.Database.SetDone(item, true);
        var view = Make(ui);
        var entries = UiTest.Named<ItemsControl>(view, "Entries");
        entries.UpdateLayout();

        var more = UiTest.Descendants<Button>(entries).Single();
        more.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, more));
        ui.Pump();

        Assert.Equal(["Reopen", "Delete", "Copy title"], UiTest.Labels(ui.Menus[^1]));
    });
}
