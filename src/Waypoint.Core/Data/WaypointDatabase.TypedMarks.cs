using System.Globalization;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Notes;

namespace Waypoint.Core.Data;

/// <summary>
/// What the one-time repair of typed marks did: how many lines in how many notes it changed and the backup it made
/// first, or why it couldn't make one, and whether it has stopped trying.
/// </summary>
public sealed record TypedMarksRepairOutcome(int Lines, int Notes, string? Backup = null, Exception? Problem = null, bool GaveUp = false);

/// <summary>The one-time repair of notes whose typed headings, lists, and quotes were saved as plain text.</summary>
public sealed partial class WaypointDatabase
{
    /// <summary>Set once the repair has run, or found nothing to repair, or given up, so it never runs again.</summary>
    public const string TypedMarksRepairedKey = "notes_typed_marks_repaired";

    /// <summary>What the repair changed, as "lines notes backup-file-name", until a PC has said so in Ask.</summary>
    public const string TypedMarksToSayKey = "notes_typed_marks_to_say";

    /// <summary>A backup has to be made before any note changes. After this many failures in a row, the repair stops trying.</summary>
    public const int TypedMarksTries = 3;

    /// <summary>How many times in a row no backup could be made.</summary>
    private const string TypedMarksFailuresKey = "notes_typed_marks_backup_failures";

    /// <summary>The ending of the backup made just before the repair, which is kept apart from the daily ones.</summary>
    private const string BeforeNoteRepairSuffix = "-before-note-repair";

    /// <summary>
    /// Runs <see cref="TypedMarkRepair"/> over every note once, leaving out notes still exactly as they were written
    /// from their sources, so they keep their link to them. A backup copy is made first. If it can't be, the notes are
    /// left as they are and it's tried again the next time, up to <see cref="TypedMarksTries"/> times. The notes change
    /// through the usual save, in one write, so their files, Discord posts, and other PCs follow as for any edit. Null
    /// when it ran before.
    /// </summary>
    public TypedMarksRepairOutcome? RepairTypedNoteMarks(DateTime now)
    {
        if (GetPreference(TypedMarksRepairedKey) is not null)
        {
            return null;
        }

        if (NotesToRepair(this).Count == 0)
        {
            SetPreference(TypedMarksRepairedKey, "nothing");
            return new TypedMarksRepairOutcome(0, 0);
        }

        string backup;
        try
        {
            backup = SaveCopy(now, BeforeNoteRepairSuffix);

            // The copy is marked as repaired, so restoring it gives the notes back as they were, without repairing them again.
            using var connection = OpenFile(backup, SqliteOpenMode.ReadWrite);
            WritePreference(connection, TypedMarksRepairedKey, "kept as it was");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            var failures = (int.TryParse(GetPreference(TypedMarksFailuresKey), CultureInfo.InvariantCulture, out var before) ? before : 0) + 1;
            var gaveUp = failures >= TypedMarksTries;
            SetPreference(TypedMarksFailuresKey, failures.ToString(CultureInfo.InvariantCulture));
            if (gaveUp)
            {
                SetPreference(TypedMarksRepairedKey, "gave up, no backup could be made");
            }

            return new TypedMarksRepairOutcome(0, 0, Problem: exception, GaveUp: gaveUp);
        }

        var name = Path.GetFileName(backup);
        return InOneWrite(write =>
        {
            // Read again in the write, so a note saved since the look above is repaired as it is now.
            if (write.GetPreference(TypedMarksRepairedKey) is not null)
            {
                return new TypedMarksRepairOutcome(0, 0, backup);
            }

            var (lines, notes) = (0, 0);
            foreach (var (id, title, body, changed) in NotesToRepair(write))
            {
                if (write.SaveNote(id, title, body, now))
                {
                    lines += changed;
                    notes++;
                }
            }

            write.SetPreference(TypedMarksRepairedKey, now.ToString("O", CultureInfo.InvariantCulture));
            write.SetPreference(TypedMarksToSayKey, notes == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"{lines} {notes} {name}"));
            return new TypedMarksRepairOutcome(lines, notes, backup);
        });
    }

    /// <summary>The live notes the repair changes, as they'd be after it. Notes still as written from their sources aren't.</summary>
    private static List<(long Id, string Title, string Body, int Lines)> NotesToRepair(WaypointDatabase database)
    {
        using var lease = database.Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, body, generated_hash FROM items WHERE kind = 'Note' AND canceled = 0 AND body IS NOT NULL";
        var found = new List<(long, string, string, int)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var body = reader.GetString(2);
            if (!reader.IsDBNull(3) && reader.GetString(3) == BodyHash(body))
            {
                continue;
            }

            var (repaired, lines) = TypedMarkRepair.Repair(body);
            if (lines > 0)
            {
                found.Add((reader.GetInt64(0), reader.GetString(1), repaired, lines));
            }
        }

        return found;
    }

    /// <summary>
    /// What Ask says about the repair, taken with <paramref name="take"/>, which reads and clears it in one step where
    /// the notes are kept, so it's said once, on one PC. Null when there's nothing to say.
    /// </summary>
    public static string? TakeTypedMarksMessage(Func<string, string?> take) => TypedMarksMessage(take(TypedMarksToSayKey));

    /// <summary>What Ask says about the repair, from <see cref="TypedMarksToSayKey"/>, or null when there's nothing to say.</summary>
    public static string? TypedMarksMessage(string? toSay)
    {
        var parts = (toSay ?? "").Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var lines) || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var notes) || lines == 0)
        {
            return null;
        }

        var lineWords = lines == 1 ? "1 line" : $"{lines} lines";
        var noteWords = notes == 1 ? "1 note" : $"{notes} notes";
        return $"Turned {lineWords} in {noteWords} back into the headings, lists and quotes they were typed as. The notes as they were are kept in the backups folder as {parts[2]}.";
    }

    /// <summary>
    /// Reads a preference and clears it in one step, so of two PCs asking at once only one gets it. Null when it
    /// isn't set or is empty.
    /// </summary>
    public string? TakePreference(string key)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM preferences WHERE key = $key AND value <> '' RETURNING value";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }
}
