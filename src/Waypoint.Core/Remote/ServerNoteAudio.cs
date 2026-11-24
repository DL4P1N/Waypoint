using Waypoint.Core.Data;
using Waypoint.Core.Notes;
using Waypoint.Core.Transcription;

namespace Waypoint.Core.Remote;

/// <summary>
/// A note's audio in server mode. Recording and transcribing stay on this PC, and so do the jobs that did them. Kept
/// audio is a file kept with the note on the server, so it isn't lost with this PC, and a copy stays here in the
/// audio folder for playing. A note whose audio is only on the server fetches it when it's kept again here.
/// Deleting removes Waypoint's own copies here and on the server, never the file the user imported.
/// </summary>
public sealed class ServerNoteAudio(ServerNotesData notes, WaypointDatabase local, string dataDirectory, Func<DateTime>? clock = null) : INoteAudio
{
    private readonly string _audioFolder = Path.Combine(dataDirectory, "audio");
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.Now);

    public IReadOnlyList<NoteRecording> For(long noteId)
    {
        var sources = notes.GetNoteSources(noteId).Where(source => source.Kind == NoteSourceKind.Transcript).ToList();
        if (sources.Count == 0)
        {
            return [];
        }

        var jobs = local.GetTranscriptionJobsForNote(noteId);
        return sources.Select(source =>
        {
            var job = JobFor(source, jobs);
            var original = job is not null && File.Exists(job.OriginalPath) ? job.OriginalPath : null;
            return new NoteRecording(source.Id, source.Name, Here(source, job), original, TranscriptLines.Parse(notes.GetSourceTimestamps(source.Id)), OnServer: source.FilePath is not null);
        }).ToList();
    }

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

        var jobs = local.GetTranscriptionJobsForNote(noteId);
        var sources = notes.GetNoteSources(noteId);
        var kept = 0;
        var notSent = 0;
        var notRead = 0;
        foreach (var recording in recordings.Where(recording => recording.CanKeep))
        {
            var source = sources.First(found => found.Id == recording.SourceId);
            var job = JobFor(source, jobs);
            if (recording.OnServer && await notes.FetchFileAsync(noteId, source.Id, CachePath(source), cancellationToken))
            {
                kept++;
                continue;
            }

            if (recording.OriginalPath is not { } original)
            {
                continue;
            }

            var target = await CopyAsync(original, cancellationToken);
            if (job is not null)
            {
                local.SetJobKeptAudio(job.Id, target);
            }

            var result = await SendAsync(noteId, source.Id, target, cancellationToken);
            if (result == FileSendResult.NotRead)
            {
                notRead++;
            }
            else if (result != FileSendResult.Sent)
            {
                notSent++;
            }

            kept++;
        }

        if (kept == 0)
        {
            return new NoteAudioResult(false, "The original recording isn't on this PC anymore, and the server doesn't have it, so its audio can't be kept. The transcript and notes are still here.");
        }

        if (notRead > 0)
        {
            return new NoteAudioResult(true, "Kept this note's audio on this PC. It couldn't go to the server, because the copy here couldn't be read, such as while another program has it open.");
        }

        if (notSent > 0)
        {
            return new NoteAudioResult(true, "Kept this note's audio on this PC. It couldn't go to the server, because it's too big or the server couldn't be reached.");
        }

        return new NoteAudioResult(true, recordings.Count(recording => !recording.IsKept) > kept
            ? "Kept the audio that was still here. Some of the original recordings are gone."
            : "Kept this note's audio. Click a line in the transcript to play it.");
    }

    public NoteAudioResult Delete(long noteId)
    {
        if (Busy(noteId) is { } busy)
        {
            return new NoteAudioResult(false, busy);
        }

        var jobs = local.GetTranscriptionJobsForNote(noteId);
        var sources = notes.GetNoteSources(noteId).Where(source => source.Kind == NoteSourceKind.Transcript).ToList();
        var withAudio = sources.Where(source => source.FilePath is not null || Here(source, JobFor(source, jobs)) is not null).ToList();
        if (withAudio.Count == 0)
        {
            return new NoteAudioResult(false, "This note has no kept audio.");
        }

        // The server's copy goes first, since it needs the server. If it's away, nothing is deleted yet.
        foreach (var source in withAudio.Where(source => source.FilePath is not null))
        {
            notes.DropFile(noteId, source.Id);
        }

        foreach (var source in withAudio)
        {
            var job = JobFor(source, jobs);
            foreach (var path in new[] { job?.KeptAudioPath, CachePath(source) }.OfType<string>().Where(IsOurs))
            {
                TryDelete(path);
            }

            if (job is not null)
            {
                local.SetJobKeptAudio(job.Id, null);
            }
        }

        return new NoteAudioResult(true, "Deleted this note's audio. The transcript and notes are still here.");
    }

    private string? Busy(long noteId) =>
        local.GetTranscriptionJobsForNote(noteId).Any(job => job.IsActive)
            ? "This recording is still being turned into notes. Try again once it's done."
            : null;

    private async Task<FileSendResult> SendAsync(long noteId, long sourceId, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await notes.KeepFileAsync(noteId, sourceId, path, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return FileSendResult.Away;
        }
    }

    /// <summary>The copy on this PC that can be played: the one kept when it was transcribed, else one fetched from the server.</summary>
    private string? Here(NoteSource source, TranscriptionJob? job) =>
        job?.KeptAudioPath is { } kept && File.Exists(kept) ? kept
        : source.FilePath is not null && File.Exists(CachePath(source)) ? CachePath(source)
        : null;

    /// <summary>Where audio fetched from the server is kept, named by its source so it's found again.</summary>
    private string CachePath(NoteSource source)
    {
        var extension = Path.GetExtension(source.FilePath ?? "");
        return Path.Combine(_audioFolder, $"server-{source.Id}{(extension.Length is > 1 and <= 10 ? extension : ".audio")}");
    }

    private bool IsOurs(string path)
    {
        var folder = Path.GetFullPath(_audioFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    }

    private static TranscriptionJob? JobFor(NoteSource source, IReadOnlyList<TranscriptionJob> jobs) =>
        jobs.LastOrDefault(job => string.Equals(source.Name, $"Transcript of {job.Name}", StringComparison.OrdinalIgnoreCase))
        ?? (jobs.Count == 1 ? jobs[0] : null);

    private async Task<string> CopyAsync(string source, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_audioFolder);
        var stem = $"{_clock():yyyy-MM-dd HHmm} {Path.GetFileNameWithoutExtension(source)}";
        var extension = Path.GetExtension(source);
        var target = Path.Combine(_audioFolder, stem + extension);
        for (var n = 2; File.Exists(target); n++)
        {
            target = Path.Combine(_audioFolder, $"{stem} ({n}){extension}");
        }

        var part = target + ".part";
        await using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
        await using (var to = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            await from.CopyToAsync(to, cancellationToken);
        }

        File.Move(part, target);
        return target;
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
