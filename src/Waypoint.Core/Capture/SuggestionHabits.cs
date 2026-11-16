namespace Waypoint.Core.Capture;

/// <summary>One finished Activation session: the kind of item it was on, whether it was working through steps, and how long it ran.</summary>
public sealed record PastSitting(ItemKind Kind, bool OnSteps, DateTime EndedAt, double Minutes);

/// <summary>One item checked off, with the day it was due and the day it was done.</summary>
public sealed record PastFinish(ItemKind Kind, DateOnly DueDate, DateOnly DoneOn);

/// <summary>How a startable reminder was answered. "10 more minutes" isn't kept, since it isn't an answer yet.</summary>
public enum ReminderAnswer
{
    Started,
    NotNow,
}

public sealed record PastAnswer(DateTime At, ReminderAnswer Answer);

/// <summary>
/// What the suggestion has learned from what already happened, with nothing extra asked of the user. Each size
/// is null until there's enough to go on, and then it's the middle value, rounded and kept inside fixed bounds,
/// so one odd session can't swing it. RunsLate holds the kinds that are usually checked off after their due day,
/// which then start a day earlier. HardBlocks holds the three-hour parts of the day when reminders almost always
/// get Not now, and then a short item goes first.
/// </summary>
public sealed record SuggestionHabits(
    int? LearnedStepMinutes = null,
    int? LearnedTaskMinutes = null,
    int? LearnedSittingMinutes = null,
    int? LearnedDayMinutes = null,
    IReadOnlyList<ItemKind>? RunsLate = null,
    IReadOnlyList<int>? HardBlocks = null)
{
    /// <summary>Only the last three months count, so old habits fade on their own.</summary>
    public const int LookBackDays = 90;

    /// <summary>How many of something it takes before it changes a guess.</summary>
    public const int MinSamples = 5;

    /// <summary>Shorter than this is a session started by mistake.</summary>
    public const double ShortestSitting = 3;

    /// <summary>Longer than this is a session left running, not one sitting.</summary>
    public const double LongestSitting = 180;

    /// <summary>A part of the day is hard when at least this share of its reminder answers were Not now.</summary>
    public const double HardShare = 0.8;

    public const int BlockHours = 3;

    public static readonly (int Low, int High) StepBounds = (10, 30);
    public static readonly (int Low, int High) TaskBounds = (10, 45);
    public static readonly (int Low, int High) SittingBounds = (30, 90);
    public static readonly (int Low, int High) DayBounds = (60, 180);

    public static SuggestionHabits Defaults { get; } = new();

    public int StepMinutes => LearnedStepMinutes ?? NextAction.StepMinutes;

    public int TaskMinutes => LearnedTaskMinutes ?? NextAction.TaskMinutes;

    public int SittingMinutes => LearnedSittingMinutes ?? NextAction.SittingMinutes;

    public int DayMinutes => LearnedDayMinutes ?? NextAction.DayMinutes;

    /// <summary>One extra day of lead for a kind that usually runs late, and none otherwise.</summary>
    public int LeadDays(ItemKind kind) => RunsLate?.Contains(kind) == true ? 1 : 0;

    /// <summary>Whether reminders around this time are almost always passed on.</summary>
    public bool IsHardTime(DateTime at) => HardBlocks?.Contains(at.Hour / BlockHours) == true;

    /// <summary>Works it all out from what's stored. The same inputs and now always give the same answer.</summary>
    public static SuggestionHabits Learn(IEnumerable<PastSitting> sittings, IEnumerable<PastFinish> finishes, IEnumerable<PastAnswer> answers, DateTime now)
    {
        var since = now.AddDays(-LookBackDays);
        var counted = sittings
            .Where(sitting => sitting.EndedAt > since && sitting.EndedAt <= now && sitting.Minutes is >= ShortestSitting and <= LongestSitting)
            .ToList();

        var step = Typical(counted.Where(sitting => sitting.OnSteps).Select(sitting => sitting.Minutes), StepBounds, 5);
        var task = Typical(counted.Where(sitting => !sitting.OnSteps && sitting.Kind == ItemKind.Task).Select(sitting => sitting.Minutes), TaskBounds, 5);
        var sitting = Typical(counted.Where(sitting => !sitting.OnSteps && sitting.Kind is ItemKind.Deadline or ItemKind.Exam).Select(sitting => sitting.Minutes), SittingBounds, 5);
        var day = Typical(counted.GroupBy(sitting => DateOnly.FromDateTime(sitting.EndedAt)).Select(group => group.Sum(sitting => sitting.Minutes)), DayBounds, 15);

        var sinceDay = DateOnly.FromDateTime(since);
        var late = finishes
            .Where(finish => finish.DoneOn > sinceDay && finish.Kind is ItemKind.Task or ItemKind.Deadline)
            .GroupBy(finish => finish.Kind)
            .Where(group => group.Count() >= MinSamples && group.Count(finish => finish.DoneOn > finish.DueDate) * 2 > group.Count())
            .Select(group => group.Key)
            .Order()
            .ToList();

        var hard = answers
            .Where(answer => answer.At > since && answer.At <= now)
            .GroupBy(answer => answer.At.Hour / BlockHours)
            .Where(group => group.Count() >= MinSamples && group.Count(answer => answer.Answer == ReminderAnswer.NotNow) >= group.Count() * HardShare)
            .Select(group => group.Key)
            .Order()
            .ToList();

        return new SuggestionHabits(step, task, sitting, day, late, hard);
    }

    /// <summary>The middle value rounded to a step and kept in bounds, or null with too few to go on.</summary>
    public static int? Typical(IEnumerable<double> values, (int Low, int High) bounds, int roundTo)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count < MinSamples)
        {
            return null;
        }

        var middle = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
        var rounded = (int)Math.Round(middle / roundTo, MidpointRounding.AwayFromZero) * roundTo;
        return Math.Clamp(rounded, bounds.Low, bounds.High);
    }
}
