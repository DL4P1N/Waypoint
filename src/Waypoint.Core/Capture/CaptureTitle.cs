using System.Text.RegularExpressions;

namespace Waypoint.Core.Capture;

/// <summary>
/// A title for an item made from a long capture: its first sentence, kept to about <see cref="MaxLength"/>
/// characters and cut between words. The whole text is kept on the item's note, so nothing typed is lost.
/// </summary>
public static partial class CaptureTitle
{
    public const int MaxLength = 120;

    private const string Ellipsis = "…";

    private const int ShortestSentence = 12;

    /// <summary>
    /// The title to show, and the full text when the title is shorter than it, or null when the text is already a
    /// title: one line of at most <see cref="MaxLength"/> characters.
    /// </summary>
    public static (string Title, string? Full) Shorten(string text)
    {
        var full = text.Trim();
        if (full.Length <= MaxLength && !full.Contains('\n'))
        {
            return (full, null);
        }

        var flat = Spaces().Replace(full, " ");
        // A stop this early is more likely "Dr." or "Ch." than the end of a sentence.
        var end = SentenceEnd().Matches(flat).FirstOrDefault(match => match.Index + 1 >= ShortestSentence);
        var sentence = end is null ? flat : flat[..(end.Index + 1)];
        var firstLine = full.Split('\n', 2)[0].Trim();
        if (firstLine.Length > 0 && firstLine.Length < sentence.Length)
        {
            sentence = Spaces().Replace(firstLine, " ");
        }

        if (sentence.Length > MaxLength)
        {
            var cut = sentence.LastIndexOf(' ', MaxLength - Ellipsis.Length);
            sentence = (cut > 0 ? sentence[..cut] : sentence[..(MaxLength - Ellipsis.Length)]).TrimEnd(',', ';', ':', ' ', '-') + Ellipsis;
        }

        return (sentence, full);
    }

    /// <summary>The end of the first sentence: a stop, question, or exclamation mark followed by a space.</summary>
    [GeneratedRegex(@"[.!?](?=\s)")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
