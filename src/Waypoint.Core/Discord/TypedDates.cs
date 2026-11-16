using System.Globalization;
using System.Text.RegularExpressions;

namespace Waypoint.Core.Discord;

/// <summary>
/// Dates and times typed into slash command options on a phone, read the plain way without the sorter:
/// today, tomorrow, a weekday, "in 3 days", 11/14, Nov 14, or 2026-11-14, and 3pm, 3:30 pm, 15:30, or noon.
/// </summary>
public static partial class TypedDates
{
    /// <summary>A date with no year that's this far behind means next year's.</summary>
    private const int PastDaysBeforeNextYear = 30;

    /// <summary>An hour typed with no am or pm, from 1 to 7, is taken as the afternoon, since that's when things happen.</summary>
    private const int LastAfternoonGuess = 7;

    private static readonly string[] MonthDayFormats = ["MMM d", "MMMM d", "MMM d yyyy", "MMMM d yyyy", "MMM d, yyyy", "MMMM d, yyyy"];

    public static DateOnly? ParseDate(string? text, DateOnly today)
    {
        var clean = text?.Trim().ToLowerInvariant().TrimEnd('.') ?? "";
        if (clean.Length == 0)
        {
            return null;
        }

        switch (clean)
        {
            case "today" or "tonight":
                return today;
            case "tomorrow" or "tmrw" or "tmr":
                return today.AddDays(1);
        }

        if (InDays().Match(clean) is { Success: true } inDays)
        {
            return today.AddDays(int.Parse(inDays.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        var next = clean.StartsWith("next ", StringComparison.Ordinal);
        if (Weekday(next ? clean[5..] : clean) is { } day)
        {
            var ahead = ((int)day - (int)today.DayOfWeek + 7) % 7;
            return today.AddDays(next && ahead == 0 ? 7 : ahead);
        }

        if (DateOnly.TryParseExact(clean, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
        {
            return iso;
        }

        if (Slashed().Match(clean) is { Success: true } slashed)
        {
            var month = int.Parse(slashed.Groups[1].Value, CultureInfo.InvariantCulture);
            var date = int.Parse(slashed.Groups[2].Value, CultureInfo.InvariantCulture);
            var year = slashed.Groups[3].Success ? int.Parse(slashed.Groups[3].Value, CultureInfo.InvariantCulture) : (int?)null;
            return Make(year is < 100 ? 2000 + year : year, month, date, today);
        }

        foreach (var format in MonthDayFormats)
        {
            if (DateTime.TryParseExact(clean, format, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var named))
            {
                return Make(format.Contains("yyyy", StringComparison.Ordinal) ? named.Year : null, named.Month, named.Day, today);
            }
        }

        return null;
    }

    public static TimeOnly? ParseTime(string? text)
    {
        var clean = text?.Trim().ToLowerInvariant().Replace(".", "", StringComparison.Ordinal) ?? "";
        switch (clean)
        {
            case "":
                return null;
            case "noon":
                return new TimeOnly(12, 0);
            case "midnight":
                return new TimeOnly(0, 0);
        }

        if (Clock().Match(clean) is not { Success: true } match)
        {
            return null;
        }

        var hour = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        var half = match.Groups[3].Value;
        if (minute > 59)
        {
            return null;
        }

        if (half.Length > 0)
        {
            if (hour is < 1 or > 12)
            {
                return null;
            }

            hour = hour % 12 + (half.StartsWith('p') ? 12 : 0);
        }
        else if (hour is >= 1 and <= LastAfternoonGuess)
        {
            hour += 12;
        }

        return hour <= 23 ? new TimeOnly(hour, minute) : null;
    }

    private static DateOnly? Make(int? year, int month, int day, DateOnly today)
    {
        if (month is < 1 or > 12 || day < 1)
        {
            return null;
        }

        var chosen = year ?? today.Year;
        if (day > DateTime.DaysInMonth(chosen, month))
        {
            return null;
        }

        var date = new DateOnly(chosen, month, day);
        return year is null && date.DayNumber < today.DayNumber - PastDaysBeforeNextYear ? date.AddYears(1) : date;
    }

    private static DayOfWeek? Weekday(string text)
    {
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            var name = day.ToString().ToLowerInvariant();
            if (text == name || (text.Length >= 3 && name.StartsWith(text, StringComparison.Ordinal)))
            {
                return day;
            }
        }

        return null;
    }

    [GeneratedRegex(@"^in (\d{1,3}) days?$")]
    private static partial Regex InDays();

    [GeneratedRegex(@"^(\d{1,2})/(\d{1,2})(?:/(\d{2}|\d{4}))?$")]
    private static partial Regex Slashed();

    [GeneratedRegex(@"^(\d{1,2})(?::(\d{2}))?\s*(am|pm|a|p)?$")]
    private static partial Regex Clock();
}
