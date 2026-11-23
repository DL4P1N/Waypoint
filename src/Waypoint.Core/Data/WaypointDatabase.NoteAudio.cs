using Waypoint.Core.Transcription;

namespace Waypoint.Core.Data;

/// <summary>Where a note's kept audio is recorded: on its transcript source, and on the job that made it.</summary>
public sealed partial class WaypointDatabase
{
    /// <summary>Points a source at its kept file, or at none. The source's text is never changed.</summary>
    public bool SetNoteSourceFile(long sourceId, string? filePath)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE note_sources SET file_path = $path WHERE id = $id";
        command.Parameters.AddWithValue("$path", (object?)filePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", sourceId);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>Every recording job a note has had, oldest first.</summary>
    public IReadOnlyList<TranscriptionJob> GetTranscriptionJobsForNote(long noteId) => ReadJobs("WHERE note_id = $id ORDER BY id", noteId);

    /// <summary>Records whether a job's audio is kept, and where.</summary>
    public bool SetJobKeptAudio(long jobId, string? keptPath)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE transcription_jobs SET keep_audio = $keep, kept_audio_path = $path WHERE id = $id";
        command.Parameters.AddWithValue("$keep", keptPath is null ? 0 : 1);
        command.Parameters.AddWithValue("$path", (object?)keptPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", jobId);
        return command.ExecuteNonQuery() > 0;
    }
}
