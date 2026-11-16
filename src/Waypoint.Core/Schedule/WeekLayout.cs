using System.Globalization;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Schedule;

public enum WeekBlockKind
{
    Class,
    Work,
    Event,

    /// <summary>A reminder or exam: a moment rather than a stretch, so it never takes up free time.</summary>
    Marker,
}

/// <summary>
/// Something at a time on one day. End is null for a marker. An end at or before the start
/// means it runs past midnight, so this day shows it until the end of the day.
/// </summary>
public sealed record WeekEntry(DateOnly Date, TimeOnly Start, TimeOnly? End, string Title, WeekBlockKind Kind, StoredItem? Item = null, string? Detail = null)
{
    public bool IsBusy => Kind != WeekBlockKind.Marker;
}

/// <summary>An entry with its place in the day: From and To are offsets from midnight, and Column is out of Columns side by side.</summary>
public sealed record PlacedBlock(WeekEntry Entry, TimeSpan From, TimeSpan To, int Column, int Columns);

/// <summary>Time with nothing on it, as offsets from midnight.</summary>
public sealed record FreeGap(DateOnly Date, TimeSpan From, TimeSpan To)
{
    public TimeSpan Length => To - From;
}

public sealed record WeekDay(DateOnly Date, IReadOnlyList<PlacedBlock> Blocks, IReadOnlyList<StoredItem> AllDay, IReadOnlyList<FreeGap> Free);

/// <summary>Sunday to Saturday, with the hours worth showing. LastHour is exclusive, so 22 means the grid ends at 10 PM.</summary>
public sealed record WeekPlan(DateOnly Start, IReadOnlyList<WeekDay> Days, int FirstHour, int LastHour);

/// <summary>
/// Free time only counts inside the waking window, and only stretches of at least MinimumFree.
/// A marker is drawn as if it took MarkerLength, so two close ones sit side by side instead of on top of each other.
/// </summary>
public sealed record WeekOptions(
    int FirstHour = 8,
    int LastHour = 22,
    TimeSpan? FreeFrom = null,
    TimeSpan? FreeUntil = null,
    TimeSpan? MinimumFree = null,
    TimeSpan? MarkerLength = null)
{
    public TimeSpan FreeStart => FreeFrom ?? TimeSpan.FromHours(8);

    public TimeSpan FreeEnd => FreeUntil ?? TimeSpan.FromHours(22);

    public TimeSpan ShortestFree => MinimumFree ?? TimeSpan.FromMinutes(30);

    public TimeSpan MarkerSpan => MarkerLength ?? TimeSpan.FromMinutes(30);
}

/// <summary>The week view's math: which day an entry is on, how overlapping ones share the width, and where the free time is.</summary>
public static class WeekLayout
{
    private static readonly TimeSpan EndOfDay = TimeSpan.FromHours(24);

    /// <summary>The Sunday a week starts on.</summary>
    public static DateOnly StartOf(DateOnly date) => date.AddDays(-(int)date.DayOfWeek);

    /// <summary>"Nov 1 to 7", or "Nov 29 to Dec 5" when the week crosses a month when the week crosses a month.</summary>
    public static string Label(DateOnly start)
    {
        var last = start.AddDays(6);
        var first = start.ToString("MMM d", CultureInfo.InvariantCulture);
        var end = last.Month == start.Month ? last.Day.ToString(CultureInfo.InvariantCulture) : last.ToString("MMM d", CultureInfo.InvariantCulture);
        var year = last.Year == start.Year ? "" : $", {last.Year}";
        return $"{first} to {end}{year}";
    }

    /// <summary>"1h 30m", "45m", "2h".</summary>
    public static string Length(TimeSpan length)
    {
        var hours = (int)length.TotalHours;
        var minutes = length.Minutes;
        return hours == 0 ? $"{minutes}m" : minutes == 0 ? $"{hours}h" : $"{hours}h {minutes}m";
    }

    /// <summary>Every class meeting in the week starting on this day, skipping days with no class.</summary>
    public static IReadOnlyList<WeekEntry> ClassEntries(ClassSchedule? schedule, DateOnly start) =>
        schedule is null ? []
        : Enumerable.Range(0, 7)
            .SelectMany(offset => schedule.GetClasses(start.AddDays(offset)))
            .Select(occurrence => new WeekEntry(
                DateOnly.FromDateTime(occurrence.Start),
                occurrence.Meeting.Start,
                occurrence.Meeting.End,
                ItemGroups.ShortCourse(occurrence.Meeting.Course.Trim()),
                WeekBlockKind.Class,
                Detail: string.IsNullOrWhiteSpace(occurrence.Meeting.Room) ? null : occurrence.Meeting.Room))
            .ToList();

    public static IReadOnlyList<WeekEntry> ShiftEntries(IEnumerable<WorkShift> shifts) =>
        shifts.Select(shift => new WeekEntry(shift.Date, shift.Start, shift.End, "Work", WeekBlockKind.Work)).ToList();

    /// <summary>
    /// An item with a day and a time becomes a block or a marker. Events get a block, an hour long unless
    /// they have an end. Reminders and exams are markers. Anything else, timed or not, belongs in the day's top strip, so this gives null.
    /// </summary>
    public static WeekEntry? ItemEntry(StoredItem item, TimeOnly? end)
    {
        if (item.DueDate is not { } date || item.DueAt is not { } dueAt)
        {
            return null;
        }

        var start = TimeOnly.FromDateTime(dueAt.LocalDateTime);
        if (item.Kind is ItemKind.Reminder or ItemKind.Exam)
        {
            return new WeekEntry(date, start, null, item.Title, WeekBlockKind.Marker, item);
        }

        return item.Kind == ItemKind.Event || ItemGroups.IsScheduled(item)
            ? new WeekEntry(date, start, end ?? start.Add(EventTimes.DefaultLength), item.Title, WeekBlockKind.Event, item)
            : null;
    }

    /// <summary>True for a dated item that goes in the top strip rather than the grid.</summary>
    public static bool IsAllDay(StoredItem item) => item.DueDate is not null && ItemEntry(item, null) is null;

    /// <summary>
    /// Lays out one day's entries. Entries that overlap, directly or through a chain of others, form a cluster
    /// and share its width: each takes the leftmost column that's free when it starts.
    /// </summary>
    public static IReadOnlyList<PlacedBlock> Place(IEnumerable<WeekEntry> entries, TimeSpan markerLength)
    {
        var spans = entries
            .Select(entry => (Entry: entry, Span: Span(entry, markerLength)))
            .OrderBy(item => item.Span.From)
            .ThenByDescending(item => item.Span.To)
            .ThenBy(item => item.Entry.Title, StringComparer.Ordinal)
            .ToList();

        var placed = new List<PlacedBlock>();
        var cluster = new List<(WeekEntry Entry, TimeSpan From, TimeSpan To, int Column)>();
        var columnEnds = new List<TimeSpan>();
        var clusterEnd = TimeSpan.MinValue;

        void Flush()
        {
            placed.AddRange(cluster.Select(item => new PlacedBlock(item.Entry, item.From, item.To, item.Column, columnEnds.Count)));
            cluster.Clear();
            columnEnds.Clear();
        }

        foreach (var (entry, (from, to)) in spans)
        {
            if (cluster.Count > 0 && from >= clusterEnd)
            {
                Flush();
            }

            var column = columnEnds.FindIndex(end => end <= from);
            if (column < 0)
            {
                column = columnEnds.Count;
                columnEnds.Add(to);
            }
            else
            {
                columnEnds[column] = to;
            }

            cluster.Add((entry, from, to, column));
            clusterEnd = cluster.Count == 1 || to > clusterEnd ? to : clusterEnd;
        }

        Flush();
        return placed;
    }

    /// <summary>The gaps between busy stretches inside the window, leaving out any shorter than the minimum.</summary>
    public static IReadOnlyList<FreeGap> FreeGaps(DateOnly date, IEnumerable<(TimeSpan From, TimeSpan To)> busy, TimeSpan windowStart, TimeSpan windowEnd, TimeSpan minimum)
    {
        var gaps = new List<FreeGap>();
        var cursor = windowStart;
        foreach (var (from, to) in busy.Where(span => span.To > windowStart && span.From < windowEnd).OrderBy(span => span.From))
        {
            if (from > cursor)
            {
                Add(cursor, from);
            }

            if (to > cursor)
            {
                cursor = to;
            }
        }

        Add(cursor, windowEnd);
        return gaps;

        void Add(TimeSpan from, TimeSpan to)
        {
            if (to > from && to - from >= minimum)
            {
                gaps.Add(new FreeGap(date, from, to));
            }
        }
    }

    /// <summary>
    /// Builds the week. <paramref name="shown"/> is what the filter and search let through. Free time comes from
    /// <paramref name="busy"/>, which should hold everything with a time whether shown or not, because a class
    /// filtered out of view still fills that hour. Past days get no free time, and today's starts from now.
    /// </summary>
    public static WeekPlan Build(DateOnly start, IEnumerable<WeekEntry> shown, IEnumerable<StoredItem> allDay, IEnumerable<WeekEntry> busy, DateTime now, WeekOptions? options = null)
    {
        options ??= new WeekOptions();
        var shownList = shown.ToList();
        var allDayList = allDay.ToList();
        var busyList = busy.Where(entry => entry.IsBusy).ToList();
        var today = DateOnly.FromDateTime(now);

        var days = Enumerable.Range(0, 7).Select(offset =>
        {
            var date = start.AddDays(offset);
            var blocks = Place(shownList.Where(entry => entry.Date == date), options.MarkerSpan);
            var strip = allDayList.Where(item => item.DueDate == date).ToList();

            var windowStart = options.FreeStart;
            if (date == today)
            {
                var rounded = TimeSpan.FromMinutes(Math.Ceiling(now.TimeOfDay.TotalMinutes / 15) * 15);
                windowStart = rounded > windowStart ? rounded : windowStart;
            }

            var free = date < today ? []
                : FreeGaps(date, busyList.Where(entry => entry.Date == date).Select(entry => Span(entry, options.MarkerSpan)), windowStart, options.FreeEnd, options.ShortestFree);
            return new WeekDay(date, blocks, strip, free);
        }).ToList();

        var spans = days.SelectMany(day => day.Blocks).ToList();
        var first = spans.Count == 0 ? options.FirstHour : Math.Min(options.FirstHour, spans.Min(block => (int)block.From.TotalHours));
        var last = spans.Count == 0 ? options.LastHour : Math.Max(options.LastHour, spans.Max(block => (int)Math.Ceiling(block.To.TotalHours)));
        return new WeekPlan(start, days, first, Math.Min(last, 24));
    }

    private static (TimeSpan From, TimeSpan To) Span(WeekEntry entry, TimeSpan markerLength)
    {
        var from = entry.Start.ToTimeSpan();
        var to = entry.End is not { } end ? from + markerLength
            : end > entry.Start ? end.ToTimeSpan()
            : EndOfDay;
        return (from, to > EndOfDay ? EndOfDay : to);
    }
}
