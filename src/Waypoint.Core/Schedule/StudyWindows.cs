using System.Globalization;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Schedule;

/// <summary>One open stretch today and the thing worth studying in it, with a first action to start on.</summary>
public sealed record StudyOpportunity(StoredItem Target, DateTime Start, DateTime End, string FirstAction)
{
    /// <summary>How it reads on To do and in the briefing.</summary>
    public string Line =>
        $"Study window: {Time(Start)} to {Time(End)} for {Target.Title}{(Target.DueDate is { } due ? $" ({due.ToString("ddd", CultureInfo.InvariantCulture)})" : "")}";

    private static string Time(DateTime time) => time.ToString("h:mm tt", CultureInfo.InvariantCulture);
}

/// <summary>
/// Finds one realistic study window from what's already known: classes, shifts, timed items, exams, and
/// deadlines. It's worked out fresh each time and nothing is stored, so a missed window simply isn't
/// there the next day, and there's never a pile of unfinished sessions or anything to catch up on.
/// </summary>
public static class StudyWindows
{
    public const int MinMinutes = 30;
    public const int MaxMinutes = 60;
    public const int ExamDays = 7;
    public const int DeadlineDays = 3;

    /// <summary>Windows start on the next five minutes.</summary>
    public const int RoundMinutes = 5;

    /// <summary>Without a wind-down tonight, suggestions stop here.</summary>
    public static readonly TimeOnly DefaultDayEnd = new(22, 0);

    private static readonly TimeOnly DayStart = new(8, 0);
    private static readonly TimeSpan TimedItemLength = TimeSpan.FromHours(1);

    /// <summary>The soonest exam this week, or else the soonest deadline in the next few days. Past ones are left alone.</summary>
    public static StoredItem? PickTarget(IReadOnlyList<StoredItem> open, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var live = open.Where(item => item.ParentRef is null && !item.Done && item.DueDate >= today && !(item.DueAt?.LocalDateTime <= now)).ToList();

        return live
                .Where(item => item.Kind == ItemKind.Exam && item.DueDate!.Value.DayNumber - today.DayNumber <= ExamDays)
                .OrderBy(item => item.DueDate).ThenBy(item => item.DueAt ?? DateTimeOffset.MaxValue).ThenBy(item => item.Id)
                .FirstOrDefault()
            ?? live
                .Where(item => item.Kind == ItemKind.Deadline && !ItemGroups.IsScheduled(item) && item.DueDate!.Value.DayNumber - today.DayNumber <= DeadlineDays)
                .OrderBy(item => item.DueDate).ThenBy(item => item.DueAt ?? DateTimeOffset.MaxValue).ThenBy(item => item.Id)
                .FirstOrDefault();
    }

    /// <summary>When the day's suggestions stop: the start of tonight's wind-down if there is one, else 10 PM.</summary>
    public static DateTime DayEnd(DateTime now, BedtimeSchedule? bedtime)
    {
        var fallback = DateOnly.FromDateTime(now).ToDateTime(DefaultDayEnd);
        if (bedtime is null)
        {
            return fallback;
        }

        var plan = bedtime.GetPlan(now);
        var stages = bedtime.Stages(plan);
        return plan.IsSchoolNight && stages.Count > 0 && stages[0].At < fallback ? stages[0].At : fallback;
    }

    /// <summary>What's already booked today: classes with travel, shifts with the commute, and timed items.</summary>
    public static IReadOnlyList<(DateTime Start, DateTime End)> Busy(DateOnly day, ClassSchedule? classes, IEnumerable<WorkShift> shifts, IEnumerable<StoredItem> open)
    {
        var busy = new List<(DateTime Start, DateTime End)>();
        foreach (var occurrence in classes?.GetClasses(day) ?? [])
        {
            busy.Add((occurrence.Start.AddMinutes(-occurrence.Meeting.TravelMinutes), day.ToDateTime(occurrence.Meeting.End).AddMinutes(occurrence.Meeting.TravelMinutes)));
        }

        foreach (var shift in shifts.Where(shift => shift.Date == day))
        {
            busy.Add((shift.LeaveAt, day.ToDateTime(shift.End).AddMinutes(shift.CommuteMinutes)));
        }

        foreach (var item in open.Where(item => item.DueDate == day && item.DueAt is not null && ItemGroups.IsScheduled(item)))
        {
            var start = item.DueAt!.Value.LocalDateTime;
            busy.Add((start, start + TimedItemLength));
        }

        return busy.OrderBy(entry => entry.Start).ToList();
    }

    /// <summary>The first free stretch of at least half an hour from now until the day ends, capped at an hour.</summary>
    public static (DateTime Start, DateTime End)? FirstFree(DateTime now, DateTime dayEnd, IReadOnlyList<(DateTime Start, DateTime End)> busy)
    {
        var dayStart = DateOnly.FromDateTime(now).ToDateTime(DayStart);
        var cursor = RoundUp(now > dayStart ? now : dayStart);

        foreach (var (start, end) in busy.OrderBy(entry => entry.Start).Append((dayEnd, dayEnd)))
        {
            var gapEnd = start < dayEnd ? start : dayEnd;
            if ((gapEnd - cursor).TotalMinutes >= MinMinutes)
            {
                var length = Math.Min((gapEnd - cursor).TotalMinutes, MaxMinutes);
                return (cursor, cursor.AddMinutes(length));
            }

            if (end > cursor)
            {
                cursor = RoundUp(end);
            }

            if (cursor >= dayEnd)
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether To do shows the window in place of the pick: when it starts now or within one rounding step, since
    /// a window always starts on the next five minutes, or whenever nothing else is picked. A window the user
    /// skipped today or turned down with Not now steps aside, the same as the pick does.
    /// </summary>
    public static bool ShowsNow(StudyOpportunity study, bool hasPick, DateTime now, IReadOnlyCollection<string> skipped, IReadOnlySet<string> declined)
    {
        var target = ItemRef.Format(study.Target);
        if (skipped.Contains(target) || declined.Contains(target))
        {
            return false;
        }

        return !hasPick || study.Start <= now.AddMinutes(RoundMinutes);
    }

    /// <summary>Today's one suggestion, or null if there's nothing to study or no room for it.</summary>
    public static StudyOpportunity? Find(DateTime now, IReadOnlyList<StoredItem> open, IReadOnlyList<(DateTime Start, DateTime End)> busy, DateTime dayEnd)
    {
        if (PickTarget(open, now) is not { } target || FirstFree(now, dayEnd, busy) is not { } window)
        {
            return null;
        }

        return new StudyOpportunity(target, window.Start, window.End, FirstAction(target));
    }

    /// <summary>Everything above in one call, for To do and the briefing.</summary>
    public static StudyOpportunity? ForToday(DateTime now, IReadOnlyList<StoredItem> open, ClassSchedule? classes, IEnumerable<WorkShift> shifts, BedtimeSchedule? bedtime) =>
        Find(now, open, Busy(DateOnly.FromDateTime(now), classes, shifts, open), DayEnd(now, bedtime));

    /// <summary>A concrete first action that needs no sorter. An item's own steps still come first when it's started.</summary>
    public static string FirstAction(StoredItem target)
    {
        var subject = target.Course ?? target.Title;
        return target.Kind == ItemKind.Exam
            ? $"Open your {subject} notes and read the first page"
            : $"Open {target.Title} and read what it asks for";
    }

    /// <summary>To the next five minutes, so windows start on a time that reads easily.</summary>
    private static DateTime RoundUp(DateTime time)
    {
        var minutes = (int)Math.Ceiling(time.TimeOfDay.TotalMinutes / RoundMinutes) * RoundMinutes;
        return time.Date.AddMinutes(minutes);
    }
}
