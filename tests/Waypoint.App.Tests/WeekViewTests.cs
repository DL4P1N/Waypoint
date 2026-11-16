using System.Windows;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Capture;

namespace Waypoint.App.Tests;

public sealed class WeekViewTests
{
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    [Fact]
    public void The_no_date_line_opens_the_list_where_those_items_are() => Ui.Run(ui =>
    {
        ui.Database.CompleteCapture(ui.Database.AddCapture("laundry"), [new SortedItem(ItemKind.Task, "laundry", null, null)]);
        var view = new TodayView();
        view.Connect(ui.Database, () => { }, _ => { }, _ => { }, () => { });
        ui.Host(view);
        view.Refresh(null);
        ui.Pump();

        Click(UiTest.Named<Button>(view, "ViewToggle"));
        var week = UiTest.Named<WeekView>(view, "WeekPage");
        var line = UiTest.Named<Button>(week, "UndatedLine");
        Assert.Equal(Visibility.Visible, week.Visibility);
        Assert.Equal(Visibility.Visible, line.Visibility);
        Assert.Equal("1 item with no date is in the list.", UiTest.Named<TextBlock>(week, "Undated").Text);

        Click(line);
        ui.Pump();

        Assert.Equal(Visibility.Collapsed, week.Visibility);
        Assert.Equal(Visibility.Visible, UiTest.Named<ScrollViewer>(view, "ItemsScroll").Visibility);
        Assert.Contains(UiTest.Descendants<TextBlock>(view), text => text.Text == "laundry" && text.IsVisible);
    });

    [Fact]
    public void Blocks_are_reached_from_the_keyboard_read_out_by_name_and_open_their_menu_on_Enter() => Ui.Run(ui =>
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        ui.Database.CompleteCapture(ui.Database.AddCapture("paper"), [new SortedItem(ItemKind.Deadline, "Paper two", today, null, new TimeOnly(23, 59))]);
        var view = new TodayView();
        view.Connect(ui.Database, () => { }, _ => { }, _ => { }, () => { });
        ui.Host(view);
        view.Refresh(null);
        ui.Pump();
        Click(UiTest.Named<Button>(view, "ViewToggle"));
        ui.Pump();
        var week = UiTest.Named<WeekView>(view, "WeekPage");

        var block = UiTest.Descendants<Border>(week).Single(border => System.Windows.Automation.AutomationProperties.GetName(border).StartsWith("Paper two", StringComparison.Ordinal));
        Assert.True(block.Focusable);
        Assert.Equal("Paper two, 11:59 PM", System.Windows.Automation.AutomationProperties.GetName(block));
        Assert.Same(Application.Current.Resources["FocusRing"], block.FocusVisualStyle);

        ui.Focus(block);
        var before = ui.Menus.Count;
        Assert.True(ui.Press(block, System.Windows.Input.Key.Enter));
        Assert.Equal(before + 1, ui.Menus.Count);
        Assert.NotNull(ui.RightClick(block));

        var labels = UiTest.Descendants<Button>(week).Select(button => button.Content as string).ToList();
        Assert.Contains("Previous week", labels);
        Assert.Contains("Next week", labels);
        Assert.DoesNotContain("Back", labels);
    });

    [Fact]
    public void The_week_starts_on_Sunday() => Ui.Run(ui =>
    {
        var week = new WeekView();
        Assert.Equal(DayOfWeek.Sunday, week.Start.DayOfWeek);
        Assert.True(week.Start <= DateOnly.FromDateTime(DateTime.Now) && DateOnly.FromDateTime(DateTime.Now) < week.Start.AddDays(7));
    });

    [Fact]
    public void A_free_block_drops_the_word_when_its_column_is_narrow() => Ui.Run(ui =>
    {
        var typeface = new System.Windows.Media.Typeface("Segoe UI");
        var length = TimeSpan.FromMinutes(100);

        Assert.Equal("Free 1h 40m", WeekView.FreeLabel(length, 200, typeface, 11, 1));
        Assert.Equal("1h 40m", WeekView.FreeLabel(length, 50, typeface, 11, 1));
    });

    [Fact]
    public void A_dated_item_on_top_shows_its_time_under_the_title_and_the_window_hears_of_the_switch() => Ui.Run(ui =>
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        ui.Database.CompleteCapture(ui.Database.AddCapture("paper"), [new SortedItem(ItemKind.Deadline, "Paper two", today, null, new TimeOnly(23, 59))]);
        var view = new TodayView();
        view.Connect(ui.Database, () => { }, _ => { }, _ => { }, () => { });
        var switches = 0;
        view.WeekShownChanged += () => switches++;
        ui.Host(view);
        view.Refresh(null);
        ui.Pump();

        Click(UiTest.Named<Button>(view, "ViewToggle"));
        ui.Pump();

        Assert.True(view.WeekShown);
        Assert.Equal(1, switches);
        var week = UiTest.Named<WeekView>(view, "WeekPage");
        Assert.Contains(UiTest.Descendants<TextBlock>(week), text => text.Text == "Paper two");
        Assert.Contains(UiTest.Descendants<TextBlock>(week), text => text.Text == "11:59 PM");
    });

    [Fact]
    public void All_seven_days_fit_in_the_to_do_page_of_the_smallest_window() => Ui.Run(ui =>
    {
        // At the window's smallest width, 1040, To do beside the side menu and Ask is 650 wide, and the week gets about 440.
        var view = new TodayView { Width = 650 };
        view.Connect(ui.Database, () => { }, _ => { }, _ => { }, () => { });
        ui.Host(view);
        view.Refresh(null);
        ui.Pump();

        Click(UiTest.Named<Button>(view, "ViewToggle"));
        ui.Pump();

        var week = UiTest.Named<WeekView>(view, "WeekPage");
        var header = UiTest.Named<Grid>(week, "Header");
        var right = header.TranslatePoint(new Point(header.ColumnDefinitions.Sum(column => column.ActualWidth), 0), week).X;
        Assert.Equal(8, header.ColumnDefinitions.Count);
        Assert.True(right <= week.ActualWidth, $"The last day ends at {right:0}, past the page's {week.ActualWidth:0}");
    });
}
