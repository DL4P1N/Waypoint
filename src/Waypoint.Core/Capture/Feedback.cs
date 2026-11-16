namespace Waypoint.Core.Capture;

/// <summary>One "This didn't help": which card or menu it was on, the item if there was one, when, and an optional note.</summary>
public sealed record FeedbackEntry(long Id, DateTime At, string Feature, string? ItemRef, string? ItemTitle, string? Note);

/// <summary>
/// "This didn't help" keeps only the click and an optional one-line note, for the user to read later on Settings.
/// Nothing is asked afterwards, nothing is counted, and nothing else about using the app is kept.
/// </summary>
public static class Feedback
{
    public const string Reminder = "reminder";
    public const string Suggestion = "suggestion";
    public const string Session = "session";
    public const string Row = "row";
    public const string AfterThisCard = "after-this";
    public const string LateNight = "late-night";

    /// <summary>A note is one short line.</summary>
    public const int MaxNote = 200;

    /// <summary>The words Settings and the export use for where it was.</summary>
    public static string Label(string feature) => feature switch
    {
        Reminder => "Reminder card",
        Suggestion => "Suggestion on To do",
        Session => "Session card",
        Row => "To do row menu",
        AfterThisCard => "Earlier you meant to",
        LateNight => "Late-night check-in",
        _ => feature,
    };

    /// <summary>One line, trimmed and cut to <see cref="MaxNote"/>. Blank is no note.</summary>
    public static string? CleanNote(string? note)
    {
        var line = string.Join(" ", (note ?? "").Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length == 0 ? null : line.Length <= MaxNote ? line : line[..MaxNote].TrimEnd();
    }

    /// <summary>One entry as it reads on Settings and in the export: "Nov 11, 3:05 PM · Reminder card · Call the bank: too early".</summary>
    public static string Describe(FeedbackEntry entry)
    {
        var parts = new List<string> { entry.At.ToString("MMM d, h:mm tt", System.Globalization.CultureInfo.InvariantCulture), Label(entry.Feature) };
        if (entry.ItemTitle is { Length: > 0 } title)
        {
            parts.Add(title);
        }

        var line = string.Join("  ·  ", parts);
        return entry.Note is { } note ? $"{line}: {note}" : line;
    }

    /// <summary>Everything kept, oldest first, as plain text for the export.</summary>
    public static string AsText(IEnumerable<FeedbackEntry> entries) =>
        string.Join(Environment.NewLine, new[] { "This didn't help", "" }.Concat(entries.OrderBy(entry => entry.At).ThenBy(entry => entry.Id).Select(entry =>
            $"{entry.At.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)}  {Label(entry.Feature)}{(entry.ItemTitle is { Length: > 0 } title ? $"  {title}" : "")}{(entry.ItemRef is { } reference ? $" ({reference})" : "")}{(entry.Note is { } note ? $": {note}" : "")}"))) + Environment.NewLine;
}
