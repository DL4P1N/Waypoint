using System.Globalization;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Capture;

/// <summary>Spending and the short morning summary. Both are worked out locally, so bank-like data stays off the AI unless asked.</summary>
public static class Briefing
{
    private const int MaxListed = 3;
    private const int GetAheadDays = 3;

    /// <summary>What's left in the month, spread over the days remaining. Never below zero.</summary>
    public static long SafeToSpendToday(long monthlyCents, long spentBeforeToday, long spentToday, DateOnly today)
    {
        return Math.Max(TodaysShare(monthlyCents, spentBeforeToday, today) - spentToday, 0);
    }

    /// <summary>
    /// Why there's nothing left to spend today, so a bare $0.00 isn't a puzzle. Null while there's room.
    /// </summary>
    public static string? SafeToSpendReason(long monthlyCents, long spentBeforeToday, long spentToday, DateOnly today)
    {
        if (SafeToSpendToday(monthlyCents, spentBeforeToday, spentToday, today) > 0)
        {
            return null;
        }

        var share = TodaysShare(monthlyCents, spentBeforeToday, today);
        return share <= 0
            ? $"This month's {Money(monthlyCents)} budget is used up."
            : $"{Money(spentToday)} spent today, more than today's {Money(share)} share of the month.";
    }

    /// <summary>What's left in the month, split evenly over the days remaining, today included.</summary>
    private static long TodaysShare(long monthlyCents, long spentBeforeToday, DateOnly today)
    {
        var daysLeft = DateTime.DaysInMonth(today.Year, today.Month) - today.Day + 1;
        return (monthlyCents - spentBeforeToday) / daysLeft;
    }

    public static string Money(long cents) => (cents / 100m).ToString("$#,##0.00", CultureInfo.InvariantCulture);

    /// <summary>The morning briefing. A stale balance, when there is one, is said on the safe to spend line rather than on its own.</summary>
    public static string Build(DateTime now, IReadOnlyList<StoredItem> items, ClassSchedule? schedule, long? safeToSpendCents, string? staleBalances = null)
    {
        var today = DateOnly.FromDateTime(now);
        var classes = (schedule?.GetClasses(today) ?? [])
            .Select(occurrence => $"{occurrence.Meeting.Course} at {occurrence.Start.ToString("h:mm tt", CultureInfo.InvariantCulture)}");

        var lines = new[]
        {
            Line("Class", classes),
            Line("Scheduled today", Scheduled(items, today)),
            Line("Due today", Titles(items, today)),
            Line("Scheduled tomorrow", Scheduled(items, today.AddDays(1))),
            Line("Due tomorrow", Titles(items, today.AddDays(1))),
            safeToSpendCents is { } cents ? $"Safe to spend today: {Money(cents)}{(staleBalances is null ? "" : $". {staleBalances}")}" : null,
        };

        var text = string.Join("\n", lines.Where(line => line is not null));
        return text.Length > 0 ? text : "Nothing on the schedule today.";
    }

    /// <summary>The week ahead: exams, how much is due, the busiest day, and one thing to start early.</summary>
    public static string BuildWeek(DateTime now, IReadOnlyList<StoredItem> items)
    {
        var today = DateOnly.FromDateTime(now);
        var first = today.DayOfWeek == DayOfWeek.Sunday ? today.AddDays(1) : today;
        var last = first.AddDays(6);

        var due = items
            .Where(item => item.ParentRef is null && item.DueDate >= first && item.DueDate <= last)
            .Where(item => item.Kind is ItemKind.Exam or ItemKind.Deadline)
            .ToList();
        if (due.Count == 0)
        {
            return "Nothing due in the week ahead.";
        }

        var exams = due.Where(item => item.Kind == ItemKind.Exam).Select(item => $"{item.Title} ({item.DueDate:ddd})");
        var deadlines = due.Count(item => item.Kind == ItemKind.Deadline);
        var busiest = due.GroupBy(item => item.DueDate!.Value).OrderByDescending(day => day.Count()).ThenBy(day => day.Key).First();
        var ahead = due.Where(item => item.DueDate!.Value.DayNumber - today.DayNumber >= GetAheadDays).OrderBy(item => item.Kind != ItemKind.Exam).ThenBy(item => item.DueDate).FirstOrDefault();

        var lines = new[]
        {
            Line("Exams", exams),
            deadlines > 0 ? $"Deadlines: {deadlines}" : null,
            busiest.Count() > 1 ? $"Busiest day: {busiest.Key.ToString("dddd", CultureInfo.InvariantCulture)} ({busiest.Count()} due)" : null,
            ahead is not null ? $"Get ahead on: {ahead.Title}" : null,
        };

        return string.Join("\n", lines.Where(line => line is not null));
    }

    private static IEnumerable<string> Titles(IEnumerable<StoredItem> items, DateOnly day) =>
        items.Where(item => item.ParentRef is null && item.DueDate == day && !ItemGroups.IsScheduled(item)).Select(item => item.Title);

    private static IEnumerable<string> Scheduled(IEnumerable<StoredItem> items, DateOnly day) =>
        items
            .Where(item => item.ParentRef is null && item.DueDate == day && ItemGroups.IsScheduled(item))
            .OrderBy(item => item.DueAt ?? DateTimeOffset.MaxValue)
            .Select(item => item.DueAt is { } at ? $"{item.Title} at {at.LocalDateTime.ToString("h:mm tt", CultureInfo.InvariantCulture)}" : item.Title);

    private static string? Line(string label, IEnumerable<string> entries)
    {
        var all = entries.ToList();
        if (all.Count == 0)
        {
            return null;
        }

        var listed = string.Join(", ", all.Take(MaxListed));
        return all.Count > MaxListed ? $"{label}: {listed}, and {all.Count - MaxListed} more" : $"{label}: {listed}";
    }
}
