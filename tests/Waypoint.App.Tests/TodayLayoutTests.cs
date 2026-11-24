using System.Windows;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.App.Tests;

/// <summary>How To do fits a short or narrow window, and what Tab stops on.</summary>
public sealed class TodayLayoutTests
{
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    private static TodayView Make(UiTest ui)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, "Email advisor", today.AddDays(1)), DateTime.Now);
        var view = new TodayView();
        view.Connect(ui.Database, () => { }, _ => { }, _ => { }, () => { });
        ui.Host(view);
        view.Refresh(null);
        ui.Pump();
        return view;
    }

    private static bool Within(DependencyObject element, DependencyObject container)
    {
        for (var at = element; at is not null; at = System.Windows.Media.VisualTreeHelper.GetParent(at))
        {
            if (ReferenceEquals(at, container))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void In_the_list_the_cards_scroll_away_with_the_rows_and_in_the_week_they_keep_to_a_share_of_the_height() => Ui.Run(ui =>
    {
        var view = Make(ui);
        var cards = UiTest.Named<StackPanel>(view, "Cards");
        var list = UiTest.Named<ScrollViewer>(view, "ItemsScroll");
        var suggestion = UiTest.Named<Border>(view, "SuggestionCard");
        Assert.Equal(Visibility.Visible, suggestion.Visibility);
        Assert.True(Within(cards, list));

        view.Height = 480;
        Click(UiTest.Named<Button>(view, "ViewToggle"));
        ui.Pump();
        var strip = UiTest.Named<ScrollViewer>(view, "WeekCards");
        Assert.Equal(Visibility.Visible, strip.Visibility);
        Assert.True(Within(cards, strip));
        Assert.True(strip.ActualHeight <= Math.Max(96, view.ActualHeight * TodayView.WeekCardsShare) + 0.5);

        Click(UiTest.Named<Button>(view, "ViewToggle"));
        ui.Pump();
        Assert.True(Within(cards, list));
        Assert.Equal(Visibility.Collapsed, strip.Visibility);
    });

    [Fact]
    public void In_a_narrow_week_the_day_names_wrap_rather_than_being_cut() => Ui.Run(ui =>
    {
        var view = Make(ui);
        Click(UiTest.Named<Button>(view, "ViewToggle"));
        view.Width = 560;
        ui.Pump();

        var header = UiTest.Named<Grid>(UiTest.Named<WeekView>(view, "WeekPage"), "Header");
        var names = header.Children.OfType<TextBlock>().ToList();
        Assert.Equal(7, names.Count);
        foreach (var name in names)
        {
            Assert.Equal(TextWrapping.Wrap, name.TextWrapping);
            var column = header.ColumnDefinitions[Grid.GetColumn(name)].ActualWidth;
            Assert.True(name.ActualWidth + name.Margin.Left + name.Margin.Right <= column + 0.5, $"{name.Text} is wider than its day");
        }
    });

    [Fact]
    public void Tab_never_stops_on_the_lists_that_only_hold_rows() => Ui.Run(ui =>
    {
        var view = Make(ui);
        // The filter column is a control of its own, with its own lists.
        var column = UiTest.Named<NestedColumn>(view, "Column");
        var lists = UiTest.Descendants<ItemsControl>(view).Where(control => control.GetType() == typeof(ItemsControl) && !Within(control, column)).ToList();
        Assert.NotEmpty(lists);
        Assert.All(lists, list => Assert.False(list.Focusable, $"{list.Name} takes focus"));
    });
}
