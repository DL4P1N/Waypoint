using System.Globalization;

namespace Waypoint.Core.Theme;

/// <summary>
/// Section headings show in sentence case. Report sections keep their capitals as names the code matches on,
/// so they're turned into words only where they're shown.
/// </summary>
public static class Headings
{
    private static readonly HashSet<string> Names = new(
        CultureInfo.InvariantCulture.DateTimeFormat.MonthNames
            .Concat(CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames)
            .Concat(CultureInfo.InvariantCulture.DateTimeFormat.DayNames)
            .Concat(CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedDayNames)
            .Where(name => name.Length > 0),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>"SPENT IN OCTOBER SO FAR" becomes "Spent in October so far". Month and day names keep their capital.</summary>
    public static string Sentence(string? heading)
    {
        if (string.IsNullOrWhiteSpace(heading) || heading.Any(char.IsLower))
        {
            return heading ?? "";
        }

        var words = heading.ToLowerInvariant().Split(' ');
        for (var index = 0; index < words.Length; index++)
        {
            var word = words[index];
            var bare = word.TrimEnd(',', '.', ':');
            if (index == 0 || Names.Contains(bare))
            {
                words[index] = Capitalized(word);
            }
        }

        return string.Join(' ', words);
    }

    private static string Capitalized(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];
}
