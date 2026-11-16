using System.Globalization;

namespace Waypoint.Core.Schedule;

/// <summary>
/// How long since the last keyboard or mouse input anywhere on the PC. Null when there's no reading, such as on a
/// server that hasn't heard from the PC yet. Nothing else about how the PC is used is seen or kept.
/// </summary>
public interface IUserIdle
{
    TimeSpan? IdleFor(DateTime now);
}

/// <summary>Where tonight's check-ins keep their place, so a restart doesn't send one early or start them again.</summary>
public interface ILateCheckInLog : IBedtimeLog
{
    /// <summary>In UTC, so the spacing holds when the clocks change.</summary>
    DateTime? LastCheckIn { get; set; }

    /// <summary>The time picked with Stop at, in UTC. One card goes out then, and the check-ins end.</summary>
    DateTime? CheckInStopAt { get; set; }
}

/// <summary>One check-in to send. StopTime is the one reminder at the time picked with Stop at.</summary>
public sealed record LateCheckIn(BedtimePlan Plan, DateTime At, bool StopTime = false);

/// <summary>
/// Past bedtime on a night with a wind-down, a card about every 30 minutes while the PC is still in use. Once the PC
/// has had no input for 15 minutes, it's taken as bed and nothing more goes out that night. The idle time is only
/// read inside that window.
/// </summary>
public sealed class LateCheckIns(BedtimeSchedule schedule, ILateCheckInLog log, IUserIdle? idle)
{
    public const string SaveAndStop = "Save my spot and stop";
    public const string StopAtLabel = "Stop at...";
    public const string NotTonight = "Not tonight";
    public const string RunOut = "That button has run out.";

    public static readonly TimeSpan InUseWithin = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(30);

    /// <summary>
    /// With no reading, the PC counts as gone for the night once this long has passed since the last input a reading
    /// showed, or since readings were first missed. It's the same as a real idle reading, so a short gap in the
    /// server's reports, such as waiting on the first one or after a restart, only sends nothing for now.
    /// </summary>
    public static readonly TimeSpan UnknownFor = InUseWithin;

    /// <summary>How early the PC starts reporting to the server, so the server has a reading by bedtime.</summary>
    public static readonly TimeSpan ReportLead = TimeSpan.FromMinutes(5);

    /// <summary>The picks Stop at offers, counted from when it's pressed.</summary>
    public static readonly int[] StopAtMinutes = [15, 30, 45, 60];

    // A stop time the PC slept through is dropped rather than sent late.
    private static readonly TimeSpan StopTimeWindow = TimeSpan.FromMinutes(30);

    // Kept in memory only, in UTC: the last input a reading showed, or when readings were first missed.
    private DateTime? _lastInput;
    private DateTime? _unknownSince;

    // Whether the log may still hold a time from a night that's over, so it's only cleared when there's something to clear.
    private bool _mayHoldTimes = true;

    /// <summary>
    /// Tonight's plan from bedtime, less <paramref name="lead"/>, until morning, on nights with a wind-down and with
    /// check-ins on. Null otherwise.
    /// </summary>
    public static BedtimePlan? Window(BedtimeSchedule schedule, DateTime now, TimeSpan lead = default)
    {
        if (!schedule.Settings.LateCheckIns)
        {
            return null;
        }

        var plan = schedule.GetPlan(now);
        return plan.IsSchoolNight && schedule.Stages(plan).Count > 0 && now >= plan.Bedtime - lead && now < plan.NightEnds ? plan : null;
    }

    /// <summary>The check-in to send right now, if any. Each one returned is counted as sent.</summary>
    public LateCheckIn? Check(DateTime now)
    {
        if (Window(schedule, now) is not { } plan)
        {
            Forget(now);
            return null;
        }

        var utcNow = now.ToUniversalTime();
        var bedtime = plan.Bedtime.ToUniversalTime();
        if (log.WasSent(plan.Morning, BedtimeTask.LateCheckIns))
        {
            return null;
        }

        // Anything left from an earlier night, such as one the PC was shut down in, goes now.
        if (_mayHoldTimes)
        {
            if (log.LastCheckIn < bedtime)
            {
                log.LastCheckIn = null;
            }

            if (log.CheckInStopAt < bedtime)
            {
                log.CheckInStopAt = null;
            }

            _mayHoldTimes = false;
        }

        if (idle?.IdleFor(now) is not { } idleFor)
        {
            _unknownSince ??= utcNow;
            if (utcNow - (_lastInput ?? _unknownSince) >= UnknownFor)
            {
                End(plan.Morning);
            }

            return null;
        }

        _unknownSince = null;
        _lastInput = utcNow - idleFor;
        if (idleFor >= InUseWithin)
        {
            End(plan.Morning);
            return null;
        }

        if (log.CheckInStopAt is { } stopAt)
        {
            if (stopAt > utcNow)
            {
                return null;
            }

            End(plan.Morning);
            return utcNow - stopAt < StopTimeWindow ? new LateCheckIn(plan, now, StopTime: true) : null;
        }

        // The bedtime reminder counts as the first, so the next comes 30 minutes after it.
        var last = log.LastCheckIn is { } sent ? sent
            : log.WasSent(plan.Morning, BedtimeTask.Bed) ? bedtime
            : (DateTime?)null;
        if (last is { } previous && utcNow - previous < Every)
        {
            return null;
        }

        log.LastCheckIn = utcNow;
        _mayHoldTimes = true;
        return new LateCheckIn(plan, now);
    }

    /// <summary>
    /// Outside a night's window, clears the last check-in time and any Stop at time, such as at app exit or after a
    /// night the PC was shut down in. Inside one they're still needed, so they stay.
    /// </summary>
    public void Forget(DateTime now)
    {
        if (!_mayHoldTimes || Window(schedule, now) is not null)
        {
            return;
        }

        if (log.LastCheckIn is not null)
        {
            log.LastCheckIn = null;
        }

        if (log.CheckInStopAt is not null)
        {
            log.CheckInStopAt = null;
        }

        _mayHoldTimes = false;
    }

    /// <summary>Whether a card's buttons for this night still apply: only while that night's window is open.</summary>
    public bool IsCurrent(DateOnly morning, DateTime now) => Window(schedule, now)?.Morning == morning;

    /// <summary>Not tonight, or Save my spot and stop, for the night the card was sent. The next night starts as usual.</summary>
    public string StopTonight(DateOnly morning, DateTime now)
    {
        if (!IsCurrent(morning, now))
        {
            return RunOut;
        }

        End(morning);
        return "No more check-ins tonight.";
    }

    /// <summary>One card at the time picked, and none before it.</summary>
    public string StopAt(DateOnly morning, DateTime at, DateTime now)
    {
        if (!IsCurrent(morning, now))
        {
            return RunOut;
        }

        if (log.WasSent(morning, BedtimeTask.LateCheckIns))
        {
            return "Check-ins are already off for tonight.";
        }

        if (at <= now)
        {
            return "That time has passed.";
        }

        log.CheckInStopAt = at.ToUniversalTime();
        _mayHoldTimes = true;
        return $"Okay. One reminder at {Clock(at)}, then no more check-ins tonight.";
    }

    /// <summary>The two wind-down steps, each only if its reminder is on, that aren't checked off tonight.</summary>
    public IReadOnlyList<string> Unmarked(IReadOnlySet<string> marks) =>
        BedtimeRoutine.WindDownSteps(schedule.Settings)
        .Where(step => !marks.Contains(step))
        .ToList();

    /// <summary>
    /// The card's headline and lines: anything still unchecked first, then the first thing tomorrow, when to leave,
    /// and the sleep still possible.
    /// </summary>
    public (string Headline, string Text) Describe(LateCheckIn checkIn, IReadOnlyList<string>? unmarked = null)
    {
        var plan = checkIn.Plan;
        var headline = checkIn.StopTime ? $"It's {Clock(checkIn.At)}, the time you picked to stop" : $"It's {Clock(checkIn.At)}";
        var lines = new List<string>();
        if (unmarked is { Count: > 0 })
        {
            lines.Add($"Not checked off yet: {string.Join(", ", unmarked.Select(step => step.ToLowerInvariant()))}");
        }

        if (plan.Shift is { } shift)
        {
            lines.Add($"Tomorrow: work at {Clock(shift.Starts)}");
        }
        else if (plan.FirstClass is { } first)
        {
            lines.Add($"Tomorrow: {first.Meeting.Course} at {Clock(first.Start)}");
        }

        if (schedule.LeaveAt(plan) is { } leave)
        {
            lines.Add(plan.Shift is { IsRemote: true } ? $"Work starts: {Clock(leave)}" : $"Leave by: {Clock(leave)}");
            lines.Add($"Sleep if you stop now: {Sleep(leave.AddMinutes(-schedule.Settings.ReadyMinutes) - checkIn.At)}");
        }

        return (headline, string.Join("\n", lines));
    }

    /// <summary>The picks for Stop at, as times on the clock, rounded to 5 minutes.</summary>
    public static IReadOnlyList<DateTime> StopAtChoices(DateTime now)
    {
        var start = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute / 5 * 5, 0, now.Kind);
        return StopAtMinutes.Select(minutes => start.AddMinutes(minutes)).ToList();
    }

    public static string Clock(DateTime time) => time.ToString("h:mm tt", CultureInfo.InvariantCulture);

    private static string Sleep(TimeSpan span)
    {
        var minutes = (int)Math.Floor(span.TotalMinutes / 5) * 5;
        if (minutes <= 0)
        {
            return "none before getting ready";
        }

        var (hours, rest) = (minutes / 60, minutes % 60);
        var hourText = hours == 1 ? "1 hour" : $"{hours} hours";
        return hours == 0 ? $"{rest} minutes" : rest == 0 ? hourText : $"{hourText} {rest} minutes";
    }

    /// <summary>Ends the night and forgets when the last one went out, so nothing hinting at PC use stays saved.</summary>
    private void End(DateOnly morning)
    {
        log.MarkSent(morning, BedtimeTask.LateCheckIns);
        log.CheckInStopAt = null;
        log.LastCheckIn = null;
    }
}
