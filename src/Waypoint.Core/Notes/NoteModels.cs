using Waypoint.Core.Capture;

namespace Waypoint.Core.Notes;

/// <summary>
/// What a note was made from. Kept in <c>note_sources</c>, so a slide deck, a PDF, typed notes, and later a
/// lecture transcript can all sit on one note side by side.
/// </summary>
public static class NoteSourceKind
{
    public const string Pdf = "pdf";
    public const string Slides = "slides";
    public const string Document = "document";
    public const string Sheet = "sheet";
    public const string Image = "image";
    public const string Text = "text";

    /// <summary>What the user typed in the note before notes were first written from a file, kept so it isn't lost.</summary>
    public const string Typed = "typed";

    /// <summary>What Whisper heard in a recording, saved unchanged. Its timestamps are kept beside it.</summary>
    public const string Transcript = "transcript";

    public static string ForFile(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => Pdf,
        ".pptx" => Slides,
        ".docx" => Document,
        ".xlsx" => Sheet,
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp" => Image,
        _ => Text,
    };

    public static string Label(string kind) => kind switch
    {
        Pdf => "PDF",
        Slides => "Slides",
        Document => "Document",
        Sheet => "Spreadsheet",
        Image => "Picture",
        Typed => "Typed notes",
        Transcript => "Transcript",
        _ => "Text",
    };
}

/// <summary>One source on a note. Text is exactly what was read from it, after redaction, and is never edited.</summary>
public sealed record NoteSource(long Id, long NoteId, string Kind, string Name, string Text, string? FilePath, DateTime CreatedAt);

/// <summary>
/// Whether a note's text was written from its sources. EditedSince is true when the text no longer matches what
/// was written, or when it was typed by hand and never written at all, so replacing it needs a yes first.
/// </summary>
public sealed record NoteGeneration(DateTime? GeneratedAt, bool EditedSince, int SourceCount)
{
    public bool WasGenerated => GeneratedAt is not null;
}

/// <summary>A note's text as it was before new notes replaced it, so the replacement can be undone.</summary>
public sealed record NoteBodyState(string Body, DateTime? GeneratedAt, string? GeneratedHash);

/// <summary>A date or task found in a source. It's only a suggestion until the user adds it.</summary>
public sealed record SuggestedItem(ItemKind Kind, string Title, DateOnly? Date, TimeOnly? Time = null);

/// <summary>A stored suggestion waiting on the user.</summary>
public sealed record NoteSuggestion(long Id, long NoteId, SuggestedItem Item);

/// <summary>Notes written from sources, with what they suggest adding. Cut means some text was too long to send.</summary>
public sealed record WrittenNotes(string Markdown, IReadOnlyList<SuggestedItem> Suggestions, bool Cut = false);

/// <summary>
/// What the writer is told besides the sources: the class, a change the user asked for, today's date for years,
/// and the moments the user marked while recording, one per line, to stress in the notes.
/// </summary>
public sealed record NoteWriteContext(string? Course, string? Request, DateTime Now, string? Marks = null);
