using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Files;
using Waypoint.Core.Recording;
using Waypoint.Core.Remote;

namespace Waypoint.Core.Notes;

/// <summary>What the notes folder sync reads and changes. The database has these, and so does the server.</summary>
public interface INoteFileStore
{
    IReadOnlyList<NoteFileState> GetNoteFileStates();

    IReadOnlyList<NoteFileRecord> GetNoteFileRecords();

    void SetNoteFileRecord(NoteFileRecord record);

    void RemoveNoteFileRecord(long noteId);

    void ClearNoteFileRecords();

    /// <summary>The full path of the notes folder the file records were made against, or null when none is known.</summary>
    string? NoteFilesRoot();

    void SetNoteFilesRoot(string root);

    /// <summary>A stamp when the notes were restored from a backup and this notes folder hasn't been rewritten since.</summary>
    string? PendingNotesRestore();

    /// <summary>Says this notes folder has been rewritten from the restore with that stamp.</summary>
    void NotesRestoreHandled(string stamp);

    IReadOnlyList<NoteFolder> GetNoteFolders();

    FolderEdit CreateNoteFolder(string name, long? parentId, DateTime now);

    NoteDetail? GetNote(long id);

    long CreateNote(string title, string body, DateTime now, long? folderId = null);

    /// <summary>
    /// A new note made under an id of the caller's, so making it again with the same id, such as after a failure
    /// before the caller saved what it was for, gives back the note already made rather than a second one.
    /// </summary>
    long CreateNoteOnce(string clientId, string title, string body, DateTime now, long? folderId) => CreateNote(title, body, now, folderId);

    bool SaveNote(long id, string title, string body, DateTime now);

    /// <summary>
    /// Saves the note only if its text is still <paramref name="expectedBody"/>, checked and saved in one step where
    /// the note is kept, so a change landing in between, such as a reply added from Discord, is never overwritten.
    /// When it changed, the result has the note as it is now. See <see cref="NoteSaving.SaveKeepingOther"/>.
    /// </summary>
    NoteSaveResult SaveNoteIfUnchanged(long id, string title, string body, string expectedBody, DateTime now);

    void DeleteNote(long id, DateTime now);

    bool MoveNoteToFolder(long noteId, long? folderId);
}

/// <summary>
/// Everything the Notes page and the recording queue read and change. Local mode passes straight to this PC's
/// database and runs Haiku here. Server mode sends the same calls to the server, which keeps the notes and runs
/// Haiku there, so the page works the same either way.
/// </summary>
public interface INotesData : INoteFileStore, IPreferences
{
    /// <summary>False when notes can't be made from files or recordings, such as in tests with no writer.</summary>
    bool CanMakeNotes { get; }

    /// <summary>False when there's nothing to write practice questions with.</summary>
    bool CanStudy { get; }

    IReadOnlyList<NoteDetail> GetNotes(string? filter = null);

    void RestoreNote(long id, DateTime now);

    IReadOnlyList<NoteDetail> SearchNotes(string? words);

    IReadOnlyList<NoteDetail> SearchNotesInFolder(string? words, long? folderId, bool includeSubfolders = true);

    IReadOnlyList<NoteDetail> GetNotesInFolder(long? folderId, bool includeSubfolders = false);

    NoteFolder? GetNoteFolder(long id);

    int CountNotesInFolder(long? folderId);

    FolderEdit RenameNoteFolder(long id, string name);

    bool MoveNoteFolder(long id, long? parentId, int position);

    /// <summary>
    /// Puts the folders in that order inside <paramref name="parentId"/> all at once, so a failure partway never
    /// leaves half of the new order. False, with nothing changed, if any of them can't go there.
    /// </summary>
    bool ReorderNoteFolders(long? parentId, IReadOnlyList<long> ids);

    FolderDeleteResult DeleteNoteFolder(long id, NotesInFolder notes);

    long? FindClassFolder(string course);

    FolderEdit CreateClassFolder(string course, DateTime now);

    IReadOnlyList<string> GetClassNames();

    IReadOnlyList<NoteSource> GetNoteSources(long noteId);

    string? GetSourceTimestamps(long sourceId);

    NoteGeneration? GetNoteGeneration(long noteId);

    void RestoreNoteBody(long noteId, NoteBodyState state, DateTime now);

    string? GetNoteCourse(long noteId);

    IReadOnlyList<NoteSuggestion> GetNoteSuggestions(long noteId);

    HandAddResult AcceptNoteSuggestion(long suggestionId, DateTime now);

    bool SkipNoteSuggestion(long suggestionId);

    IReadOnlyList<Handoff> GetSavedSpots(string? words = null);

    /// <summary>The saved spot still waiting for its next step, if it was asked about recently.</summary>
    Handoff? GetWaitingSpot(DateTime now);

    /// <summary>Answers "what's the next thing to do?" for that spot, the same as typing the answer.</summary>
    OpReply SetSpotNext(string next, DateTime now);

    IReadOnlyList<StoredItem> GetOpenItems();

    OpReply StartActivation(string? taskRef, string? firstAction, string method, DateTime now);

    // Making notes from files and recordings, the same steps as NoteMaker.

    NotePlacement PlaceNewNote(long? pickedFolderId, bool folderPicked, DateTime now);

    long? FolderFor(NotePlacement placement, DateTime now);

    /// <summary>
    /// With <paramref name="onceKey"/>, the same file sent again, such as after the answer was lost, gives the note
    /// already made rather than making another.
    /// </summary>
    StartedNote StartFromFile(FileText file, long? pickedFolderId, bool folderPicked, DateTime now, string? onceKey = null);

    StartedNote AttachFile(long noteId, FileText file, DateTime now);

    StartedNote StartForRecording(string fileName, long? pickedFolderId, bool folderPicked, DateTime now, string? title = null);

    /// <summary>
    /// Saves a transcript on its note. Given the same <paramref name="onceKey"/> again, such as when a job is picked
    /// up again after the app stopped partway, the transcript already saved is kept and no second one is added.
    /// </summary>
    StartedNote AttachTranscript(long noteId, string recordingName, string transcript, string? timestampsJson, DateTime now, string? keptAudioPath = null, string? onceKey = null);

    Task<NoteWriteOutcome> WriteNotesAsync(long noteId, string? request, bool replaceEdits, DateTime now, CancellationToken cancellationToken = default);

    NoteWriteOutcome SaveWrittenAnyway(long noteId, WrittenNotes written, DateTime now);

    // Studying from a class folder, the same steps as StudyMaker.

    long? StudyFolderFor(StudyExam exam);

    /// <summary>Remembers a folder picked to study from for an exam with no class folder.</summary>
    void LinkStudyFolder(StudyExam exam, long folderId);

    long? FindStudyNote(long folderId, string examTitle);

    IReadOnlyList<StoredItem> ExamsFor(string course, DateOnly today);

    StudyExam? ExamForNote(long noteId, DateOnly today);

    Task<StudyOutcome> MakeStudyAsync(StudyExam exam, long folderId, bool replaceEdits, DateTime now, CancellationToken cancellationToken = default);

    StudyOutcome SaveStudyAnyway(long noteId, string markdown, DateTime now);
}

/// <summary>
/// Today's behavior: every call goes to the database on this PC, and notes are written here with Haiku. The server
/// runs this same class against its own database, so both ends follow the same rules.
/// </summary>
public sealed class LocalNotesData(WaypointDatabase database, NoteMaker? maker = null, IClaudeRunner? studyRunner = null) : INotesData
{
    private readonly StudyMaker _study = new(database, studyRunner ?? new NoRunner());

    public bool CanMakeNotes => maker is not null;

    public bool CanStudy => studyRunner is not null;

    private NoteMaker Maker => maker ?? throw new InvalidOperationException("Making notes from files isn't available right now.");

    public string? GetPreference(string key) => database.GetPreference(key);

    public void SetPreference(string key, string value) => database.SetPreference(key, value);

    public IReadOnlyList<NoteFileState> GetNoteFileStates() => database.GetNoteFileStates();

    public IReadOnlyList<NoteFileRecord> GetNoteFileRecords() => database.GetNoteFileRecords();

    public void SetNoteFileRecord(NoteFileRecord record) => database.SetNoteFileRecord(record);

    public void RemoveNoteFileRecord(long noteId) => database.RemoveNoteFileRecord(noteId);

    public void ClearNoteFileRecords() => database.ClearNoteFileRecords();

    public string? NoteFilesRoot() => database.NoteFilesRoot();

    public void SetNoteFilesRoot(string root) => database.SetNoteFilesRoot(root);

    public string? PendingNotesRestore() => database.PendingNotesRestore();

    public void NotesRestoreHandled(string stamp) => database.NotesRestoreHandled(stamp);

    public IReadOnlyList<NoteFolder> GetNoteFolders() => database.GetNoteFolders();

    public FolderEdit CreateNoteFolder(string name, long? parentId, DateTime now) => database.CreateNoteFolder(name, parentId, now);

    public NoteDetail? GetNote(long id) => database.GetNote(id);

    public long CreateNote(string title, string body, DateTime now, long? folderId = null) => database.CreateNote(title, body, now, folderId);

    public long CreateNoteOnce(string clientId, string title, string body, DateTime now, long? folderId) => database.CreateNoteOnce(clientId, title, body, now, folderId);

    public bool SaveNote(long id, string title, string body, DateTime now) => database.SaveNote(id, title, body, now);

    public NoteSaveResult SaveNoteIfUnchanged(long id, string title, string body, string expectedBody, DateTime now) =>
        database.SaveNoteIfUnchanged(id, title, body, expectedBody, now) ? NoteSaveResult.Saved
        : database.GetNote(id) is { } stored ? NoteSaveResult.Changed(stored)
        : NoteSaveResult.Gone;

    public void DeleteNote(long id, DateTime now) => database.DeleteNote(id, now);

    public bool MoveNoteToFolder(long noteId, long? folderId) => database.MoveNoteToFolder(noteId, folderId);

    public IReadOnlyList<NoteDetail> GetNotes(string? filter = null) => database.GetNotes(filter);

    public void RestoreNote(long id, DateTime now) => database.RestoreNote(id, now);

    public IReadOnlyList<NoteDetail> SearchNotes(string? words) => database.SearchNotes(words);

    public IReadOnlyList<NoteDetail> SearchNotesInFolder(string? words, long? folderId, bool includeSubfolders = true) =>
        database.SearchNotesInFolder(words, folderId, includeSubfolders);

    public IReadOnlyList<NoteDetail> GetNotesInFolder(long? folderId, bool includeSubfolders = false) => database.GetNotesInFolder(folderId, includeSubfolders);

    public NoteFolder? GetNoteFolder(long id) => database.GetNoteFolder(id);

    public int CountNotesInFolder(long? folderId) => database.CountNotesInFolder(folderId);

    public FolderEdit RenameNoteFolder(long id, string name) => database.RenameNoteFolder(id, name);

    public bool MoveNoteFolder(long id, long? parentId, int position) => database.MoveNoteFolder(id, parentId, position);

    public bool ReorderNoteFolders(long? parentId, IReadOnlyList<long> ids) => database.ReorderNoteFolders(parentId, ids);

    public FolderDeleteResult DeleteNoteFolder(long id, NotesInFolder notes) => database.DeleteNoteFolder(id, notes);

    public long? FindClassFolder(string course) => database.FindClassFolder(course);

    public FolderEdit CreateClassFolder(string course, DateTime now) => database.CreateClassFolder(course, now);

    public IReadOnlyList<string> GetClassNames() => database.GetClassNames();

    public IReadOnlyList<NoteSource> GetNoteSources(long noteId) => database.GetNoteSources(noteId);

    public string? GetSourceTimestamps(long sourceId) => database.GetSourceTimestamps(sourceId);

    public NoteGeneration? GetNoteGeneration(long noteId) => database.GetNoteGeneration(noteId);

    public void RestoreNoteBody(long noteId, NoteBodyState state, DateTime now) => database.RestoreNoteBody(noteId, state, now);

    public string? GetNoteCourse(long noteId) => database.GetNoteCourse(noteId);

    public IReadOnlyList<NoteSuggestion> GetNoteSuggestions(long noteId) => database.GetNoteSuggestions(noteId);

    public HandAddResult AcceptNoteSuggestion(long suggestionId, DateTime now) => database.AcceptNoteSuggestion(suggestionId, now);

    public bool SkipNoteSuggestion(long suggestionId) => database.SkipNoteSuggestion(suggestionId);

    public IReadOnlyList<Handoff> GetSavedSpots(string? words = null) => database.GetSavedSpots(words);

    public Handoff? GetWaitingSpot(DateTime now) => database.GetWaitingSpot(now);

    public OpReply SetSpotNext(string next, DateTime now) => database.SetHandoffNext(next, now);

    public IReadOnlyList<StoredItem> GetOpenItems() => database.GetOpenItems();

    public OpReply StartActivation(string? taskRef, string? firstAction, string method, DateTime now) =>
        database.StartActivation(taskRef, firstAction, null, method, now);

    public NotePlacement PlaceNewNote(long? pickedFolderId, bool folderPicked, DateTime now) =>
        maker?.PlaceNewNote(pickedFolderId, folderPicked, now) ?? new NotePlacement(folderPicked ? pickedFolderId : null);

    public long? FolderFor(NotePlacement placement, DateTime now) => maker is null ? placement.FolderId : maker.FolderFor(placement, now);

    public StartedNote StartFromFile(FileText file, long? pickedFolderId, bool folderPicked, DateTime now, string? onceKey = null) =>
        Maker.StartFromFile(file, pickedFolderId, folderPicked, now, onceKey: onceKey);

    public StartedNote AttachFile(long noteId, FileText file, DateTime now) => Maker.AttachFile(noteId, file, now);

    public StartedNote StartForRecording(string fileName, long? pickedFolderId, bool folderPicked, DateTime now, string? title = null) =>
        Maker.StartForRecording(fileName, pickedFolderId, folderPicked, now, title);

    public StartedNote AttachTranscript(long noteId, string recordingName, string transcript, string? timestampsJson, DateTime now, string? keptAudioPath = null, string? onceKey = null) =>
        Maker.AttachTranscript(noteId, recordingName, transcript, timestampsJson, now, keptAudioPath, onceKey);

    /// <summary>The server's version, kept once however many times a PC sends it with the same id.</summary>
    public StartedNote AttachTranscriptOnce(long noteId, string recordingName, string transcript, string? timestampsJson, DateTime now, string? clientId) =>
        Maker.AttachTranscript(noteId, recordingName, transcript, timestampsJson, now, null, clientId);

    public Task<NoteWriteOutcome> WriteNotesAsync(long noteId, string? request, bool replaceEdits, DateTime now, CancellationToken cancellationToken = default) =>
        Maker.WriteAsync(noteId, request, replaceEdits, now, cancellationToken);

    /// <summary>The server's version, with the moments marked while recording sent from the PC that recorded them.</summary>
    public Task<NoteWriteOutcome> WriteNotesAsync(long noteId, string? request, bool replaceEdits, DateTime now, IReadOnlyList<RecordingMark>? marks, CancellationToken cancellationToken = default) =>
        Maker.WriteAsync(noteId, request, replaceEdits, now, cancellationToken, marks);

    public NoteWriteOutcome SaveWrittenAnyway(long noteId, WrittenNotes written, DateTime now) => Maker.SaveAnyway(noteId, written, now);

    public long? StudyFolderFor(StudyExam exam) => _study.FolderFor(exam);

    public void LinkStudyFolder(StudyExam exam, long folderId) => _study.LinkFolder(exam, folderId);

    public long? FindStudyNote(long folderId, string examTitle) => _study.FindStudyNote(folderId, examTitle);

    public IReadOnlyList<StoredItem> ExamsFor(string course, DateOnly today) => _study.ExamsFor(course, today);

    public StudyExam? ExamForNote(long noteId, DateOnly today) => _study.ExamForNote(noteId, today);

    public Task<StudyOutcome> MakeStudyAsync(StudyExam exam, long folderId, bool replaceEdits, DateTime now, CancellationToken cancellationToken = default) =>
        _study.MakeAsync(exam, folderId, replaceEdits, now, cancellationToken);

    public StudyOutcome SaveStudyAnyway(long noteId, string markdown, DateTime now) => _study.SaveAnyway(noteId, markdown, now);

    /// <summary>Stands in when nothing can write questions, so lookups still work and making them says why it can't.</summary>
    private sealed class NoRunner : IClaudeRunner
    {
        public Task<string> RunAsync(string systemPrompt, string schema, string input, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Studying from notes isn't available right now.");
    }
}
