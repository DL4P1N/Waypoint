using Waypoint.Core.Data;
using Waypoint.Core.Transcription;

namespace Waypoint.Core.Notes;

/// <summary>
/// One recording on a note. KeptPath is the copy in <c>&lt;data&gt;\audio\</c> when the audio was kept and the file
/// is still there. OriginalPath is the file it was imported from, when that's still on the PC, so the audio can be
/// kept later. Lines are the transcript's lines with times, empty when whisper wrote none. OnServer is true in
/// server mode when the server has the audio, so it can be fetched to this PC.
/// </summary>
public sealed record NoteRecording(long SourceId, string Name, string? KeptPath, string? OriginalPath, IReadOnlyList<TranscriptLine> Lines, bool OnServer = false)
{
    public bool IsKept => KeptPath is not null;

    public bool CanKeep => !IsKept && (OriginalPath is not null || OnServer);
}

/// <summary>Keeping and deleting a note's audio, on this PC alone or with the server.</summary>
public interface INoteAudio
{
    IReadOnlyList<NoteRecording> For(long noteId);

    Task<NoteAudioResult> KeepAsync(long noteId, CancellationToken cancellationToken = default);

    NoteAudioResult Delete(long noteId);
}

/// <summary>What came of keeping or deleting a note's audio.</summary>
public sealed record NoteAudioResult(bool Changed, string Message);

/// <summary>
/// Keeping audio is a choice made per note. Kept audio is what lets a transcript line play from its moment.
/// Deleting only ever removes Waypoint's own copy in the audio folder, never the file the user imported, and never
/// the transcript or notes.
/// </summary>
public sealed class NoteAudio(WaypointDatabase database, string dataDirectory, Func<DateTime>? clock = null) : INoteAudio
{
    private readonly string _audioFolder = Path.Combine(dataDirectory, "audio");
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.Now);

    /// <summary>The note's recordings, in the order they were added.</summary>
    public IReadOnlyList<NoteRecording> For(long noteId)
    {
        var sources = database.GetNoteSources(noteId).Where(source => source.Kind == NoteSourceKind.Transcript).ToList();
        if (sources.Count == 0)
        {
            return [];
        }

        var jobs = database.GetTranscriptionJobsForNote(noteId);
        return sources.Select(source =>
        {
            var job = JobFor(source, jobs);
            var kept = source.FilePath is { } path && File.Exists(path) ? path : null;
            var original = job is not null && File.Exists(job.OriginalPath) ? job.OriginalPath : null;
            return new NoteRecording(source.Id, source.Name, kept, original, TranscriptLines.Parse(database.GetSourceTimestamps(source.Id)));
        }).ToList();
    }

    /// <summary>Why the audio can't be kept or deleted right now, or null when it can.</summary>
    public string? Busy(long noteId) =>
        database.GetTranscriptionJobsForNote(noteId).Any(job => job.IsActive)
            ? "This recording is still being turned into notes. Try again once it's done."
            : null;

    /// <summary>Copies each recording's original file into the audio folder, so its lines can be played.</summary>
    public async Task<NoteAudioResult> KeepAsync(long noteId, CancellationToken cancellationToken = default)
    {
        if (Busy(noteId) is { } busy)
        {
            return new NoteAudioResult(false, busy);
        }

        var recordings = For(noteId);
        if (recordings.Count == 0)
        {
            return new NoteAudioResult(false, "This note has no recording.");
        }

        if (recordings.All(recording => recording.IsKept))
        {
            return new NoteAudioResult(false, "This note's audio is already kept.");
        }

        var jobs = database.GetTranscriptionJobsForNote(noteId);
        var kept = 0;
        foreach (var recording in recordings.Where(recording => recording.CanKeep))
        {
            var target = await CopyAsync(recording.OriginalPath!, cancellationToken);
            database.SetNoteSourceFile(recording.SourceId, target);
            if (JobFor(database.GetNoteSources(noteId).First(source => source.Id == recording.SourceId), jobs) is { } job)
            {
                database.SetJobKeptAudio(job.Id, target);
            }

            kept++;
        }

        if (kept == 0)
        {
            return new NoteAudioResult(false, "The original recording isn't on this PC anymore, so its audio can't be kept. The transcript and notes are still here.");
        }

        return new NoteAudioResult(true, recordings.Count(recording => !recording.IsKept) > kept
            ? "Kept the audio that was still on this PC. Some of the original recordings are gone."
            : "Kept this note's audio. Click a line in the transcript to play it.");
    }

    /// <summary>Deletes Waypoint's kept copies of the note's audio. The transcript, notes, and the user's own files stay.</summary>
    public NoteAudioResult Delete(long noteId)
    {
        if (Busy(noteId) is { } busy)
        {
            return new NoteAudioResult(false, busy);
        }

        var sources = database.GetNoteSources(noteId).Where(source => source.Kind == NoteSourceKind.Transcript && source.FilePath is not null).ToList();
        if (sources.Count == 0)
        {
            return new NoteAudioResult(false, "This note has no kept audio.");
        }

        var jobs = database.GetTranscriptionJobsForNote(noteId);
        foreach (var source in sources)
        {
            if (IsOurs(source.FilePath!))
            {
                TryDelete(source.FilePath!);
            }

            database.SetNoteSourceFile(source.Id, null);
            if (JobFor(source, jobs) is { } job)
            {
                database.SetJobKeptAudio(job.Id, null);
            }
        }

        return new NoteAudioResult(true, "Deleted this note's audio. The transcript and notes are still here.");
    }

    /// <summary>Only a file inside the audio folder is Waypoint's own copy, safe to delete.</summary>
    public bool IsOurs(string path)
    {
        var folder = Path.GetFullPath(_audioFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The job that made a transcript, matched by its name, else the note's only job.</summary>
    private static TranscriptionJob? JobFor(NoteSource source, IReadOnlyList<TranscriptionJob> jobs) =>
        jobs.LastOrDefault(job => source.FilePath is not null && string.Equals(job.KeptAudioPath, source.FilePath, StringComparison.OrdinalIgnoreCase))
        ?? jobs.LastOrDefault(job => string.Equals(source.Name, $"Transcript of {job.Name}", StringComparison.OrdinalIgnoreCase))
        ?? (jobs.Count == 1 ? jobs[0] : null);

    private async Task<string> CopyAsync(string source, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_audioFolder);
        var stem = $"{_clock():yyyy-MM-dd HHmm} {Path.GetFileNameWithoutExtension(source)}";
        // Never ending in .part, so the start-up sweep of the audio folder can't take it for a copy cut short.
        var extension = Platform.PartFiles.FinishedExtension(Path.GetExtension(source));
        var target = Path.Combine(_audioFolder, stem + extension);
        for (var n = 2; File.Exists(target); n++)
        {
            target = Path.Combine(_audioFolder, $"{stem} ({n}){extension}");
        }

        // A copy cut short is never finished, so it goes. One left by a crash is swept when transcribing starts up.
        var part = target + ".part";
        try
        {
            await using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
            await using (var to = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                await from.CopyToAsync(to, cancellationToken);
            }

            File.Move(part, target);
            return target;
        }
        finally
        {
            Platform.PartFiles.TryDelete(part);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // In use, such as while it's playing. The note no longer points at it either way.
        }
    }
}
