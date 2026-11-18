using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using Waypoint.Core.Capture;

namespace Waypoint.App;

/// <summary>
/// The Transition Assist buttons for a card. MoreTime is null on the follow-up, and SaveSpot is null when a spot
/// was saved recently. SaveSpot returns true once something was saved. Clear only clears the reminder, for Start.
/// NotNow clears it too and counts the answer as Not now. The card's button for it reads Skip, and MoreTime's reads 10 more minutes.
/// </summary>
public sealed record ReminderTransition(bool ShowPrimary, Action Clear, Action NotNow, Action? MoreTime, Func<bool>? SaveSpot);

/// <summary>
/// A waiting-on nudge's buttons, in place of Start and the snooze. FollowUp gives a note to copy, and StillWaiting
/// checks back in a few days. Done is the card's main button.
/// </summary>
public sealed record ReminderWaiting(Action FollowUp, Action StillWaiting);

/// <summary>
/// A dose's card: Took it is the main button and Not now comes back once, so the card that comes back has no Not now.
/// Skip lets that day go.
/// </summary>
public sealed record ReminderDose(bool OffersNotNow, Action Skip);

/// <summary>Corner card that stays up until answered and doesn't take focus from what's running.</summary>
public partial class ReminderWindow : Window
{
    private const int ScreenMargin = 16;
    private const int CardGap = 8;

    private readonly Action _primary;
    private readonly Action _snooze;
    private readonly Action? _skip;
    private readonly Action? _start;
    private readonly ReminderTransition? _transition;
    private readonly ReminderWaiting? _waiting;
    private readonly ReminderDose? _dose;

    /// <summary>How many cards up from the corner this one sits, so they don't cover each other.</summary>
    public int Slot { get; init; }

    /// <summary>Keeps This didn't help for this card and returns its ID. Without it, the card has no This didn't help.</summary>
    public Func<long>? FeedbackKept
    {
        init
        {
            FeedbackLine.Kept = value;
            FeedbackLine.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public Action<long, string>? FeedbackNoted
    {
        init => FeedbackLine.Noted = value;
    }

    public ReminderWindow(DueReminder reminder, string primaryLabel, int snoozeMinutes, Action primary, Action snooze, Action? skip = null, Action? start = null, ReminderTransition? transition = null, ReminderWaiting? waiting = null, ReminderDose? dose = null)
    {
        _primary = primary;
        _snooze = snooze;
        _skip = skip;
        _start = waiting is null ? start : null;
        _transition = waiting is null ? transition : null;
        _waiting = waiting;
        _dose = dose;
        transition = _transition;
        start = _start;
        InitializeComponent();
        WindowStyling.Apply(this);

        var item = reminder.Item;
        Kind.Text = KindLabel(item.Kind);
        ItemTitle.Text = reminder.Message ?? item.Title;
        Primary.Content = primaryLabel;
        Snooze.ToolTip = $"Back in {MinutesText(snoozeMinutes)}";
        Skip.Visibility = skip is null ? Visibility.Collapsed : Visibility.Visible;
        Start.Visibility = start is null ? Visibility.Collapsed : Visibility.Visible;

        // With Transition Assist, the snooze becomes one follow-up ten minutes later, named for what it does, and Skip
        // ends it with nothing more to come. Skip still counts as Not now in the suggestion's habits.
        if (transition is not null)
        {
            Primary.Visibility = transition.ShowPrimary ? Visibility.Visible : Visibility.Collapsed;
            Snooze.Content = CardLabels.MoreTime;
            Snooze.ToolTip = $"Back once, in {MinutesText(TransitionAssist.FollowUpMinutes)}";
            Snooze.Visibility = transition.MoreTime is null ? Visibility.Collapsed : Visibility.Visible;
            SkipOne.Visibility = Visibility.Visible;
            SaveSpot.Content = TransitionAssist.SaveSpotLabel;
            SaveSpot.Visibility = transition.SaveSpot is null ? Visibility.Collapsed : Visibility.Visible;
        }

        // A wait is on someone else, so there's nothing to start: Follow up, Still waiting, or Done.
        if (waiting is not null)
        {
            FollowUp.Visibility = Visibility.Visible;
            StillWaiting.Visibility = Visibility.Visible;
            Snooze.Visibility = Visibility.Collapsed;
        }

        // Took it, then Not now (back once, in ten minutes), then Skip, the same as the Discord card.
        if (dose is not null)
        {
            Snooze.Content = CardLabels.NotNow;
            Snooze.ToolTip = $"Back once, in {MinutesText(TransitionAssist.FollowUpMinutes)}";
            Snooze.Visibility = dose.OffersNotNow ? Visibility.Visible : Visibility.Collapsed;
            SkipOne.ToolTip = "Nothing more for this time. It comes back next time.";
            SkipOne.Visibility = Visibility.Visible;
        }

        Details.Text = DetailLine(reminder, DateOnly.FromDateTime(DateTime.Now));

        Loaded += (_, _) =>
        {
            Left = SystemParameters.WorkArea.Right - ActualWidth - ScreenMargin;
            Top = SystemParameters.WorkArea.Bottom - ((ActualHeight + CardGap) * (Slot + 1)) - ScreenMargin + CardGap;
        };
    }

    private static string MinutesText(int minutes) =>
        minutes % 1440 == 0 ? Count(minutes / 1440, "a day", "days")
        : minutes % 60 == 0 ? Count(minutes / 60, "an hour", "hours")
        : Count(minutes, "1 minute", "minutes");

    private static string Count(int count, string one, string many) => count == 1 ? one : $"{count} {many}";

    /// <summary>The small header over the title, in sentence case so it reads quietly: "Reminder", not "REMINDER".</summary>
    public static string KindLabel(ItemKind kind) => kind.ToString();

    /// <summary>
    /// The line under the title, like a To do row's: when it's due, how often it comes back, and the class. A card
    /// with its own message names the item first, since the title shows the message instead.
    /// </summary>
    public static string DetailLine(DueReminder reminder, DateOnly today)
    {
        var item = reminder.Item;
        var day = item.DueDate is { } date ? ItemGroups.DayLabel(date, today) : null;
        var time = item.DueAt?.LocalDateTime.ToString("h:mm tt", CultureInfo.CurrentCulture);
        var when = $"{day} {time}".Trim();
        var parts = new[]
        {
            reminder.Message is null ? null : item.Title,
            when.Length > 0 ? when : null,
            item.Recurs?.Label,
            ToDoFilters.CourseOf(item, null),
        };
        return string.Join("  ·  ", parts.Where(part => part is { Length: > 0 }));
    }

    private void OnPrimaryClicked(object sender, RoutedEventArgs e)
    {
        _primary();
        Close();
    }

    private void OnSnoozeClicked(object sender, RoutedEventArgs e)
    {
        (_transition?.MoreTime ?? _snooze)();
        Close();
    }

    /// <summary>
    /// Starting isn't finishing, so a repeating reminder comes back later unless the item gets done. With
    /// Transition Assist the session holds the task from here, so the reminder is cleared instead of nagging.
    /// </summary>
    private void OnStartClicked(object sender, RoutedEventArgs e)
    {
        (_transition?.Clear ?? _snooze)();
        _start?.Invoke();
        Close();
    }

    private void OnSkipOneClicked(object sender, RoutedEventArgs e)
    {
        _dose?.Skip();
        _transition?.NotNow();
        Close();
    }

    /// <summary>The card stays up, so Start is still one click away once the spot is saved.</summary>
    private void OnSaveSpotClicked(object sender, RoutedEventArgs e)
    {
        if (_transition?.SaveSpot is { } save && save())
        {
            SaveSpot.Visibility = Visibility.Collapsed;
        }
    }

    private void OnFollowUpClicked(object sender, RoutedEventArgs e)
    {
        _waiting?.FollowUp();
        Close();
    }

    private void OnStillWaitingClicked(object sender, RoutedEventArgs e)
    {
        _waiting?.StillWaiting();
        Close();
    }

    private void OnSkipClicked(object sender, RoutedEventArgs e)
    {
        _skip?.Invoke();
        Close();
    }
}
