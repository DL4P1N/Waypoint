using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

/// <summary>One saved copy of the database. BeforeRestore marks the copy taken just before a restore.</summary>
public sealed record BackupFile(string Path, DateTime At, bool BeforeRestore);

/// <summary>What came of a restore. SafetyCopy is how things were just before, so the restore can be undone.</summary>
public sealed record RestoreResult(bool Restored, string? SafetyCopy, string? Problem = null);

public sealed partial class WaypointDatabase
{
    /// <summary>Copies older than this many days go, but the newest this many always stay.</summary>
    public const int BackupsKept = 14;

    /// <summary>Copies taken just before a restore are kept apart from the daily ones, the newest this many.</summary>
    public const int BeforeRestoreKept = 3;

    private const string BackupFolderName = "backups";
    private const string BackupPrefix = "waypoint-";
    private const string BeforeRestoreSuffix = "-before-restore";
    private const string BackupStamp = "yyyy-MM-dd-HHmmss";
    private const string UnfiledFolderName = "Unfiled";
    private const int MaxFileNameLength = 80;

    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public string DatabasePath => Path.GetFullPath(new SqliteConnectionStringBuilder(_connectionString).DataSource);

    /// <summary>A backups folder next to the database, so it follows WAYPOINT_DATA_DIR.</summary>
    public string BackupFolder => Path.Combine(Path.GetDirectoryName(DatabasePath)!, BackupFolderName);

    /// <summary>Saved copies, newest first.</summary>
    public IReadOnlyList<BackupFile> GetBackups()
    {
        if (!Directory.Exists(BackupFolder))
        {
            return [];
        }

        var backups = new List<BackupFile>();
        foreach (var path in Directory.GetFiles(BackupFolder, $"{BackupPrefix}*.db"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var stamp = name[BackupPrefix.Length..];
            var beforeRestore = stamp.EndsWith(BeforeRestoreSuffix, StringComparison.OrdinalIgnoreCase);
            if (beforeRestore)
            {
                stamp = stamp[..^BeforeRestoreSuffix.Length];
            }

            if (DateTime.TryParseExact(stamp, BackupStamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            {
                backups.Add(new BackupFile(path, at, beforeRestore));
            }
        }

        return backups.OrderByDescending(backup => backup.At).ThenBy(backup => backup.BeforeRestore).ToList();
    }

    /// <summary>The daily copy. Returns its path, or null if today already has one.</summary>
    public string? BackUpIfDue(DateTime now) =>
        GetBackups().Any(backup => !backup.BeforeRestore && backup.At.Date == now.Date) ? null : BackUpNow(now);

    /// <summary>
    /// Copies the database with SQLite's online backup, which is safe while the app has it open, then
    /// clears out old copies. Returns the new copy's path.
    /// </summary>
    public string BackUpNow(DateTime now)
    {
        var path = SaveCopy(now, "");
        PruneBackups(now);
        return path;
    }

    /// <summary>
    /// Replaces everything with a saved copy. The file is checked first, and the current data is saved
    /// as a before-restore copy, so nothing is lost if it was the wrong one. The app should restart after.
    /// </summary>
    public RestoreResult RestoreBackup(string backupPath, DateTime now)
    {
        var source = Path.GetFullPath(backupPath);
        if (RestoreProblem(source) is { } problem)
        {
            return new RestoreResult(false, null, problem);
        }

        var safetyCopy = SaveCopy(now, BeforeRestoreSuffix);
        PruneBackups(now);

        using (var backup = OpenFile(source, SqliteOpenMode.ReadOnly))
        using (var live = Open())
        {
            backup.BackupDatabase(live);
        }

        // The notes folder still holds the notes from before. Its next sync writes the restored ones over it.
        MarkNotesRestored();

        // The restored pages went through the log. Folding them into the file and emptying the log leaves nothing
        // from before the restore in it, so the file on its own is the restored copy.
        Execute("PRAGMA wal_checkpoint(TRUNCATE)");

        return new RestoreResult(true, safetyCopy);
    }

    /// <summary>
    /// Writes each note as its own Markdown file, in a folder for each notes folder and Unfiled for the
    /// rest. Returns how many were written.
    /// </summary>
    public int ExportNotes(string destination)
    {
        var folders = Flatten(GetNoteFolders()).ToDictionary(folder => folder.Id);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var written = 0;

        foreach (var note in GetNotes().OrderBy(note => note.Id))
        {
            var directory = Path.Combine([destination, .. FolderPath(note.FolderId, folders)]);
            Directory.CreateDirectory(directory);

            var baseName = SafeFileName(note.Title, UntitledNote);
            var path = Path.Combine(directory, $"{baseName}.md");
            for (var copy = 2; used.Contains(path) || File.Exists(path); copy++)
            {
                path = Path.Combine(directory, $"{baseName} ({copy}).md");
            }

            used.Add(path);
            File.WriteAllText(path, NoteMarkdown(note, GetNoteSources(note.Id)), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.SetLastWriteTime(path, note.At);
            written++;
        }

        // "This didn't help" notes go along as plain text, so they're kept with everything else exported.
        if (GetFeedback() is { Count: > 0 } feedback)
        {
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, FeedbackFileName), Feedback.AsText(feedback), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        return written;
    }

    /// <summary>The export's file of "This didn't help" notes.</summary>
    public const string FeedbackFileName = "Feedback.txt";

    /// <summary>
    /// The note, then its sources under a Sources heading: each transcript and file's text, unchanged, so the export
    /// holds everything the notes were written from and not only the notes.
    /// </summary>
    private static string NoteMarkdown(NoteDetail note, IReadOnlyList<Notes.NoteSource> sources)
    {
        var text = new StringBuilder();
        text.Append("# ").Append(note.Title).Append("\n\n");
        text.Append("Made ").Append(note.At.ToString("MMMM d, yyyy, h:mm tt", CultureInfo.InvariantCulture)).Append("\n\n");
        if (note.About is { Length: > 0 } about)
        {
            text.Append("About: ").Append(about).Append("\n\n");
        }

        text.Append(note.Body.TrimEnd()).Append('\n');
        if (sources.Count > 0)
        {
            text.Append("\n## Sources\n");
            foreach (var source in sources)
            {
                text.Append("\n### ").Append(source.Name).Append("\n\n");
                text.Append("Added ").Append(source.CreatedAt.ToString("MMMM d, yyyy, h:mm tt", CultureInfo.InvariantCulture)).Append("\n\n");
                text.Append(source.Text.Replace("\r\n", "\n").TrimEnd()).Append('\n');
            }
        }

        return text.ToString();
    }

    private static List<string> FolderPath(long? folderId, IReadOnlyDictionary<long, NoteFolder> folders)
    {
        var parts = new List<string>();
        var seen = new HashSet<long>();
        for (var at = folderId; at is { } id && folders.TryGetValue(id, out var folder) && seen.Add(id); at = folder.ParentId)
        {
            parts.Insert(0, SafeFileName(folder.Name, "Folder"));
        }

        return parts.Count > 0 ? parts : [UnfiledFolderName];
    }

    private static string SafeFileName(string name, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(letter => invalid.Contains(letter) || char.IsControl(letter) ? '-' : letter).ToArray()).Trim().TrimEnd('.', ' ');
        if (clean.Length > MaxFileNameLength)
        {
            clean = clean[..MaxFileNameLength].TrimEnd('.', ' ');
        }

        if (clean.Length == 0)
        {
            return fallback;
        }

        return ReservedFileNames.Contains(clean) ? $"{clean} note" : clean;
    }

    /// <summary>
    /// Why that file can't be restored, or null when it can. Checked before anything is stopped for a restore, so a
    /// wrong file never makes Waypoint start again for nothing.
    /// </summary>
    public string? RestoreProblem(string backupPath)
    {
        var source = Path.GetFullPath(backupPath);
        if (!File.Exists(source))
        {
            return "That backup file is gone.";
        }

        return string.Equals(source, DatabasePath, StringComparison.OrdinalIgnoreCase)
            ? "That's the database Waypoint is using now, not a backup."
            : BackupProblem(source);
    }

    /// <summary>Null if the file is a readable Waypoint database.</summary>
    private static string? BackupProblem(string path)
    {
        try
        {
            using var connection = OpenFile(path, SqliteOpenMode.ReadOnly);
            using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA integrity_check";
            if (check.ExecuteScalar() as string != "ok")
            {
                return "That backup is damaged, so nothing was changed.";
            }

            using var tables = connection.CreateCommand();
            tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('captures', 'items')";
            return (long)tables.ExecuteScalar()! == 2 ? null : "That file isn't a Waypoint backup, so nothing was changed.";
        }
        catch (SqliteException)
        {
            return "That file isn't a Waypoint backup, so nothing was changed.";
        }
    }

    /// <summary>Writes to a partial file first, so a copy cut short never looks like a finished backup.</summary>
    private string SaveCopy(DateTime now, string suffix)
    {
        Directory.CreateDirectory(BackupFolder);
        // Two copies in the same second get the next free second, so every name still reads as a time.
        var at = now;
        var path = BackupPath(at, suffix);
        while (File.Exists(path))
        {
            at = at.AddSeconds(1);
            path = BackupPath(at, suffix);
        }

        var partial = $"{path}.partial";
        File.Delete(partial);
        using (var source = Open())
        using (var target = OpenFile(partial, SqliteOpenMode.ReadWriteCreate))
        {
            source.BackupDatabase(target);

            // The copy keeps a plain journal, so it's one file with no log beside it, and opening it to check it or
            // restore from it never leaves one behind in the backups folder.
            using var plain = target.CreateCommand();
            plain.CommandText = "PRAGMA journal_mode = DELETE";
            plain.ExecuteScalar();
        }

        File.Move(partial, path);
        return path;
    }

    private string BackupPath(DateTime at, string suffix) =>
        Path.Combine(BackupFolder, $"{BackupPrefix}{at.ToString(BackupStamp, CultureInfo.InvariantCulture)}{suffix}.db");

    /// <summary>
    /// Daily copies and before-restore copies are counted apart, so restoring a few times never pushes out a daily
    /// one. Only the newest few before-restore copies stay.
    /// </summary>
    private void PruneBackups(DateTime now)
    {
        var cutoff = now.Date.AddDays(-BackupsKept);
        var backups = GetBackups();
        var old = backups.Where(backup => !backup.BeforeRestore).Skip(BackupsKept).Where(backup => backup.At.Date <= cutoff)
            .Concat(backups.Where(backup => backup.BeforeRestore).Skip(BeforeRestoreKept))
            .ToList();
        foreach (var copy in old)
        {
            try
            {
                File.Delete(copy.Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left for the next day's pass.
            }
        }
    }

    /// <summary>Unpooled, so the file is let go as soon as the copy is done.</summary>
    private static SqliteConnection OpenFile(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
