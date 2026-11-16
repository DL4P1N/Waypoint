namespace Waypoint.Core.Transcription;

/// <summary>Where a transcription job is. Queued, converting, transcribing, and writing are unfinished, and resume after a restart.</summary>
public static class TranscriptionStatus
{
    public const string Queued = "queued";
    public const string Converting = "converting";
    public const string Transcribing = "transcribing";

    /// <summary>The transcript is saved on the note, and the notes are being written from it.</summary>
    public const string Writing = "writing";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Canceled = "canceled";

    public static bool IsActive(string status) => status is Queued or Converting or Transcribing or Writing;
}

/// <summary>
/// One recording being turned into a note. Kept in <c>transcription_jobs</c>, so a crash or restart picks it up
/// again. WavPath is the 16 kHz copy Whisper reads, deleted once the transcript is saved. KeptAudioPath is set only
/// when the user asked to keep the audio.
/// </summary>
public sealed record TranscriptionJob(
    long Id,
    long NoteId,
    string OriginalPath,
    string? WavPath,
    bool KeepAudio,
    string? KeptAudioPath,
    string Status,
    int Progress,
    double? DurationSeconds,
    string? Error,
    DateTime? StartedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public string Name => Path.GetFileName(OriginalPath);

    public bool IsActive => TranscriptionStatus.IsActive(Status);
}

/// <summary>A program to run, with its arguments passed one by one so nothing needs quoting.</summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory);

/// <summary>How a program ended. ErrorTail is its last few lines of output, for saying what went wrong.</summary>
public sealed record ProcessResult(int ExitCode, string ErrorTail);

/// <summary>Runs a child process in the background. Every output line goes to onLine as it arrives.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string> onLine, CancellationToken cancellationToken);
}

/// <summary>Turns an audio or video file into the 16 kHz mono 16-bit WAV that Whisper reads. Returns its length.</summary>
public interface IAudioConverter
{
    Task<TimeSpan> ConvertAsync(string inputPath, string wavPath, CancellationToken cancellationToken);

    /// <summary>The extension of the smaller copy <see cref="CompressAsync"/> writes.</summary>
    string CompressedExtension => ".mp3";

    /// <summary>
    /// Writes a smaller copy of a WAV for keeping, which still plays from any moment. False, or an error, when it
    /// can't, and the WAV is kept as it is instead.
    /// </summary>
    Task<bool> CompressAsync(string wavPath, string outputPath, CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>
/// Downloads a file to <c>partPath</c>. When the file is already partly there it carries on from the end, and when
/// the server can't do that it starts the file over. Progress reports the bytes in the file so far.
/// </summary>
public interface IFileDownloader
{
    Task DownloadAsync(Uri url, string partPath, IProgress<long>? progress, CancellationToken cancellationToken);
}

/// <summary>What changed. Outcome is set when a job finished, with what to tell the user.</summary>
public sealed record TranscriptionChange(long? JobId, long? NoteId, string? Outcome = null);

/// <summary>How far the one-time download has got. Problem is set when it stopped.</summary>
public sealed record SetupState(bool Running, long Done, long Total, string? Problem = null)
{
    public int Percent => Total <= 0 ? 0 : (int)Math.Clamp(Done * 100 / Total, 0, 100);
}
