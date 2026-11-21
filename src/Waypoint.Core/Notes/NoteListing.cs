using Waypoint.Core.Capture;

namespace Waypoint.Core.Notes;

/// <summary>
/// A note as a list of notes sends it to the PC: its id, title, date, folder, and about line, with only the line the
/// list shows under the title in place of the whole body. The note itself is read when it's opened.
/// </summary>
public static class NoteListing
{
    /// <summary>Long enough for a row in the list, which trims what doesn't fit.</summary>
    public const int MaxLine = 200;

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

    public static NoteDetail Slim(NoteDetail note) => note with { Body = FirstLine(note.Title, note.Body) };

    public static IReadOnlyList<NoteDetail> Slim(IEnumerable<NoteDetail> notes) => notes.Select(Slim).ToList();
}
