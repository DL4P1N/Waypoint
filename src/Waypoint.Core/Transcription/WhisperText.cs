using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Waypoint.Core.Transcription;

/// <summary>
/// The hint Whisper gets: course names and assignment titles, so names it would otherwise mishear come out right.
/// Whisper only reads about 224 tokens of prompt, so it's capped well under that, and a name is never cut in half.
/// </summary>
public static partial class WhisperPrompt
{
    public const int MaxChars = 600;

    public static string Build(string? noteCourse, IEnumerable<string> courses, IEnumerable<(string Title, string? Course)> assignments, int maxChars = MaxChars)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var classNames = new List<string>();
        foreach (var course in (noteCourse is null ? courses : courses.Prepend(noteCourse)).Select(Clean))
        {
            if (course.Length > 0 && seen.Add(course))
            {
                classNames.Add(course);
            }
        }

        // The note's own class's work first, then the rest, soonest first as given.
        var titles = assignments
            .Select((found, index) => (Title: Clean(found.Title), Mine: noteCourse is not null && string.Equals(found.Course?.Trim(), noteCourse.Trim(), StringComparison.OrdinalIgnoreCase), index))
            .Where(found => found.Title.Length > 0)
            .OrderByDescending(found => found.Mine)
            .ThenBy(found => found.index)
            .Select(found => found.Title)
            .Where(seen.Add)
            .ToList();

        var prompt = new StringBuilder();
        Add(prompt, "Classes: ", classNames, maxChars);
        Add(prompt, "Assignments: ", titles, maxChars);
        return prompt.ToString().Trim();
    }

    private static void Add(StringBuilder prompt, string heading, IReadOnlyList<string> names, int maxChars)
    {
        var added = 0;
        foreach (var name in names)
        {
            var piece = added == 0 ? $"{(prompt.Length > 0 ? " " : "")}{heading}{name}" : $", {name}";
            if (prompt.Length + piece.Length + 1 > maxChars)
            {
                break;
            }

            prompt.Append(piece);
            added++;
        }

        if (added > 0)
        {
            prompt.Append('.');
        }
    }

    /// <summary>Plain words only: no quotes, line breaks, or control characters, and single spaces.</summary>
    private static string Clean(string text) =>
        Spaces().Replace(new string(text.Select(c => char.IsControl(c) || c is '"' ? ' ' : c).ToArray()), " ").Trim().TrimEnd('.', ',');

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>Reads whisper-cli's progress lines, printed with <c>-pp</c>, and works out the time left.</summary>
public static partial class WhisperProgress
{
    /// <summary>Measured with the BLAS build and 16 threads: 75 minutes of lecture took about 13.</summary>
    public const double RealTimeFactor = 0.17;

    /// <summary>The percent from a line such as "whisper_print_progress_callback: progress =  40%", or null.</summary>
    public static int? Parse(string line)
    {
        var match = ProgressLine().Match(line);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var percent)
            ? Math.Clamp(percent, 0, 100)
            : null;
    }

    /// <summary>
    /// Time left. Once a few percent are done the pace so far is used. Before that it's the measured speed times the
    /// recording's length. Null when there's nothing to go on.
    /// </summary>
    public static TimeSpan? Remaining(double? durationSeconds, int progress, TimeSpan elapsed)
    {
        if (progress >= 100)
        {
            return TimeSpan.Zero;
        }

        if (progress >= 5 && elapsed > TimeSpan.FromSeconds(15))
        {
            return elapsed * ((100.0 - progress) / progress);
        }

        if (durationSeconds is { } seconds and > 0)
        {
            var total = TimeSpan.FromSeconds(seconds * RealTimeFactor);
            var left = total * ((100.0 - progress) / 100.0);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>"about 12 minutes left", "about 1 minute left", or "less than a minute left".</summary>
    public static string Words(TimeSpan left)
    {
        if (left < TimeSpan.FromSeconds(50))
        {
            return "less than a minute left";
        }

        var minutes = Math.Max(1, (int)Math.Round(left.TotalMinutes, MidpointRounding.AwayFromZero));
        return minutes == 1 ? "about 1 minute left" : $"about {minutes} minutes left";
    }

    [GeneratedRegex(@"progress\s*=\s*(\d{1,3})\s*%")]
    private static partial Regex ProgressLine();
}
