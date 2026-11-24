using System.Globalization;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Discord;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Background;

/// <summary>The timed checks CheckAlerts runs, other than reminders and held notices.</summary>
public sealed partial class WaypointWorker
{
    private void CheckBedtime()
    {
        var now = _clock();
        if (!BedtimeSchedule.IsQuiet(now))
        {
            foreach (var notice in TakeHeldToSend())
            {
                _ = SendHeldNoticeAsync(notice);
            }
        }

        if (_bedtime.Check(now) is not { } reminder)
        {
            return;
        }

        // A spot saved during tonight's wind-down means the save-your-spot stage has nothing left to ask.
        var spotSaved = BedtimeRoutine.GetStatus(_database, BedtimeSchedule, now)?.Steps.Any(step => step.Label == BedtimeRoutine.SaveProject && step.Done) == true;
        if (BedtimeRoutine.Skips(reminder, spotSaved))
        {
            _host.Changed();
            return;
        }

        var (headline, details) = BedtimeRoutine.Describe(reminder, spotSaved, _database.GetActiveSession(now)?.TaskTitle, BedtimeSchedule.Settings);
        var button = reminder.Task switch
        {
            BedtimeTask.EveningStep => AlertActionKind.EveningStepDone,
            BedtimeTask.WindDownStep => AlertActionKind.WindDownStepDone,
            BedtimeTask.Bed => AlertActionKind.SnoozeBedtime,
            _ => (AlertActionKind?)null,
        };
        // The heads-up is a natural point to bring back something put off earlier, on the card already going out.
        var held = reminder.Task == BedtimeTask.HeadsUp ? TakeAfterThisLine() : default;
        var stepName = BedtimeRoutine.StepName(reminder.Task, BedtimeSchedule.Settings);
        _ = NotifyAsync(headline, held.Line is { } line ? $"{details}\n{line}" : details, bedtimeButton: button, actions: held.Actions, saveSpot: BedtimeRoutine.OffersSaveSpot(reminder, spotSaved), bedtimeLabel: stepName is null ? null : DoneLabel(stepName));
        _host.Changed();
    }


    private void CheckSessionPrompt()
    {
        if (_database.TakeDuePrompt(_clock()) is { } prompt)
        {
            _ = NotifyAsync(prompt.Headline, prompt.Text);
            _host.Changed();
        }
    }

    /// <summary>
    /// Each one is marked as it goes out, so a wait only checks in once. Bought and Skip are on the card, since
    /// typing only works in the capture channel. They wait out the quiet part of the night, buttons and all.
    /// </summary>
    private void CheckWaitList()
    {
        var now = _clock();
        if (BedtimeSchedule.IsQuiet(now))
        {
            return;
        }

        foreach (var item in _database.TakeWaitCheckIns(now))
        {
            var why = item.Reason is { } reason ? $"You wanted it: {reason}\n" : "";
            var id = item.Id.ToString(CultureInfo.InvariantCulture);
            _ = NotifyAsync($"Wait's over: {item.Description}", $"{why}Price: {Briefing.Money(item.PriceCents)}\nStill want it?", CardTone.Money, toBudgetChannel: true,
                actions: [new CardAction("Bought", WaitBought + id), new CardAction(CardLabels.Skip, WaitSkip + id, ButtonRole.Skip)]);
        }
    }

    private void CheckPayCalendar()
    {
        if (_database.TakePayCalendarNotice(DateOnly.FromDateTime(_clock())) is { } notice)
        {
            _ = NotifyAsync("Pay calendar", notice, CardTone.Money, toBudgetChannel: true, canWait: true);
        }
    }

    private void CheckBriefing()
    {
        var now = _clock();
        var time = TimeOnly.FromDateTime(now);
        var today = DateOnly.FromDateTime(now).ToString("O", CultureInfo.InvariantCulture);
        if (BedtimeSchedule.IsQuiet(now) || time < _briefingTime || time >= BriefingCutoff || _database.GetPreference(LastBriefingKey) == today)
        {
            return;
        }

        // Built before it's marked sent, so one that fails partway is tried again on the next check instead of
        // leaving the day without one. Marking it and taking the held item are one write, then it goes out, so it
        // can't go out twice.
        var safeToSpend = _database.GetSafeToSpend(DateOnly.FromDateTime(now));
        var attention = BudgetReport.Attention(_database.GetBudgetReport(DateOnly.FromDateTime(now)));
        var open = _database.GetOpenItems();
        var study = StudyWindows.ForToday(now, open, Schedule, _database.GetShifts(DateOnly.FromDateTime(now)), BedtimeSchedule)?.Line;
        var body = Briefing.Build(now, open, Schedule, safeToSpend, _database.GetStaleBalances(DateOnly.FromDateTime(now)));
        var held = _database.InOneWrite(write =>
        {
            write.SetPreference(LastBriefingKey, today);
            return TakeAfterThisLine(write);
        });
        var briefing = string.Join("\n", new[] { body, study, held.Line }.OfType<string>().Concat(attention));

        // One ping a morning. Safe to spend is already a line in it, and the Budget view keeps the number too.
        _ = NotifyAsync("Today", briefing, actions: held.Actions);
    }

    /// <summary>Sunday evening, or Monday morning if the PC was off for it.</summary>
    private void CheckWeeklyReset()
    {
        var now = _clock();
        var time = TimeOnly.FromDateTime(now);
        var today = DateOnly.FromDateTime(now);
        var isDue = (today.DayOfWeek == DayOfWeek.Sunday && time >= _weeklyResetTime)
            || (today.DayOfWeek == DayOfWeek.Monday && time < WeeklyResetCutoff);
        var week = (today.DayOfWeek == DayOfWeek.Monday ? today.AddDays(-1) : today).ToString("O", CultureInfo.InvariantCulture);
        if (BedtimeSchedule.IsQuiet(now) || !isDue || _database.GetPreference(LastWeeklyResetKey) == week)
        {
            return;
        }

        // Built before it's marked sent, so one that fails partway is tried again on the next check, and marked
        // before it goes out, so it can't go out twice.
        var attention = BudgetReport.Attention(_database.GetBudgetReport(today));
        var review = _database.GetSpendingReview(today).Rows.Take(ReviewLinesInReset).Select(row => row.Label);
        var reset = string.Join("\n", new[] { Briefing.BuildWeek(now, _database.GetOpenItems()) }.Concat(attention).Concat(review));
        _database.SetPreference(LastWeeklyResetKey, week);
        _host.ShowWeeklyReset(reset);
        _ = NotifyAsync("Your week", reset);
    }

    /// <summary>Once a day. A failure is logged and tried again the next day or the next start.</summary>
    private void CheckBackup()
    {
        var now = _clock();
        var today = DateOnly.FromDateTime(now);
        if (_backupCheckedOn == today)
        {
            return;
        }

        _backupCheckedOn = today;
        _database.BackUpIfDue(now);
    }

}
