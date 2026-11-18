using Waypoint.Core.Files;
using Waypoint.Core.Notes;
using Waypoint.Core.Recording;

namespace Waypoint.Core.Remote;

/// <summary>An answer that may be nothing, such as a note that's gone, which plain JSON can't say on its own.</summary>
public sealed record Found<T>(T? Value);

/// <summary>ClientId, when given, makes sending it again after a lost answer give the same note rather than another.</summary>
public sealed record NewNoteRequest(string Title, string Body, long? FolderId, string? ClientId = null);

public sealed record SaveNoteRequest(string Title, string Body);

public sealed record NewFolderRequest(string Name, long? ParentId);

public sealed record MoveFolderRequest(long? ParentId, int Position);

public sealed record MoveNoteRequest(long? FolderId);

public sealed record CourseRequest(string Course);

public sealed record PlaceRequest(long? PickedFolderId, bool FolderPicked);

/// <summary>A file read on the PC, with its text recognized there, so the server only ever sees text.</summary>
public sealed record FromFileRequest(FileText File, long? PickedFolderId, bool FolderPicked);

/// <summary>At is when the recording started, so it's filed under the class that was on then.</summary>
public sealed record RecordingNoteRequest(string FileName, long? PickedFolderId, bool FolderPicked, DateTime At, string? Title);

/// <summary>ClientId, when given, makes sending it again after a lost answer keep it once.</summary>
public sealed record TranscriptRequest(string RecordingName, string Transcript, string? TimestampsJson, string? ClientId = null);

/// <summary>Marks are the moments marked while recording, which stay in the PC's own recordings.</summary>
public sealed record WriteNotesRequest(string? Request, bool ReplaceEdits, IReadOnlyList<RecordingMark>? Marks);

public sealed record StudyRequest(StudyExam Exam, long FolderId, bool ReplaceEdits);

public sealed record MarkdownRequest(string Markdown);

/// <summary>Writing with Haiku can take minutes, longer than one request waits, so it runs on the server as a job.</summary>
public sealed record NoteJobStarted(string Id);

/// <summary>How a notes job stands, or how it ended.</summary>
public enum NoteJobOutcome
{
    Running,

    /// <summary>It ran to the end, with what came of it in <see cref="NoteJobState.Notes"/> or <see cref="NoteJobState.Study"/>.</summary>
    Succeeded,

    /// <summary>It broke off partway, with a plain reason in <see cref="NoteJobState.Problem"/>.</summary>
    Failed,

    /// <summary>It was stopped by the PC. One stopped by the server shutting down is told as lost and interrupted.</summary>
    Cancelled,

    /// <summary>The server doesn't know how the job ended, such as one cut off by a restart. See <see cref="NoteJobState.Interrupted"/>.</summary>
    Lost,
}

/// <summary>
/// Where a job is. Outcome says how it stands, and Problem why a failed one failed, in plain words for the person.
/// Done and Gone are what PCs from before Outcome read: Gone is set only for a lost job, and Done for every job
/// that ended, so those PCs stop checking either way. From a server from before Outcome, it's worked out from them.
/// Interrupted marks a lost job the server knows was cut off by a restart while it ran. It's sent as Lost, so every
/// PC tells the person the server restarted while writing them, and a PC from before Interrupted reads it the same.
/// </summary>
public sealed record NoteJobState(
    bool Done,
    bool Gone = false,
    NoteWriteOutcome? Notes = null,
    StudyOutcome? Study = null,
    NoteJobOutcome? Outcome = null,
    string? Problem = null,
    bool Interrupted = false)
{
    public static NoteJobState Running() => new(Done: false, Outcome: NoteJobOutcome.Running);

    public static NoteJobState Lost() => new(Done: false, Gone: true, Outcome: NoteJobOutcome.Lost);

    /// <summary>A job that was running when the server restarted. It isn't run again on its own.</summary>
    public static NoteJobState InterruptedByRestart() => Lost() with { Interrupted = true };

    public static NoteJobState Failed(string problem) => new(Done: true, Outcome: NoteJobOutcome.Failed, Problem: problem);

    public static NoteJobState Cancelled() => new(Done: true, Outcome: NoteJobOutcome.Cancelled);

    /// <summary>How it stands, also for a server that only sends Done and Gone.</summary>
    public NoteJobOutcome Kind() =>
        Outcome ?? (Gone ? NoteJobOutcome.Lost : Done ? NoteJobOutcome.Succeeded : NoteJobOutcome.Running);
}
