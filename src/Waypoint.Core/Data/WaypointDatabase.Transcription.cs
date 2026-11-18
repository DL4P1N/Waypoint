using System.Globalization;
using Waypoint.Core.Notes;
using Waypoint.Core.Transcription;

namespace Waypoint.Core.Data;

/// <summary>
/// Recordings waiting to be turned into notes, kept so a crash or restart carries on with them, and the timestamps
/// Whisper gives a transcript, kept next to its text.
/// </summary>
public sealed partial class WaypointDatabase
{
    private const string JobColumns = "id, note_id, original_path, wav_path, keep_audio, kept_audio_path, status, progress, duration_seconds, error, started_at, created_at, updated_at";

    private void CreateTranscriptionTables()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS transcription_jobs (
                id INTEGER PRIMARY KEY,
                note_id INTEGER NOT NULL REFERENCES items(id),
                original_path TEXT NOT NULL,
                wav_path TEXT,
                keep_audio INTEGER NOT NULL DEFAULT 0,
                kept_audio_path TEXT,
                status TEXT NOT NULL,
                progress INTEGER NOT NULL DEFAULT 0,
                duration_seconds REAL,
                error TEXT,
                started_at TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS transcription_jobs_note ON transcription_jobs(note_id);
            """);

        // Whisper's JSON with a start and end time for each line, for later features that jump to a moment.
        AddColumnIfMissing("note_sources", "timestamps", "TEXT");
        CreateRecordingTables();
    }

    public long AddTranscriptionJob(long noteId, string originalPath, bool keepAudio, DateTime now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO transcription_jobs (note_id, original_path, keep_audio, status, created_at, updated_at)
            VALUES ($note, $path, $keep, $status, $now, $now)
            RETURNING id
            """;
        command.Parameters.AddWithValue("$note", noteId);
        command.Parameters.AddWithValue("$path", originalPath);
        command.Parameters.AddWithValue("$keep", keepAudio ? 1 : 0);
        command.Parameters.AddWithValue("$status", TranscriptionStatus.Queued);
        command.Parameters.AddWithValue("$now", FormatReminder(now));
        return (long)command.ExecuteScalar()!;
    }

    public TranscriptionJob? GetTranscriptionJob(long id) => ReadJobs("WHERE id = $id", id).FirstOrDefault();

    /// <summary>Jobs still to run, oldest first, which is the order they run in.</summary>
    public IReadOnlyList<TranscriptionJob> GetActiveTranscriptionJobs() =>
        ReadJobs($"WHERE status IN ('{TranscriptionStatus.Queued}', '{TranscriptionStatus.Converting}', '{TranscriptionStatus.Transcribing}', '{TranscriptionStatus.Writing}') ORDER BY id", null);

    /// <summary>The latest job for each note that is still running or failed, for the note list and the note's status line.</summary>
    public IReadOnlyDictionary<long, TranscriptionJob> GetOpenTranscriptionJobsByNote() =>
        ReadJobs($"WHERE status NOT IN ('{TranscriptionStatus.Done}', '{TranscriptionStatus.Canceled}') ORDER BY id", null)
            .GroupBy(job => job.NoteId)
            .ToDictionary(group => group.Key, group => group.Last());

    /// <summary>
    /// Saves a job's state. A job the user canceled stays canceled, so a step that was already running when they
    /// canceled can't bring it back. Returns false when it was canceled or is gone.
    /// </summary>
    public bool SaveTranscriptionJob(TranscriptionJob job, DateTime now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE transcription_jobs SET wav_path = $wav, kept_audio_path = $kept, status = $status, progress = $progress,
                duration_seconds = $duration, error = $error, started_at = $started, updated_at = $now
            WHERE id = $id AND (status != $canceled OR $status = $canceled)
            """;
        command.Parameters.AddWithValue("$wav", (object?)job.WavPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$kept", (object?)job.KeptAudioPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", job.Status);
        command.Parameters.AddWithValue("$progress", job.Progress);
        command.Parameters.AddWithValue("$duration", (object?)job.DurationSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)job.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$started", job.StartedAt is { } started ? FormatReminder(started) : DBNull.Value);
        command.Parameters.AddWithValue("$now", FormatReminder(now));
        command.Parameters.AddWithValue("$canceled", TranscriptionStatus.Canceled);
        command.Parameters.AddWithValue("$id", job.Id);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>Saves a transcript, unchanged, as a source on the note, with Whisper's timestamps beside it. Returns 0 if the note is gone.</summary>
    public long AddTranscriptSource(long noteId, string name, string text, string? timestampsJson, DateTime now, string? filePath = null) =>
        AddTranscriptSource(noteId, name, text, timestampsJson, now, filePath, clientId: null);

    /// <summary>
    /// The same, sent from a PC with an id of its own. Sent again with the same id, such as after the answer was
    /// lost, it gives the source already kept rather than adding another.
    /// </summary>
    public long AddTranscriptSource(long noteId, string name, string text, string? timestampsJson, DateTime now, string? filePath, string? clientId)
    {
        lock (_sentOnce)
        {
            if (clientId is not null && SourceSentAs(noteId, clientId) is { } kept)
            {
                return kept;
            }

            // The source, its timestamps, and the id it was sent under are one write, so a failure between can't
            // leave one without its timestamps, or one the PC sends again and gets twice.
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var id = AddNoteSource(connection, noteId, NoteSourceKind.Transcript, name, text, now, filePath);
            if (id != 0 && timestampsJson is not null)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE note_sources SET timestamps = $json WHERE id = $id";
                command.Parameters.AddWithValue("$json", timestampsJson);
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }

            if (id != 0 && clientId is not null)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE note_sources SET client_id = $client WHERE id = $id";
                command.Parameters.AddWithValue("$client", clientId);
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            return id;
        }
    }

    private long? SourceSentAs(long noteId, string clientId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM note_sources WHERE client_id = $client AND note_id = $note";
        command.Parameters.AddWithValue("$client", clientId);
        command.Parameters.AddWithValue("$note", noteId);
        return command.ExecuteScalar() is long id ? id : null;
    }


    public string? GetSourceTimestamps(long sourceId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT timestamps FROM note_sources WHERE id = $id";
        command.Parameters.AddWithValue("$id", sourceId);
        return command.ExecuteScalar() as string;
    }

    private List<TranscriptionJob> ReadJobs(string where, long? id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {JobColumns} FROM transcription_jobs {where}";
        if (id is { } value)
        {
            command.Parameters.AddWithValue("$id", value);
        }

        var jobs = new List<TranscriptionJob>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            jobs.Add(new TranscriptionJob(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4) != 0,
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                (int)reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetDouble(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : ParseTime(reader.GetString(10)),
                ParseTime(reader.GetString(11)),
                ParseTime(reader.GetString(12))));
        }

        return jobs;
    }

    private static DateTime ParseTime(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture);
}
