using Microsoft.Data.Sqlite;
using Waypoint.Core.Data;
using Waypoint.Core.Remote;

namespace Waypoint.Core.Tests;

/// <summary>
/// The server's notes jobs as kept in the database: how each ended, marking ones cut off by a restart, pruning, and
/// adding the table to a database made before it without touching what's already there.
/// </summary>
public sealed class NoteJobStoreTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-notejobs-{Guid.NewGuid()}");

    public NoteJobStoreTests() => Directory.CreateDirectory(_folder);

    private string DatabasePath => Path.Combine(_folder, "waypoint.db");

    public void Dispose()
    {
        for (var attempt = 0; ; attempt++)
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_folder, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(100);
            }
        }
    }

    [Fact]
    public void A_finished_job_keeps_its_kind_note_times_outcome_and_reason()
    {
        var database = new WaypointDatabase(DatabasePath);
        database.AddNoteJob("job-a", NoteJobKind.Study, null, Start);

        Assert.True(database.FinishNoteJob("job-a", NoteJobOutcome.Failed, Start.AddMinutes(3), "Claude isn't signed in.", null, noteId: 42));

        var kept = new WaypointDatabase(DatabasePath).GetNoteJob("job-a")!;
        Assert.Equal(NoteJobKind.Study, kept.Kind);
        Assert.Equal(42, kept.NoteId);
        Assert.Equal(Start, kept.StartedAt);
        Assert.Equal(Start.AddMinutes(3), kept.FinishedAt);
        Assert.Equal(NoteJobOutcome.Failed, kept.Outcome);
        Assert.Equal("Claude isn't signed in.", kept.Problem);
        Assert.False(kept.Interrupted);
        Assert.Null(database.GetNoteJob("made-up-job"));
    }

    [Fact]
    public void Running_jobs_are_marked_interrupted_once_and_then_stay_that_way()
    {
        var database = new WaypointDatabase(DatabasePath);
        database.AddNoteJob("running", NoteJobKind.Notes, 7, Start);
        database.AddNoteJob("done", NoteJobKind.Notes, 8, Start);
        database.FinishNoteJob("done", NoteJobOutcome.Succeeded, Start.AddMinutes(1), null, """{"done":true}""");

        Assert.Equal(1, database.InterruptRunningNoteJobs(Start.AddMinutes(5)));
        Assert.Equal(0, database.InterruptRunningNoteJobs(Start.AddMinutes(6)));

        var cut = database.GetNoteJob("running")!;
        Assert.Equal(NoteJobOutcome.Lost, cut.Outcome);
        Assert.True(cut.Interrupted);
        Assert.Equal(Start.AddMinutes(5), cut.FinishedAt);
        Assert.Equal(NoteJobOutcome.Succeeded, database.GetNoteJob("done")!.Outcome);

        // The old run finishing late doesn't change it.
        Assert.False(database.FinishNoteJob("running", NoteJobOutcome.Succeeded, Start.AddMinutes(7), null, """{"done":true}"""));
        Assert.True(database.GetNoteJob("running")!.Interrupted);
    }

    [Fact]
    public void Pruning_forgets_jobs_that_ended_before_the_cutoff_and_keeps_running_ones()
    {
        var database = new WaypointDatabase(DatabasePath);
        database.AddNoteJob("old", NoteJobKind.Notes, 1, Start);
        database.FinishNoteJob("old", NoteJobOutcome.Cancelled, Start.AddMinutes(1), null, null);
        database.AddNoteJob("recent", NoteJobKind.Notes, 2, Start);
        database.FinishNoteJob("recent", NoteJobOutcome.Cancelled, Start.AddMinutes(30), null, null);
        database.AddNoteJob("running", NoteJobKind.Notes, 3, Start);

        Assert.Equal(1, database.PruneNoteJobs(Start.AddMinutes(10)));

        Assert.Null(database.GetNoteJob("old"));
        Assert.NotNull(database.GetNoteJob("recent"));
        Assert.NotNull(database.GetNoteJob("running"));
    }

    [Fact]
    public void Opening_a_database_from_before_jobs_were_kept_adds_the_table_and_keeps_everything_else()
    {
        var first = new WaypointDatabase(DatabasePath);
        first.SetPreference("made_up_setting", "kept");
        var noteId = first.CreateNote("Cells", "The cell is the unit of life.", Start, null);

        // As a server from before this change left it: no note_jobs table.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString()))
        {
            connection.Open();
            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP INDEX IF EXISTS note_jobs_finished_at; DROP TABLE note_jobs;";
            drop.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var reopened = new WaypointDatabase(DatabasePath);

        Assert.Equal("kept", reopened.GetPreference("made_up_setting"));
        Assert.Equal("Cells", reopened.GetNote(noteId)!.Title);
        reopened.AddNoteJob("job-a", NoteJobKind.Notes, noteId, Start);
        Assert.Equal(NoteJobOutcome.Running, reopened.GetNoteJob("job-a")!.Outcome);

        // Opening it again changes nothing.
        Assert.NotNull(new WaypointDatabase(DatabasePath).GetNoteJob("job-a"));
    }
}
