using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Waypoint.Core;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Remote;
using Waypoint.Core.Schedule;

namespace Waypoint.App;

/// <summary>The cards on To do: the setup card, the study window, the suggestion, and tonight's card, with Save my spot.</summary>
public partial class TodayView
{
    /// <summary>
    /// The card shows only on a fresh copy, decided the first time the page connects: anyone who already has things in
    /// Waypoint never sees it. It stays until every line is done or it's hidden, and then it's gone for good.
    /// </summary>
    private void DecideSetupCard()
    {
        if (_database.GetPreference(SetupKey) is null)
        {
            var fresh = _database.GetOpenItems().Count == 0 && _database.GetFinishedForList(DateTime.Now).Count == 0;
            _database.SetPreference(SetupKey, fresh ? SetupShowing : SetupDone);
        }
    }

    /// <summary>The setup lines not done yet. Each goes once it's done, or once it's been opened, with nothing to tick off.</summary>
    private void ShowSetup()
    {
        var steps = _database.GetPreference(SetupKey) == SetupShowing ? SetupStepsLeft() : [];
        if (steps.Count == 0 && _database.GetPreference(SetupKey) == SetupShowing)
        {
            Work.Fire(() => _database.SetPreference(SetupKey, SetupDone));
        }

        SetupCard.Visibility = steps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Rebuilt only when a line comes or goes, since the page refreshes every minute and focus on a line would be lost.
        if (SetupSteps.ItemsSource is not IEnumerable<SetupStep> shown || !shown.SequenceEqual(steps))
        {
            SetupSteps.ItemsSource = steps;
        }
        if (Empty.Text is EmptyHint or EmptyBesideSetup)
        {
            Empty.Text = AllEmptyText;
        }
    }

    private string AllEmptyText =>
        SetupCard.Visibility == Visibility.Visible && SetupSteps.ItemsSource is IEnumerable<SetupStep> steps && steps.Any(step => step.Key == "task")
            ? EmptyBesideSetup
            : EmptyHint;

    private List<SetupStep> SetupStepsLeft()
    {
        var opened = (_database.GetPreference(SetupOpenedKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var hasItems = _database.GetOpenItems().Count > 0 || _database.GetFinishedForList(DateTime.Now).Count > 0;
        var hasHours = Enumerable.Range(0, 7).Any(offset => _database.GetShifts(today.AddDays(offset)).Count > 0);

        var steps = new List<SetupStep>();
        if (!hasItems)
        {
            steps.Add(new SetupStep("task", "Add your first task with +, or type anything in Ask"));
        }

        if (!hasHours && !opened.Contains("hours"))
        {
            steps.Add(new SetupStep("hours", "Add your work hours in Settings"));
        }

        if (!opened.Contains("pay"))
        {
            steps.Add(new SetupStep("pay", "Say how you're paid, on Budget"));
        }

        if (!opened.Contains("discord"))
        {
            steps.Add(new SetupStep("discord", "Connect Discord in Settings, for To do on your phone"));
        }

        return steps;
    }

    private async void OnSetupStepClicked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SetupStep step)
        {
            return;
        }

        if (step.Key == "task")
        {
            AddItem(ItemKind.Task);
            Work.Show(ShowSetup);
            return;
        }

        // Opening its page is enough for the line to go. Nothing checks whether it was finished there.
        var saving = Work.Run(() =>
        {
            var opened = _database.GetPreference(SetupOpenedKey);
            _database.SetPreference(SetupOpenedKey, string.IsNullOrEmpty(opened) ? step.Key : $"{opened},{step.Key}");
        });
        if (saving.IsCompleted)
        {
            await saving;
            ShowSetup();
        }

        OpenSetupStep(step);
        if (!saving.IsCompleted)
        {
            await saving;
            Work.ShowAfterChanges(nameof(ShowSetup), ShowSetup);
        }
    }

    private void OpenSetupStep(SetupStep step)
    {
        if (step.Key == "pay")
        {
            if (PayScheduleRequested is { } openPay)
            {
                openPay();
            }
            else
            {
                _openMoney();
            }
        }
        else
        {
            SettingsRequested?.Invoke(step.Key == "hours" ? "work" : step.Key);
        }
    }

    private async void OnSetupHideClicked(object sender, RoutedEventArgs e)
    {
        SetupCard.Visibility = Visibility.Collapsed;
        await Work.Run(() => _database.SetPreference(SetupKey, SetupDone));
        Work.ShowAfterChanges(nameof(ShowSetup), ShowSetup);
    }

    /// <summary>One study window, worked out fresh from what's already known. It's gone during the wind-down.</summary>
    private void FindStudy(BedtimeStatus? tonight)
    {
        var now = DateTime.Now;
        _study = tonight is null
            ? StudyWindows.ForToday(now, _database.GetOpenItems(), _classes, _database.GetShifts(DateOnly.FromDateTime(now)), Bedtime)
            : null;
    }

    /// <summary>The same as Study for this exam on the exam's right-click menu, for an exam's study window on the card.</summary>
    private void OnPracticeClicked(object sender, RoutedEventArgs e)
    {
        if (_study is { Target: { Kind: ItemKind.Exam } exam })
        {
            StudyRequested?.Invoke(exam);
        }
    }

    /// <summary>
    /// The one thing to start next, picked locally so it's instant and never waits on the sorter. It's picked from
    /// everything open, whatever the filter, search, or Hide finished say, since it's what to do next and not a view of
    /// the list. The page keeps to this one card and the money line above the list, so a study window shows here too:
    /// once it begins or is about to, or whenever nothing else is picked. During a class, shift, or event the card says until when
    /// instead. It hides while a session is going, since Ask has its card, and during the wind-down, which has its own.
    /// </summary>
    private void ShowSuggestion()
    {
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        var open = _database.GetOpenItems();
        _sessionGoing = _database.GetActiveTaskTitle(now) is not null;
        var free = !_sessionGoing && TonightCard.Visibility != Visibility.Visible;
        var skipped = NextAction.ReadSkipped(_database.GetPreference(SkippedKey), today);
        var busy = Commitments(now, open);
        var declined = Declined(now);
        var pick = free
            ? NextAction.Pick(_database.WithoutQuietWaiting(open, today), busy, now, skipped, _database.GetSuggestionHabits(now), declined)
            : null;
        var busyLine = free && pick is null ? NextAction.BusyLine(busy, now) : null;
        _suggestedStudy = free && busyLine is null && _study is { } study && StudyWindows.ShowsNow(study, pick is not null, now, skipped, NotNowDeclines.WithParents(declined, open))
            ? study
            : null;

        var before = _suggested;
        _suggested = _suggestedStudy?.Target ?? pick?.Item;
        if (_suggested != before)
        {
            SuggestionFeedback.Reset();
        }

        SuggestionFeedback.Visibility = _suggested is not null && FeedbackData is not null ? Visibility.Visible : Visibility.Collapsed;
        SuggestionCard.Visibility = _suggested is null && busyLine is null ? Visibility.Collapsed : Visibility.Visible;
        SuggestionTitle.Text = _suggested?.Title ?? busyLine ?? "";
        SuggestionTitle.SetResourceReference(TextBlock.ForegroundProperty, _suggested is null ? "SecondaryText" : "PrimaryText");
        SuggestionReason.Text = _suggestedStudy?.Line ?? pick?.Reason ?? "";
        SuggestionReason.Visibility = SuggestionReason.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SuggestionButtons.Visibility = _suggested is null ? Visibility.Collapsed : Visibility.Visible;
        PracticeButton.Visibility = _suggestedStudy?.Target.Kind == ItemKind.Exam && StudyRequested is not null ? Visibility.Visible : Visibility.Collapsed;
        ShowRowActions();
    }

    /// <summary>
    /// One Start and one Save my spot on the page at a time. The suggestion has its own Start, so the row's Start steps
    /// aside while it shows, and during the wind-down Save my spot lives in the Tonight card. While a session is going its
    /// card is the Start, so the row's Start stays hidden and can't quietly replace it with something else.
    /// </summary>
    private void ShowRowActions()
    {
        RowStart.Visibility = _suggested is not null || _sessionGoing ? Visibility.Collapsed : Visibility.Visible;
        RowSaveSpot.Visibility = TonightCard.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private IReadOnlyList<Commitment> Commitments(DateTime now, IReadOnlyList<StoredItem> open)
    {
        var today = DateOnly.FromDateTime(now);
        var shifts = _database.GetShifts(today).Concat(_database.GetShifts(today.AddDays(1)));
        return NextAction.Commitments(now, _classes, shifts, open, item => Placement(item)?.EndTime, _tonight?.Plan.Bedtime);
    }

    /// <summary>Starts a session on the suggestion, the same as a reminder's Start, so its own steps come first.</summary>
    private async void OnSuggestionStartClicked(object sender, RoutedEventArgs e)
    {
        if (_suggestedStudy is { } study)
        {
            // A study window starts on its target with one concrete first action.
            var target = ItemRef.Format(study.Target);
            var first = study.FirstAction;
            _reply((await Work.Run(() => _database.StartActivation(target, Activation.StudyMethod, DateTime.Now, first))).Text);
            Acted();
            return;
        }

        if (_suggested is not { } item)
        {
            return;
        }

        var reference = ItemRef.Format(item);
        _reply((await Work.Run(() => _database.StartActivation(reference, SuggestedMethod, DateTime.Now))).Text);
        Acted();
    }

    /// <summary>Passes on this one for the rest of today, and shows the next suggestion if there is one.</summary>
    private async void OnSuggestionSkipClicked(object sender, RoutedEventArgs e)
    {
        if (_suggested is not { } item)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var reference = ItemRef.Format(item);
        await Work.Run(() => _database.SetPreference(SkippedKey, NextAction.AddSkipped(_database.GetPreference(SkippedKey), today, reference)));
        Work.ShowAfterChanges(nameof(ShowSuggestion), ShowSuggestion);
    }

    /// <summary>Only there during the wind-down. A step is only ever checked here by hand.</summary>
    private void ShowTonight(BedtimeStatus? tonight)
    {
        _tonight = tonight;
        TonightCard.Visibility = tonight is null ? Visibility.Collapsed : Visibility.Visible;
        if (tonight is not null)
        {
            TonightLabel.Text = $"Bed by {Time(tonight.Plan.Bedtime)} for {tonight.Plan.Commitment}";
            _marks.Clear();
            foreach (var step in tonight.Steps)
            {
                _marks[step.Label] = step.Done;
            }

            TonightSteps.ItemsSource = tonight.Steps;
        }

        ShowRowActions();
    }

    private async void OnRoutineStepClicked(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        var label = ((RoutineStep)box.DataContext).Label;
        var done = box.IsChecked == true;
        if (_tonight is not null && Sessions is { } sessions && _marks.GetValueOrDefault(label) != done)
        {
            // The same as typing "done" and the step's name: it checks the step is on tonight's list and says what's
            // left. Marked as asked straight away, so a second click before it's saved is told apart from the first.
            var before = _marks.GetValueOrDefault(label);
            _marks[label] = done;
            OpReply reply;
            try
            {
                reply = await Work.Run(() => sessions.MarkRoutineStep(label, done, DateTime.Now));
            }
            catch
            {
                if (_marks.GetValueOrDefault(label) == done)
                {
                    _marks[label] = before;
                }

                throw;
            }

            _reply(reply.Text);
        }
    }

    /// <summary>Not now on tonight's card, the same as typing snooze during the wind-down.</summary>
    private void OnBedtimeSnoozeClicked(object sender, RoutedEventArgs e) => Tonight(sessions => sessions.SnoozeBedtime(DateTime.Now));

    private void OnBedtimeSkipClicked(object sender, RoutedEventArgs e) => Tonight(sessions => sessions.SkipBedtime(DateTime.Now));

    private async void Tonight(Func<ISessionData, OpReply> change)
    {
        if (Sessions is { } sessions)
        {
            _reply((await Work.Run(() => change(sessions))).Text);
            _changed();
        }
    }

    /// <summary>The row of a session put off with Not now offers to pick it back up, the same as saying resume.</summary>
    private IEnumerable<RowMenuEntry> ResumeMenu(StoredItem item)
    {
        if (Sessions is not { } sessions || sessions.GetPausedSession()?.TaskRef != ItemRef.Format(item))
        {
            return [];
        }

        return [new RowMenuEntry("Pick up where I left off", async () =>
        {
            _reply((await Work.Run(sessions.ResumeSession)).Text);
            Acted();
        })];
    }

    /// <summary>The form saves through the same method as typing "save my spot", so it also checks off Save project tonight.</summary>
    private void OnSaveSpotClicked(object sender, RoutedEventArgs e) => OpenSaveSpot();

    /// <summary>The Save my spot form, from the page's button or the tray. It saves without sorting, so it works offline.</summary>
    public void OpenSaveSpot()
    {
        var form = SaveSpotWindow.Saving(Window.GetWindow(this), Work.Peek(() => _database.GetActiveTaskTitle(DateTime.Now)), spot =>
        {
            var (project, stopped, next, links, thoughts) = (spot.Project, spot.Stopped, spot.Next, spot.Links, spot.Thoughts);
            return Work.Run(() => _database.SaveSpotByHand(project, stopped, next, links, thoughts, DateTime.Now));
        });
        if (form.ShowDialog() == true && form.Reply is { } reply)
        {
            _reply(reply);
            Acted();
        }
    }
}
