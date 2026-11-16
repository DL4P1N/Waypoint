using System.Globalization;
using System.Text.RegularExpressions;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Notes;

/// <summary>Works out which class a new note belongs to: the class in session, or a course a file names.</summary>
public static partial class ClassLinking
{
    /// <summary>A note started a little before class, while getting settled, still counts as that class's.</summary>
    public static readonly TimeSpan EarlyStart = TimeSpan.FromMinutes(10);

    /// <summary>The class meeting now, from a few minutes before it starts until it ends. Null outside class.</summary>
    public static ClassOccurrence? CurrentClass(ClassSchedule? schedule, DateTime now)
    {
        if (schedule is null)
        {
            return null;
        }

        return schedule.GetClasses(DateOnly.FromDateTime(now))
            .Where(occurrence => now >= occurrence.Start - EarlyStart && now < DateOnly.FromDateTime(now).ToDateTime(occurrence.Meeting.End))
            .OrderBy(occurrence => Math.Abs((occurrence.Start - now).Ticks))
            .FirstOrDefault();
    }

    /// <summary>A class note's title: the course and the date, such as "CS 101, Tue Nov 3".</summary>
    public static string Title(string course, DateTime now) =>
        $"{course.Trim()}, {now.ToString("ddd MMM d", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The known course a file names, looking at its name first and then its text. Spacing and dashes don't
    /// matter, so "CS101" and "cs-101" both find CS 101. When the text names several, the one named first wins.
    /// </summary>
    public static string? NamedCourse(string fileName, string? text, IEnumerable<string> courses)
    {
        var known = courses.Where(course => !string.IsNullOrWhiteSpace(course)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return First(fileName, known) ?? (text is null ? null : First(text, known));
    }

    private static string? First(string haystack, IReadOnlyList<string> courses) =>
        courses
            .Where(course => Pieces().IsMatch(course))
            .Select(course => (Course: course, Match: Pattern(course).Match(haystack)))
            .Where(found => found.Match.Success)
            .OrderBy(found => found.Match.Index)
            .ThenByDescending(found => found.Course.Length)
            .Select(found => found.Course)
            .FirstOrDefault();

    /// <summary>The course's letters and digits in order, with only spaces, dashes, or underscores allowed between them.</summary>
    private static Regex Pattern(string course)
    {
        var pieces = Pieces().Matches(course).Select(piece => Regex.Escape(piece.Value));
        return new Regex($@"(?<![A-Za-z0-9]){string.Join(@"[\s_\-]*", pieces)}(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    [GeneratedRegex(@"[A-Za-z]+|\d+")]
    private static partial Regex Pieces();
}
