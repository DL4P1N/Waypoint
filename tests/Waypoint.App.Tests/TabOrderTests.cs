using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Waypoint.App;

namespace Waypoint.App.Tests;

/// <summary>Tab in the main window goes only to things that do something, in the order they're read.</summary>
public sealed class TabOrderTests
{
    [Fact]
    public void Plain_lists_are_never_a_Tab_stop_themselves() => Ui.Run(ui =>
    {
        var window = new ItemsWindow(ui.Database, _ => { }, () => { }, _ => { })
        {
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
        };
        window.Show();
        ui.Pump();

        Assert.False(UiTest.Named<ItemsControl>(window, "Chat").Focusable);
        Assert.False(UiTest.Named<ItemsControl>(window, "Examples").Focusable);
        Assert.All(UiTest.Descendants<ItemsControl>(window).Where(list => list.GetType() == typeof(ItemsControl)), list => Assert.False(list.Focusable));

        // The examples themselves are still reached.
        Assert.True(UiTest.Descendants<Button>(UiTest.Named<ItemsControl>(window, "Examples")).First().Focusable);
        window.Close();
    });

    [Fact]
    public void Close_window_is_left_to_Alt_F4_so_Tab_goes_from_Ask_round_to_the_pages() => Ui.Run(ui =>
    {
        var window = new ItemsWindow(ui.Database, _ => { }, () => { }, _ => { })
        {
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
        };
        window.Show();
        ui.Pump();

        var close = UiTest.Descendants<Button>(window).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Close window");
        Assert.False(close.IsTabStop);
        Assert.Equal("Alt+F4", System.Windows.Automation.AutomationProperties.GetAcceleratorKey(close));
        window.Close();
    });

    [Fact]
    public void A_columns_search_comes_before_its_plus_button() => Ui.Run(ui =>
    {
        var column = ui.Host(new NestedColumn { AddLabel = "New list", SearchHint = "Search lists" });
        var search = UiTest.Named<TextBox>(column, "SearchBox");
        var add = UiTest.Named<Button>(column, "AddButton");
        ui.Focus(search);

        search.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        ui.Pump();

        Assert.True(add.IsKeyboardFocused);
    });
}
