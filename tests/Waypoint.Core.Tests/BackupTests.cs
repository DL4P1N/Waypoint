using Microsoft.Data.Sqlite;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

public sealed class BackupTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 3, 10, 0, 0);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-backup-test-{Guid.NewGuid()}");
    private readonly WaypointDatabase _database;

    public BackupTests()
    {
        Directory.CreateDirectory(_folder);
        _database = new WaypointDatabase(Path.Combine(_folder, "waypoint.db"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void BackupsGoInAFolderNextToTheDatabase()
    {
        var path = _database.BackUpNow(Now);

        Assert.Equal(Path.Combine(_folder, "backups"), Path.GetDirectoryName(path));
        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_database.BackupFolder, "*.partial"));
    }

    [Fact]
    public void ACopyCutOffBeforeIsSweptByTheNextBackup()
    {
        Directory.CreateDirectory(_database.BackupFolder);
        var stale = Path.Combine(_database.BackupFolder, "waypoint-2026-11-01-100000.db.partial");
        File.WriteAllText(stale, "half a copy");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
        var journal = stale + "-journal";
        File.WriteAllText(journal, "its journal");
        File.SetLastWriteTimeUtc(journal, DateTime.UtcNow.AddDays(-2));

        _database.BackUpNow(Now);

        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(journal));
        Assert.Single(_database.GetBackups());
    }

    [Fact]
    public void ABackupHoldsTheNotesAtThatTime()
    {
        _database.CreateNote("Lecture 1", "Recursion basics", Now);
        var path = _database.BackUpNow(Now);

        var copy = new WaypointDatabase(path);
        Assert.Equal("Lecture 1", Assert.Single(copy.GetNotes()).Title);
    }

    [Fact]
    public void TheDailyBackupHappensOncePerDay()
    {
        Assert.NotNull(_database.BackUpIfDue(Now));
        Assert.Null(_database.BackUpIfDue(Now.AddHours(5)));
        Assert.NotNull(_database.BackUpIfDue(Now.AddDays(1)));
        Assert.Equal(2, _database.GetBackups().Count);
    }

    [Fact]
    public void TwoBackupsInTheSameSecondBothStay()
    {
        var first = _database.BackUpNow(Now);
        var second = _database.BackUpNow(Now);

        Assert.NotEqual(first, second);
        Assert.Equal(2, _database.GetBackups().Count);
    }

    [Fact]
    public void OldBackupsAreClearedAfterAboutTwoWeeks()
    {
        for (var day = 0; day < 20; day++)
        {
            _database.BackUpIfDue(Now.AddDays(day));
        }

        var backups = _database.GetBackups();
        Assert.Equal(WaypointDatabase.BackupsKept, backups.Count);
        Assert.Equal(Now.AddDays(19), backups[0].At);
        Assert.Equal(Now.AddDays(6), backups[^1].At);
    }

    [Fact]
    public void OldBackupsStayWhenThereAreFewOfThem()
    {
        _database.BackUpNow(Now.AddDays(-60));
        _database.BackUpNow(Now);

        Assert.Equal(2, _database.GetBackups().Count);
    }

    [Fact]
    public void RestoreBringsBackTheBackupAndSavesWhatWasThereFirst()
    {
        _database.CreateNote("Kept", "from before", Now);
        var backup = _database.BackUpNow(Now);
        _database.CreateNote("Added later", "after the backup", Now.AddHours(1));

        var result = _database.RestoreBackup(backup, Now.AddHours(2));

        Assert.True(result.Restored);
        Assert.Equal("Kept", Assert.Single(_database.GetNotes()).Title);
        Assert.NotNull(result.SafetyCopy);
        Assert.Contains("before-restore", Path.GetFileName(result.SafetyCopy));
        Assert.Equal(2, new WaypointDatabase(result.SafetyCopy).GetNotes().Count);
        Assert.Contains(_database.GetBackups(), file => file.BeforeRestore && file.Path == result.SafetyCopy);
    }

    [Fact]
    public void ARestoreThatFailsAfterTheCopyIsInIsToldAsRestoredWithAWarning()
    {
        _database.CreateNote("Kept", "from before", Now);

        // The backup carries a trigger that turns down marking the notes as restored, so the step after the copy fails.
        using (var connection = _database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER no_restore_mark BEFORE INSERT ON preferences WHEN NEW.key = 'notes_restored' BEGIN SELECT RAISE(ABORT, 'made to fail'); END";
            command.ExecuteNonQuery();
        }

        var backup = _database.BackUpNow(Now);
        _database.CreateNote("Added later", "after the backup", Now.AddHours(1));

        var result = _database.RestoreBackup(backup, Now.AddHours(2));

        Assert.True(result.Restored);
        Assert.Null(result.Problem);
        Assert.Contains("notes folder", result.Warning);
        Assert.Equal("Kept", Assert.Single(_database.GetNotes()).Title);
        Assert.DoesNotContain("—", result.Warning);
    }

    [Fact]
    public void BeforeRestoreCopiesDoNotPushOutDailyBackups()
    {
        var restoreFrom = Path.Combine(_folder, "saved.db");
        File.Move(_database.BackUpNow(Now.AddDays(-30)), restoreFrom);
        for (var day = 0; day < 20; day++)
        {
            _database.BackUpIfDue(Now.AddDays(day));
            if (day % 4 == 0)
            {
                Assert.True(_database.RestoreBackup(restoreFrom, Now.AddDays(day).AddHours(1)).Restored);
            }
        }

        var backups = _database.GetBackups();
        var daily = backups.Where(backup => !backup.BeforeRestore).ToList();
        Assert.Equal(WaypointDatabase.BackupsKept, daily.Count);
        Assert.Equal(Now.AddDays(6), daily[^1].At);
        var safety = backups.Where(backup => backup.BeforeRestore).ToList();
        Assert.Equal(WaypointDatabase.BeforeRestoreKept, safety.Count);
        Assert.Equal(Now.AddDays(16).AddHours(1), safety[0].At);
    }

    [Fact]
    public void RestoringTheOldestBeforeRestoreCopyReadsItInsteadOfDeletingItFirst()
    {
        // Three restores leave three before-restore copies, the oldest holding only the first note.
        var restoreFrom = _database.BackUpNow(Now.AddDays(-1));
        for (var restore = 0; restore < WaypointDatabase.BeforeRestoreKept; restore++)
        {
            _database.CreateNote($"Made up lecture {restore + 1}", "Made up notes", Now);
            Assert.True(_database.RestoreBackup(restoreFrom, Now.AddHours(restore + 1)).Restored);
        }

        var oldest = _database.GetBackups().Where(backup => backup.BeforeRestore).ToList()[^1];
        var result = _database.RestoreBackup(oldest.Path, Now.AddHours(5));

        Assert.True(result.Restored);
        Assert.Equal("Made up lecture 1", Assert.Single(_database.GetNotes()).Title);
        Assert.True(File.Exists(oldest.Path));

        // The next pass clears it out, now that it isn't being restored from.
        _database.BackUpNow(Now.AddHours(6));
        Assert.False(File.Exists(oldest.Path));
        Assert.Equal(WaypointDatabase.BeforeRestoreKept, _database.GetBackups().Count(backup => backup.BeforeRestore));
    }

    [Fact]
    public void ABeforeRestoreCopyDoesNotCountAsTheDailyBackup()
    {
        var backup = _database.BackUpNow(Now.AddDays(-1));
        _database.RestoreBackup(backup, Now);

        Assert.NotNull(_database.BackUpIfDue(Now.AddMinutes(1)));
    }

    [Fact]
    public void RestoreRefusesAFileThatIsNotABackup()
    {
        _database.CreateNote("Safe", "body", Now);
        var junk = Path.Combine(_folder, "junk.db");
        File.WriteAllText(junk, "not a database at all, just some text that is long enough to have a header");

        var result = _database.RestoreBackup(junk, Now);

        Assert.False(result.Restored);
        Assert.NotNull(result.Problem);
        Assert.Null(result.SafetyCopy);
        Assert.Single(_database.GetNotes());
    }

    [Fact]
    public void RestoreRefusesAnUnrelatedDatabase()
    {
        var other = Path.Combine(_folder, "other.db");
        using (var connection = new SqliteConnection($"Data Source={other};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE things (id INTEGER)";
            command.ExecuteNonQuery();
        }

        Assert.False(_database.RestoreBackup(other, Now).Restored);
    }

    [Fact]
    public void RestoreRefusesTheLiveDatabaseAndMissingFiles()
    {
        Assert.False(_database.RestoreBackup(_database.DatabasePath, Now).Restored);
        Assert.False(_database.RestoreBackup(Path.Combine(_folder, "missing.db"), Now).Restored);
        Assert.Empty(_database.GetBackups());
    }

    [Fact]
    public void ExportWritesOneMarkdownFilePerNoteGroupedByFolder()
    {
        var school = _database.CreateNoteFolder("School", null, Now).Id;
        var cs = _database.CreateNoteFolder("CS 101", school, Now).Id;
        _database.CreateNote("Recursion", "A function that calls itself.", Now, cs);
        _database.CreateNote("Groceries", "eggs", Now);
        _database.CreateNote("Groceries", "milk", Now);
        var gone = _database.CreateNote("Deleted", "x", Now);
        _database.DeleteNote(gone, Now);
        var output = Path.Combine(_folder, "export");

        var written = _database.ExportNotes(output);

        Assert.Equal(3, written);
        var recursion = File.ReadAllText(Path.Combine(output, "School", "CS 101", "Recursion.md"));
        Assert.StartsWith("# Recursion\n", recursion);
        Assert.Contains("A function that calls itself.", recursion);
        Assert.True(File.Exists(Path.Combine(output, "Unfiled", "Groceries.md")));
        Assert.True(File.Exists(Path.Combine(output, "Unfiled", "Groceries (2).md")));
    }

    [Fact]
    public void ExportIncludesEachNotesTranscriptsAndFileText()
    {
        var note = _database.CreateNote("Lecture 4", "# Mitosis\n- phases", Now);
        _database.AddTranscriptSource(note, "Transcript of lecture 4.m4a", "Today we cover mitosis.", null, Now);
        _database.AddNoteSource(note, Waypoint.Core.Notes.NoteSourceKind.Pdf, "slides.pdf", "Prophase, metaphase.", Now);
        _database.CreateNote("Plain", "no sources", Now);
        var output = Path.Combine(_folder, "export");

        _database.ExportNotes(output);

        var lecture = File.ReadAllText(Path.Combine(output, "Unfiled", "Lecture 4.md"));
        Assert.Contains("# Mitosis\n- phases", lecture);
        Assert.Contains("## Sources", lecture);
        Assert.Contains("### Transcript of lecture 4.m4a", lecture);
        Assert.Contains("Today we cover mitosis.", lecture);
        Assert.Contains("### slides.pdf", lecture);
        Assert.Contains("Prophase, metaphase.", lecture);
        Assert.True(lecture.IndexOf("phases", StringComparison.Ordinal) < lecture.IndexOf("## Sources", StringComparison.Ordinal));
        Assert.DoesNotContain("## Sources", File.ReadAllText(Path.Combine(output, "Unfiled", "Plain.md")));
    }

    [Fact]
    public void ExportMakesTitlesSafeForFileNames()
    {
        _database.CreateNote("What? A/B: test", "body", Now);
        _database.CreateNote("CON", "body", Now);
        var output = Path.Combine(_folder, "export");

        _database.ExportNotes(output);

        var names = Directory.GetFiles(Path.Combine(output, "Unfiled")).Select(Path.GetFileName).ToList();
        Assert.Contains("What- A-B- test.md", names);
        Assert.Contains("CON note.md", names);
    }
}
