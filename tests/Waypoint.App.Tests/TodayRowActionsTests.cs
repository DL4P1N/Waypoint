using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.App.Tests;

/// <summary>What a To do row does when it's finished, changed, or deleted by click or key: each says so on the page's bar, with Undo where there's a way back.</summary>
public sealed class TodayRowActionsTests
{
    private static DateOnly InDays(int days) => DateOnly.FromDateTime(DateTime.Now).AddDays(days);

    private static StoredItem Add(UiTest ui, string title, int inDays) =>
        ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, title, InDays(inDays)), DateTime.Now).Item!;

    private static (TodayView View, List<string> Replies) Make(UiTest ui)
    {
        var replies = new List<string>();
        var view = new TodayView();
        view.Connect(ui.Database, () => { }, _ => { }, replies.Add, () => { });
        ui.Host(view);
        view.Refresh(null);
        ui.Pump();
        return (view, replies);
    }

    private static CheckBox Row(TodayView view, string title)
    {
        view.UpdateLayout();
        return UiTest.Descendants<CheckBox>(UiTest.Named<ScrollViewer>(view, "ItemsScroll"))
            .Single(box => box.DataContext is { } row && row.GetType().Name == "ItemRow" && UiTest.Prop<string>(row, "Title") == title);
    }

    private static UndoBar Bar(TodayView view) => UiTest.Named<UndoBar>(view, "Bar");

    private static string BarText(TodayView view) => UiTest.Named<TextBlock>(Bar(view), "Message").Text;

    private static Button UndoButton(TodayView view) => UiTest.Named<Button>(Bar(view), "UndoButton");

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    private static StoredItem Stored(UiTest ui, long id) => ui.Database.GetOpenItems().Single(item => item.Id == id);

    [Fact]
    public void Checking_a_box_says_so_with_undo_even_when_done_rows_are_hidden() => Ui.Run(ui =>
    {
        var task = Add(ui, "Return library books", 1);
        var (view, _) = Make(ui);
        UiTest.Named<CheckBox>(view, "HideFinished").IsChecked = true;
        ui.Pump();

        Row(view, "Return library books").IsChecked = true;
        ui.Pump();

        Assert.DoesNotContain(ui.Database.GetOpenItems(), item => item.Id == task.Id);
        Assert.Equal(Visibility.Visible, Bar(view).Visibility);
        Assert.Equal("Marked Return library books done", BarText(view));
        Assert.Equal(Visibility.Visible, UndoButton(view).Visibility);

        Click(UndoButton(view));
        ui.Pump();
        Assert.False(Stored(ui, task.Id).Done);
        Assert.False(Row(view, "Return library books").IsChecked);
    });

    [Fact]
    public void A_click_on_a_rows_words_picks_it_and_only_the_box_marks_it_done() => Ui.Run(ui =>
    {
        var task = Add(ui, "Water the ferns", 1);
        var (view, _) = Make(ui);
        var row = Row(view, "Water the ferns");
        var words = UiTest.Descendants<TextBlock>(row).First(block => block.Text == "Water the ferns");

        var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent };
        words.RaiseEvent(press);
        ui.Pump();

        Assert.True(press.Handled);
        Assert.False(Stored(ui, task.Id).Done);
        Assert.True(UiTest.Prop<bool>(Row(view, "Water the ferns").DataContext, "IsSelected"));

        // A second click on the same words lets it go again.
        words.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
        Assert.False(UiTest.Prop<bool>(row.DataContext, "IsSelected"));

        // The box itself isn't held back.
        var onBox = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent };
        row.RaiseEvent(onBox);
        Assert.False(onBox.Handled);
    });

    [Fact]
    public void Rename_from_the_menu_says_so_on_the_bar_and_undo_puts_the_old_name_back() => Ui.Run(ui =>
    {
        var task = Add(ui, "Call the dentist", 2);
        var (view, replies) = Make(ui);
        ui.RenameAnswer = "Call the dentist about Friday";

        ui.Choose(ui.RightClick(Row(view, "Call the dentist"))!, "Rename");
        ui.Pump();

        Assert.Equal("Call the dentist about Friday", Stored(ui, task.Id).Title);
        Assert.Equal("Renamed to Call the dentist about Friday", BarText(view));
        Assert.Empty(replies);

        Click(UndoButton(view));
        ui.Pump();
        Assert.Equal("Call the dentist", Stored(ui, task.Id).Title);
    });

    [Fact]
    public void Reschedule_from_the_menu_says_so_on_the_bar_and_undo_moves_it_back() => Ui.Run(ui =>
    {
        var task = Add(ui, "Pick up the parcel", 3);
        var (view, replies) = Make(ui);

        ui.Choose(ui.RightClick(Row(view, "Pick up the parcel"))!, "Reschedule", "Tomorrow");
        ui.Pump();

        Assert.Equal(InDays(1), Stored(ui, task.Id).DueDate);
        Assert.StartsWith("Moved Pick up the parcel to ", BarText(view), StringComparison.Ordinal);
        Assert.Equal(Visibility.Visible, UndoButton(view).Visibility);
        Assert.Empty(replies);

        Click(UndoButton(view));
        ui.Pump();
        Assert.Equal(InDays(3), Stored(ui, task.Id).DueDate);
    });

    [Fact]
    public void A_move_that_cant_be_put_back_says_why_and_offers_no_undo() => Ui.Run(ui =>
    {
        var undated = new StoredItem(1, ItemKind.Task, "Sort the drawer", null, null, false);
        Assert.Contains("no date before", TodayView.MoveBack(undated, null).Why, StringComparison.Ordinal);

        var dated = undated with { DueDate = InDays(2) };
        Assert.Contains("no time before", TodayView.MoveBack(dated, new TimeOnly(9, 0)).Why, StringComparison.Ordinal);
        Assert.Equal((InDays(2), (TimeOnly?)null, (string?)null), TodayView.MoveBack(dated, null));
    });

    [Fact]
    public void Stop_reminders_says_so_on_the_bar() => Ui.Run(ui =>
    {
        Add(ui, "Renew the bus pass", 2);
        var (view, replies) = Make(ui);

        ui.Choose(ui.RightClick(Row(view, "Renew the bus pass"))!, "Stop reminders");
        ui.Pump();

        Assert.Equal("No more reminders for Renew the bus pass", BarText(view));
        Assert.Empty(replies);
    });

    [Fact]
    public void Delete_on_a_focused_row_deletes_it_with_undo() => Ui.Run(ui =>
    {
        var task = Add(ui, "Book the hall", 2);
        var (view, _) = Make(ui);
        var row = Row(view, "Book the hall");
        ui.Focus(row);

        Assert.True(ui.Press(row, Key.Delete));

        Assert.DoesNotContain(ui.Database.GetOpenItems(), item => item.Id == task.Id && !item.Done);
        Assert.Equal("Deleted Book the hall", BarText(view));
        Click(UndoButton(view));
        ui.Pump();
        Assert.False(Stored(ui, task.Id).Done);
    });

    [Fact]
    public void Each_row_has_a_more_button_that_opens_its_menu() => Ui.Run(ui =>
    {
        Add(ui, "Clean the oven", 2);
        var (view, _) = Make(ui);
        var more = UiTest.Descendants<Button>(UiTest.Named<ScrollViewer>(view, "ItemsScroll"))
            .Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "More for Clean the oven");

        // Shown while the row is pointed at or focused, and never a stop of its own for Tab.
        Assert.False(more.Focusable);
        Assert.Equal(0, more.Opacity);
        ui.Focus(Row(view, "Clean the oven"));
        Assert.Equal(1, more.Opacity);

        var before = ui.Menus.Count;
        Click(more);
        ui.Pump();
        Assert.True(ui.Menus.Count > before);
        Assert.Contains("Mark done", UiTest.Labels(ui.Menus[^1]));
        ui.Close(ui.Menus[^1]);
    });
}
