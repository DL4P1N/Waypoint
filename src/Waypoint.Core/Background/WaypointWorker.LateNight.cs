using System.Globalization;
using Waypoint.Core.Discord;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Background;

/// <summary>
/// Late-night check-ins on Discord, past bedtime on nights with a wind-down, while the PC is still in use. Buttons:
/// Done for each wind-down step still unchecked, Save my spot and stop, Stop at..., and Not tonight. Every button
/// carries the night it was sent for, so an old card can't change another night.
/// </summary>
public sealed partial class WaypointWorker
{
    /// <summary>The check-in buttons. The night follows as yyyyMMdd, and a Stop at pick adds the time as HHmm on that date or the next.</summary>
    public const string LateSaveAndStop = "late-save:";
    public const string LateStopAt = "late-stop-at:";
    public const string LateStopAtPick = "late-stop-pick:";
    public const string LateNotTonight = "late-not-tonight:";
    public const string LateWindDownStepDone = "late-done:";
    public const string LateEveningStepDone = "late-evening-step:";

    private const string NightFormat = "yyyyMMdd";
    private const string PickedTimeFormat = "yyyyMMddHHmm";

    /// <summary>What the PC's idle time comes from, if anything. The server's is the PC's last report.</summary>
    public IUserIdle? UserIdle { get; }

    /// <summary>Clears any check-in times saved for a night that's over, such as at app exit.</summary>
    public void ForgetLateNight() => _lateCheckIns.Forget(_clock());

    /// <summary>Discord only: without it, or before this copy knows it may use the bot, nothing is read or sent.</summary>
    private void CheckLateNight()
    {
        var now = _clock();
        if (Discord is null)
        {
            _lateCheckIns.Forget(now);
            return;
        }

        if (_lateCheckIns.Check(now) is not { } checkIn)
        {
            return;
        }

        var (headline, text, actions) = LateNightCard(checkIn);
        _ = NotifyAsync(headline, text, actions: actions, saveSpot: checkIn.StopTime, feedback: Capture.Feedback.LateNight);
        _host.Changed();
    }

    /// <summary>A check-in's lines and buttons, with the wind-down steps first while they're unchecked.</summary>
    private (string Headline, string Text, IReadOnlyList<CardAction> Actions) LateNightCard(LateCheckIn checkIn)
    {
        var night = Night(checkIn.Plan.Morning);
        var unmarked = _lateCheckIns.Unmarked(_database.GetRoutineMarks(checkIn.Plan.Morning));
        var (headline, text) = _lateCheckIns.Describe(checkIn, unmarked);
        var actions = new List<CardAction>();
        var settings = BedtimeSchedule.Settings;
        if (unmarked.Contains(settings.EveningStepLabel()))
        {
            actions.Add(new CardAction(DoneLabel(settings.EveningStepLabel()), LateEveningStepDone + night));
        }

        if (unmarked.Contains(settings.WindDownStepLabel()))
        {
            actions.Add(new CardAction(DoneLabel(settings.WindDownStepLabel()), LateWindDownStepDone + night));
        }

        if (!checkIn.StopTime)
        {
            actions.Add(new CardAction(LateCheckIns.SaveAndStop, LateSaveAndStop + night));
            actions.Add(new CardAction(LateCheckIns.StopAtLabel, LateStopAt + night, ButtonRole.Other));
            actions.Add(new CardAction(LateCheckIns.NotTonight, LateNotTonight + night, ButtonRole.Skip));
        }

        return (headline, text, actions);
    }

    /// <summary>Save my spot and stop ends that night's check-ins, then the bridge opens the Save my spot form.</summary>
    private bool OpensSpotFormForLateNight(string action)
    {
        if (TakeNight(action, LateSaveAndStop) is not { } morning || !_lateCheckIns.IsCurrent(morning, _clock()))
        {
            return false;
        }

        _lateCheckIns.StopTonight(morning, _clock());
        _host.Changed();
        return true;
    }

    /// <summary>A check-in's buttons, or null for any other button. A press after its night's window says it has run out.</summary>
    private ButtonResult? OnLateNightButton(string action)
    {
        var now = _clock();
        if (TakeNight(action, LateSaveAndStop) is { } toSave)
        {
            // Only reached without the form, so the session going now is saved as it is.
            if (!_lateCheckIns.IsCurrent(toSave, now))
            {
                return new ButtonResult(Outcome: LateCheckIns.RunOut);
            }

            var stopped = _lateCheckIns.StopTonight(toSave, now);
            return _database.GetActiveSession(now)?.TaskTitle is { } title
                ? new ButtonResult(Outcome: $"{_database.SaveSpotByHand(title, null, null, null, null, now).Text} {stopped}")
                : new ButtonResult(Outcome: stopped);
        }

        if (TakeNight(action, LateNotTonight) is { } notTonight)
        {
            return new ButtonResult(Outcome: _lateCheckIns.StopTonight(notTonight, now));
        }

        if (TakeNight(action, LateStopAt) is { } stopAtNight)
        {
            if (!_lateCheckIns.IsCurrent(stopAtNight, now))
            {
                return new ButtonResult(Outcome: LateCheckIns.RunOut);
            }

            var night = Night(stopAtNight);
            var picks = LateCheckIns.StopAtChoices(now)
                .Select(at => new CardAction(LateCheckIns.Clock(at), $"{LateStopAtPick}{night}:{at.ToString(PickedTimeFormat, CultureInfo.InvariantCulture)}", ButtonRole.Other))
                .Append(new CardAction(LateCheckIns.NotTonight, LateNotTonight + night, ButtonRole.Skip))
                .ToArray();
            return new ButtonResult(DiscordText.Reply("Stop at what time?") with { Actions = picks });
        }

        if (action.StartsWith(LateStopAtPick, StringComparison.Ordinal))
        {
            var parts = action[LateStopAtPick.Length..].Split(':');
            return parts.Length == 2
                && DateOnly.TryParseExact(parts[0], NightFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var pickNight)
                && DateTime.TryParseExact(parts[1], PickedTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                    ? new ButtonResult(Outcome: _lateCheckIns.StopAt(pickNight, at, now))
                    : new ButtonResult(Outcome: LateCheckIns.RunOut);
        }

        var step = TakeNight(action, LateEveningStepDone) is { } eveningNight ? (Night: eveningNight, Step: BedtimeSchedule.Settings.EveningStepLabel())
            : TakeNight(action, LateWindDownStepDone) is { } windDownNight ? (Night: windDownNight, Step: BedtimeSchedule.Settings.WindDownStepLabel())
            : ((DateOnly Night, string Step)?)null;
        if (step is { } mark)
        {
            return MarkFromCheckIn(mark.Night, mark.Step, now);
        }

        return null;
    }

    /// <summary>A wind-down step's Done, marked the same way as the routine card. The card is drawn again without it.</summary>
    private ButtonResult MarkFromCheckIn(DateOnly morning, string step, DateTime now)
    {
        if (!_lateCheckIns.IsCurrent(morning, now))
        {
            return new ButtonResult(Outcome: LateCheckIns.RunOut);
        }

        var reply = BedtimeRoutine.Mark(_database, BedtimeSchedule, step, true, now);
        _host.Changed();
        if (LateCheckIns.Window(BedtimeSchedule, now) is not { } plan || _database.WasSent(morning, BedtimeTask.LateCheckIns))
        {
            return new ButtonResult(Outcome: reply.Text);
        }

        var (headline, text, actions) = LateNightCard(new LateCheckIn(plan, now));
        return new ButtonResult(DiscordText.Notice(headline, $"{reply.Text}\n{text}") with { Actions = actions, FeedbackFeature = Capture.Feedback.LateNight });
    }

    /// <summary>A wind-down step's button, such as "Done: Stretch".</summary>
    public static string DoneLabel(string step) => $"Done: {step}";

    private static string Night(DateOnly morning) => morning.ToString(NightFormat, CultureInfo.InvariantCulture);

    private static DateOnly? TakeNight(string action, string prefix) =>
        action.StartsWith(prefix, StringComparison.Ordinal)
        && DateOnly.TryParseExact(action[prefix.Length..], NightFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var morning)
            ? morning
            : null;
}
