using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Files;
using Waypoint.Core.Notes;
using Waypoint.Core.Platform;
using Waypoint.Core.Remote;

namespace Waypoint.Server;

/// <summary>
/// The notes on the server, with notes and practice questions written here by Haiku through <c>claude -p</c>, where
/// sorting runs too. Writing takes minutes, so each one runs as a job the PC checks on, kept by <see cref="NoteJobs"/>.
/// </summary>
public sealed class ServerNotes
{
    private readonly NoteJobs _jobs;

    public ServerNotes(WaypointServer server, IServiceProvider services)
    {
        var runner = services.GetService<IClaudeRunner>() ?? new ClaudeCliRunner(server.DataDirectory);
        Database = server.Database;
        Data = new LocalNotesData(server.Database, new NoteMaker(server.Database, new NoteWriter(runner), server.Worker.Schedule), runner);

        // Files sent in Discord's notes channel make notes the same way as files sent from the PC's Notes page.
        server.Worker.NoteFiles = Data;
        Attachments = Path.Combine(server.DataDirectory, "attachments");

        // An upload cut off when the server last stopped suddenly is never finished, so it goes rather than fill the disk.
        PartFiles.SweepStale(Attachments, PartFiles.EndingIn(".part"), PartFiles.StaleAfter);

        // Jobs kept as running from before this start were cut off by a restart, and are marked so, never run again.
        _jobs = new NoteJobs(services.GetService<ILogger<ServerNotes>>() ?? NullLogger<ServerNotes>.Instance, server.Database, MaxRunning);

        // Jobs still going as the server shuts down are stopped, and each lets go of what it held once it ends.
        services.GetService<IHostApplicationLifetime>()?.ApplicationStopping.Register(_jobs.StopAll);

        // And before a restore, so none finishes against the restored data.
        services.GetService<ServerMaintenance>()?.AddWriter(_jobs.StopAllAsync);
    }

    public LocalNotesData Data { get; }

    public WaypointDatabase Database { get; }

    /// <summary>Where files kept with notes are stored, one per source.</summary>
    public string Attachments { get; }

    /// <summary>How many notes or practice questions are written at once. Each is a claude -p, which is slow and heavy.</summary>
    public const int MaxRunning = 2;

    /// <summary>
    /// Starts a job, or returns null when <see cref="MaxRunning"/> are already running, so the PC can try later. With
    /// <paramref name="once"/>, a job already started under it is given back instead.
    /// </summary>
    public NoteJobStarted? Start(NoteJobKind kind, long? noteId, Func<CancellationToken, Task<NoteJobState>> work, string? once = null) =>
        _jobs.Start(kind, noteId, work, once);

    /// <summary>The same, for a start the PC sent with a key, saying in the answer whether it was started now or given back.</summary>
    public NoteJobStarted? Start(NoteJobKind kind, long? noteId, Func<CancellationToken, Task<NoteJobState>> work, HttpContext context)
    {
        if (SentOnce.Key(context) is not { } once)
        {
            return _jobs.Start(kind, noteId, work);
        }

        var started = _jobs.Start(kind, noteId, work, once, out var again);
        if (started is not null)
        {
            SentOnce.Tell(context, again);
        }

        return started;
    }

    public NoteJobState State(string id) => _jobs.State(id);

    public void Stop(string id) => _jobs.Stop(id);
}

/// <summary>
/// The Notes page's routes, under /api/notes, so they need the pairing token like every other route. "Now" is the
/// server's clock, except where a recording started, which the PC knows.
/// </summary>
public static class NotesRoutes
{
    /// <summary>The largest file kept with a note unless the server's settings say otherwise: a few hours of lecture audio.</summary>
    public const long DefaultMaxAttachmentBytes = 300L * 1024 * 1024;

    public const string MaxAttachmentSetting = "Waypoint:MaxAttachmentBytes";

    public static void Map(RouteGroupBuilder api)
    {
        var notes = new Lazy<ServerNotes>(() => ActivatorUtilities.CreateInstance<ServerNotes>(((IEndpointRouteBuilder)api).ServiceProvider));
        // Made now, not on the first request: building it hands the worker its note files, which Discord uploads need.
        _ = notes.Value;
        var group = api.MapGroup("/notes");
        MapNotes(group, notes);
        MapFolders(group, notes);
        MapSources(group, notes);
        MapMaking(group, notes);
        MapStudy(group, notes);
        MapFileSync(group, notes);
        MapAttachments(group, notes);
    }

    private static void MapNotes(RouteGroupBuilder group, Lazy<ServerNotes> notes)
    {
        group.MapGet("/{id:long}", (long id) => new Found<NoteDetail>(notes.Value.Data.GetNote(id)));
        group.MapPost("/", (NewNoteRequest request) => new Found<long>(request.ClientId is { Length: > 0 } client
            ? notes.Value.Database.CreateNoteOnce(client, request.Title, request.Body, DateTime.Now, request.FolderId)
            : notes.Value.Data.CreateNote(request.Title, request.Body, DateTime.Now, request.FolderId)));
        group.MapPut("/{id:long}", (long id, SaveNoteRequest request) => SaveNote(notes.Value.Data, id, request));
        group.MapDelete("/{id:long}", (long id) =>
        {
            notes.Value.Data.DeleteNote(id, DateTime.Now);
            return Results.NoContent();
        });
        group.MapPost("/{id:long}/restore", (long id) =>
        {
            notes.Value.Data.RestoreNote(id, DateTime.Now);
            return Results.NoContent();
        });
        group.MapPut("/{id:long}/folder", (long id, MoveNoteRequest request) => new FlagValue(notes.Value.Data.MoveNoteToFolder(id, request.FolderId)));
        // Lists carry each note's first line rather than its whole body. The note is read whole when it's opened.
        group.MapGet("/search", (string? words) => NoteListing.Slim(notes.Value.Data.SearchNotes(words)));
        group.MapGet("/search/in", (string? words, long? folder, bool? subfolders) => NoteListing.Slim(notes.Value.Data.SearchNotesInFolder(words, folder, subfolders ?? true)));
        group.MapGet("/in", (long? folder, bool? subfolders) => NoteListing.Slim(notes.Value.Data.GetNotesInFolder(folder, subfolders ?? false)));
        group.MapGet("/spots", (string? words) => notes.Value.Data.GetSavedSpots(string.IsNullOrEmpty(words) ? null : words));
        group.MapGet("/spots/waiting", () => new SpotValue(notes.Value.Data.GetWaitingSpot(DateTime.Now)));
        group.MapPost("/spots/next", (TextValue request) => notes.Value.Data.SetSpotNext(request.Value ?? "", DateTime.Now));
        group.MapGet("/classes", () => notes.Value.Data.GetClassNames());
    }

    private static void MapFolders(RouteGroupBuilder group, Lazy<ServerNotes> notes)
    {
        group.MapGet("/folders", () => notes.Value.Data.GetNoteFolders());
        group.MapGet("/folders/{id:long}", (long id) => new Found<NoteFolder>(notes.Value.Data.GetNoteFolder(id)));
        group.MapGet("/folders/count", (long? folder) => new Found<int>(notes.Value.Data.CountNotesInFolder(folder)));
        group.MapPost("/folders", (NewFolderRequest request) => notes.Value.Data.CreateNoteFolder(request.Name, request.ParentId, DateTime.Now));
        group.MapPut("/folders/{id:long}/name", (long id, NameRequest request) => notes.Value.Data.RenameNoteFolder(id, request.Name));
        group.MapPut("/folders/{id:long}/place", (long id, MoveFolderRequest request) => new FlagValue(notes.Value.Data.MoveNoteFolder(id, request.ParentId, request.Position)));
        group.MapPut("/folders/order", (FolderOrderRequest request) => new FlagValue(notes.Value.Data.ReorderNoteFolders(request.ParentId, request.Ids ?? [])));
        group.MapDelete("/folders/{id:long}", (long id, [Microsoft.AspNetCore.Mvc.FromQuery(Name = "notes")] string? inside) =>
            Enum.TryParse<NotesInFolder>(inside ?? nameof(NotesInFolder.Refuse), out var keep)
                ? Results.Ok(new Found<FolderDeleteResult>(notes.Value.Data.DeleteNoteFolder(id, keep)))
                : Results.BadRequest());
        group.MapGet("/class-folders", (string course) => new Found<long?>(notes.Value.Data.FindClassFolder(course)));
        group.MapPost("/class-folders", (CourseRequest request) => notes.Value.Data.CreateClassFolder(request.Course, DateTime.Now));
    }

    private static void MapSources(RouteGroupBuilder group, Lazy<ServerNotes> notes)
    {
        group.MapGet("/{id:long}/sources", (long id) => notes.Value.Data.GetNoteSources(id));
        group.MapGet("/sources/{id:long}/timestamps", (long id) => new TextValue(notes.Value.Data.GetSourceTimestamps(id)));
        group.MapGet("/{id:long}/generation", (long id) => new Found<NoteGeneration>(notes.Value.Data.GetNoteGeneration(id)));
        group.MapPost("/{id:long}/restore-body", (long id, NoteBodyState state) => new FlagValue(notes.Value.Data.RestoreNoteBody(id, state, DateTime.Now)));
        group.MapGet("/{id:long}/course", (long id) => new TextValue(notes.Value.Data.GetNoteCourse(id)));
        group.MapGet("/{id:long}/suggestions", (long id) => notes.Value.Data.GetNoteSuggestions(id));
        group.MapPost("/suggestions/{id:long}/accept", (long id) => notes.Value.Data.AcceptNoteSuggestion(id, DateTime.Now));
        group.MapPost("/suggestions/{id:long}/skip", (long id) => new FlagValue(notes.Value.Data.SkipNoteSuggestion(id)));
    }

    private static void MapMaking(RouteGroupBuilder group, Lazy<ServerNotes> notes)
    {
        group.MapPost("/place", (PlaceRequest request) => notes.Value.Data.PlaceNewNote(request.PickedFolderId, request.FolderPicked, DateTime.Now));
        group.MapPost("/folder-for", (NotePlacement placement) => new Found<long?>(notes.Value.Data.FolderFor(placement, DateTime.Now)));
        group.MapPost("/from-file", (FromFileRequest request) => notes.Value.Data.StartFromFile(request.File, request.PickedFolderId, request.FolderPicked, DateTime.Now, request.ClientId));
        group.MapPost("/{id:long}/files", (long id, FileText file) => notes.Value.Data.AttachFile(id, file, DateTime.Now));
        group.MapPost("/for-recording", (RecordingNoteRequest request) =>
            notes.Value.Data.StartForRecording(request.FileName, request.PickedFolderId, request.FolderPicked, request.At, request.Title));
        group.MapPost("/{id:long}/transcript", (long id, TranscriptRequest request) =>
            notes.Value.Data.AttachTranscriptOnce(id, request.RecordingName, request.Transcript, request.TimestampsJson, DateTime.Now, request.ClientId));
        group.MapPost("/{id:long}/write", (long id, WriteNotesRequest request, HttpContext context) =>
        {
            var data = notes.Value.Data;
            return Started(notes.Value.Start(NoteJobKind.Notes, id, async stop =>
                new NoteJobState(Done: true, Notes: await data.WriteNotesAsync(id, request.Request, request.ReplaceEdits, DateTime.Now, request.Marks, stop)), context));
        });
        group.MapPost("/{id:long}/written", (long id, WrittenNotes written) => notes.Value.Data.SaveWrittenAnyway(id, written, DateTime.Now));
        group.MapGet("/jobs/{job}", (string job) => notes.Value.State(job));
        group.MapDelete("/jobs/{job}", (string job) =>
        {
            notes.Value.Stop(job);
            return Results.NoContent();
        });
    }

    /// <summary>
    /// A save with the text the PC expects the note to have is checked against it and saved in one write. One
    /// without, from a PC from before, saves as it always has.
    /// </summary>
    private static NoteSaveReply SaveNote(LocalNotesData data, long id, SaveNoteRequest request)
    {
        if (request.ExpectedBody is not { } expected)
        {
            return new NoteSaveReply(data.SaveNote(id, request.Title, request.Body, DateTime.Now));
        }

        var result = data.SaveNoteIfUnchanged(id, request.Title, request.Body, expected, DateTime.Now, request.ExpectedTitle);
        return new NoteSaveReply(result.Outcome == NoteSaveOutcome.Saved, Checked: true, result.Stored);
    }

    /// <summary>The new job, or 429 when the server is already writing as many as it will at once.</summary>
    private static IResult Started(NoteJobStarted? job) =>
        job is null ? Results.StatusCode(StatusCodes.Status429TooManyRequests) : Results.Ok(job);

    private static void MapStudy(RouteGroupBuilder group, Lazy<ServerNotes> notes)
    {
        group.MapGet("/study/folder", (string? course, string? title, string? item) =>
            new Found<long?>(notes.Value.Data.StudyFolderFor(new StudyExam(title ?? "", course, ItemRef: string.IsNullOrEmpty(item) ? null : item))));
        group.MapPost("/study/link", (StudyRequest request) =>
        {
            notes.Value.Data.LinkStudyFolder(request.Exam, request.FolderId);
            return Results.NoContent();
        });
        group.MapGet("/study/find", (long folder, string exam) => new Found<long?>(notes.Value.Data.FindStudyNote(folder, exam)));
        group.MapGet("/study/exams", (string course, string today) =>
            ParseDay(today) is { } day ? Results.Ok(notes.Value.Data.ExamsFor(course, day)) : Results.BadRequest());
        group.MapGet("/{id:long}/study-exam", (long id, string today) =>
            ParseDay(today) is { } day ? Results.Ok(new Found<StudyExam>(notes.Value.Data.ExamForNote(id, day))) : Results.BadRequest());
        group.MapPost("/study", (StudyRequest request, HttpContext context) =>
        {
            var data = notes.Value.Data;
            return Started(notes.Value.Start(NoteJobKind.Study, null, async stop =>
                new NoteJobState(Done: true, Study: await data.MakeStudyAsync(request.Exam, request.FolderId, request.ReplaceEdits, DateTime.Now, stop)), context));
        });
        group.MapPost("/{id:long}/study-anyway", (long id, MarkdownRequest request) => notes.Value.Data.SaveStudyAnyway(id, request.Markdown, DateTime.Now));
    }

    /// <summary>
    /// The PC keeps its notes folder in step with these, using what the files and notes were when they last matched.
    /// Each PC's notes folder is its own, so the records are kept for the PC named in the request. A PC from before
    /// that names none uses the records kept for all.
    /// </summary>
    private static void MapFileSync(RouteGroupBuilder group, Lazy<ServerNotes> notes)
    {
        static string? Pc(HttpContext context) =>
            context.Request.Headers[ServerClient.ClientHeader].ToString() is { Length: > 0 } id ? id : null;

        group.MapGet("/file-states", () => notes.Value.Data.GetNoteFileStates());

        // Moves whenever anything the sync reads here changes, so a PC that finds it where it was after its last
        // pass skips the next one. Its value means nothing else. Null when it can't be read, and then the PC syncs.
        group.MapGet("/stamp", () => new TextValue(notes.Value.Database.NotesChangeStamp()));
        group.MapGet("/files", (HttpContext context) =>
            Pc(context) is { } pc ? notes.Value.Database.GetNoteFileRecords(pc) : notes.Value.Data.GetNoteFileRecords());
        group.MapPut("/files", (NoteFileRecord record, HttpContext context) =>
        {
            if (Pc(context) is { } pc)
            {
                notes.Value.Database.SetNoteFileRecord(pc, record);
            }
            else
            {
                notes.Value.Data.SetNoteFileRecord(record);
            }

            return Results.NoContent();
        });
        group.MapDelete("/files/{id:long}", (long id, HttpContext context) =>
        {
            if (Pc(context) is { } pc)
            {
                notes.Value.Database.RemoveNoteFileRecord(pc, id);
            }
            else
            {
                notes.Value.Data.RemoveNoteFileRecord(id);
            }

            return Results.NoContent();
        });
        group.MapGet("/files/restored", (HttpContext context) => new PreferenceValue(
            Pc(context) is { } pc ? notes.Value.Database.PendingNotesRestore(pc) : notes.Value.Database.PendingNotesRestore()));
        group.MapPut("/files/restored", (PreferenceValue handled, HttpContext context) =>
        {
            if (handled.Value is { } stamp)
            {
                if (Pc(context) is { } pc)
                {
                    notes.Value.Database.NotesRestoreHandled(pc, stamp);
                }
                else
                {
                    notes.Value.Database.NotesRestoreHandled(stamp);
                }
            }

            return Results.NoContent();
        });
        group.MapDelete("/files", (HttpContext context) =>
        {
            if (Pc(context) is { } pc)
            {
                notes.Value.Database.ClearNoteFileRecords(pc);
            }
            else
            {
                notes.Value.Data.ClearNoteFileRecords();
            }

            return Results.NoContent();
        });
    }

    /// <summary>
    /// A file kept with one of a note's sources, such as the audio of a lecture. It's stored whole in the attachments
    /// folder, up to a size limit, and a file over it is refused before any of it is kept.
    /// </summary>
    private static void MapAttachments(RouteGroupBuilder group, Lazy<ServerNotes> notes)
    {
        group.MapPut("/{id:long}/sources/{source:long}/file", async (long id, long source, HttpContext context, IConfiguration configuration) =>
        {
            var limit = configuration.GetValue(MaxAttachmentSetting, DefaultMaxAttachmentBytes);
            if (context.Request.ContentLength is > 0 and var length && length > limit)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            if (Source(notes.Value, id, source) is not { } found)
            {
                return Results.NotFound();
            }

            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            {
                size.MaxRequestBodySize = limit + 1;
            }

            Directory.CreateDirectory(notes.Value.Attachments);
            var target = Path.Combine(notes.Value.Attachments, $"source-{found.Id}{Extension(found)}");

            // Each upload writes its own file, so two at once for the same source never touch each other's.
            var part = $"{target}.{Guid.NewGuid():N}.part";
            try
            {
                await using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                {
                    var buffer = new byte[1 << 16];
                    long total = 0;
                    int read;
                    while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
                    {
                        total += read;
                        if (total > limit)
                        {
                            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                        }

                        await file.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                    }
                }

                // Replaces an earlier copy only once the new one is all here, one upload or delete at a time.
                var gate = SourceGate(found.Id);
                await gate.WaitAsync(context.RequestAborted);
                try
                {
                    File.Move(part, target, overwrite: true);
                    notes.Value.Database.SetNoteSourceFile(found.Id, target);
                }
                finally
                {
                    gate.Release();
                }

                return Results.NoContent();
            }
            catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            finally
            {
                // One still held now is swept the next time the server starts.
                PartFiles.TryDelete(part);
            }
        }).WithMetadata(Api.LargeBody.Instance);

        group.MapGet("/{id:long}/sources/{source:long}/file", (long id, long source) =>
            Source(notes.Value, id, source) is { FilePath: { } path } found && IsAttachment(notes.Value, path) && File.Exists(path)
                ? Results.File(path, "application/octet-stream", $"{Path.GetFileNameWithoutExtension(found.Name)}{Extension(found)}")
                : Results.NotFound());

        group.MapDelete("/{id:long}/sources/{source:long}/file", async (long id, long source, HttpContext context) =>
        {
            if (Source(notes.Value, id, source) is not { } found)
            {
                return Results.NotFound();
            }

            var gate = SourceGate(found.Id);
            await gate.WaitAsync(context.RequestAborted);
            try
            {
                // Only the server's own copy is ever deleted, and the transcript and notes stay.
                var path = Source(notes.Value, id, source)?.FilePath;
                if (path is not null && IsAttachment(notes.Value, path) && File.Exists(path))
                {
                    File.Delete(path);
                }

                notes.Value.Database.SetNoteSourceFile(found.Id, null);
            }
            finally
            {
                gate.Release();
            }

            return Results.NoContent();
        });
    }

    /// <summary>One gate per source, so replacing or deleting its file happens one request at a time.</summary>
    private static readonly ConcurrentDictionary<long, SemaphoreSlim> SourceGates = new();

    private static SemaphoreSlim SourceGate(long source) => SourceGates.GetOrAdd(source, _ => new SemaphoreSlim(1, 1));

    private static NoteSource? Source(ServerNotes notes, long noteId, long sourceId) =>
        notes.Data.GetNoteSources(noteId).FirstOrDefault(source => source.Id == sourceId);

    private static bool IsAttachment(ServerNotes notes, string path)
    {
        var folder = Path.GetFullPath(notes.Attachments).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(folder, StringComparison.Ordinal);
    }

    /// <summary>The kind of file, from the name it had, kept short and plain since it becomes part of a path.</summary>
    private static string Extension(NoteSource source)
    {
        var name = source.FilePath ?? source.Name.Replace("Transcript of ", "", StringComparison.Ordinal);
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return extension.Length is > 1 and <= 10 && extension[1..].All(char.IsAsciiLetterOrDigit) ? extension : ".bin";
    }

    private static DateOnly? ParseDay(string text) =>
        DateOnly.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
}
