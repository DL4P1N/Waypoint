using System.Globalization;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Notes;

namespace Waypoint.Core.Data;

/// <summary>The one-time repair of notes whose typed headings, lists, and quotes were saved as plain text.</summary>
public sealed partial class WaypointDatabase
{
    /// <summary>Set once the repair has run, or found nothing to repair, so it never runs again.</summary>
    public const string TypedMarksRepairedKey = "notes_typed_marks_repaired";

    /// <summary>
    /// What the repair changed, as "lines notes", until the PC has said so in Ask. Empty once it has, or when nothing
    /// changed.
    /// </summary>
    public const string TypedMarksToSayKey = "notes_typed_marks_to_say";

    /// <summary>
    /// Runs <see cref="TypedMarkRepair"/> over every note once. A backup copy is made first, and if it can't be, the
    /// notes are left as they are and it's tried again the next time the database opens. The notes change through
    /// the usual save, in one write, so their files, Discord posts, and other PCs follow as for any edit.
    /// </summary>
    internal void RepairTypedNoteMarks(DateTime now)
    {
        if (GetPreference(TypedMarksRepairedKey) is not null)
        {
            return;
        }

        if (!ReadNotes("AND i.body IS NOT NULL").Any(note => TypedMarkRepair.Repair(note.Body).Lines > 0))
        {
            SetPreference(TypedMarksRepairedKey, "nothing");
            return;
        }

        try
        {
            // The copy is marked as repaired, so restoring it gives the notes back as they were, without repairing them again.
            var copy = SaveCopy(now, "");
            using var connection = OpenFile(copy, SqliteOpenMode.ReadWrite);
            WritePreference(connection, TypedMarksRepairedKey, "kept as it was");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            return;
        }

        InOneWrite(write =>
        {
            // Read again in the write, so a note saved since the look above is repaired as it is now.
            if (write.GetPreference(TypedMarksRepairedKey) is not null)
            {
                return;
            }

            var (lines, notes) = (0, 0);
            foreach (var note in write.ReadNotes("AND i.body IS NOT NULL"))
            {
                var (body, changed) = TypedMarkRepair.Repair(note.Body);
                if (changed > 0 && write.SaveNote(note.Id, note.Title, body, now))
                {
                    lines += changed;
                    notes++;
                }
            }

            write.SetPreference(TypedMarksRepairedKey, now.ToString("O", CultureInfo.InvariantCulture));
            write.SetPreference(TypedMarksToSayKey, notes == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"{lines} {notes}"));
        });
    }

    /// <summary>
    /// What Ask says about the repair where the notes are kept, here or on the server, or null when there's nothing to
    /// say. Taking it clears it, so it's said once, on one PC.
    /// </summary>
    public static string? TakeTypedMarksMessage(Remote.IPreferences notes)
    {
        if (TypedMarksMessage(notes.GetPreference(TypedMarksToSayKey)) is not { } message)
        {
            return null;
        }

        notes.SetPreference(TypedMarksToSayKey, "");
        return message;
    }

    /// <summary>What Ask says about the repair, from <see cref="TypedMarksToSayKey"/>, or null when there's nothing to say.</summary>
    public static string? TypedMarksMessage(string? toSay)
    {
        var parts = (toSay ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var lines) || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var notes) || lines == 0)
        {
            return null;
        }

        var lineWords = lines == 1 ? "1 line" : $"{lines} lines";
        var noteWords = notes == 1 ? "1 note" : $"{notes} notes";
        return $"Turned {lineWords} in {noteWords} back into the headings, lists and quotes they were typed as. A copy from before is kept with the other backups.";
    }
}
