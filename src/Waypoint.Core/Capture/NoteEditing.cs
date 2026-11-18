namespace Waypoint.Core.Capture;

/// <summary>A note as the Notes page shows it. Body is its own text once edited, and what was written until then.</summary>
public sealed record NoteDetail(long Id, string Title, string Body, string? About, DateTime At, long? FolderId = null);

/// <summary>What came of naming a list. Name is cleaned up, and Problem says why nothing changed.</summary>
public sealed record ListEdit(string Name, string? Problem = null);

/// <summary>
/// What's open in the note editor. A refresh only ever asks this whether it may load over the
/// editor, so text being typed can't be replaced from underneath.
/// </summary>
public sealed class NoteDraft
{
    private string _savedTitle = "";
    private string _savedBody = "";
    private bool _baselineKnown = true;

    /// <summary>Null for a new note that hasn't been saved yet.</summary>
    public long? Id { get; private set; }

    public string Title { get; private set; } = "";

    public string Body { get; private set; } = "";

    public bool IsDirty => Title != _savedTitle || Body != _savedBody;

    /// <summary>A new note with nothing in it isn't worth keeping.</summary>
    public bool ShouldSave => IsDirty && (Id is not null || !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Body));

    public void Load(NoteDetail? note)
    {
        _baselineKnown = true;
        Id = note?.Id;
        _savedTitle = Title = note?.Title ?? "";
        _savedBody = Body = note?.Body ?? "";
    }

    public void Edit(string title, string body)
    {
        Title = title;
        Body = body;
    }

    public void MarkSaved(long id) => MarkSaved(id, Title, Body);

    /// <summary>
    /// Marks the text a save sent as saved, for a save that finished after more was typed. What was typed since
    /// still counts as not saved, so it's saved next.
    /// </summary>
    public void MarkSaved(long id, string title, string body)
    {
        _baselineKnown = true;
        Id = id;
        _savedTitle = title;
        _savedBody = body;
    }

    /// <summary>
    /// Goes on as a different note, keeping what's typed, such as the copy made of a note that changed on the server
    /// while it was away. Nothing is marked saved, so typing not yet saved is saved to the new note.
    /// </summary>
    public void MoveTo(long id)
    {
        Id = id;
        _baselineKnown = false;
    }

    /// <summary>The title a note's other version is kept under when it changed in two places at once.</summary>
    public const string ElsewhereSuffix = " (edited elsewhere)";

    /// <summary>
    /// The text the stored note should still have, the one the editor loaded or last saved. A save goes ahead only if
    /// it does. If it changed meanwhile, such as from its .md file while the editor had focus or a reply added from
    /// Discord, saving over it would lose that change, so it's kept as its own note first. Only the text counts,
    /// since a saved title is tidied (trimmed, or taken from the first line) and would always look changed. Null
    /// after <see cref="MoveTo"/>, when there's nothing loaded to compare with until the next save.
    /// </summary>
    public string? ExpectedStoredBody => _baselineKnown && Id is not null ? _savedBody : null;

    /// <summary>True if the stored note moved on and nobody is working in the editor. Null means it's gone.</summary>
    public bool ShouldReload(NoteDetail? stored, bool editing) =>
        Id is not null && !editing && !IsDirty && (stored is null || stored.Title != Title || stored.Body != Body);
}
