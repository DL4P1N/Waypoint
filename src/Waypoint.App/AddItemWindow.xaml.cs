using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.App;

/// <summary>A class or filter the add form can put a new item in. Course or FilterId is set, or neither for Personal.</summary>
public sealed record FormPlace(string Label, string? Course = null, long? FilterId = null)
{
    /// <summary>What a screen reader says for the place, rather than the record's fields.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// The To do page's add form: title, type, date, optional time, optional class or filter, and an optional
/// reminder. It hands what was typed to the page, which stores it, and stays open to say what's missing.
/// </summary>
public partial class AddItemWindow : Window
{
    private static readonly Choice<ItemKind>[] KindChoices =
    [
        new("Task", ItemKind.Task),
        new("Deadline", ItemKind.Deadline),
        new("Reminder", ItemKind.Reminder),
        new("Event", ItemKind.Event),
        new("Exam", ItemKind.Exam),
    ];

    private readonly Func<HandAddedItem, string?> _add;
    private readonly DateOnly _today;
    private DateOnly? _date;

    /// <summary>Set once a date is chosen by hand, so changing the type doesn't quietly change it back.</summary>
    private bool _dateChosen;
    private bool _showingDate;

    /// <param name="add">Stores the item, returning why it couldn't, or null once it's added.</param>
    public AddItemWindow(Window owner, ItemKind kind, DateOnly today, IReadOnlyList<FormPlace> places, FormPlace? place, Func<HandAddedItem, string?> add)
    {
        InitializeComponent();
        WindowStyling.Apply(this);
        Owner = owner;
        _add = add;
        _today = today;

        Kinds.ItemsSource = KindChoices;
        Places.ItemsSource = places;
        Places.SelectedItem = place ?? places.FirstOrDefault();
        PlacePanel.Visibility = places.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Kinds.SelectedItem = KindChoices.FirstOrDefault(choice => choice.Value == kind) ?? KindChoices[0];

        Loaded += (_, _) => TitleBox.Focus();
    }

    private ItemKind Kind => Kinds.SelectedItem is Choice<ItemKind> choice ? choice.Value : ItemKind.Task;

    /// <summary>
    /// A task starts with no date, and only a task can go without one. Anything else starts on today. An end only
    /// means something for an event, and "When it starts" only for something that starts.
    /// </summary>
    private void OnKindPicked(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Kind == ItemKind.Task && !_dateChosen)
        {
            _date = null;
        }
        else if (Kind != ItemKind.Task && _date is null)
        {
            _date = _today;
        }

        EndBox.Visibility = Kind == ItemKind.Event ? Visibility.Visible : Visibility.Collapsed;
        TimeBox.Tag = Kind == ItemKind.Event ? "Starts, optional (5:30 PM)" : "Time, optional (5:30 PM)";
        ShowReminders();
        ShowDate();
    }

    /// <summary>The reminder choices that make sense for this type, keeping the one picked when it still fits.</summary>
    private void ShowReminders()
    {
        var picked = Reminders.SelectedItem as Choice<TimeSpan?>;
        var choices = ReminderChoicesFor(Kind);
        Reminders.ItemsSource = choices;
        Reminders.SelectedItem = choices.FirstOrDefault(choice => choice.Value == picked?.Value) ?? choices[0];
    }

    /// <summary>A task or deadline is due rather than starting, so it gets "When it's due" in place of "When it starts".</summary>
    internal static IReadOnlyList<Choice<TimeSpan?>> ReminderChoicesFor(ItemKind kind) =>
    [
        new("None", null),
        new(kind is ItemKind.Task or ItemKind.Deadline ? "When it's due" : "When it starts", TimeSpan.Zero),
        new("15 minutes before", TimeSpan.FromMinutes(15)),
        new("1 hour before", TimeSpan.FromHours(1)),
        new("The day before", TimeSpan.FromDays(1)),
    ];

    /// <summary>No date (for a task), Today and Tomorrow as one-click choices, and Pick a date... for anything else.</summary>
    private void ShowDate()
    {
        _showingDate = true;
        var choices = new List<Choice<DateOnly?>>();
        if (Kind == ItemKind.Task)
        {
            choices.Add(new("No date", null));
        }

        choices.Add(new("Today", _today));
        choices.Add(new("Tomorrow", _today.AddDays(1)));
        Dates.ItemsSource = choices;
        Dates.SelectedItem = choices.FirstOrDefault(choice => choice.Value == _date);
        _showingDate = false;

        DateButton.Content = _date is { } date && Dates.SelectedItem is null
            ? date.ToString("ddd, MMM d", CultureInfo.InvariantCulture)
            : "Pick a date...";
    }

    private void OnDateChosen(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_showingDate || Dates.SelectedItem is not Choice<DateOnly?> choice)
        {
            return;
        }

        _date = choice.Value;
        _dateChosen = true;
        ShowDate();
    }

    private void OnDateClicked(object sender, RoutedEventArgs e)
    {
        var picker = new PickDateWindow(this, TitleBox.Text.Trim(), _date ?? _today, null, asksForTime: false, action: "Choose");
        if (picker.ShowDialog() == true)
        {
            _date = picker.Date;
            _dateChosen = true;
            ShowDate();
        }
    }

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        if (!TryTime(TimeBox.Text, out var time) || !TryTime(EndBox.Visibility == Visibility.Visible ? EndBox.Text : "", out var end))
        {
            Show("Try a time like 5:30 PM.");
            return;
        }

        if (end is not null && time is null)
        {
            Show("An end time needs a start time.");
            return;
        }

        var place = Places.SelectedItem as FormPlace;
        var item = new HandAddedItem(
            Kind,
            TitleBox.Text,
            _date,
            time,
            end,
            place?.Course,
            Reminders.SelectedItem is Choice<TimeSpan?> reminder ? reminder.Value : null,
            place?.FilterId);

        if (_add(item) is { } problem)
        {
            Show(problem);
            return;
        }

        DialogResult = true;
    }

    private void Show(string problem)
    {
        Problem.Text = problem;
        Problem.Visibility = Visibility.Visible;
        LiveRegion.Announce(Problem);
    }

    private static bool TryTime(string text, out TimeOnly? time)
    {
        var clean = text.Trim().ToUpperInvariant();
        time = null;
        if (clean.Length == 0)
        {
            return true;
        }

        if (TimeOnly.TryParseExact(clean, PickDateWindow.TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            time = parsed;
            return true;
        }

        return false;
    }

    internal sealed record Choice<T>(string Label, T Value)
    {
        /// <summary>What a screen reader says for it, rather than the record's fields.</summary>
        public override string ToString() => Label;
    }
}
