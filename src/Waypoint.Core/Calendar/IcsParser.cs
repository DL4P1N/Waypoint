using System.Globalization;
using System.Text.RegularExpressions;

namespace Waypoint.Core.Calendar;

/// <summary>An assignment or event from the calendar feed.</summary>
public sealed record CalendarEvent(string Uid, string Title, string? Course, DateOnly DueDate, DateTimeOffset? DueAt);

public static partial class IcsParser
{
    /// <summary>Reads the events in an iCalendar document, skipping cancelled or malformed ones.</summary>
    public static IReadOnlyList<CalendarEvent> Parse(string ics)
    {
        if (!ics.Contains("BEGIN:VCALENDAR", StringComparison.Ordinal))
        {
            throw new FormatException("The calendar feed didn't return a calendar.");
        }

        var events = new List<CalendarEvent>();
        Dictionary<string, string>? current = null;

        // Lines longer than 75 characters continue on the next line after one space.
        foreach (var line in FoldedLine().Replace(ics, "").Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text == "BEGIN:VEVENT")
            {
                current = [];
            }
            else if (text == "END:VEVENT")
            {
                if (current is not null && ToEvent(current) is { } calendarEvent)
                {
                    events.Add(calendarEvent);
                }

                current = null;
            }
            else if (current is not null && text.IndexOf(':') is > 0 and var colon)
            {
                var name = text[..colon].Split(';')[0];
                current[name] = text[(colon + 1)..];
            }
        }

        return events;
    }

    private static CalendarEvent? ToEvent(Dictionary<string, string> properties)
    {
        if (!properties.TryGetValue("UID", out var uid)
            || !properties.TryGetValue("SUMMARY", out var summary)
            || !properties.TryGetValue("DTSTART", out var start)
            || properties.GetValueOrDefault("STATUS") == "CANCELLED")
        {
            return null;
        }

        var match = CourseSuffix().Match(Unescape(summary));
        var title = match.Success ? match.Groups["title"].Value : Unescape(summary);
        var course = match.Success ? match.Groups["course"].Value.Trim() : null;
        title = RepeatedSpaces().Replace(title, " ").Trim();

        if (DateOnly.TryParseExact(start, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return new CalendarEvent(uid, title, course, date, null);
        }

        if (DateTimeOffset.TryParseExact(
            start,
            "yyyyMMdd'T'HHmmss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var dueAt))
        {
            return new CalendarEvent(uid, title, course, DateOnly.FromDateTime(dueAt.ToLocalTime().DateTime), dueAt);
        }

        return null;
    }

    private static string Unescape(string value) => EscapedCharacter().Replace(
        value,
        match => match.Groups[1].Value is "n" or "N" ? " " : match.Groups[1].Value);

    [GeneratedRegex(@"\r?\n[ \t]")]
    private static partial Regex FoldedLine();

    [GeneratedRegex(@"^(?<title>.*?)\s*\[(?<course>[^\]]+)\]\s*$")]
    private static partial Regex CourseSuffix();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex RepeatedSpaces();

    [GeneratedRegex(@"\\(.)")]
    private static partial Regex EscapedCharacter();
}
