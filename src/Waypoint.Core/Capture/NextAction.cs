using System.Globalization;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Capture;

/// <summary>Something at a set time that work has to stop for. Starts is when to stop, such as the leave time for a class.</summary>
public sealed record Commitment(DateTime Starts, DateTime Ends, string Label);

/// <summary>The one thing suggested at the top of To do, and one short line saying why.</summary>
public sealed record NextActionPick(StoredItem Item, string Reason);

/// <summary>
/// Picks the one thing to suggest at the top of To do, from dates, steps, and the schedule alone, so it's
/// instant, works offline, and gives the same answer every time. It weighs how much is left against the
/// days until it's due, not just the date, and only picks something whose next sitting fits before the
/// next class, shift, event, or bedtime. It suggests nothing while one of those is happening. The sizes below are
/// the starting guesses, and <see cref="SuggestionHabits"/> replaces them once there's enough history to go on.
/// </summary>
public static class NextAction
{
    /// <summary>One step is a short sitting, since the sorter makes each one under 30 minutes.</summary>
    public const int StepMinutes = 20;

    /// <summary>A task with no steps is counted as a small one, the same as the energy buttons do.</summary>
    public const int TaskMinutes = 15;

    /// <summary>A deadline or exam with no steps starts with one longer sitting.</summary>
    public const int SittingMinutes = 60;

    /// <summary>How much a deadline with no steps is guessed to need in all.</summary>
    public const int DeadlineMinutes = 120;

    /// <summary>How much studying an exam with no steps is guessed to need in all.</summary>
    public const int ExamMinutes = 180;

    /// <summary>How much work one day is counted as holding, so a big item needs more days.</summary>
    public const int DayMinutes = 120;

    /// <summary>Time kept free before a commitment, to wrap up.</summary>
    public const int BufferMinutes = 5;

    /// <summary>A free stretch shorter than this is worth naming in the reason.</summary>
    public static readonly TimeSpan ShortStretch = TimeSpan.FromHours(3);

    private static readonly TimeSpan DefaultEventLength = TimeSpan.FromHours(1);
    private static readonly TimeSpan NightLength = TimeSpan.FromHours(8);
    private const int LongEventTitle = 24;

    /// <summary>
    /// The best open item to start now, or null if nothing fits. Skipped holds the refs of items the user
    /// passed on today. Only top-level tasks, deadlines, and exams count, and steps only count toward their parent.
    /// Habits sizes the work from the user's own history. At a time of day when reminders are almost always passed
    /// on, anything not pressing is ordered shortest first, so the suggestion is easier to say yes to.
    /// </summary>
    public static NextActionPick? Pick(IReadOnlyList<StoredItem> open, IReadOnlyList<Commitment> busy, DateTime now, IReadOnlyCollection<string> skipped, SuggestionHabits? habits = null, IReadOnlySet<string>? declined = null)
    {
        habits ??= SuggestionHabits.Defaults;

        // Something just turned down with Not now is passed over like a skip, until its snooze window ends.
        if (declined is { Count: > 0 })
        {
            skipped = skipped.Concat(NotNowDeclines.WithParents(declined, open)).ToHashSet(StringComparer.Ordinal);
        }

        var upcoming = busy.Where(commitment => commitment.Ends > now).ToList();
        if (upcoming.Any(commitment => commitment.Starts <= now))
        {
            return null;
        }

        var next = upcoming.MinBy(commitment => commitment.Starts);
        var free = next is null ? (TimeSpan?)null : next.Starts - now - TimeSpan.FromMinutes(BufferMinutes);
        var today = DateOnly.FromDateTime(now);

        var hard = habits.IsHardTime(now);
        var best = Candidates(open, now, skipped, habits)
            .Where(candidate => free is not { } room || TimeSpan.FromMinutes(candidate.SittingMinutes) <= room)
            .OrderBy(candidate => hard && !candidate.Pressing(today, habits) ? 1 : 0)
            .ThenBy(candidate => hard && !candidate.Pressing(today, habits) ? candidate.SittingMinutes : 0)
            .ThenBy(candidate => candidate.Slack(today, habits) ?? int.MaxValue)
            // With the same days to spare, today's and upcoming work comes before anything left from earlier, and
            // among those from earlier the most recent comes first, so a task left weeks ago doesn't win every day.
            .ThenBy(candidate => candidate.Item.DueDate < today ? 1 : 0)
            .ThenBy(candidate => candidate.Item.DueDate is { } due ? (due < today ? -due.DayNumber : due.DayNumber) : int.MaxValue)
            .ThenBy(candidate => candidate.Item.DueAt ?? DateTimeOffset.MaxValue)
            .ThenByDescending(candidate => candidate.WorkMinutes)
            .ThenBy(candidate => ItemRef.Format(candidate.Item), StringComparer.Ordinal)
            .FirstOrDefault();

        return best is null ? null : new NextActionPick(best.Item, Reason(best, today, free < ShortStretch ? next : null, hard && !best.Pressing(today, habits)));
    }

    /// <summary>
    /// Everything work has to stop for, from today and tomorrow: classes from their leave time, shifts from
    /// theirs, timed events and exams, and bedtime if it's known. EndOf gives an event's stored end time.
    /// </summary>
    public static IReadOnlyList<Commitment> Commitments(
        DateTime now,
        ClassSchedule? classes,
        IEnumerable<WorkShift> shifts,
        IEnumerable<StoredItem> items,
        Func<StoredItem, TimeOnly?>? endOf = null,
        DateTime? bedtime = null)
    {
        var today = DateOnly.FromDateTime(now);
        var found = new List<Commitment>();

        if (classes is not null)
        {
            foreach (var day in new[] { today, today.AddDays(1) })
            {
                found.AddRange(classes.GetClasses(day).Select(occurrence => new Commitment(
                    occurrence.Start.AddMinutes(-occurrence.Meeting.TravelMinutes),
                    day.ToDateTime(occurrence.Meeting.End),
                    "class")));
            }
        }

        foreach (var shift in shifts)
        {
            var ends = shift.Date.ToDateTime(shift.End);
            found.Add(new Commitment(shift.LeaveAt, ends <= shift.Starts ? ends.AddDays(1) : ends, "work"));
        }

        foreach (var item in items)
        {
            if (item.Done || item.ParentRef is not null || item.DueAt is not { } dueAt || item.Kind == ItemKind.Reminder || !ItemGroups.IsScheduled(item))
            {
                continue;
            }

            var starts = dueAt.LocalDateTime;
            var ends = endOf?.Invoke(item) is { } end ? DateOnly.FromDateTime(starts).ToDateTime(end) : starts.Add(DefaultEventLength);
            if (ends <= starts)
            {
                ends = ends.AddDays(1);
            }

            found.Add(new Commitment(starts, ends, EventLabel(item)));
        }

        if (bedtime is { } bed)
        {
            found.Add(new Commitment(bed, bed.Add(NightLength), "bed"));
        }

        return found.Where(commitment => commitment.Ends > now).OrderBy(commitment => commitment.Starts).ToList();
    }

    /// <summary>
    /// What shows in the suggestion's place while a class, shift, or event is going, so the empty spot doesn't look broken.
    /// Null when nothing is going, or only bedtime is, which has its own card.
    /// </summary>
    public static string? BusyLine(IReadOnlyList<Commitment> busy, DateTime now)
    {
        if (busy.Where(commitment => commitment.Starts <= now && commitment.Ends > now && commitment.Label != "bed").MaxBy(commitment => commitment.Ends) is not { } going)
        {
            return null;
        }

        var what = going.Label switch
        {
            "class" => "Class",
            "work" => "Work",
            var label => char.ToUpperInvariant(label[0]) + label[1..],
        };
        return $"{what} until {going.Ends.ToString("h:mm tt", CultureInfo.InvariantCulture)}. A suggestion will be here after.";
    }

    /// <summary>The refs passed on with "Not this one", kept only for the day they were passed on.</summary>
    public static IReadOnlySet<string> ReadSkipped(string? stored, DateOnly today)
    {
        var parts = (stored ?? "").Split('|', 2);
        if (parts.Length < 2 || parts[0] != Day(today))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The stored text with one more ref passed on today. Anything from an earlier day is dropped.</summary>
    public static string AddSkipped(string? stored, DateOnly today, string itemRef)
    {
        var skipped = ReadSkipped(stored, today).Append(itemRef).Distinct(StringComparer.Ordinal);
        return $"{Day(today)}|{string.Join(',', skipped)}";
    }

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string EventLabel(StoredItem item) =>
        item.Kind == ItemKind.Exam ? "the exam"
        : item.Title.Length <= LongEventTitle ? item.Title
        : "the next event";

    private static IEnumerable<Candidate> Candidates(IReadOnlyList<StoredItem> open, DateTime now, IReadOnlyCollection<string> skipped, SuggestionHabits habits)
    {
        var today = DateOnly.FromDateTime(now);
        foreach (var item in open)
        {
            if (item.Done || item.ParentRef is not null || item.Recurs is not null || item.Kind is not (ItemKind.Task or ItemKind.Deadline or ItemKind.Exam))
            {
                continue;
            }

            // A past feed item left unchecked, or a feed entry that's really an event, isn't something to start on.
            if (item.Source == ItemSource.Calendar && (item.DueDate < today || (item.Kind != ItemKind.Exam && ItemGroups.IsScheduled(item))))
            {
                continue;
            }

            // An exam that has started is past preparing for.
            if (item.Kind == ItemKind.Exam && (item.DueDate < today || item.DueAt?.LocalDateTime <= now))
            {
                continue;
            }

            if (skipped.Contains(ItemRef.Format(item)))
            {
                continue;
            }

            var steps = ItemGroups.StepsOf(item, open).Count(step => !step.Done);
            yield return steps > 0
                ? new Candidate(item, steps, habits.StepMinutes, steps * habits.StepMinutes, Usual(habits.LearnedStepMinutes, "steps usually take you about {0} min"))
                : item.Kind switch
                {
                    ItemKind.Task => new Candidate(item, 0, habits.TaskMinutes, habits.TaskMinutes, Usual(habits.LearnedTaskMinutes, "usually takes you about {0} min")),
                    ItemKind.Exam => new Candidate(item, 0, habits.SittingMinutes, ExamMinutes, Usual(habits.LearnedSittingMinutes, "your sittings usually run about {0} min")),
                    _ => new Candidate(item, 0, habits.SittingMinutes, DeadlineMinutes, Usual(habits.LearnedSittingMinutes, "your sittings usually run about {0} min")),
                };
        }
    }

    private static string? Usual(int? learned, string format) =>
        learned is { } minutes ? string.Format(CultureInfo.InvariantCulture, format, minutes) : null;

    private static string Reason(Candidate candidate, DateOnly today, Commitment? next, bool shortFirst)
    {
        var parts = new List<string> { DuePhrase(candidate.Item, today) };
        if (candidate.Steps > 0)
        {
            parts.Add(candidate.Steps == 1 ? "1 step left" : $"{candidate.Steps} steps left");
        }

        if (candidate.Usual is { } usual)
        {
            parts.Add(usual);
        }

        if (shortFirst)
        {
            parts.Add("a short one for this time of day");
        }

        if (next is not null)
        {
            parts.Add($"fits before {next.Label}");
        }

        return string.Join(", ", parts);
    }

    private static string DuePhrase(StoredItem item, DateOnly today)
    {
        var lead = item.Kind == ItemKind.Exam ? "Exam" : "Due";
        return item.DueDate switch
        {
            null => "No due date",
            { } day when day < today => "Overdue",
            { } day when day == today => $"{lead} today",
            { } day when day == today.AddDays(1) => $"{lead} tomorrow",
            { } day => $"{lead} {ItemGroups.DayLabel(day, today)}",
        };
    }

    /// <summary>SittingMinutes is the next stretch of work, and WorkMinutes is everything left. Usual says where a learned size came from.</summary>
    private sealed record Candidate(StoredItem Item, int Steps, int SittingMinutes, int WorkMinutes, string? Usual)
    {
        /// <summary>Out of days to spare, so it goes first whatever the time of day.</summary>
        public bool Pressing(DateOnly today, SuggestionHabits habits) => Slack(today, habits) is <= 0;

        /// <summary>
        /// Days to spare once the work left is spread at a day's worth at a time, or null with no date. A deadline
        /// can be worked on the day it's due, and an exam can't. Late items count as due today, and the order in Pick puts them after today's work.
        /// A kind that usually runs late loses a day, so it comes up a day sooner.
        /// </summary>
        public int? Slack(DateOnly today, SuggestionHabits habits)
        {
            if (Item.DueDate is not { } due)
            {
                return null;
            }

            var days = Math.Max(0, due.DayNumber - today.DayNumber);
            var available = Item.Kind == ItemKind.Exam ? days : days + 1;
            var needed = (WorkMinutes + habits.DayMinutes - 1) / habits.DayMinutes;
            return available - needed - habits.LeadDays(Item.Kind);
        }
    }
}
