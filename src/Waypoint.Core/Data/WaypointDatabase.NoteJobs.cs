using System.Globalization;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Remote;

namespace Waypoint.Core.Data;

/// <summary>What a server job writes: notes for one note, or practice questions for an exam.</summary>
public enum NoteJobKind
{
    Notes,
    Study,
}

/// <summary>
/// A server job as kept in note_jobs. Problem is only ever the plain reason from <see cref="PlainProblem"/>, and Result
/// is what a job that ran to the end came to, kept so the PC can still be told after a restart. Interrupted is set for
/// a job that was still running when the server stopped.
/// </summary>
public sealed record NoteJobRecord(
    string Id,
    NoteJobKind Kind,
    long? NoteId,
    DateTime StartedAt,
    DateTime? FinishedAt,
    NoteJobOutcome Outcome,
    string? Problem,
    string? Result,
    bool Interrupted);

/// <summary>
/// The server's notes and practice question jobs, kept so how each ended survives a restart. No prompt text, file
/// paths, or exception text is kept. A job still running when the server stopped is marked interrupted when it starts
/// again, and never run again on its own. Rows are pruned a while after they end.
/// </summary>
public sealed partial class WaypointDatabase
{
    private void CreateNoteJobTables() => Execute("""
        CREATE TABLE IF NOT EXISTS note_jobs (
            id TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            note_id INTEGER,
            started_at TEXT NOT NULL,
            finished_at TEXT,
            outcome TEXT NOT NULL,
            problem TEXT,
            result TEXT,
            interrupted INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS note_jobs_finished_at ON note_jobs (finished_at);
        """);

    public void AddNoteJob(string id, NoteJobKind kind, long? noteId, DateTime startedAt)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO note_jobs (id, kind, note_id, started_at, outcome)
            VALUES ($id, $kind, $note, $started, $outcome)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$note", (object?)noteId ?? DBNull.Value);
        command.Parameters.AddWithValue("$started", JobTime(startedAt));
        command.Parameters.AddWithValue("$outcome", nameof(NoteJobOutcome.Running));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Keeps how a job ended. Only a job still running is changed, so one already marked interrupted stays that way.
    /// A note id found by the end, such as the study note made, fills in one not known at the start. Interrupted is
    /// for one cut off as the server stopped.
    /// </summary>
    public bool FinishNoteJob(string id, NoteJobOutcome outcome, DateTime finishedAt, string? problem, string? result, long? noteId = null, bool interrupted = false)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE note_jobs
            SET outcome = $outcome, finished_at = $finished, problem = $problem, result = $result,
                note_id = COALESCE(note_id, $note), interrupted = $interrupted
            WHERE id = $id AND outcome = $running
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$outcome", outcome.ToString());
        command.Parameters.AddWithValue("$finished", JobTime(finishedAt));
        command.Parameters.AddWithValue("$problem", (object?)problem ?? DBNull.Value);
        command.Parameters.AddWithValue("$result", (object?)result ?? DBNull.Value);
        command.Parameters.AddWithValue("$note", (object?)noteId ?? DBNull.Value);
        command.Parameters.AddWithValue("$interrupted", interrupted ? 1 : 0);
        command.Parameters.AddWithValue("$running", nameof(NoteJobOutcome.Running));
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// As the server starts: every job left running was cut off by the stop, so it's marked as ended now, as
    /// interrupted, and never run again. Returns how many there were.
    /// </summary>
    public int InterruptRunningNoteJobs(DateTime now)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE note_jobs
            SET outcome = $lost, finished_at = $now, interrupted = 1, problem = NULL, result = NULL
            WHERE outcome = $running
            """;
        command.Parameters.AddWithValue("$lost", nameof(NoteJobOutcome.Lost));
        command.Parameters.AddWithValue("$now", JobTime(now));
        command.Parameters.AddWithValue("$running", nameof(NoteJobOutcome.Running));
        return command.ExecuteNonQuery();
    }

    public NoteJobRecord? GetNoteJob(string id)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, kind, note_id, started_at, finished_at, outcome, problem, result, interrupted
            FROM note_jobs WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadNoteJob(reader) : null;
    }

    /// <summary>Forgets jobs that ended before <paramref name="before"/>. Running ones are kept. Returns how many went.</summary>
    public int PruneNoteJobs(DateTime before)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM note_jobs WHERE finished_at IS NOT NULL AND finished_at < $before";
        command.Parameters.AddWithValue("$before", JobTime(before));
        return command.ExecuteNonQuery();
    }

    private static NoteJobRecord? ReadNoteJob(SqliteDataReader reader)
    {
        if (!Enum.TryParse<NoteJobKind>(reader.GetString(1), out var kind) || !Enum.TryParse<NoteJobOutcome>(reader.GetString(5), out var outcome))
        {
            return null;
        }

        return new NoteJobRecord(
            reader.GetString(0),
            kind,
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            ParseJobTime(reader.GetString(3)),
            reader.IsDBNull(4) ? null : ParseJobTime(reader.GetString(4)),
            outcome,
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt64(8) != 0);
    }

    /// <summary>Times are UTC, written so they sort as text, which the prune relies on.</summary>
    private static string JobTime(DateTime at) =>
        DateTime.SpecifyKind(at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : at, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseJobTime(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
