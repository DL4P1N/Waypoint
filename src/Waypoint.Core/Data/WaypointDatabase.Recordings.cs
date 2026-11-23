using Waypoint.Core.Recording;

namespace Waypoint.Core.Data;

/// <summary>
/// Lectures recorded on this PC, saved from the moment recording starts so a crash or power cut never loses track
/// of one, and the moments marked while recording, so the notes can stress them.
/// </summary>
public sealed partial class WaypointDatabase
{
    private const string RecordingColumns = "id, path, input, keep_audio, title, status, started_at, ended_at, duration_seconds, job_id, note_id";

    private void CreateRecordingTables()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS recordings (
                id INTEGER PRIMARY KEY,
                path TEXT NOT NULL,
                input TEXT NOT NULL,
                keep_audio INTEGER NOT NULL DEFAULT 0,
                title TEXT NOT NULL,
                status TEXT NOT NULL,
                started_at TEXT NOT NULL,
                ended_at TEXT,
                duration_seconds REAL,
                job_id INTEGER,
                note_id INTEGER
            );
            CREATE TABLE IF NOT EXISTS recording_marks (
                id INTEGER PRIMARY KEY,
                recording_id INTEGER NOT NULL REFERENCES recordings(id),
                offset_ms INTEGER NOT NULL,
                text TEXT,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS recording_marks_recording ON recording_marks(recording_id);
            """);
    }

    /// <summary>Saves a new recording before any audio is written. The path is filled in by the caller from the id.</summary>
    public long AddRecording(AudioInput input, bool keepAudio, string title, DateTime startedAt)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recordings (path, input, keep_audio, title, status, started_at)
            VALUES ('', $input, $keep, $title, $status, $started)
            RETURNING id
            """;
        command.Parameters.AddWithValue("$input", input.ToString());
        command.Parameters.AddWithValue("$keep", keepAudio ? 1 : 0);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$status", RecordingStatus.Recording);
        command.Parameters.AddWithValue("$started", FormatReminder(startedAt));
        return (long)command.ExecuteScalar()!;
    }

    public void SetRecordingPath(long id, string path)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recordings SET path = $path WHERE id = $id";
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void FinishRecording(long id, string status, DateTime? endedAt, double durationSeconds)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recordings SET status = $status, ended_at = COALESCE($ended, ended_at), duration_seconds = $duration WHERE id = $id";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$ended", endedAt is { } ended ? FormatReminder(ended) : DBNull.Value);
        command.Parameters.AddWithValue("$duration", durationSeconds);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void SetRecordingStatus(long id, string status)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recordings SET status = $status WHERE id = $id";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Links a recording to the transcription job and note made from it, which is also how its marks reach the note.</summary>
    public void HandOffRecording(long id, long jobId, long noteId)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recordings SET status = $status, job_id = $job, note_id = $note WHERE id = $id";
        command.Parameters.AddWithValue("$status", RecordingStatus.HandedOff);
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$note", noteId);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Puts a handed-off recording back to waiting to be made into notes, as when its transcribing was stopped.</summary>
    public void TakeBackRecording(long id)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recordings SET status = $status, job_id = NULL, note_id = NULL WHERE id = $id AND status = $handed";
        command.Parameters.AddWithValue("$status", RecordingStatus.Stopped);
        command.Parameters.AddWithValue("$handed", RecordingStatus.HandedOff);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public RecordingInfo? GetRecording(long id) => ReadRecordings("WHERE id = $id", id).FirstOrDefault();

    public IReadOnlyList<RecordingInfo> GetRecordingsWithStatus(string status)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {RecordingColumns} FROM recordings WHERE status = $status ORDER BY id";
        command.Parameters.AddWithValue("$status", status);
        return ReadRecordings(command);
    }

    public long AddRecordingMark(long recordingId, TimeSpan offset, string? text, DateTime now)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recording_marks (recording_id, offset_ms, text, created_at)
            VALUES ($recording, $offset, $text, $now)
            RETURNING id
            """;
        command.Parameters.AddWithValue("$recording", recordingId);
        command.Parameters.AddWithValue("$offset", (long)Math.Max(0, offset.TotalMilliseconds));
        command.Parameters.AddWithValue("$text", string.IsNullOrWhiteSpace(text) ? DBNull.Value : text.Trim());
        command.Parameters.AddWithValue("$now", FormatReminder(now));
        return (long)command.ExecuteScalar()!;
    }

    public IReadOnlyList<RecordingMark> GetRecordingMarks(long recordingId) =>
        ReadMarks("WHERE recording_id = $id ORDER BY offset_ms, id", recordingId);

    /// <summary>The marks from every recording made into this note, in the order they happened.</summary>
    public IReadOnlyList<RecordingMark> GetRecordingMarksForNote(long noteId) =>
        ReadMarks("WHERE recording_id IN (SELECT id FROM recordings WHERE note_id = $id) ORDER BY recording_id, offset_ms, id", noteId);

    private List<RecordingMark> ReadMarks(string where, long id)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id, recording_id, offset_ms, text FROM recording_marks {where}";
        command.Parameters.AddWithValue("$id", id);
        var marks = new List<RecordingMark>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            marks.Add(new RecordingMark(
                reader.GetInt64(0),
                reader.GetInt64(1),
                TimeSpan.FromMilliseconds(reader.GetInt64(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return marks;
    }

    private List<RecordingInfo> ReadRecordings(string where, long id)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {RecordingColumns} FROM recordings {where}";
        command.Parameters.AddWithValue("$id", id);
        return ReadRecordings(command);
    }

    private static List<RecordingInfo> ReadRecordings(Microsoft.Data.Sqlite.SqliteCommand command)
    {
        var recordings = new List<RecordingInfo>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            recordings.Add(new RecordingInfo(
                reader.GetInt64(0),
                reader.GetString(1),
                Enum.TryParse<AudioInput>(reader.GetString(2), out var input) ? input : AudioInput.Microphone,
                reader.GetInt64(3) != 0,
                reader.GetString(4),
                reader.GetString(5),
                ParseTime(reader.GetString(6)),
                reader.IsDBNull(7) ? null : ParseTime(reader.GetString(7)),
                reader.IsDBNull(8) ? null : reader.GetDouble(8),
                reader.IsDBNull(9) ? null : reader.GetInt64(9),
                reader.IsDBNull(10) ? null : reader.GetInt64(10)));
        }

        return recordings;
    }
}
