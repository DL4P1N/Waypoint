using System.Globalization;
using System.Text.RegularExpressions;

namespace Waypoint.Core.Budget;

/// <summary>Pay dates waiting on a yes. AmountCents is only set if the file printed one.</summary>
public sealed record PayProposal(IReadOnlyList<DateOnly> Dates, long? AmountCents, DateTime CreatedAt);

/// <summary>Reads a pay calendar table without the sorter, so its dates are exact and it works offline.</summary>
public static partial class PayCalendar
{
    private const int MinRows = 6;
    private const int HeaderGap = 30;

    /// <summary>Kept as the exchange's reply, so the next note can tell it's answering this and nothing else.</summary>
    public const string AskedNote = "Asked whether to use the pay dates found in the file.";

    private static readonly string[] YesWords = ["yes", "yep", "yeah", "ok", "okay", "use these", "use them", "keep it"];
    private static readonly string[] NoWords = ["no", "nope", "cancel", "dont", "don't", "not these"];
    private static readonly string[] PlainWords = ["use these", "use them", "not these"];

    /// <summary>
    /// Finds the column under a pay day heading and takes every date in it. Null if the text isn't plainly a
    /// calendar. Hours in the table are never read as pay.
    /// </summary>
    public static PayProposal? TryParse(string text, DateTime now)
    {
        var headings = Heading().Matches(text).ToList();
        var payColumn = headings.FindIndex(match => match.Groups["pay"].Success);
        if (payColumn < 0)
        {
            return null;
        }

        var first = payColumn;
        while (first > 0 && Gap(headings[first - 1], headings[first]) <= HeaderGap)
        {
            first--;
        }

        var last = payColumn;
        while (last < headings.Count - 1 && Gap(headings[last], headings[last + 1]) <= HeaderGap)
        {
            last++;
        }

        var columns = last - first + 1;
        var column = payColumn - first;
        var dates = DateText().Matches(text, headings[last].Index + headings[last].Length).Select(match => ParseDate(match.Value)).ToList();
        if (dates.Any(date => date is null) || dates.Count / columns < MinRows)
        {
            return null;
        }

        var paydays = Enumerable.Range(0, dates.Count / columns).Select(row => dates[(row * columns) + column]!.Value).ToList();
        if (paydays.Zip(paydays.Skip(1)).Any(pair => pair.First >= pair.Second))
        {
            return null;
        }

        return new PayProposal(paydays, OneAmount(text), now);
    }

    /// <summary>True for yes, false for no, null for anything else.</summary>
    public static bool? ParseWord(string text)
    {
        var word = text.Trim().TrimEnd('.', '!').ToLowerInvariant();
        return YesWords.Contains(word) ? true : NoWords.Contains(word) ? false : null;
    }

    /// <summary>Words that only ever mean the pay dates. A bare yes needs the question right before it.</summary>
    public static bool NamesProposal(string text) => PlainWords.Contains(text.Trim().TrimEnd('.', '!').ToLowerInvariant());

    private static int Gap(Match left, Match right) => right.Index - (left.Index + left.Length);

    private static DateOnly? ParseDate(string text) =>
        DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static long? OneAmount(string text)
    {
        var amounts = Money().Matches(text).Select(match => match.Groups[1].Value.Replace(",", "")).Distinct().ToList();
        return amounts.Count == 1 ? (long)(decimal.Parse(amounts[0], CultureInfo.InvariantCulture) * 100) : null;
    }

    [GeneratedRegex(@"(?i)(?:pay\s*)?period\s*(?:begin|start|end)\w*|(?<pay>pay\s*day|pay\s*date|check\s*date)")]
    private static partial Regex Heading();

    [GeneratedRegex(@"(?i)\b(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:tember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\.?\s+\d{1,2},?\s+\d{4}\b|\b\d{1,2}/\d{1,2}/\d{2,4}\b|\b\d{4}-\d{2}-\d{2}\b")]
    private static partial Regex DateText();

    [GeneratedRegex(@"\$\s?([\d,]+\.\d{2})")]
    private static partial Regex Money();
}
