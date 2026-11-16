using System.Globalization;
using System.Text.RegularExpressions;

namespace Waypoint.Core.Files;

/// <summary>Takes out numbers that identify a person or an account, before a file's text is kept or sent anywhere.</summary>
public static partial class Redaction
{
    public const string Removed = "[removed]";

    private const int FirstYear = 1900;
    private const int LastYear = 2100;

    public static string Apply(string text)
    {
        var clean = Labeled().Replace(text, match => $"{match.Groups[1].Value}{Removed}");
        clean = Ssn().Replace(clean, Removed);
        clean = Amex().Replace(clean, Removed);
        clean = CardGroups().Replace(clean, match => match.Value.Split(' ', '-').All(IsYear) ? match.Value : Removed);
        return LongRun().Replace(clean, Removed);
    }

    /// <summary>Names run words together with underscores, so any long run of digits goes.</summary>
    public static string ApplyToName(string name) => AnyLongRun().Replace(Apply(name), Removed);

    private static bool IsYear(string group) =>
        int.TryParse(group, CultureInfo.InvariantCulture, out var year) && year is >= FirstYear and <= LastYear;

    [GeneratedRegex(@"(?i)(\b(?:ssn|social security(?: number| no\.?)?|account(?: number| no\.?| #)?|acct\.?(?: number| no\.?| #)?|routing(?: number| no\.?| #)?|card(?: number| no\.?| #)?)\s*[:#]?\s*)[\dXx*][\dXx* -]{3,}\d")]
    private static partial Regex Labeled();

    [GeneratedRegex(@"\b\d{3}[-. ]\d{2}[-. ]\d{4}\b")]
    private static partial Regex Ssn();

    [GeneratedRegex(@"\b\d{4}[ -]\d{6}[ -]\d{5}\b")]
    private static partial Regex Amex();

    [GeneratedRegex(@"\d{9,}")]
    private static partial Regex AnyLongRun();

    [GeneratedRegex(@"\b\d{4}[ -]\d{4}[ -]\d{4}[ -]\d{1,7}\b")]
    private static partial Regex CardGroups();

    [GeneratedRegex(@"\b\d{9,19}\b")]
    private static partial Regex LongRun();
}
