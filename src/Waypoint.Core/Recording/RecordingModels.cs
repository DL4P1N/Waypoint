namespace Waypoint.Core.Recording;

/// <summary>What a recording listens to. Computer audio is what the PC plays, such as a Zoom class.</summary>
public enum AudioInput
{
    Microphone,
    ComputerAudio,
    Both,
}

/// <summary>
/// Where a recording is. Recording is unfinished, and finding one at start means Waypoint closed without stopping
/// it. Stopped is saved and waiting to be made into notes. HandedOff is being transcribed. Done means the
/// transcript is saved and the recording's file is gone unless the user kept a copy.
/// </summary>
public static class RecordingStatus
{
    public const string Recording = "recording";
    public const string Stopped = "stopped";
    public const string HandedOff = "handed_off";
    public const string Done = "done";
    public const string Empty = "empty";
}

/// <summary>One recording, kept in <c>recordings</c> from the moment it starts, so a crash never loses track of it.</summary>
public sealed record RecordingInfo(
    long Id,
    string Path,
    AudioInput Input,
    bool KeepAudio,
    string Title,
    string Status,
    DateTime StartedAt,
    DateTime? EndedAt,
    double? DurationSeconds,
    long? JobId,
    long? NoteId)
{
    public TimeSpan Length => TimeSpan.FromSeconds(DurationSeconds ?? 0);
}

/// <summary>A moment flagged while recording. Text is a line the user typed, or null for a plain "this matters".</summary>
public sealed record RecordingMark(long Id, long RecordingId, TimeSpan Offset, string? Text);

/// <summary>Keeps the PC from going to sleep while something is recorded. Only sleep is blocked, never closing or shutting down.</summary>
public interface IKeepAwake
{
    void Hold();

    void Release();
}

/// <summary>For tests, and for when there's nothing to hold.</summary>
public sealed class NoKeepAwake : IKeepAwake
{
    public bool Held { get; private set; }

    public void Hold() => Held = true;

    public void Release() => Held = false;
}
