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
}
