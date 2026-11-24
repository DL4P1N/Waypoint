using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using Waypoint.App;

namespace Waypoint.App.Tests;

/// <summary>Ask can be put to the side for a narrower window, and stays that way next time.</summary>
public sealed class AskHideTests
{
    private static ItemsWindow Shown(UiTest ui)
    {
        var window = new ItemsWindow(ui.Database, _ => { }, () => { }, _ => { })
        {
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
        };
        window.Show();
        ui.Pump();
        return window;
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    [Fact]
    public void Hiding_Ask_leaves_a_strip_lets_the_window_be_narrower_and_is_remembered() => Ui.Run(ui =>
    {
        var window = Shown(ui);
        Assert.Equal(ItemsWindow.AskOpenMinWidth, window.MinWidth);

        Click(UiTest.Named<Button>(window, "AskHide"));
        ui.Pump();

        Assert.False(window.AskShown);
        Assert.Equal(Visibility.Collapsed, UiTest.Named<Border>(window, "AskColumn").Visibility);
        Assert.Equal(Visibility.Visible, UiTest.Named<Border>(window, "AskStrip").Visibility);
        Assert.Equal(780, window.MinWidth);
        Assert.True(UiTest.Named<Button>(window, "AskShow").IsKeyboardFocused);
        window.Close();

        // The next window opens with Ask still to the side.
        var again = Shown(ui);
        Assert.False(again.AskShown);
        Assert.Equal(780, again.MinWidth);

        Click(UiTest.Named<Button>(again, "AskShow"));
        ui.Pump();
        Assert.True(again.AskShown);
        Assert.Equal(ItemsWindow.AskOpenMinWidth, again.MinWidth);
        Assert.True(UiTest.Named<TextBox>(again, "Input").IsKeyboardFocused);
        again.Close();

        var third = Shown(ui);
        Assert.True(third.AskShown);
        third.Close();
    });

    [Fact]
    public void A_reply_while_Ask_is_to_the_side_marks_its_strip_until_it_opens() => Ui.Run(ui =>
    {
        var window = Shown(ui);
        window.ShowAsk(false);
        ui.Pump();
        var news = UiTest.Named<Ellipse>(window, "AskNews");
        var strip = UiTest.Named<Button>(window, "AskShow");
        Assert.Equal(Visibility.Collapsed, news.Visibility);

        window.ShowReply("Added Call the bank for Friday.");
        ui.Pump();

        Assert.Equal(Visibility.Visible, news.Visibility);
        Assert.Contains("new reply", System.Windows.Automation.AutomationProperties.GetName(strip), StringComparison.Ordinal);

        window.ShowAsk(true);
        ui.Pump();
        Assert.Equal(Visibility.Collapsed, news.Visibility);
        Assert.Equal("Show Ask", System.Windows.Automation.AutomationProperties.GetName(strip));
        window.Close();
    });

    [Fact]
    public void Opening_Ask_in_a_window_at_the_right_edge_moves_it_left_to_stay_on_screen()
    {
        // 800 wide at the right edge of a 1920 screen grows to 1040, so it moves left by 240.
        Assert.Equal(880, ItemsWindow.LeftOnScreen(1120, 1040, 0, 1920));

        // Room already: it stays put. Wider than the screen: it keeps to the screen's left edge.
        Assert.Equal(100, ItemsWindow.LeftOnScreen(100, 1040, 0, 1920));
        Assert.Equal(1920, ItemsWindow.LeftOnScreen(2400, 1040, 1920, 2880));
    }

    [Fact]
    public void The_new_reply_dot_takes_the_strip_buttons_text_color_so_it_shows_over_the_hover_fill() => Ui.Run(ui =>
    {
        var window = Shown(ui);
        window.ShowAsk(false);
        window.ShowReply("Added Call the bank for Friday.");
        ui.Pump();

        var news = UiTest.Named<Ellipse>(window, "AskNews");
        var strip = UiTest.Named<Button>(window, "AskShow");
        Assert.Equal(((System.Windows.Media.SolidColorBrush)strip.Foreground).Color, ((System.Windows.Media.SolidColorBrush)news.Fill).Color);
        window.ShowAsk(true);
        window.Close();
    });
}
