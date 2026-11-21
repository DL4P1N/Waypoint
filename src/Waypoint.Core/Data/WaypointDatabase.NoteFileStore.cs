using Waypoint.Core.Notes;

namespace Waypoint.Core.Data;

/// <summary>The notes folder sync works on this database directly in local mode.</summary>
public sealed partial class WaypointDatabase : INoteFileStore
{
    NoteSaveResult INoteFileStore.SaveNoteIfUnchanged(long id, string title, string body, string expectedBody, DateTime now, string? expectedTitle) =>
        SaveNoteIfUnchanged(id, title, body, expectedBody, now, expectedTitle: expectedTitle) ? NoteSaveResult.Saved
        : GetNote(id) is { } stored ? NoteSaveResult.Changed(stored)
        : NoteSaveResult.Gone;
}
