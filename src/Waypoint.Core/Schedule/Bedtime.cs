namespace Waypoint.Core.Schedule;

/// <summary>
/// Sleep targets, in local time. The minutes on each stage count back from bedtime, and null turns that stage off.
/// The two wind-down steps are named by the user, and each gets its own reminder and checklist line.
/// </summary>
public sealed record BedtimeSettings(
    double SleepHours = 8,
    int ReadyMinutes = 45,
    int? EveningStepMinutes = 75,
    int? WindDownStepMinutes = 30,
    TimeOnly? LatestBedtime = null,
    int? HeadsUpMinutes = 90,
    int? SaveSpotMinutes = 60,
    int? RoutineMinutes = 45,
    int? WrapUpMinutes = 15,
    int? BedMinutes = 0,
    int SnoozeMinutes = 15,
    bool QuietAfterWrapUp = false,
    string[]? RoutineSteps = null,
    bool LateCheckIns = true,
    string? EveningStepName = BedtimeSettings.DefaultEveningStepName,
    string? WindDownStepName = BedtimeSettings.DefaultWindDownStepName)
{
    public const string DefaultEveningStepName = "Evening step";
    public const string DefaultWindDownStepName = "Wind-down step";

    /// <summary>The evening step's name as the checklist and cards show it. A blank name falls back to the default.</summary>
    public string EveningStepLabel() => Label(EveningStepName, DefaultEveningStepName);

    /// <summary>The wind-down step's name as the checklist and cards show it. A blank name falls back to the default.</summary>
    public string WindDownStepLabel() => Label(WindDownStepName, DefaultWindDownStepName);

    private static string Label(string? name, string fallback) => string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
}

/// <summary>One night, ending a full night's sleep after bedtime. Shift is set when work is what sets the morning.</summary>
public sealed record BedtimePlan(DateTime Bedtime, DateTime NightEnds, ClassOccurrence? FirstClass, WorkShift? Shift = null, DateOnly Morning = default)
{
    private static readonly TimeOnly Noon = new(12, 0);

    /// <summary>True if there's class or work the next morning.</summary>
    public bool IsSchoolNight => Shift is not null || (FirstClass is { } first && TimeOnly.FromDateTime(first.Start) < Noon);

    /// <summary>What the morning is for, as it reads in a reminder.</summary>
    public string? Commitment => Shift is not null ? "work" : FirstClass?.Meeting.Course;
}

/// <summary>The stretch of a night the wind-down covers. The status card and routine checklist hang off this.</summary>
public sealed record WindDown(BedtimePlan Plan, DateTime Start, DateTime End);

/// <summary>Works back from the morning's first class or shift, and never later than the latest bedtime.</summary>
public sealed class BedtimeSchedule(BedtimeSettings settings, ClassSchedule? schedule, Func<DateOnly, WorkShift?>? firstShift = null)
{
    private static readonly TimeOnly DefaultLatestBedtime = new(1, 0);
    private static readonly TimeSpan AfterBedtime = TimeSpan.FromHours(1);

    /// <summary>Settable, so a change on the Settings page applies right away without a restart.</summary>
    public BedtimeSettings Settings { get; set; } = settings;

    /// <summary>The first night that isn't over yet, which may have started yesterday evening.</summary>
    public BedtimePlan GetPlan(DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var plan = ForMorning(today);
        return now < plan.NightEnds ? plan : ForMorning(today.AddDays(1));
    }

    /// <summary>A night's stages with the time each is due, earliest first. Anything turned off is left out.</summary>
    public IReadOnlyList<(BedtimeTask Task, DateTime At)> Stages(BedtimePlan plan) =>
        new (BedtimeTask Task, int? Minutes)[]
        {
            (BedtimeTask.HeadsUp, Settings.HeadsUpMinutes),
            (BedtimeTask.SaveSpot, Settings.SaveSpotMinutes),
            (BedtimeTask.Routine, Settings.RoutineMinutes),
            (BedtimeTask.WrapUp, Settings.WrapUpMinutes),
            (BedtimeTask.Bed, Settings.BedMinutes),
            (BedtimeTask.EveningStep, Settings.EveningStepMinutes),
            (BedtimeTask.WindDownStep, Settings.WindDownStepMinutes),
        }
        .Where(stage => stage.Minutes is not null)
        .Select(stage => (stage.Task, At: plan.Bedtime.AddMinutes(-stage.Minutes!.Value)))
        .OrderBy(stage => stage.At)
        .ToList();

    /// <summary>Tonight's wind-down, or null outside it and on nights with no morning to get up for.</summary>
    public WindDown? GetWindDown(DateTime now)
    {
        var plan = GetPlan(now);
        var stages = Stages(plan);
        if (!plan.IsSchoolNight || stages.Count == 0)
        {
            return null;
        }

        var start = stages[0].At;
        var end = plan.Bedtime + AfterBedtime;
        return now >= start && now < end ? new WindDown(plan, start, end) : null;
    }

    /// <summary>When to leave for the morning's shift or first class, whichever set the night, or null if neither.</summary>
    public DateTime? LeaveAt(BedtimePlan plan) =>
        plan.Shift?.LeaveAt ?? plan.FirstClass?.Start.AddMinutes(-plan.FirstClass.Meeting.TravelMinutes);

    /// <summary>From the final wrap-up until morning, if turned on, when anything that can wait does.</summary>
    public bool IsQuiet(DateTime now)
    {
        var plan = GetPlan(now);
        return Settings.QuietAfterWrapUp
            && plan.IsSchoolNight
            && now >= plan.Bedtime.AddMinutes(-(Settings.WrapUpMinutes ?? 0))
            && now < plan.NightEnds;
    }

    private BedtimePlan ForMorning(DateOnly morning)
    {
        var sleep = TimeSpan.FromHours(Settings.SleepHours);
        var latestTime = Settings.LatestBedtime ?? DefaultLatestBedtime;
        var latest = (latestTime.Hour < 12 ? morning : morning.AddDays(-1)).ToDateTime(latestTime);

        var firstClass = schedule?.GetFirstClass(morning);
        var classLeave = firstClass?.Start.AddMinutes(-firstClass.Meeting.TravelMinutes);
        var shift = firstShift?.Invoke(morning) is { IsMorning: true } morningShift && !(classLeave <= morningShift.LeaveAt) ? morningShift : null;

        var bedtime = latest;
        if ((shift?.LeaveAt ?? classLeave) is { } leaveAt)
        {
            var workedBack = leaveAt.AddMinutes(-Settings.ReadyMinutes) - sleep;
            bedtime = workedBack < latest ? workedBack : latest;
        }

        return new BedtimePlan(bedtime, bedtime + sleep, firstClass, shift, morning);
    }
}

public enum BedtimeTask
{
    HeadsUp,
    SaveSpot,
    Routine,
    EveningStep,
    WindDownStep,
    WrapUp,
    Bed,

    /// <summary>Not a stage. Marked once late-night check-ins are over for the night, so "no more bedtime reminders tonight" ends them too.</summary>
    LateCheckIns,
}

/// <summary>The stage to send now. Also holds routine reminders that came due while the PC was off.</summary>
public sealed record BedtimeReminder(BedtimeTask Task, BedtimePlan Plan, IReadOnlyList<BedtimeTask>? Also = null);

/// <summary>What's gone out tonight. It lives outside the app so a restart doesn't send anything twice.</summary>
public interface IBedtimeLog
{
    DateTime? BedtimeSnoozedUntil { get; set; }

    bool WasSent(DateOnly morning, BedtimeTask task);

    void MarkSent(DateOnly morning, BedtimeTask task);
}

/// <summary>Decides when to send the evening stages, once each, on nights before a morning class or shift.</summary>
public sealed class BedtimeReminders(BedtimeSchedule schedule, IBedtimeLog? log = null)
{
    private static readonly TimeSpan BedStageWindow = TimeSpan.FromMinutes(30);

    private readonly IBedtimeLog _log = log ?? new MemoryLog();

    /// <summary>
    /// Returns the stage to send right now, if any. A PC that wakes late gets only the latest stage, not
    /// the pile, and past bedtime it's too late for anything but the bedtime reminder itself.
    /// </summary>
    public BedtimeReminder? Check(DateTime now)
    {
        var plan = schedule.GetPlan(now);
        if (!plan.IsSchoolNight)
        {
            return null;
        }

        if (TakeSnooze(now))
        {
            return new BedtimeReminder(BedtimeTask.Bed, plan);
        }

        if (now >= plan.Bedtime + BedStageWindow)
        {
            return null;
        }

        var due = schedule.Stages(plan)
            .Where(stage => stage.At <= now && (now < plan.Bedtime || stage.Task == BedtimeTask.Bed) && !_log.WasSent(plan.Morning, stage.Task))
            .ToList();
        if (due.Count == 0)
        {
            return null;
        }

        foreach (var (task, _) in due)
        {
            _log.MarkSent(plan.Morning, task);
        }

        var also = due.SkipLast(1).Select(stage => stage.Task).Where(task => task is BedtimeTask.EveningStep or BedtimeTask.WindDownStep).ToList();
        return new BedtimeReminder(due[^1].Task, plan, also.Count > 0 ? also : null);
    }

    /// <summary>A snooze comes back once. One left over from a night that's over is dropped.</summary>
    private bool TakeSnooze(DateTime now)
    {
        if (_log.BedtimeSnoozedUntil is not { } until || until > now)
        {
            return false;
        }

        _log.BedtimeSnoozedUntil = null;
        return now - until < BedStageWindow;
    }

    private sealed class MemoryLog : IBedtimeLog
    {
        private readonly HashSet<(DateOnly, BedtimeTask)> _sent = [];

        public DateTime? BedtimeSnoozedUntil { get; set; }

        public bool WasSent(DateOnly morning, BedtimeTask task) => _sent.Contains((morning, task));

        public void MarkSent(DateOnly morning, BedtimeTask task) => _sent.Add((morning, task));
    }
}
