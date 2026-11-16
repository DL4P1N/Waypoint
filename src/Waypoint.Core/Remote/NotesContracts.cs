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

/// <summary>Where a job is. Gone means the server no longer knows it, such as after a restart.</summary>
public sealed record NoteJobState(bool Done, bool Gone = false, NoteWriteOutcome? Notes = null, StudyOutcome? Study = null);
