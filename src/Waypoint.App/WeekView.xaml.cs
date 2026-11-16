using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Waypoint.Core.Capture;
using Waypoint.Core.Schedule;
using Waypoint.Core.Theme;

namespace Waypoint.App;

/// <summary>
/// Sunday to Saturday in time blocks: classes, shifts, and events, with reminders and exams as short marks and
/// dated items along the top. Free time is shaded so it stands out. To do decides what goes in, so the
/// column's filter and search apply here too, and right-click on an item opens the same menu as its row.
/// </summary>
public partial class WeekView : UserControl
{
    private const double HourHeight = 44;
    private const double TimeColumn = 52;

    // Narrow enough that all seven days fit beside the filters and Ask in the smallest window, 1040 wide.
    private const double MinimumDayWidth = 44;
    private const double MinimumTextHeight = 16;

    private Action<FrameworkElement, PlacementMode, StoredItem>? _openMenu;
    private bool _scrolledOnce;

    public WeekView()
    {
        InitializeComponent();
        Start = WeekLayout.StartOf(DateOnly.FromDateTime(DateTime.Now));
    }

    /// <summary>The week moved, so the page should build it again.</summary>
    public event Action? WeekChanged;

    /// <summary>The line about items with no date was clicked, so the page should show them.</summary>
    public event Action? UndatedClicked;

    public DateOnly Start { get; private set; }

    public void Connect(Action<FrameworkElement, PlacementMode, StoredItem> openMenu) => _openMenu = openMenu;

    /// <summary>Lays the week out again. The scroll position stays where it was, except the first time, which starts near now.</summary>
    public void Show(WeekPlan plan, DateTime now, int undated)
    {
        var offset = GridScroll.VerticalOffset;
        var today = DateOnly.FromDateTime(now);
        WeekLabel.Text = WeekLayout.Label(plan.Start);
        ThisWeekButton.IsEnabled = plan.Start != WeekLayout.StartOf(today);
        Undated.Text = undated == 1 ? "1 item with no date is in the list." : $"{undated} items with no date are in the list.";
        UndatedLine.Visibility = undated > 0 ? Visibility.Visible : Visibility.Collapsed;

        ShowHeader(plan, today);
        ShowHours(plan, now);

        if (!_scrolledOnce)
        {
            _scrolledOnce = true;
            var hour = plan.Start == WeekLayout.StartOf(today) ? Math.Max(plan.FirstHour, now.Hour - 1) : plan.FirstHour;
            offset = (hour - plan.FirstHour) * HourHeight;
        }

        Dispatcher.InvokeAsync(() => GridScroll.ScrollToVerticalOffset(offset), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void Columns(Grid grid)
    {
        grid.ColumnDefinitions.Clear();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TimeColumn) });
        for (var day = 0; day < 7; day++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = MinimumDayWidth });
        }
    }

    private void ShowHeader(WeekPlan plan, DateOnly today)
    {
        Header.Children.Clear();
        Header.RowDefinitions.Clear();
        Columns(Header);
        Header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        foreach (var (day, index) in plan.Days.Select((day, index) => (day, index)))
        {
            var name = new TextBlock
            {
                Text = day.Date.ToString("ddd d", CultureInfo.InvariantCulture),
                FontSize = 12,
                Margin = new Thickness(6, 0, 4, 6),
                FontWeight = day.Date == today ? FontWeights.SemiBold : FontWeights.Normal,
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, day.Date == today ? "Accent" : "SecondaryText");
            Place(Header, name, index + 1, 0);

            var strip = new StackPanel { Margin = new Thickness(2, 0, 2, 6) };
            foreach (var item in day.AllDay)
            {
                strip.Children.Add(StripItem(item, today));
            }

            Place(Header, strip, index + 1, 1);
        }
    }

    /// <summary>A dated item along the top of its day, with its time in front when it has one.</summary>
    private FrameworkElement StripItem(StoredItem item, DateOnly today)
    {
        var time = item.DueAt is { } dueAt ? Clock(TimeOnly.FromDateTime(dueAt.LocalDateTime)) + " " : "";
        var title = new TextBlock
        {
            Text = item.Title,
            FontSize = 12,

            // Two lines before it trims, breaking only between words, since the day columns are narrow. The tooltip has the whole title.
            TextWrapping = TextWrapping.WrapWithOverflow,
            LineHeight = 16,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxHeight = 32,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextDecorations = item.Done ? TextDecorations.Strikethrough : null,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, item.Done ? "FinishedText" : "PrimaryText");

        // The time goes on a line of its own under the title, so it doesn't take the title's room.
        var text = new StackPanel();
        text.Children.Add(title);
        if (time.Length > 0)
        {
            var at = new TextBlock { Text = time.Trim(), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            at.SetResourceReference(TextBlock.ForegroundProperty, item.Done ? "FinishedDetail" : "SecondaryText");
            text.Children.Add(at);
        }

        var border = new Border
        {
            Margin = new Thickness(0, 0, 0, 3),
            Padding = new Thickness(6, 2, 4, 2),
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = MarkOf(item, today),
            Child = text,
            ToolTip = ToolTipFor(item, time.Trim()),
        };
        border.SetResourceReference(Border.BackgroundProperty, "Surface");
        Reachable(border);
        Menu(border, item);
        return border;
    }

    private void ShowHours(WeekPlan plan, DateTime now)
    {
        Hours.Children.Clear();
        Columns(Hours);
        var hours = plan.LastHour - plan.FirstHour;
        var height = hours * HourHeight;
        var today = DateOnly.FromDateTime(now);

        var labels = new Grid { Height = height, VerticalAlignment = VerticalAlignment.Top };
        for (var hour = plan.FirstHour; hour < plan.LastHour; hour++)
        {
            var label = new TextBlock
            {
                Text = Clock(new TimeOnly(hour, 0)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, (hour - plan.FirstHour) * HourHeight - 1, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
            labels.Children.Add(label);
        }

        Place(Hours, labels, 0, 0);

        foreach (var (day, index) in plan.Days.Select((day, index) => (day, index)))
        {
            Place(Hours, DayColumn(plan, day, today, now, height), index + 1, 0);
        }
    }

    private Grid DayColumn(WeekPlan plan, WeekDay day, DateOnly today, DateTime now, double height)
    {
        var column = new Grid { Height = height, VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true };

        var edge = new Border { BorderThickness = new Thickness(1, 0, 0, 0) };
        edge.SetResourceReference(Border.BorderBrushProperty, "Rule");
        column.Children.Add(edge);

        for (var hour = plan.FirstHour; hour < plan.LastHour; hour++)
        {
            var line = new Border
            {
                Height = HourHeight,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, (hour - plan.FirstHour) * HourHeight, 0, 0),
                BorderThickness = new Thickness(0, 1, 0, 0),
                IsHitTestVisible = false,
            };
            line.SetResourceReference(Border.BorderBrushProperty, "Rule");
            column.Children.Add(line);
        }

        foreach (var gap in day.Free)
        {
            column.Children.Add(FreeBlock(plan, gap));
        }

        foreach (var block in day.Blocks)
        {
            column.Children.Add(BlockCell(plan, block, today));
        }

        if (day.Date == today && now.Hour >= plan.FirstHour && now.Hour < plan.LastHour)
        {
            var line = new Border
            {
                Height = 2,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(1, Top(plan, now.TimeOfDay) - 1, 0, 0),
                IsHitTestVisible = false,
            };
            line.SetResourceReference(Border.BackgroundProperty, "Accent");
            column.Children.Add(line);
        }

        return column;
    }

    private static double Top(WeekPlan plan, TimeSpan at) => (at.TotalHours - plan.FirstHour) * HourHeight;

    private static FrameworkElement FreeBlock(WeekPlan plan, FreeGap gap)
    {
        var top = Top(plan, gap.From);
        var height = Math.Max(0, Top(plan, gap.To) - top);
        // Free time is faint, so it reads as room around the real blocks rather than as one of them.
        var shade = new Border { Opacity = 0.05, CornerRadius = new CornerRadius(3) };
        shade.SetResourceReference(Border.BackgroundProperty, "MoneyMark");

        var cell = new Grid
        {
            Height = height,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(3, top + 1, 2, 0),
            ToolTip = $"Free {WeekLayout.Length(gap.Length)}, {Clock(TimeOnly.FromTimeSpan(gap.From))} to {Clock(EndClock(gap.To))}",
        };
        cell.Children.Add(shade);
        if (height >= MinimumTextHeight)
        {
            var label = new TextBlock
            {
                Text = "Free " + WeekLayout.Length(gap.Length),
                FontSize = 12,
                Margin = new Thickness(6, 2, 4, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");

            // A narrow column drops the word, so the length still shows in full.
            cell.SizeChanged += (_, e) => label.Text = FreeLabel(
                gap.Length,
                e.NewSize.Width - label.Margin.Left - label.Margin.Right,
                new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch),
                label.FontSize,
                VisualTreeHelper.GetDpi(label).PixelsPerDip);
            cell.Children.Add(label);
        }

        return cell;
    }

    /// <summary>"Free 1h 40m" when that fits in the room, otherwise just "1h 40m".</summary>
    public static string FreeLabel(TimeSpan length, double room, Typeface typeface, double fontSize, double pixelsPerDip)
    {
        var time = WeekLayout.Length(length);
        var full = "Free " + time;
        var width = new FormattedText(full, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black, pixelsPerDip)
            .WidthIncludingTrailingWhitespace;
        return width <= room ? full : time;
    }

    /// <summary>A block sits in its share of the day's width, so overlapping ones are side by side.</summary>
    private FrameworkElement BlockCell(WeekPlan plan, PlacedBlock block, DateOnly today)
    {
        var cell = new Grid();
        for (var column = 0; column < block.Columns; column++)
        {
            cell.ColumnDefinitions.Add(new ColumnDefinition());
        }

        var top = Top(plan, block.From);
        var height = Math.Max(MinimumTextHeight, Top(plan, block.To) - top);
        var entry = block.Entry;
        var done = entry.Item?.Done == true;
        var time = entry.End is null ? Clock(entry.Start) : $"{Clock(entry.Start)} to {Clock(EndClock(block.To))}";

        FrameworkElement shown;
        if (entry.Kind == WeekBlockKind.Marker)
        {
            var text = new TextBlock
            {
                Text = $"{Clock(entry.Start)} {entry.Title}",
                FontSize = 12,
                Margin = new Thickness(4, 1, 2, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextDecorations = done ? TextDecorations.Strikethrough : null,
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, done ? "FinishedText" : "PrimaryText");
            var mark = new Border
            {
                Height = height,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(2, top, 2, 0),
                BorderThickness = new Thickness(0, 2, 0, 0),
                BorderBrush = entry.Item is { } item ? MarkOf(item, today) : Resource("Accent"),
                Background = Brushes.Transparent,
                Child = text,
            };
            shown = mark;
        }
        else
        {
            var lines = new StackPanel();
            var title = new TextBlock
            {
                Text = entry.Title,
                FontSize = 12,

                // Breaks only between words, so a course code reads "ENGL 101" and never "EN GL 101". The tooltip has the full title.
                TextWrapping = height >= 2 * MinimumTextHeight + 8 ? TextWrapping.WrapWithOverflow : TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextDecorations = done ? TextDecorations.Strikethrough : null,
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, done ? "FinishedText" : "PrimaryText");
            lines.Children.Add(title);

            if (height >= 2 * MinimumTextHeight + 4)
            {
                var detail = new TextBlock
                {
                    Text = entry.Detail is { } room ? $"{time}  ·  {room}" : time,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                detail.SetResourceReference(TextBlock.ForegroundProperty, done ? "FinishedDetail" : "SecondaryText");
                lines.Children.Add(detail);
            }

            var box = new Border
            {
                Height = height - 2,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(2, top + 1, 2, 0),
                Padding = new Thickness(6, 2, 4, 2),
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(3, 0, 0, 0),
                BorderBrush = entry.Kind switch
                {
                    WeekBlockKind.Class => Resource("Accent"),
                    WeekBlockKind.Work => Resource("Soon"),
                    _ => entry.Item is { } item ? MarkOf(item, today) : Resource("QuietMark"),
                },
                Child = lines,
                ClipToBounds = true,
            };
            box.SetResourceReference(Border.BackgroundProperty, "SurfaceHover");
            shown = box;
        }

        shown.ToolTip = entry.Item is { } owner ? ToolTipFor(owner, time) : $"{entry.Title}{(entry.Detail is { } where ? $", {where}" : "")}\n{time}";
        Reachable(shown);
        if (entry.Item is { } menuItem)
        {
            Menu(shown, menuItem);
        }

        Grid.SetColumn(shown, block.Column);
        cell.Children.Add(shown);
        return cell;
    }

    /// <summary>
    /// A block can be reached with Tab, shows the focus ring, and is read out as its tooltip says, so the week works
    /// from the keyboard and with a screen reader as well as with the mouse.
    /// </summary>
    private static void Reachable(FrameworkElement block)
    {
        block.Focusable = true;
        block.SetResourceReference(FocusVisualStyleProperty, "FocusRing");
        if (block.ToolTip is string tip)
        {
            AutomationProperties.SetName(block, tip.Replace("\n", ", ", StringComparison.Ordinal));
        }
    }

    /// <summary>The row's menu, from a right-click, Shift+F10, the Menu key, or Enter.</summary>
    private void Menu(FrameworkElement element, StoredItem item)
    {
        element.ContextMenuOpening += (_, e) =>
        {
            e.Handled = true;
            _openMenu?.Invoke(element, e.CursorLeft < 0 ? PlacementMode.Bottom : PlacementMode.MousePoint, item);
        };
        element.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                _openMenu?.Invoke(element, PlacementMode.Bottom, item);
            }
        };
    }

    private static string ToolTipFor(StoredItem item, string time)
    {
        var parts = new[] { item.Kind is ItemKind.Task or ItemKind.Deadline ? null : item.Kind.ToString(), time.Length > 0 ? time : null, item.Course };
        var detail = string.Join("  ·  ", parts.Where(part => !string.IsNullOrEmpty(part)));
        return detail.Length > 0 ? $"{item.Title}\n{detail}" : item.Title;
    }

    private static Brush MarkOf(StoredItem item, DateOnly today) =>
        item.Done ? Resource("FinishedDetail") : ThemeBrushes.For(ItemMarks.For(item, today));

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>Midnight at the end of a day reads as 12:00 AM rather than failing.</summary>
    private static TimeOnly EndClock(TimeSpan at) => at >= TimeSpan.FromHours(24) ? TimeOnly.MinValue : TimeOnly.FromTimeSpan(at);

    private static string Clock(TimeOnly time) => time.Minute == 0
        ? time.ToString("h tt", CultureInfo.CurrentCulture)
        : time.ToString("h:mm tt", CultureInfo.CurrentCulture);

    private static void Place(Grid grid, UIElement element, int column, int row)
    {
        Grid.SetColumn(element, column);
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    /// <summary>Keeps the day names over their columns when the scroll bar comes and goes.</summary>
    private void OnGridScrolled(object sender, ScrollChangedEventArgs e) =>
        Header.Margin = new Thickness(28, 0, 24 + (GridScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible ? SystemParameters.VerticalScrollBarWidth : 0), 0);

    private void OnBackClicked(object sender, RoutedEventArgs e) => MoveTo(Start.AddDays(-7));

    private void OnNextClicked(object sender, RoutedEventArgs e) => MoveTo(Start.AddDays(7));

    private void OnThisWeekClicked(object sender, RoutedEventArgs e) => MoveTo(WeekLayout.StartOf(DateOnly.FromDateTime(DateTime.Now)));

    /// <summary>Moves to the week a day is in, without asking the page to rebuild, for when the page is about to.</summary>
    public void ShowWeekOf(DateOnly date) => Start = WeekLayout.StartOf(date);

    private void OnUndatedClicked(object sender, RoutedEventArgs e) => UndatedClicked?.Invoke();

    private void MoveTo(DateOnly start)
    {
        Start = start;
        WeekChanged?.Invoke();
    }
}
