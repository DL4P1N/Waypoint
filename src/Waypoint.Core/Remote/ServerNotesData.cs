using System.Collections.Concurrent;
using System.Globalization;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Files;
using Waypoint.Core.Notes;
using Waypoint.Core.Recording;

namespace Waypoint.Core.Remote;

/// <summary>
/// The Notes page's calls, sent to the server, which keeps the notes and writes them with Haiku. Reads show the last
/// answer while it's away. Typing in a note while it's away is kept in <see cref="OfflineNotes"/> and saved when
/// it's back, so it's never lost. Other changes, such as folders and moving notes, say they can't be made yet.
/// </summary>
public sealed class ServerNotesData : INotesData
{
    private static readonly HttpMethod Get = HttpMethod.Get;
    private static readonly HttpMethod Post = HttpMethod.Post;
    private static readonly HttpMethod Put = HttpMethod.Put;
    private static readonly HttpMethod Delete = HttpMethod.Delete;

    private readonly ServerClient _server;
    private readonly OfflineNotes? _offline;
    private readonly Func<long, IReadOnlyList<RecordingMark>>? _marks;
    private readonly bool _fresh;

    // The server's text for each note when it was last read or saved, which an edit made while away started from.
    private readonly ConcurrentDictionary<long, string> _serverBodies = new();

    // Notes made while away, by the id that stood for them, once the server gave them real ones.
    private readonly ConcurrentDictionary<long, long> _sentNew = new();

    // Notes that changed on the server while away, by the copy that kept what was typed here, until the page has
    // moved its editor over to the copy. Until then a save to the note goes to the copy, so it can't overwrite
    // the server's version.
    private readonly ConcurrentDictionary<long, long> _keptBoth = new();

    // Notes made while away and deleted before they were sent, kept for undo while the app is open.
    private readonly ConcurrentDictionary<long, OfflineNote> _deletedNew = new();

    // The client's count of state changes when the server last answered a note save saying it checked the text
    // expected, which a server from before never says, or -1. Until one has, and again once the server was away or
    // its state changed, since it may have come back as one from before, a save reads the note first, as before.
    private int _checksSavesSince = -1;

    private bool ServerChecksSaves => Volatile.Read(ref _checksSavesSince) is var since and >= 0 && since == _server.StateChanges;

    /// <param name="offline">Where typing waits while the server is away. Without it, typing then says it can't be saved.</param>
    /// <param name="marks">The moments marked while recording a note's lecture, kept in this PC's recordings.</param>
    public ServerNotesData(ServerClient server, OfflineNotes? offline = null, Func<long, IReadOnlyList<RecordingMark>>? marks = null)
        : this(server, offline, marks, fresh: false)
    {
    }

    private ServerNotesData(ServerClient server, OfflineNotes? offline, Func<long, IReadOnlyList<RecordingMark>>? marks, bool fresh)
    {
        _server = server;
        _offline = offline;
        _marks = marks;
        _fresh = fresh;
    }

    /// <summary>How often a note being written is checked on. Tests make it shorter.</summary>
    public TimeSpan JobPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a note being written is waited for, through the server being away for a while.</summary>
    public TimeSpan JobWait { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// For the notes folder sync. Every read is asked of the server, never the last answer kept, and nothing waits
    /// to be sent, so a sync while the server is away stops instead of acting on old notes.
    /// </summary>
    public ServerNotesData ForSync() => new(_server, null, _marks, fresh: true);

    /// <summary>Reads what the page asked for lately again, off the calling thread. See <see cref="ServerClient.RefreshReadsAsync"/>.</summary>
    public Task RefreshReadsAsync() => _server.RefreshReadsAsync();

    /// <summary>Answers the page's reads from those just made. See <see cref="ServerClient.UseRecentReads"/>.</summary>
    public IDisposable UseRecentReads() => _server.UseRecentReads();

    public bool CanMakeNotes => true;

    public bool CanStudy => true;

    public int WaitingToSave => _offline?.Count ?? 0;

    /// <summary>Notes typed while away that the server turned down for good, kept in <see cref="UnsavedPath"/>.</summary>
    public int NotSaved => _offline?.Unsaved ?? 0;

    public string? UnsavedPath => _offline?.UnsavedPath;

    /// <summary>What to show while a file of notes typed while away was found damaged and set aside. Null when none was.</summary>
    public string? Unreadable => _offline?.Unreadable;

    /// <summary>
    /// A plain text copy of the notes the server turned down, and of any file of them found damaged, for opening.
    /// Null when there are none.
    /// </summary>
    public string? WriteUnsavedCopy() => _offline?.WriteUnsavedCopy();

    /// <summary>The notes files set aside so far were opened, or their notice closed, so it isn't shown again.</summary>
    public void MarkSetAsideSeen() => _offline?.MarkSetAsideSeen();

    private static string Text(string? value) => Uri.EscapeDataString(value ?? "");

    /// <summary>Unfiled is no folder at all, so it's left out of the address rather than sent empty.</summary>
    private static string Folder(long? folderId) => folderId is { } id ? $"&folder={id}" : "";

    private static string Day(DateOnly date) => date.ToString("O", CultureInfo.InvariantCulture);

    private T Read<T>(string path, T empty) => _fresh ? _server.Send<T>(Get, path) : _server.GetOr(path, empty);

    /// <summary>An id for something sent, with this PC's own in front, so two PCs can never pick the same.</summary>
    private string Sent(string id) => $"{_server.ClientId}:{id}";

    /// <summary>A note made while away goes by a stand-in id until it's sent, and by its real one after.</summary>
    private long Real(long id) => id < 0 && _sentNew.TryGetValue(id, out var real) ? real : id;

    // Preferences

    public string? GetPreference(string key) => Read($"api/preferences/{Uri.EscapeDataString(key)}", new PreferenceValue(null)).Value;

    public void SetPreference(string key, string value) => _server.Send(Put, $"api/preferences/{Uri.EscapeDataString(key)}", new PreferenceValue(value));

    // The notes folder sync

    public IReadOnlyList<NoteFileState> GetNoteFileStates() => _server.Send<List<NoteFileState>>(Get, "api/notes/file-states");

    public IReadOnlyList<NoteFileRecord> GetNoteFileRecords() => _server.Send<List<NoteFileRecord>>(Get, "api/notes/files");

    public void SetNoteFileRecord(NoteFileRecord record) => _server.Send(Put, "api/notes/files", record);

    public void RemoveNoteFileRecord(long noteId) => _server.Send(Delete, $"api/notes/files/{noteId}");

    public void ClearNoteFileRecords() => _server.Send(Delete, "api/notes/files");

    // Each PC has its own notes folder, so each keeps its own.
    private string NoteFilesRootKey => $"note_files_synced_root:{_server.ClientId}";

    public string? NoteFilesRoot() => _server.Send<PreferenceValue>(Get, $"api/preferences/{Uri.EscapeDataString(NoteFilesRootKey)}").Value;

    public void SetNoteFilesRoot(string root) => _server.Send(Put, $"api/preferences/{Uri.EscapeDataString(NoteFilesRootKey)}", new PreferenceValue(root));

    public string? PendingNotesRestore() => _server.Send<PreferenceValue>(Get, "api/notes/files/restored").Value;

    public void NotesRestoreHandled(string stamp) => _server.Send(Put, "api/notes/files/restored", new PreferenceValue(stamp));

    /// <summary>
    /// The server's count of changes to anything the sync reads there, asked of it each time and never taken from a
    /// kept answer, so a sync that finds it where it was after its last pass skips reading every note. Null when it
    /// can't be had: the server is away or busy, or is one from before that has none, and then the sync makes a full
    /// pass as it always has. A restore or a restart of the server gives a count no earlier one matches.
    /// </summary>
    public string? NotesChangeStamp()
    {
        try
        {
            return _server.Send<TextValue>(Get, "api/notes/stamp")?.Value is { Length: > 0 } stamp ? stamp : null;
        }
        catch (Exception exception) when (exception is ServerUnavailableException or InvalidOperationException or HttpRequestException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // Notes

    public NoteDetail? GetNote(long id)
    {
        // An editor still open on a note kept as a copy reads the copy, the same as it saves to it.
        id = Real(id);
        if (_keptBoth.TryGetValue(id, out var copy))
        {
            id = copy;
        }

        if (id < 0)
        {
            return _offline?.Find(id) is { } made ? new NoteDetail(made.Id, made.Title, made.Body, null, made.At, made.FolderId) : null;
        }

        NoteDetail? note;
        bool fresh;
        if (_fresh)
        {
            note = _server.Send<Found<NoteDetail>>(Get, $"api/notes/{id}").Value;
            fresh = true;
        }
        else
        {
            note = _server.GetOr($"api/notes/{id}", new Found<NoteDetail>(null), out fresh).Value;
        }

        // Only the server's answer says what it has. A kept one may be older than a save made since.
        if (note is not null && fresh)
        {
            _serverBodies[id] = note.Body;
        }

        return Overlay(note);
    }

    /// <summary>
    /// Every note, each with only its first line, as the list shows it. Words to look for are looked for on the
    /// server, which has the whole of each note.
    /// </summary>
    public IReadOnlyList<NoteDetail> GetNotes(string? filter = null)
    {
        var clean = filter?.Trim() ?? "";
        return clean.Length > 0 ? SearchNotes(clean) : WithOffline(Read<List<NoteDetail>>("api/notes", []), _ => true);
    }

    public IReadOnlyList<NoteDetail> SearchNotes(string? words) =>
        WithOffline(Read<List<NoteDetail>>($"api/notes/search?words={Text(words)}", []), made => Matches(made, words));

    public IReadOnlyList<NoteDetail> SearchNotesInFolder(string? words, long? folderId, bool includeSubfolders = true) =>
        WithOffline(
            Read<List<NoteDetail>>($"api/notes/search/in?words={Text(words)}{Folder(folderId)}&subfolders={includeSubfolders}", []),
            made => made.FolderId == folderId && Matches(made, words));

    public IReadOnlyList<NoteDetail> GetNotesInFolder(long? folderId, bool includeSubfolders = false) =>
        WithOffline(Read<List<NoteDetail>>($"api/notes/in?subfolders={includeSubfolders}{Folder(folderId)}", []), made => made.FolderId == folderId);

    /// <summary>A new note works while the server is away: it's kept here and made there when it's back.</summary>
    // The server may make it and the answer still be lost, so it's sent with an id that sending it again from
    // here reuses, and the server gives back the same note.
    public long CreateNote(string title, string body, DateTime now, long? folderId = null) => CreateNoteOnce(Guid.NewGuid().ToString("N"), title, body, now, folderId);

    public long CreateNoteOnce(string clientId, string title, string body, DateTime now, long? folderId)
    {
        try
        {
            var id = _server.Send<Found<long>>(Post, "api/notes", new NewNoteRequest(title, body, folderId, Sent(clientId))).Value;
            RememberMade(new NoteDetail(id, title, body, null, now, folderId));
            return id;
        }
        catch (ServerUnavailableException) when (_offline is not null)
        {
            return _offline.AddNew(title, body, folderId, now, clientId).Id;
        }
    }

    /// <summary>Typing while the server is away is kept here and saved when it's back. False only if the note is gone.</summary>
    public bool SaveNote(long id, string title, string body, DateTime now)
    {
        id = Real(id);
        if (_keptBoth.TryGetValue(id, out var copy))
        {
            id = copy;
        }

        if (id < 0)
        {
            if (_offline?.Find(id) is not { } made)
            {
                return false;
            }

            _offline.Keep(id, title, body, null, made.FolderId, now);
            return true;
        }

        if (_offline?.Find(id) is null)
        {
            try
            {
                var saved = _server.Send<FlagValue>(Put, $"api/notes/{id}", new SaveNoteRequest(title, body)).Value;
                if (saved)
                {
                    RememberSaved(id, title, body);
                }

                return saved;
            }
            catch (ServerUnavailableException) when (_offline is not null)
            {
            }
        }

        // Once one edit is waiting, later ones wait behind it, so they're saved in the order they were typed.
        _offline!.Keep(id, title, body, _serverBodies.TryGetValue(id, out var basedOn) ? basedOn : null, null, now);
        return true;
    }

    /// <summary>
    /// The server checks the note still has <paramref name="expectedBody"/> and saves it in one write. A server from
    /// before can't, so the note is read first and compared here, as the editor used to, and the save then goes
    /// ahead as it always has. A note made while away, one kept as a copy, or one with typing still waiting to be
    /// sent, saves as before: what's waiting keeps the text it started from, and both versions are kept when it's
    /// sent if the note changed meanwhile.
    /// </summary>
    public NoteSaveResult SaveNoteIfUnchanged(long id, string title, string body, string expectedBody, DateTime now, string? expectedTitle = null)
    {
        var asked = id;
        id = Real(id);
        if (id < 0 || _keptBoth.ContainsKey(id) || _offline?.Find(id) is not null)
        {
            return SaveNote(asked, title, body, now) ? NoteSaveResult.Saved : NoteSaveResult.Gone;
        }

        try
        {
            if (!ServerChecksSaves)
            {
                var stored = _server.Send<Found<NoteDetail>>(Get, $"api/notes/{id}").Value;
                if (stored is null)
                {
                    return NoteSaveResult.Gone;
                }

                _serverBodies[id] = stored.Body;
                if (stored.Body != expectedBody)
                {
                    return NoteSaveResult.Changed(stored);
                }

                // Renamed there meanwhile: it keeps the new title, unless this save renames it too.
                if (expectedTitle is not null && stored.Title != WaypointDatabase.NoteTitle(expectedTitle, expectedBody))
                {
                    if (title != expectedTitle)
                    {
                        return NoteSaveResult.Changed(stored);
                    }

                    title = expectedTitle = stored.Title;
                }
            }

            var reply = _server.Send<NoteSaveReply>(Put, $"api/notes/{id}", new SaveNoteRequest(title, body, expectedBody, expectedTitle));
            Volatile.Write(ref _checksSavesSince, reply.Checked ? _server.StateChanges : -1);
            if (reply.Value)
            {
                // A save that kept a newer title says so, and that's what the note has now.
                RememberSaved(id, reply.Stored?.Title ?? title, body);
                return reply.Stored is { } kept ? new NoteSaveResult(NoteSaveOutcome.Saved, kept) : NoteSaveResult.Saved;
            }

            if (reply.Stored is { } changed)
            {
                _serverBodies[id] = changed.Body;
                return NoteSaveResult.Changed(changed);
            }

            return NoteSaveResult.Gone;
        }
        catch (ServerUnavailableException) when (_offline is not null)
        {
            // Kept with the text the editor started from, so a change made on the server meanwhile is noticed when
            // it's sent, and both versions are kept.
            _offline.Keep(id, title, body, expectedBody, null, now, expectedTitle);
            return NoteSaveResult.Saved;
        }
    }

    /// <summary>A note made while away and not sent yet is only on this PC, so it's taken out of what's waiting.</summary>
    public void DeleteNote(long id, DateTime now)
    {
        id = Real(id);
        if (id < 0)
        {
            if (_offline?.Remove(id) is { } made)
            {
                _deletedNew[id] = made;
            }

            return;
        }

        _server.Send(Delete, $"api/notes/{id}");
    }

    public void RestoreNote(long id, DateTime now)
    {
        id = Real(id);
        if (id < 0)
        {
            if (_deletedNew.TryRemove(id, out var made))
            {
                _offline?.PutBack(made);
            }

            return;
        }

        _server.Send(Post, $"api/notes/{id}/restore");
    }

    public bool MoveNoteToFolder(long noteId, long? folderId) => _server.Send<FlagValue>(Put, $"api/notes/{Real(noteId)}/folder", new MoveNoteRequest(folderId)).Value;

    // Folders

    public IReadOnlyList<NoteFolder> GetNoteFolders() => Read<List<NoteFolder>>("api/notes/folders", []);

    public NoteFolder? GetNoteFolder(long id) => Read($"api/notes/folders/{id}", new Found<NoteFolder>(null)).Value;

    public int CountNotesInFolder(long? folderId) => Read($"api/notes/folders/count?{Folder(folderId)}", new Found<int>(0)).Value;

    public FolderEdit CreateNoteFolder(string name, long? parentId, DateTime now) => _server.Send<FolderEdit>(Post, "api/notes/folders", new NewFolderRequest(name, parentId));

    public FolderEdit RenameNoteFolder(long id, string name) => _server.Send<FolderEdit>(Put, $"api/notes/folders/{id}/name", new NameRequest(name));

    public bool MoveNoteFolder(long id, long? parentId, int position) =>
        _server.Send<FlagValue>(Put, $"api/notes/folders/{id}/place", new MoveFolderRequest(parentId, position)).Value;

    /// <summary>
    /// One request, so the server puts them all in order or none. A server from before that can't, so there they're
    /// moved one at a time as they used to be.
    /// </summary>
    public bool ReorderNoteFolders(long? parentId, IReadOnlyList<long> ids)
    {
        try
        {
            return _server.Send<FlagValue>(Put, "api/notes/folders/order", new FolderOrderRequest(parentId, ids)).Value;
        }
        catch (ServerRefusedException refused) when (refused.Status is 404 or 405)
        {
            var moved = true;
            for (var position = 0; position < ids.Count; position++)
            {
                moved &= MoveNoteFolder(ids[position], parentId, position);
            }

            return moved;
        }
    }

    public FolderDeleteResult DeleteNoteFolder(long id, NotesInFolder notes) =>
        _server.Send<Found<FolderDeleteResult>>(Delete, $"api/notes/folders/{id}?notes={notes}").Value;

    public long? FindClassFolder(string course) => Read($"api/notes/class-folders?course={Text(course)}", new Found<long?>(null)).Value;

    public FolderEdit CreateClassFolder(string course, DateTime now) => _server.Send<FolderEdit>(Post, "api/notes/class-folders", new CourseRequest(course));

    public IReadOnlyList<string> GetClassNames() => Read<List<string>>("api/notes/classes", []);

    // Sources, written notes, and suggestions

    public IReadOnlyList<NoteSource> GetNoteSources(long noteId) => Real(noteId) is var id and > 0 ? Read<List<NoteSource>>($"api/notes/{id}/sources", []) : [];

    public string? GetSourceTimestamps(long sourceId) => Read($"api/notes/sources/{sourceId}/timestamps", new TextValue(null)).Value;

    public NoteGeneration? GetNoteGeneration(long noteId) =>
        Real(noteId) is var id and > 0 ? Read($"api/notes/{id}/generation", new Found<NoteGeneration>(null)).Value : new NoteGeneration(null, true, 0);

    public bool RestoreNoteBody(long noteId, NoteBodyState state, DateTime now) => _server.Send<FlagValue>(Post, $"api/notes/{Real(noteId)}/restore-body", state).Value;

    public string? GetNoteCourse(long noteId) => Real(noteId) is var id and > 0 ? Read($"api/notes/{id}/course", new TextValue(null)).Value : null;

    public IReadOnlyList<NoteSuggestion> GetNoteSuggestions(long noteId) =>
        Real(noteId) is var id and > 0 ? Read<List<NoteSuggestion>>($"api/notes/{id}/suggestions", []) : [];

    public HandAddResult AcceptNoteSuggestion(long suggestionId, DateTime now) => _server.Send<HandAddResult>(Post, $"api/notes/suggestions/{suggestionId}/accept");

    public bool SkipNoteSuggestion(long suggestionId) => _server.Send<FlagValue>(Post, $"api/notes/suggestions/{suggestionId}/skip").Value;

    public IReadOnlyList<Handoff> GetSavedSpots(string? words = null) => Read<List<Handoff>>($"api/notes/spots?words={Text(words)}", []);

    public Handoff? GetWaitingSpot(DateTime now) => Read("api/notes/spots/waiting", new SpotValue(null)).Spot;

    public OpReply SetSpotNext(string next, DateTime now) => _server.Send<OpReply>(Post, "api/notes/spots/next", new TextValue(next));

    public IReadOnlyList<StoredItem> GetOpenItems() => Read<List<StoredItem>>("api/items/open", []);

    public OpReply StartActivation(string? taskRef, string? firstAction, string method, DateTime now) =>
        _server.Send<OpReply>(Post, "api/activation/start", new StartRequest(taskRef, method, firstAction));

    // Making notes. Where a new note goes is decided on the server, which knows the class schedule.

    public NotePlacement PlaceNewNote(long? pickedFolderId, bool folderPicked, DateTime now)
    {
        try
        {
            return _server.Send<NotePlacement>(Post, "api/notes/place", new PlaceRequest(pickedFolderId, folderPicked));
        }
        catch (ServerUnavailableException)
        {
            return new NotePlacement(folderPicked ? pickedFolderId : null);
        }
    }

    public long? FolderFor(NotePlacement placement, DateTime now)
    {
        try
        {
            return _server.Send<Found<long?>>(Post, "api/notes/folder-for", placement).Value;
        }
        catch (ServerUnavailableException)
        {
            return placement.FolderId;
        }
    }

    public StartedNote StartFromFile(FileText file, long? pickedFolderId, bool folderPicked, DateTime now, string? onceKey = null) =>
        _server.Send<StartedNote>(Post, "api/notes/from-file", new FromFileRequest(file, pickedFolderId, folderPicked, onceKey));

    public StartedNote AttachFile(long noteId, FileText file, DateTime now) => _server.Send<StartedNote>(Post, $"api/notes/{Real(noteId)}/files", file);

    public StartedNote StartForRecording(string fileName, long? pickedFolderId, bool folderPicked, DateTime now, string? title = null) =>
        _server.Send<StartedNote>(Post, "api/notes/for-recording", new RecordingNoteRequest(Path.GetFileName(fileName), pickedFolderId, folderPicked, now, title));

    /// <summary>
    /// The transcript goes to the server as text. Audio chosen to be kept goes along as the note's file, and stays
    /// on this PC too, for playing. If it can't go, it's still kept here. A lecture's transcript can be large, so
    /// it gets the longer wait files get, and the same transcript sent again after a lost answer is kept once.
    /// </summary>
    public StartedNote AttachTranscript(long noteId, string recordingName, string transcript, string? timestampsJson, DateTime now, string? keptAudioPath = null, string? onceKey = null)
    {
        var clientId = Sent($"transcript:{noteId}:{onceKey ?? NoteFiles.Hash($"{recordingName}\n{transcript}")}");
        var started = _server.SendLong<StartedNote>(Post, $"api/notes/{noteId}/transcript", new TranscriptRequest(recordingName, transcript, timestampsJson, clientId));
        if (started.Problem is null && keptAudioPath is not null && File.Exists(keptAudioPath)
            && GetNoteSources(noteId).LastOrDefault(source => source.Kind == NoteSourceKind.Transcript) is { } source)
        {
            try
            {
                _ = KeepFileAsync(noteId, source.Id, keptAudioPath).GetAwaiter().GetResult();
            }
            catch (InvalidOperationException)
            {
                // Turned down, such as when the source is gone. The copy here is still kept.
            }
        }

        return started;
    }

    public async Task<NoteWriteOutcome> WriteNotesAsync(long noteId, string? request, bool replaceEdits, DateTime now, CancellationToken cancellationToken = default)
    {
        var marks = _marks?.Invoke(noteId);
        NoteJobState? state;
        try
        {
            state = await RunJobAsync(() => _server.Send<NoteJobStarted>(Post, $"api/notes/{Real(noteId)}/write", new WriteNotesRequest(request, replaceEdits, marks)), cancellationToken);
        }
        catch (ServerBusyException)
        {
            return new NoteWriteOutcome(NoteWriteStatus.Failed, "The notes couldn't be written yet, because the server is already writing others. The files are saved in Sources, so you can try again in a few minutes.");
        }

        return state switch
        {
            { Notes: { } outcome } => outcome,
            null when cancellationToken.IsCancellationRequested => NoteJobText.NotesStopped(),
            { } ended when ended.Kind() == NoteJobOutcome.Failed => NoteJobText.NotesFailed(ended.Problem),
            { } ended when ended.Kind() == NoteJobOutcome.Cancelled => NoteJobText.NotesStopped(),
            { } ended when ended.Kind() == NoteJobOutcome.Lost => NoteJobText.NotesLost(),
            _ => new NoteWriteOutcome(NoteWriteStatus.Failed, "The notes couldn't be written, because the server couldn't be reached. The files are saved in Sources, so you can try again."),
        };
    }

    public NoteWriteOutcome SaveWrittenAnyway(long noteId, WrittenNotes written, DateTime now) => _server.Send<NoteWriteOutcome>(Post, $"api/notes/{Real(noteId)}/written", written);

    // Studying

    public long? StudyFolderFor(StudyExam exam) =>
        Read($"api/notes/study/folder?course={Text(exam.Course)}&title={Text(exam.Title)}&item={Text(exam.ItemRef)}", new Found<long?>(null)).Value;

    public void LinkStudyFolder(StudyExam exam, long folderId) =>
        _server.Send(Post, "api/notes/study/link", new StudyRequest(exam, folderId, ReplaceEdits: false));

    public long? FindStudyNote(long folderId, string examTitle) => Read($"api/notes/study/find?folder={folderId}&exam={Text(examTitle)}", new Found<long?>(null)).Value;

    public IReadOnlyList<StoredItem> ExamsFor(string course, DateOnly today) => Read<List<StoredItem>>($"api/notes/study/exams?course={Text(course)}&today={Day(today)}", []);

    public StudyExam? ExamForNote(long noteId, DateOnly today) =>
        Real(noteId) is var id and > 0 ? Read($"api/notes/{id}/study-exam?today={Day(today)}", new Found<StudyExam>(null)).Value : null;

    public async Task<StudyOutcome> MakeStudyAsync(StudyExam exam, long folderId, bool replaceEdits, DateTime now, CancellationToken cancellationToken = default)
    {
        NoteJobState? state;
        try
        {
            state = await RunJobAsync(() => _server.Send<NoteJobStarted>(Post, "api/notes/study", new StudyRequest(exam, folderId, replaceEdits)), cancellationToken);
        }
        catch (ServerBusyException)
        {
            return new StudyOutcome(StudyStatus.Failed, "The practice questions couldn't be written yet, because the server is already writing others. You can try again in a few minutes.");
        }

        return state switch
        {
            { Study: { } outcome } => outcome,
            null when cancellationToken.IsCancellationRequested => NoteJobText.StudyStopped(),
            { } ended when ended.Kind() == NoteJobOutcome.Failed => NoteJobText.StudyFailed(ended.Problem),
            { } ended when ended.Kind() == NoteJobOutcome.Cancelled => NoteJobText.StudyStopped(),
            { } ended when ended.Kind() == NoteJobOutcome.Lost => NoteJobText.StudyLost(),
            _ => new StudyOutcome(StudyStatus.Failed, "The practice questions couldn't be written, because the server couldn't be reached. You can try again."),
        };
    }

    public StudyOutcome SaveStudyAnyway(long noteId, string markdown, DateTime now) => _server.Send<StudyOutcome>(Post, $"api/notes/{Real(noteId)}/study-anyway", new MarkdownRequest(markdown));

    // Files kept with a note

    public string FilePath(long noteId, long sourceId) => $"api/notes/{Real(noteId)}/sources/{sourceId}/file";

    public Task<FileSendResult> KeepFileAsync(long noteId, long sourceId, string localPath, CancellationToken cancellationToken = default) =>
        _server.UploadAsync(FilePath(noteId, sourceId), localPath, cancellationToken);

    public Task<bool> FetchFileAsync(long noteId, long sourceId, string target, CancellationToken cancellationToken = default) =>
        _server.DownloadAsync(FilePath(noteId, sourceId), target, cancellationToken);

    public void DropFile(long noteId, long sourceId) => _server.Send(Delete, FilePath(noteId, sourceId));

    // Typing kept while the server was away

    /// <summary>
    /// Saves what was typed while the server was away. A note that changed on the server in the meantime, or was
    /// deleted there, keeps both versions: what was typed here becomes a note of its own.
    /// </summary>
    public async Task<IReadOnlyList<OfflineNoteSent>> SendOfflineAsync(CancellationToken cancellationToken = default)
    {
        if (_offline is null || _offline.Count == 0 || !_server.IsOnline)
        {
            return [];
        }

        return await _offline.SendAsync(note => Task.Run(() => SendOne(note), cancellationToken), cancellationToken);
    }

    /// <summary>The page has moved its editor from a note to the copy kept beside it, so the note saves as itself again.</summary>
    public void Followed(long id) => _keptBoth.TryRemove(Real(id), out _);

    private OfflineNoteSent? SendOne(OfflineNote waiting)
    {
        try
        {
            // Notes from files written before ids were kept have none, and are sent as before.
            var clientId = waiting.ClientId ?? Guid.NewGuid().ToString("N");
            if (waiting.IsNew)
            {
                // Sent with the same id every time, so a send the server finished without the PC hearing gives
                // back the same note. That note then has the text first sent, which is brought up to date.
                _offline?.Sending(waiting);
                var first = waiting.BasedOn ?? waiting.Body;
                var made = _server.Send<Found<long>>(Post, "api/notes", new NewNoteRequest(waiting.Title, waiting.Body, waiting.FolderId, Sent(clientId))).Value;
                _sentNew[waiting.Id] = made;
                var there = _server.Send<Found<NoteDetail>>(Get, $"api/notes/{made}").Value;
                if (there is not null && there.Body != waiting.Body && there.Body != first)
                {
                    // Changed elsewhere since it was made: both versions are kept.
                    return KeepBoth(waiting, made, there, clientId);
                }

                if (there is not null && (there.Body != waiting.Body || there.Title != waiting.Title)
                    && SendOver(made, waiting, there) is { } changedSince)
                {
                    return KeepBoth(waiting, made, changedSince.Stored, clientId);
                }

                RememberMade(new NoteDetail(made, waiting.Title, waiting.Body, null, waiting.At, waiting.FolderId));
                return new OfflineNoteSent(waiting.Id, made, KeptBoth: false);
            }

            var current = _server.Send<Found<NoteDetail>>(Get, $"api/notes/{waiting.Id}").Value;
            if (current is not null && waiting.BasedOn is { } bodyThen && waiting.BasedOnTitle is { } titleThen)
            {
                // What changed only on the server stays as it is there: the text, when only the title was typed
                // here, and the title, when only the text was. Only a title changed in both places keeps both.
                // Without the title the edit started from, a rename there can't be told apart, so it's sent as
                // before and both versions are kept if the text changed there.
                if (waiting.Body == bodyThen)
                {
                    waiting = waiting with { Body = current.Body };
                }

                if (current.Title != WaypointDatabase.NoteTitle(titleThen, bodyThen)
                    && current.Title != WaypointDatabase.NoteTitle(titleThen, current.Body))
                {
                    if (waiting.Title == titleThen)
                    {
                        waiting = waiting with { Title = current.Title };
                    }
                    else if (current.Title != WaypointDatabase.NoteTitle(waiting.Title, waiting.Body))
                    {
                        return KeepBoth(waiting, waiting.Id, current, clientId);
                    }
                }
            }

            if (current is not null && (current.Body == waiting.Body || current.Body == waiting.BasedOn))
            {
                if ((current.Body != waiting.Body || current.Title != waiting.Title)
                    && SendOver(waiting.Id, waiting, current) is { } changedSince)
                {
                    // Changed on the server, or gone from it, between the read and the save: both versions are kept.
                    return KeepBoth(waiting, waiting.Id, changedSince.Stored, clientId);
                }

                RememberSaved(waiting.Id, waiting.Title, waiting.Body);
                return new OfflineNoteSent(waiting.Id, waiting.Id, KeptBoth: false);
            }

            // Changed on the server too, or gone from it: both versions are kept.
            return KeepBoth(waiting, waiting.Id, current, clientId);
        }
        catch (ServerRefusedException refused) when (refused.IsLasting)
        {
            // Sending it again would only be turned down again, and would hold up the folder sync, so it stops
            // waiting. What was typed is kept in the unsaved file.
            return new OfflineNoteSent(waiting.Id, waiting.Id, KeptBoth: false, refused.Message);
        }
        catch (Exception exception) when (exception is ServerUnavailableException or InvalidOperationException or HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// Saves what was typed over the note, as long as it still has the text and title in <paramref name="read"/>, the
    /// note just read. Null when it was saved. Otherwise the answer says what the note is now, or has nothing when
    /// it's gone. A server from before saves without checking, as it always has.
    /// </summary>
    private NoteSaveReply? SendOver(long noteId, OfflineNote waiting, NoteDetail read)
    {
        var reply = _server.Send<NoteSaveReply>(Put, $"api/notes/{noteId}", new SaveNoteRequest(waiting.Title, waiting.Body, read.Body, read.Title));
        return reply.Value ? null : reply;
    }

    /// <summary>
    /// What was typed here becomes a note of its own beside <paramref name="noteId"/>. It's sent with an id of its
    /// own, so a send the server finished without the PC hearing gives back the same copy, brought up to date.
    /// </summary>
    private OfflineNoteSent KeepBoth(OfflineNote waiting, long noteId, NoteDetail? current, string clientId)
    {
        var title = (string.IsNullOrWhiteSpace(waiting.Title) ? current?.Title ?? "Note" : waiting.Title.Trim()) + OfflineNotes.KeptBothSuffix;
        var folder = current?.FolderId ?? waiting.FolderId;
        var copy = _server.Send<Found<long>>(Post, "api/notes", new NewNoteRequest(title, waiting.Body, folder, Sent($"{clientId}:copy"))).Value;
        if (_server.Send<Found<NoteDetail>>(Get, $"api/notes/{copy}").Value is { } there && (there.Body != waiting.Body || there.Title != title))
        {
            _server.Send(Put, $"api/notes/{copy}", new SaveNoteRequest(title, waiting.Body));
        }

        _keptBoth[noteId] = copy;
        RememberMade(new NoteDetail(copy, title, waiting.Body, null, waiting.At, folder));
        return new OfflineNoteSent(waiting.Id, copy, KeptBoth: true);
    }

    /// <summary>The note's own read and the lists of notes, but not folders, sources, or anything else under api/notes.</summary>
    private static bool IsNoteList(string path) =>
        path == "api/notes" || path.StartsWith("api/notes/in?", StringComparison.Ordinal) || path.StartsWith("api/notes/search", StringComparison.Ordinal);

    /// <summary>
    /// A save that went through is what the server has now, and what shows if it goes away, rather than the text
    /// read before the save.
    /// </summary>
    private void RememberSaved(long id, string title, string body)
    {
        _serverBodies[id] = body;
        _server.Amend<Found<NoteDetail>>(
            path => path == $"api/notes/{id}",
            found => found.Value is { } note ? new Found<NoteDetail>(note with { Title = title, Body = body }) : found);
        _server.Amend<List<NoteDetail>>(
            IsNoteList,
            notes => notes.Select(note => note.Id == id ? note with { Title = title, Body = body } : note).ToList());
    }

    /// <summary>A note made here shows while the server is away, so the editor doesn't take it for deleted.</summary>
    private void RememberMade(NoteDetail made)
    {
        _serverBodies[made.Id] = made.Body;
        _server.Keep($"api/notes/{made.Id}", new Found<NoteDetail>(made));
        var inFolder = $"api/notes/in?subfolders={false}{Folder(made.FolderId)}";
        _server.Amend<List<NoteDetail>>(
            path => path == "api/notes" || path == inFolder,
            notes => notes.Any(note => note.Id == made.Id) ? notes : [made, .. notes]);
    }

    private NoteDetail? Overlay(NoteDetail? note) =>
        note is not null && _offline?.Find(note.Id) is { } typed ? note with { Title = typed.Title, Body = typed.Body } : note;

    /// <summary>Shows what was typed while away in place of the server's last text, and new notes made while away.</summary>
    private List<NoteDetail> WithOffline(IReadOnlyList<NoteDetail> notes, Func<NoteDetail, bool> include)
    {
        if (_offline is null || _offline.Count == 0)
        {
            return notes.ToList();
        }

        var waiting = _offline.All;
        var made = waiting.Where(note => note.IsNew && !_sentNew.ContainsKey(note.Id))
            .Select(note => new NoteDetail(note.Id, note.Title, note.Body, null, note.At, note.FolderId))
            .Where(include)
            .OrderByDescending(note => note.At);
        return made.Concat(notes.Select(note => Overlay(note)!)).ToList();
    }

    private static bool Matches(NoteDetail note, string? words) =>
        (words ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => $"{note.Title}\n{note.Body}".Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Starts a job and checks on it until it's done. Being away for a while doesn't end the wait, since the job goes
    /// on without the PC. Null if it couldn't be started, the wait ran out, or it was stopped here. Otherwise how it
    /// ended there, lost ones included.
    /// </summary>
    private async Task<NoteJobState?> RunJobAsync(Func<NoteJobStarted> start, CancellationToken cancellationToken)
    {
        NoteJobStarted job;
        try
        {
            job = await Task.Run(start, cancellationToken);
        }
        catch (ServerUnavailableException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        var until = DateTime.UtcNow + JobWait;
        while (DateTime.UtcNow < until)
        {
            try
            {
                await Task.Delay(JobPollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    _server.Send(Delete, $"api/notes/jobs/{job.Id}");
                }
                catch (Exception exception) when (exception is ServerUnavailableException or InvalidOperationException)
                {
                    // It finishes on its own there, and the note is saved as usual.
                }

                return null;
            }

            try
            {
                var state = await Task.Run(() => _server.Send<NoteJobState>(Get, $"api/notes/jobs/{job.Id}"), CancellationToken.None);
                if (state.Kind() != NoteJobOutcome.Running)
                {
                    return state;
                }
            }
            catch (ServerUnavailableException)
            {
                // Tried again on the next round. The server keeps working on it either way.
            }
        }

        return null;
    }
}
