using System.Windows;
using System.Windows.Controls;
using Waypoint.App;

namespace Waypoint.App.Tests;

public sealed class UndoBarTests
{
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    [Fact]
    public void The_x_closes_the_bar_without_undoing() => Ui.Run(ui =>
    {
        var bar = ui.Host(new UndoBar());
        var undone = false;
        bar.Show("Moved 3 transactions to Groceries", () => undone = true);
        ui.Pump();

        Click(UiTest.Named<Button>(bar, "CloseButton"));
        ui.Pump();

        Assert.Equal(Visibility.Collapsed, bar.Visibility);
        Assert.False(undone);
    });

    [Fact]
    public void The_message_and_form_problems_are_read_out_when_they_appear() => Ui.Run(ui =>
    {
        var bar = ui.Host(new UndoBar());
        bar.Show("Saved");
        ui.Pump();
        Assert.Equal(System.Windows.Automation.AutomationLiveSetting.Polite, System.Windows.Automation.AutomationProperties.GetLiveSetting(UiTest.Named<TextBlock>(bar, "Message")));

        var column = new NestedColumn();
        Assert.Equal(System.Windows.Automation.AutomationLiveSetting.Polite, System.Windows.Automation.AutomationProperties.GetLiveSetting(UiTest.Named<TextBlock>(column, "Problem")));
        foreach (var form in new Window[] { new PickDateWindow(null!, "Paper", DateOnly.FromDateTime(DateTime.Now), null, asksForTime: true), new SaveSpotWindow(null!, null, _ => new Waypoint.Core.Capture.OpReply("")), new WaitingWindow(null!, DateOnly.FromDateTime(DateTime.Now), null, _ => null) })
        {
            Assert.Equal(System.Windows.Automation.AutomationLiveSetting.Polite, System.Windows.Automation.AutomationProperties.GetLiveSetting(UiTest.Named<TextBlock>(form, "Problem")));
            form.Close();
        }
    });

    [Fact]
    public void The_x_shows_on_messages_with_no_undo() => Ui.Run(ui =>
    {
        var bar = ui.Host(new UndoBar());
        bar.Show("Saved");
        ui.Pump();

        Assert.Equal(Visibility.Visible, UiTest.Named<Button>(bar, "CloseButton").Visibility);
        Assert.Equal(Visibility.Collapsed, UiTest.Named<Button>(bar, "UndoButton").Visibility);
    });

    /// <summary>A page with the bar and one button, which may show a message of its own when pressed.</summary>
    private static (UserControl Page, UndoBar Bar, Button Button) Page(UiTest ui, Action<UndoBar>? onClick = null)
    {
        var bar = new UndoBar();
        var button = new Button { Content = "Do something" };
        button.Click += (_, _) => onClick?.Invoke(bar);
        var page = ui.Host(new UserControl { Content = new StackPanel { Children = { bar, button } } });
        ui.Pump();
        return (page, bar, button);
    }

    /// <summary>A mouse press and release on the button, raised in process, with the click in between as a real one has.</summary>
    private static void Press(UIElement target)
    {
        target.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
        if (target is Button button)
        {
            Click(button);
        }

        target.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseUpEvent });
    }

    [Fact]
    public void An_old_message_goes_with_the_next_thing_done_on_the_page() => Ui.Run(ui =>
    {
        var (_, bar, button) = Page(ui);
        bar.Show("Saved");

        Press(button);
        ui.Pump();

        Assert.Equal(Visibility.Collapsed, bar.Visibility);
    });

    [Fact]
    public void An_undo_stays_through_clicks_that_change_nothing() => Ui.Run(ui =>
    {
        var (_, bar, button) = Page(ui);
        bar.Show("Deleted laundry", () => { });

        Press(button);
        ui.Pump();
        Press(button);
        ui.Pump();

        Assert.Equal(Visibility.Visible, bar.Visibility);
        Assert.Equal(Visibility.Visible, UiTest.Named<Button>(bar, "UndoButton").Visibility);
    });

    [Fact]
    public void An_undo_goes_once_something_else_changes_data() => Ui.Run(ui =>
    {
        var (_, bar, button) = Page(ui, shown => shown.Changed());
        bar.Show("Deleted laundry", () => { });
        ui.Pump();

        Press(button);
        ui.Pump();

        Assert.Equal(Visibility.Collapsed, bar.Visibility);
    });

    [Fact]
    public void A_change_that_shows_its_own_undo_keeps_it() => Ui.Run(ui =>
    {
        // Pages report the change after showing what it did, so the new message mustn't be taken by its own change.
        var (_, bar, button) = Page(ui, shown =>
        {
            shown.Show("Deleted milk", () => { });
            shown.Changed();
        });
        bar.Show("Deleted laundry", () => { });
        ui.Pump();

        Press(button);
        ui.Pump();

        Assert.Equal(Visibility.Visible, bar.Visibility);
        Assert.Equal("Deleted milk", UiTest.Named<TextBlock>(bar, "Message").Text);

        // Its own change is over once the action is done, so the next one takes it.
        bar.Changed();
        Assert.Equal(Visibility.Collapsed, bar.Visibility);
    });

    [Fact]
    public void Leaving_the_page_clears_an_undo_too() => Ui.Run(ui =>
    {
        var (page, bar, _) = Page(ui);
        bar.Show("Deleted laundry", () => { });

        page.Visibility = Visibility.Collapsed;
        ui.Pump();

        Assert.Equal(Visibility.Collapsed, bar.Visibility);
    });

    [Fact]
    public void A_new_action_with_its_own_message_replaces_the_old_one() => Ui.Run(ui =>
    {
        var (_, bar, button) = Page(ui, shown => shown.Show("Added milk"));
        bar.Show("Deleted laundry");

        Press(button);
        ui.Pump();

        Assert.Equal(Visibility.Visible, bar.Visibility);
        Assert.Equal("Added milk", UiTest.Named<TextBlock>(bar, "Message").Text);
    });

    [Fact]
    public void Leaving_the_page_clears_its_message() => Ui.Run(ui =>
    {
        var (page, bar, _) = Page(ui);
        bar.Show("Saved");

        page.Visibility = Visibility.Collapsed;
        ui.Pump();
        page.Visibility = Visibility.Visible;
        ui.Pump();

        Assert.Equal(Visibility.Collapsed, bar.Visibility);
    });

    [Fact]
    public void A_message_goes_once_the_thing_it_is_about_changes() => Ui.Run(ui =>
    {
        var bar = ui.Host(new UndoBar());
        bar.Show("Added laundry", about: (1, 5L));
        ui.Pump();

        bar.Forget((1, 6L));
        Assert.Equal(Visibility.Visible, bar.Visibility);

        bar.Forget((1, 5L));
        Assert.Equal(Visibility.Collapsed, bar.Visibility);

        // A message about nothing in particular stays until it's replaced or closed.
        bar.Show("Saved");
        bar.Forget((1, 5L));
        Assert.Equal(Visibility.Visible, bar.Visibility);
    });
}
