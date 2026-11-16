using System.Windows;
using System.Windows.Controls;
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
}
