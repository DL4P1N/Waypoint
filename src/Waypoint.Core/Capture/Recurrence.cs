using System.Globalization;

namespace Waypoint.Core.Capture;

public enum RecurrenceUnit
{
    Day,
    Week,
    Month,
}

/// <summary>How often an item comes back. DayOfMonth keeps "the 31st" from drifting after a short month.</summary>
public sealed record Recurrence(RecurrenceUnit Unit, int Interval = 1, int? DayOfMonth = null)
{
    public string Label
    {
        get
        {
            var unit = Unit.ToString().ToLowerInvariant();
            return Interval > 1 ? $"Every {Interval} {unit}s" : $"Every {unit}";
        }
    }

    public string Format() => string.Join('/', Unit, Interval, DayOfMonth?.ToString(CultureInfo.InvariantCulture) ?? "");

    public static Recurrence Parse(string text)
    {
        var parts = text.Split('/');
        return new Recurrence(
            Enum.Parse<RecurrenceUnit>(parts[0]),
            int.Parse(parts[1], CultureInfo.InvariantCulture),
            parts[2].Length > 0 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : null);
    }

    /// <summary>The first occurrence after <paramref name="after"/>, so missed ones don't pile up.</summary>
    public DateOnly Next(DateOnly from, DateOnly after)
    {
        var next = from;
        do
        {
            next = Step(next);
        }
        while (next <= after);

        return next;
    }

    /// <summary>Gives a recurring item what it needs to come back: a reminder, a date, and its day of the month.</summary>
    public static SortedItem Prepare(SortedItem item, DateOnly today)
    {
        if (item.Recurs is not { } recurs)
        {
            return item;
        }

        var date = item.DueDate ?? today;
        return item with
        {
            Kind = ItemKind.Reminder,
            DueDate = date,
            Recurs = recurs with { Interval = Math.Max(recurs.Interval, 1), DayOfMonth = recurs.Unit == RecurrenceUnit.Month ? date.Day : null },
        };
    }

    private DateOnly Step(DateOnly date)
    {
        var interval = Math.Max(Interval, 1);
        if (Unit != RecurrenceUnit.Month)
        {
            return date.AddDays(Unit == RecurrenceUnit.Week ? interval * 7 : interval);
        }

        var month = new DateOnly(date.Year, date.Month, 1).AddMonths(interval);
        var day = Math.Min(DayOfMonth ?? date.Day, DateTime.DaysInMonth(month.Year, month.Month));
        return new DateOnly(month.Year, month.Month, day);
    }
}
