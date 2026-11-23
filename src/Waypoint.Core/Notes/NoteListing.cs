using System.Text;
using System.Text.RegularExpressions;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Notes;

/// <summary>
/// A note as a list of notes sends it to the PC: its id, title, date, folder, and about line, with only the line the
/// list shows under the title in place of the whole body. The note itself is read when it's opened.
/// </summary>
public static partial class NoteListing
{
    /// <summary>Long enough for a row in the list, which trims what doesn't fit.</summary>
    public const int MaxLine = 200;

    /// <summary>Where escaped characters wait while a line is made plain: Unicode's private use area, never typed.</summary>
    private const char HeldBase = '\uE000';

    private static readonly char[] LineBreaks = ['\r', '\n'];

    /// <summary>Heading, list, and quote marks, so the line reads as words.</summary>
    private static readonly char[] MarkdownMarks = ['#', '>', '-', '*', '+', ' '];

    /// <summary>The first line of words that isn't just the title again, or empty when there's none.</summary>
    public static string FirstLine(string title, string body)
    {
        var line = body.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(text => text.TrimStart(MarkdownMarks))
            .FirstOrDefault(text => text.Length > 0 && !title.StartsWith(text, StringComparison.OrdinalIgnoreCase)) ?? "";
        return line.Length > MaxLine ? line[..MaxLine] : line;
    }

    /// <summary>
    /// The line a list of notes shows under the title, as words: the first line from <see cref="FirstLine"/> with its
    /// Markdown taken out, so "\# Week 9" reads "# Week 9" and "**bold**" reads "bold".
    /// </summary>
    public static string Preview(string title, string body) => Plain(FirstLine(title, body));

    /// <summary>One line of Markdown as plain words: escapes, inline marks, links, and a checklist box taken out.</summary>
    public static string Plain(string line)
    {
        // An escaped character waits aside, so no mark below takes it for one of its own.
        var held = new List<char>();
        var text = Escaped().Replace(line, match =>
        {
            held.Add(match.Value[1]);
            return ((char)(HeldBase + held.Count - 1)).ToString();
        });

        text = Box().Replace(text, "");
        text = Image().Replace(text, "$1");
        text = Link().Replace(text, "$1");
        text = Code().Replace(text, "$1");
        text = Underline().Replace(text, "");
        text = Paired().Replace(text, "$2");
        text = Starred().Replace(text, "$1");

        var plain = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            plain.Append(ch >= HeldBase && ch - HeldBase < held.Count ? held[ch - HeldBase] : ch);
        }

        return plain.ToString().Trim();
    }

    public static NoteDetail Slim(NoteDetail note) => note with { Body = FirstLine(note.Title, note.Body) };

    public static IReadOnlyList<NoteDetail> Slim(IEnumerable<NoteDetail> notes) => notes.Select(Slim).ToList();

    [GeneratedRegex(@"\\[!-/:-@\[-`{-~]")]
    private static partial Regex Escaped();

    [GeneratedRegex(@"^\[[ xX]\]\s+")]
    private static partial Regex Box();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex Code();

    [GeneratedRegex(@"</?u>", RegexOptions.IgnoreCase)]
    private static partial Regex Underline();

    [GeneratedRegex(@"(\*\*|__|~~)(?=\S)(.+?)(?<=\S)\1")]
    private static partial Regex Paired();

    [GeneratedRegex(@"(?<![\w*])\*(?=\S)([^*]+?)(?<=\S)\*(?![\w*])")]
    private static partial Regex Starred();
}
