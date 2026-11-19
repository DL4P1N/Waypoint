using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Notes;

public enum NoteSaveOutcome
{
    Saved,

    /// <summary>The note's text isn't what the editor loaded any more, such as after a reply added from Discord.</summary>
    Changed,

    Gone,
}

/// <summary>What came of a save made only if the note was unchanged. Stored is the note as it is now, when it changed.</summary>
public sealed record NoteSaveResult(NoteSaveOutcome Outcome, NoteDetail? Stored = null)
{
    public static readonly NoteSaveResult Saved = new(NoteSaveOutcome.Saved);

    public static readonly NoteSaveResult Gone = new(NoteSaveOutcome.Gone);

    public static NoteSaveResult Changed(NoteDetail stored) => new(NoteSaveOutcome.Changed, stored);
}

/// <summary>How the note editor saves, so a change made elsewhere meanwhile is never lost under it.</summary>
public static class NoteSaving
{
    /// <summary>
    /// How many times a save tries again when the note keeps changing under it, before what was typed is kept as a
    /// note of its own instead.
    /// </summary>
    public const int Tries = 3;

    /// <summary>
    /// For each note, the version changed elsewhere that was last kept as its own note, until a save goes through. A
    /// save turned down after the copy was made, such as by a busy server, is tried again from the text the editor
    /// loaded, and finds the same version, which is then not kept twice.
    /// </summary>
    private static readonly ConditionalWeakTable<INotesData, ConcurrentDictionary<long, string>> Kept = new();

    /// <summary>
    /// Saves the editor's text over the note, as long as the note still has <paramref name="expectedBody"/>, the text
    /// the editor loaded or last saved. If something else changed it in the meantime, such as a reply added from
    /// Discord, another PC, or its .md file, that version is first kept as a note of its own, the same as before,
    /// and the save goes ahead over it. The check and the save are one step where the note is kept, so nothing can
    /// land between them. With no <paramref name="expectedBody"/> there's nothing to compare with, and it simply
    /// saves. False if the note is gone, or still changed each time it was checked, so the caller keeps the text as a
    /// new note and the note keeps the version saved elsewhere.
    /// </summary>
    public static bool SaveKeepingOther(INotesData notes, long id, string title, string body, string? expectedBody, DateTime now)
    {
        if (expectedBody is null)
        {
            return notes.SaveNote(id, title, body, now);
        }

        var kept = Kept.GetOrCreateValue(notes);
        for (var attempt = 0; attempt < Tries; attempt++)
        {
            var result = notes.SaveNoteIfUnchanged(id, title, body, expectedBody, now);
            switch (result.Outcome)
            {
                case NoteSaveOutcome.Saved:
                    kept.TryRemove(id, out _);
                    return true;
                case NoteSaveOutcome.Gone:
                    kept.TryRemove(id, out _);
                    return false;
            }

            if (result.Stored is not { } stored)
            {
                return false;
            }

            // Already the same text, what was expected after all, or kept by a save turned down after making the
            // copy: nothing would be lost, so no copy is needed.
            if (stored.Body != body && stored.Body != expectedBody
                && !(kept.TryGetValue(id, out var already) && already == stored.Body))
            {
                notes.CreateNote(stored.Title + NoteDraft.ElsewhereSuffix, stored.Body, now, stored.FolderId);
                kept[id] = stored.Body;
            }

            expectedBody = stored.Body;
        }

        // Still changing each time it's checked. Saving over it now could lose a change landing after the last check,
        // so the note keeps whatever it has and what was typed becomes a note of its own. What was kept above stays
        // known, so a try again after that fails doesn't keep the same version twice.
        return false;
    }
}
