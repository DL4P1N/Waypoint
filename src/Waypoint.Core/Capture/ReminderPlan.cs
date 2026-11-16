namespace Waypoint.Core.Capture;

/// <summary>Combines the sorter's reminder plan with fixed alerts that are always kept.</summary>
public static class ReminderPlan
{
    public const int MinRepeatMinutes = 5;

    private const int MaxPlannedReminders = 14;
    private const int MaxAssignmentNudges = 3;
    private const int MaxSteps = 8;

    /// <summary>Study sessions for an exam start this many days before it, as the sorter's prompt asks.</summary>
    public const int StudyLeadDays = 10;

    /// <summary>The last days before an exam, which get a session every day.</summary>
    public const int StudyDailyDays = 3;

    private const int StudySoonMinutes = 30;

    private static readonly TimeOnly StudyTime = new(18, 0);
    private static readonly TimeOnly LatestStudyTime = new(22, 0);

    private static readonly TimeOnly DefaultTime = new(9, 0);
    private static readonly TimeOnly EveningBefore = new(19, 0);
    private static readonly TimeSpan[] ExamLeadTimes = [TimeSpan.FromDays(1), TimeSpan.FromMinutes(45)];

    public static IReadOnlyList<PlannedReminder> Build(SortedItem item, DateTime now)
    {
        var planned = (item.Reminders ?? [])
            .Where(reminder => reminder.At > now)
            .OrderBy(reminder => reminder.At)
            .Take(MaxPlannedReminders);

        var reminders = Fixed(item, now)
            .Concat(planned)
            .DistinctBy(reminder => reminder.At)
            .OrderBy(reminder => reminder.At)
            .ToList();

        // A repeating item needs a first pop-up, since repeats only follow a snooze.
        if (reminders.Count == 0 && RepeatMinutes(item) is { } minutes)
        {
            reminders.Add(new PlannedReminder(now.AddMinutes(minutes), null));
        }

        return reminders;
    }

    /// <summary>
    /// The days an exam's study sessions belong on: every other day from ten days before, then every day for the
    /// last three, and today as well when the exam is closer than ten days. Days already past are left out.
    /// </summary>
    public static IReadOnlyList<DateOnly> StudyDays(DateOnly today, DateOnly exam)
    {
        var days = new List<DateOnly>();
        if (exam.DayNumber - today.DayNumber is > 0 and < StudyLeadDays)
        {
            days.Add(today);
        }

        for (var before = StudyLeadDays; before >= 1; before--)
        {
            var day = exam.AddDays(-before);
            var onPlan = before <= StudyDailyDays || (StudyLeadDays - before) % 2 == 0;
            if (onPlan && day >= today && !days.Contains(day))
            {
                days.Add(day);
            }
        }

        return days;
    }

    /// <summary>
    /// Haiku tends to plan fewer study sessions for an exam than the prompt asks for. When it does, sessions are
    /// added on the plan's days that have none, until there are as many as the plan has days. Reminders it did
    /// plan are kept as they are.
    /// </summary>
    public static SortedItem WithStudySessions(SortedItem item, DateTime now)
    {
        if (item.Kind != ItemKind.Exam || item.DueDate is not { } exam)
        {
            return item;
        }

        var due = DueMoment(exam, item.DueTime);
        var days = StudyDays(DateOnly.FromDateTime(now), exam);
        var reminders = (item.Reminders ?? []).ToList();
        var sessions = reminders.Count(reminder => reminder.At > now && DateOnly.FromDateTime(reminder.At) < exam);
        if (sessions >= days.Count)
        {
            return item;
        }

        var covered = reminders.Select(reminder => DateOnly.FromDateTime(reminder.At)).ToHashSet();
        foreach (var day in days.Where(day => !covered.Contains(day)))
        {
            if (sessions >= days.Count)
            {
                break;
            }

            if (SessionTime(day, now) is { } at && at < due)
            {
                reminders.Add(new PlannedReminder(at, SessionMessage(item.Title, exam.DayNumber - day.DayNumber)));
                sessions++;
            }
        }

        return item with { Reminders = reminders.OrderBy(reminder => reminder.At).ToList() };
    }

    /// <summary>Early evening, or a little from now when that's already gone today, but never after 22:00.</summary>
    private static DateTime? SessionTime(DateOnly day, DateTime now)
    {
        var at = day.ToDateTime(StudyTime);
        if (at <= now)
        {
            var soon = now.AddMinutes(StudySoonMinutes);
            at = new DateTime(soon.Year, soon.Month, soon.Day, soon.Hour, soon.Minute / 30 * 30, 0).AddMinutes(30);
        }

        return DateOnly.FromDateTime(at) == day && TimeOnly.FromDateTime(at) <= LatestStudyTime ? at : null;
    }

    /// <summary>Each session says what to do: practice questions near the end, and reviewing notes before that.</summary>
    private static string SessionMessage(string exam, int daysBefore) => daysBefore switch
    {
        1 => $"Last review for {exam}: go over the questions you missed",
        <= StudyDailyDays => $"Do practice questions for {exam}",
        _ => $"Review your notes for {exam}",
    };

    public static int? RepeatMinutes(SortedItem item) =>
        item.RepeatMinutes is { } minutes ? Math.Max(minutes, MinRepeatMinutes) : null;

    /// <summary>Turns planned steps into tasks, dropping reminders that wouldn't come before the due time.</summary>
    public static IReadOnlyList<SortedItem> StepItems(IEnumerable<PlannedStep> steps, DateTime? due) =>
        steps
            .Take(MaxSteps)
            .Select(step => new SortedItem(
                ItemKind.Task,
                step.Title,
                null,
                null,
                Reminders: step.At is { } at && (due is null || at < due) ? [new PlannedReminder(at, null)] : null))
            .ToList();

    /// <summary>When an item is due, using the default time if it has none.</summary>
    public static DateTime DueMoment(DateOnly day, TimeOnly? time) => day.ToDateTime(time ?? DefaultTime);

    /// <summary>
    /// Trims the planner's nudges for an assignment to ones that are still useful. If none are,
    /// falls back to the evening before, so every assignment gets something unless it's too close.
    /// </summary>
    public static IReadOnlyList<PlannedReminder> ForAssignment(DateTime due, IEnumerable<PlannedReminder>? planned, DateTime now)
    {
        var kept = (planned ?? [])
            .Where(reminder => reminder.At > now && reminder.At < due)
            .DistinctBy(reminder => reminder.At)
            .OrderBy(reminder => reminder.At)
            .Take(MaxAssignmentNudges)
            .ToList();

        if (kept.Count == 0)
        {
            var fallback = DateOnly.FromDateTime(due).AddDays(-1).ToDateTime(EveningBefore);
            if (fallback > now)
            {
                kept.Add(new PlannedReminder(fallback, null));
            }
        }

        return kept;
    }

    private static IEnumerable<PlannedReminder> Fixed(SortedItem item, DateTime now)
    {
        if (item.DueDate is not { } day)
        {
            return [];
        }

        var due = DueMoment(day, item.DueTime);

        return item.Kind switch
        {
            // A missed reminder still fires, so it isn't lost if the PC was off.
            ItemKind.Reminder => [new PlannedReminder(due, null)],
            ItemKind.Exam => ExamLeadTimes
                .Select(lead => new PlannedReminder(due - lead, null))
                .Where(reminder => reminder.At > now),
            _ => [],
        };
    }
}
