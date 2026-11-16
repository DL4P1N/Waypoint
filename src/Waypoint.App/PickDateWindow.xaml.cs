using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Waypoint.App;

/// <summary>A month to click a day in, plus an optional time. Recurring items keep their usual time, so they get no box.</summary>
public partial class PickDateWindow : Window
{
    internal static readonly string[] TimeFormats = ["h:mm tt", "h:mmtt", "h tt", "htt", "H:mm"];

    private DateOnly _month;

    /// <param name="action">The button's word: Move when it moves something, Choose when it only picks a date for a form.</param>
    public PickDateWindow(Window owner, string title, DateOnly date, TimeOnly? time, bool asksForTime, string action = "Move")
    {
        InitializeComponent();
        GoButton.Content = action;
        WindowStyling.Apply(this);
        Owner = owner;
        ItemTitle.Text = title;
        Date = date;
        Time = time;
        _month = new DateOnly(date.Year, date.Month, 1);
        TimeBox.Text = time?.ToString("h:mm tt", CultureInfo.InvariantCulture) ?? "";
        TimeBox.Visibility = asksForTime ? Visibility.Visible : Visibility.Collapsed;

        foreach (var name in new[] { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" })
        {
            var label = new TextBlock { Text = name, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
            DayNames.Children.Add(label);
        }

        foreach (var (label, day) in QuickDates(DateOnly.FromDateTime(DateTime.Now)))
        {
            var quick = new Button { Content = label, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 4, 10, 4), FontSize = 12 };
            quick.SetResourceReference(BorderBrushProperty, "Rule");
            quick.Click += (_, _) => Pick(day);
            QuickDays.Children.Add(quick);
        }

        ShowMonth();
    }

    /// <summary>
    /// The usual picks, one click each: today, tomorrow, the coming Saturday (today, on a weekend), and next Monday.
    /// </summary>
    public static IReadOnlyList<(string Label, DateOnly Day)> QuickDates(DateOnly today)
    {
        var weekend = today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
            ? today
            : today.AddDays(DayOfWeek.Saturday - today.DayOfWeek);
        var toMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        var monday = today.AddDays(toMonday == 0 ? 7 : toMonday);
        return [("Today", today), ("Tomorrow", today.AddDays(1)), ("This weekend", weekend), ("Next week", monday)];
    }

    private void Pick(DateOnly day)
    {
        Date = day;
        _month = new DateOnly(day.Year, day.Month, 1);
        ShowMonth();
    }

    public DateOnly Date { get; private set; }

    public TimeOnly? Time { get; private set; }

    private void ShowMonth()
    {
        MonthLabel.Text = _month.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        Days.Children.Clear();
        var first = _month.AddDays(-(int)_month.DayOfWeek);
        for (var offset = 0; offset < 42; offset++)
        {
            Days.Children.Add(DayButton(first.AddDays(offset)));
        }
    }

    private Button DayButton(DateOnly day)
    {
        var button = new Button
        {
            Content = day.Day.ToString(CultureInfo.InvariantCulture),
            Height = 34,
            BorderThickness = new Thickness(0),
            FontSize = 13,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, day.ToString("MMMM d", CultureInfo.InvariantCulture));

        if (day == Date)
        {
            button.BorderThickness = new Thickness(1);
            button.FontWeight = FontWeights.Bold;
            button.Foreground = System.Windows.Media.Brushes.Black;
            button.SetResourceReference(BackgroundProperty, "Accent");
        }
        else if (day.Month != _month.Month)
        {
            button.SetResourceReference(ForegroundProperty, "FinishedDetail");
        }

        button.Click += (_, _) => Pick(day);
        return button;
    }

    private void OnPreviousMonth(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(-1);
        ShowMonth();
    }

    private void OnNextMonth(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(1);
        ShowMonth();
    }

    private void OnMoveClicked(object sender, RoutedEventArgs e)
    {
        var text = TimeBox.Text.Trim().ToUpperInvariant();
        if (TimeBox.Visibility != Visibility.Visible || text.Length == 0)
        {
            Time = null;
            DialogResult = true;
        }
        else if (TimeOnly.TryParseExact(text, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            Time = time;
            DialogResult = true;
        }
        else
        {
            Problem.Visibility = Visibility.Visible;
            LiveRegion.Announce(Problem);
        }
    }
}
