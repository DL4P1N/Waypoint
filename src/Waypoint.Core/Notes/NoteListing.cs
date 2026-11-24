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

    /// <summary>A link's address may hold its own balanced parentheses, as Wikipedia's do, and a "title" after it.</summary>
    private const string Address = @"\((?:[^()\s]|\([^()\s]*\))*(?:\s+""[^""]*"")?\)";

    private static readonly char[] LineBreaks = ['\r', '\n'];

    /// <summary>
    /// The first line with words that aren't just the title again, as it's written in Markdown, or empty when there's
    /// none. It's what a list of notes is sent, and <see cref="Preview"/> reads it as words.
    /// </summary>
    public static string FirstLine(string title, string body)
    {
        var line = body.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(text => Plain(text) is { Length: > 0 } words && !title.StartsWith(words, StringComparison.OrdinalIgnoreCase)) ?? "";
        return line.Length > MaxLine ? line[..MaxLine] : line;
    }

    /// <summary>
    /// The line a list of notes shows under the title, as words: the first line from <see cref="FirstLine"/> with its
    /// Markdown taken out, so "\# Week 9" reads "# Week 9" and "**bold**" reads "bold".
    /// </summary>
    public static string Preview(string title, string body) => Plain(FirstLine(title, body));

    /// <summary>
    /// One line of Markdown as plain words: the marks that start a heading, quote, list item, or checklist item, then
    /// escapes, inline marks, and links taken out. Only real marks go, so "-5 degrees" and "2**3" stay as they are.
    /// </summary>
    public static string Plain(string line)
    {
        // A quote can hold a list item, and a list item a checklist box, so the marks at the start go one at a time.
        var text = line.Trim();
        for (var before = ""; before != text;)
        {
            before = text;
            text = BlockMark().Replace(text, "", 1);
        }

        // An escaped character waits aside, so no mark below takes it for one of its own.
        var held = new List<char>();
        text = Escaped().Replace(text, match =>
        {
            held.Add(match.Value[1]);
            return ((char)(HeldBase + held.Count - 1)).ToString();
        });

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

    /// <summary>A heading's #s, a quote's &gt;, a bullet, a number, or a checklist box, each with the space after it.</summary>
    [GeneratedRegex(@"^(?:#{1,6}(?:\s+|$)|>\s?|[-*+]\s+|\d{1,9}[.)]\s+|\[[ xX]\]\s+)")]
    private static partial Regex BlockMark();

    [GeneratedRegex(@"\\[!-/:-@\[-`{-~]")]
    private static partial Regex Escaped();

    [GeneratedRegex(@"!\[([^\]]*)\]" + Address)]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]+)\]" + Address)]
    private static partial Regex Link();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex Code();

    [GeneratedRegex(@"</?u>", RegexOptions.IgnoreCase)]
    private static partial Regex Underline();

    /// <summary>A pair that opens and closes at a word's edge, so "2**3**4" and "__init__.py" keep their marks.</summary>
    [GeneratedRegex(@"(?<![\w*_~])(\*\*|__|~~)(?=\S)(.+?)(?<=\S)\1(?![\w*_~]|\.\w)")]
    private static partial Regex Paired();

    [GeneratedRegex(@"(?<![\w*])\*(?=\S)([^*]+?)(?<=\S)\*(?![\w*])")]
    private static partial Regex Starred();
}
