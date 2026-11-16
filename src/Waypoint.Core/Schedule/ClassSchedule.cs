namespace Waypoint.Core.Schedule;

/// <summary>A weekly class meeting, in local time.</summary>
public sealed record ClassMeeting(string Course, string Room, DayOfWeek[] Days, TimeOnly Start, TimeOnly End, int TravelMinutes = 0);

/// <summary>One specific occurrence of a class.</summary>
public sealed record ClassOccurrence(ClassMeeting Meeting, DateTime Start);

/// <summary>
/// The term and its weekly classes. The running app keeps one of these and changes it in place with
/// <see cref="Use"/> when classes are saved on the Settings page, so everything holding it sees the new classes
/// without a restart. Its values are swapped all at once, so a reader never sees half of a change.
/// </summary>
public sealed record ClassSchedule
{
    // How far ahead the next class is looked for, so a term with no end date still finishes looking.
    private const int LookAheadDays = 366;

    private Values _values;

    public ClassSchedule(DateOnly TermStart, DateOnly TermEnd, ClassMeeting[] Meetings, DateOnly[] NoClassDates, int FirstWarningMinutes = 15) =>
        _values = new Values(TermStart, TermEnd, Meetings ?? [], NoClassDates ?? [], FirstWarningMinutes);

    /// <summary>No classes at all, for when none are set up yet.</summary>
    public static ClassSchedule Empty() => new(DateOnly.MinValue, DateOnly.MinValue, [], []);

    public DateOnly TermStart { get => _values.TermStart; init => _values = _values with { TermStart = value }; }

    public DateOnly TermEnd { get => _values.TermEnd; init => _values = _values with { TermEnd = value }; }

    public ClassMeeting[] Meetings { get => _values.Meetings; init => _values = _values with { Meetings = value ?? [] }; }

    public DateOnly[] NoClassDates { get => _values.NoClassDates; init => _values = _values with { NoClassDates = value ?? [] }; }

    public int FirstWarningMinutes { get => _values.FirstWarningMinutes; init => _values = _values with { FirstWarningMinutes = value }; }

    /// <summary>Takes on another schedule's term and classes, in place.</summary>
    public void Use(ClassSchedule other) => _values = other._values;

    public bool IsInClass(DateTime now)
    {
        var values = _values;
        var date = DateOnly.FromDateTime(now);
        var time = TimeOnly.FromDateTime(now);

        return date >= values.TermStart
            && date <= values.TermEnd
            && !values.NoClassDates.Contains(date)
            && values.Meetings.Any(meeting => meeting.Days.Contains(date.DayOfWeek) && time >= meeting.Start && time < meeting.End);
    }

    /// <summary>The earliest class on a day, or null if nothing meets then.</summary>
    public ClassOccurrence? GetFirstClass(DateOnly date) => GetClasses(date).FirstOrDefault();

    /// <summary>A day's classes in order.</summary>
    public IReadOnlyList<ClassOccurrence> GetClasses(DateOnly date)
    {
        var values = _values;
        if (date < values.TermStart || date > values.TermEnd || values.NoClassDates.Contains(date))
        {
            return [];
        }

        return values.Meetings
            .Where(meeting => meeting.Days.Contains(date.DayOfWeek))
            .Select(meeting => new ClassOccurrence(meeting, date.ToDateTime(meeting.Start)))
            .OrderBy(occurrence => occurrence.Start)
            .ToList();
    }

    /// <summary>The next class that hasn't started yet, or null once the term is over.</summary>
    public ClassOccurrence? GetNextClass(DateTime now)
    {
        var values = _values;
        if (values.Meetings.Length == 0)
        {
            return null;
        }

        var first = DateOnly.FromDateTime(now);
        if (first < values.TermStart)
        {
            first = values.TermStart;
        }

        var last = first <= DateOnly.MaxValue.AddDays(-LookAheadDays) && first.AddDays(LookAheadDays) < values.TermEnd
            ? first.AddDays(LookAheadDays)
            : values.TermEnd;
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            if (values.NoClassDates.Contains(date))
            {
                continue;
            }

            var next = values.Meetings
                .Where(meeting => meeting.Days.Contains(date.DayOfWeek))
                .Select(meeting => new ClassOccurrence(meeting, date.ToDateTime(meeting.Start)))
                .Where(occurrence => occurrence.Start > now)
                .MinBy(occurrence => occurrence.Start);

            if (next is not null)
            {
                return next;
            }

            if (date == DateOnly.MaxValue)
            {
                break;
            }
        }

        return null;
    }

    private sealed record Values(DateOnly TermStart, DateOnly TermEnd, ClassMeeting[] Meetings, DateOnly[] NoClassDates, int FirstWarningMinutes);
}
