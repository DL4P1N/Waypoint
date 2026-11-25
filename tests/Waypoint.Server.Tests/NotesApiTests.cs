using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Email;
using Waypoint.Core.Files;
using Waypoint.Core.Notes;
using Waypoint.Core.Remote;

namespace Waypoint.Server.Tests;

/// <summary>
/// The Notes page through the server, run in memory against a database in a temporary folder. Haiku is a fake that
/// answers at once, and the server can be made to go away and come back, for what happens to typing meanwhile.
/// </summary>
public sealed partial class NotesApiTests : IDisposable
{
    private const long AttachmentLimit = 1000;
    private const long RequestLimit = 100_000;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-server-{Guid.NewGuid()}");
    private readonly string _pc = Path.Combine(Path.GetTempPath(), $"waypoint-pc-{Guid.NewGuid()}");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _token;
    private readonly FakeHaiku _haiku = new();

    public NotesApiTests()
    {
        Directory.CreateDirectory(_folder);
        Directory.CreateDirectory(_pc);
        _token = ServerToken.Create(_folder);
        _factory = StartServer();
    }

    /// <summary>The server over this test's data folder. Started again, it's the server after a restart.</summary>
    private WebApplicationFactory<Program> StartServer() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Waypoint:DataDirectory", _folder);
            builder.UseSetting("Waypoint:RunBackground", "false");
            builder.UseSetting(NotesRoutes.MaxAttachmentSetting, AttachmentLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("Waypoint:MaxRequestBytes", RequestLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(new WorkerParts(_ => new NoTokens(), CaptureSorter: new NeverSorts()));
                services.AddSingleton<IClaudeRunner>(_haiku);
            });
        });

    public void Dispose()
    {
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
        Directory.Delete(_pc, recursive: true);
    }

    private sealed class NoTokens : ITokenStore
    {
        public string? Load() => null;

        public void Save(string token)
        {
        }
    }

    private sealed class NeverSorts : ICaptureSorter
    {
        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<CaptureResult?>(null);
    }

    /// <summary>Writes the same notes every time, and remembers what it was sent.</summary>
    private sealed class FakeHaiku : IClaudeRunner
    {
        public List<string> Inputs { get; } = [];

        /// <summary>When set, every run waits on it, so jobs can be kept running.</summary>
        public TaskCompletionSource? Hold { get; set; }

        public async Task<string> RunAsync(string systemPrompt, string schema, string input, CancellationToken cancellationToken)
        {
            lock (Inputs)
            {
                Inputs.Add(input);
            }

            if (Hold is { } hold)
            {
                await hold.Task.WaitAsync(cancellationToken);
            }

            return await Task.FromResult("""{"is_error": false, "structured_output": {"notes": "# Cells\nThe cell is the unit of life.", "suggestions": [{"kind": "Exam", "title": "Midterm", "date": "2026-11-20"}]}}""");
        }
    }

    /// <summary>Stands between the PC and the server, so the server can be made unreachable and reachable again.</summary>
    private sealed class Line(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public bool Down { get; set; }

        /// <summary>
        /// The next request this matches reaches the server and is done there, but the answer is lost on the way
        /// back, as when the wait runs out just as the server finishes.
        /// </summary>
        public Func<HttpRequestMessage, bool>? LoseAnswer { get; set; }

        /// <summary>The next request this matches never reaches the server.</summary>
        public Func<HttpRequestMessage, bool>? Cut { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down || (Cut is { } cut && cut(request)))
            {
                Cut = null;
                throw new HttpRequestException("No route to host");
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (LoseAnswer is { } lose && lose(request))
            {
                LoseAnswer = null;
                throw new TaskCanceledException("The wait ran out.");
            }

            return response;
        }
    }

    private static Func<HttpRequestMessage, bool> Is(HttpMethod method, string path) =>
        request => request.Method == method && request.RequestUri!.AbsolutePath == path;

    private HttpClient Paired()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return client;
    }

    private (ServerNotesData Notes, ServerClient Client, Line Line) Pc(OfflineNotes? offline = null, string? clientId = null)
    {
        var line = new Line(_factory.Server.CreateHandler());
        var client = InMemoryServer.Client(new HttpClient(line) { BaseAddress = _factory.Server.BaseAddress }, _token, clientId ?? "this-pc");
        var notes = new ServerNotesData(client, offline ?? new OfflineNotes(Path.Combine(_pc, "server-notes.json")))
        {
            JobPollInterval = TimeSpan.FromMilliseconds(20),
        };
        return (notes, client, line);
    }

    /// <summary>The server's own view, as another device would see it.</summary>
    private ServerNotesData Elsewhere() => new(InMemoryServer.Client(Paired(), _token));

    [Fact]
    public void Files_sent_in_Discord_can_make_notes_before_the_Notes_page_is_opened()
    {
        var server = _factory.Services.GetRequiredService<WaypointServer>();

        Assert.NotNull(server.Worker.NoteFiles);
    }

    [Theory]
    [InlineData("/api/notes/1")]
    [InlineData("/api/notes/folders")]
    [InlineData("/api/notes/files")]
    [InlineData("/api/notes/file-states")]
    [InlineData("/api/notes/stamp")]
    [InlineData("/api/notes/search?words=x")]
    [InlineData("/api/notes/1/sources")]
    [InlineData("/api/notes/1/sources/1/file")]
    [InlineData("/api/notes/jobs/abc")]
    public async Task Every_notes_route_needs_the_token(string path)
    {
        var none = await _factory.CreateClient().GetAsync(path);

        var wrong = _factory.CreateClient();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token");

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task A_file_sent_without_the_token_is_not_kept()
    {
        var notes = Pc().Notes;
        var id = notes.StartFromFile(new FileText("Lecture 3.txt", "Cells divide."), null, false, DateTime.Now).NoteId;
        var source = Assert.Single(notes.GetNoteSources(id));

        var response = await _factory.CreateClient().PutAsync($"/api/notes/{id}/sources/{source.Id}/file", new ByteArrayContent([1, 2, 3]));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(Assert.Single(notes.GetNoteSources(id)).FilePath);
        Assert.False(Directory.Exists(Path.Combine(_folder, "attachments")));
    }

    [Fact]
    public void A_file_sent_again_under_the_same_key_gives_the_note_already_made()
    {
        INotesData notes = Pc().Notes;
        var file = new FileText("Made up lecture.txt", "Made up cells divide.");

        var first = notes.StartFromFile(file, null, false, DateTime.Now, "discord:made-up-1:made-up-2");
        var again = notes.StartFromFile(file, null, false, DateTime.Now, "discord:made-up-1:made-up-2");
        var other = notes.StartFromFile(file, null, false, DateTime.Now);

        Assert.Equal(first.NoteId, again.NoteId);
        Assert.Single(notes.GetNoteSources(first.NoteId));
        Assert.NotEqual(first.NoteId, other.NoteId);
    }

    [Fact]
    public void A_folder_picked_to_study_from_is_kept_on_the_server()
    {
        INotesData notes = Pc().Notes;
        var folder = notes.CreateNoteFolder("MATH 120", null, DateTime.Now).Id;
        var exam = new StudyExam("Calc midterm", null, ItemRef: "item:7");

        notes.LinkStudyFolder(exam, folder);

        Assert.Equal(folder, Elsewhere().StudyFolderFor(exam));
    }

    [Fact]
    public void Undo_of_written_notes_through_the_server_says_when_the_note_changed_since()
    {
        var notes = Elsewhere();
        var database = _factory.Services.GetRequiredService<WaypointServer>().Database;
        var id = notes.CreateNote("Made up lecture", "My own lines", DateTime.Now);
        var before = database.SaveGeneratedNotes(id, "Written lines", "My own lines", DateTime.Now).Before!;

        Assert.True(notes.RestoreNoteBody(id, before, DateTime.Now));
        Assert.Equal("My own lines", notes.GetNote(id)!.Body);

        before = database.SaveGeneratedNotes(id, "Written lines", "My own lines", DateTime.Now).Before!;
        notes.SaveNote(id, "Made up lecture", "Written lines\n\nAdded later", DateTime.Now);
        Assert.False(notes.RestoreNoteBody(id, before, DateTime.Now));
        Assert.Equal("Written lines\n\nAdded later", notes.GetNote(id)!.Body);
    }

    [Fact]
    public void A_note_round_trips_through_the_server()
    {
        INotesData notes = Pc().Notes;

        var folder = notes.CreateNoteFolder("BIO 101", null, DateTime.Now);
        Assert.Null(folder.Problem);
        var id = notes.CreateNote("Lecture 1", "Cells", DateTime.Now, folder.Id);
        Assert.True(notes.SaveNote(id, "Lecture 1", "Cells and **membranes**\r\nSecond line", DateTime.Now));

        var note = notes.GetNote(id)!;
        Assert.Equal("Cells and **membranes**\r\nSecond line", note.Body);
        Assert.Equal(folder.Id, note.FolderId);
        Assert.Equal("Cells and **membranes**\r\nSecond line", Elsewhere().GetNote(id)!.Body);
        Assert.Equal(1, Assert.Single(notes.GetNoteFolders()).NoteCount);
        Assert.Equal(id, Assert.Single(notes.GetNotesInFolder(folder.Id)).Id);
        Assert.Equal(id, Assert.Single(notes.SearchNotes("membranes")).Id);
        Assert.Equal(id, Assert.Single(notes.SearchNotesInFolder("membranes", folder.Id)).Id);
        Assert.Single(notes.GetNotes("membranes"));

        Assert.True(notes.MoveNoteToFolder(id, null));
        Assert.Equal(1, notes.CountNotesInFolder(null));
        Assert.Equal("Biology", notes.RenameNoteFolder(folder.Id, "Biology").Name);
        Assert.Equal(FolderDeleteResult.Deleted, notes.DeleteNoteFolder(folder.Id, NotesInFolder.Refuse));

        notes.DeleteNote(id, DateTime.Now);
        Assert.Null(notes.GetNote(id));
        notes.RestoreNote(id, DateTime.Now);
        Assert.NotNull(notes.GetNote(id));

        notes.SetPreference("notes_folder", "unfiled");
        Assert.Equal("unfiled", notes.GetPreference("notes_folder"));
    }

    [Fact]
    public void What_the_notes_repair_left_to_say_is_taken_once_from_the_server()
    {
        var database = _factory.Services.GetRequiredService<WaypointServer>().Database;
        database.SetPreference(WaypointDatabase.TypedMarksToSayKey, "3 2 waypoint-2026-11-23-090000-before-note-repair.db");
        var first = Pc().Notes;
        var second = Pc().Notes;

        Assert.StartsWith("Turned 3 lines in 2 notes", WaypointDatabase.TakeTypedMarksMessage(first.TakePreference), StringComparison.Ordinal);
        Assert.Null(WaypointDatabase.TakeTypedMarksMessage(second.TakePreference));
        Assert.Null(database.GetPreference(WaypointDatabase.TypedMarksToSayKey));
    }

    [Fact]
    public void The_server_repairs_its_notes_typed_marks_when_it_starts()
    {
        var database = _factory.Services.GetRequiredService<WaypointServer>().Database;

        Assert.NotNull(database.GetPreference(WaypointDatabase.TypedMarksRepairedKey));
    }

    [Fact]
    public void Lists_of_notes_carry_each_notes_first_line_and_opening_one_gives_it_whole()
    {
        var notes = Pc().Notes;
        var folder = notes.CreateNoteFolder("CHEM 120", null, DateTime.Now);
        var body = "# Lab 3\n\nTitration with **phenolphthalein**\nSecond line\nThird line";
        var id = notes.CreateNote("Lab 3", "", DateTime.Now, folder.Id);
        Assert.True(notes.SaveNote(id, "Lab 3", body, DateTime.Now));

        var other = Elsewhere();
        Assert.Equal("Titration with **phenolphthalein**", other.GetNotes().Single(note => note.Id == id).Body);
        Assert.Equal("Titration with **phenolphthalein**", Assert.Single(other.GetNotesInFolder(folder.Id)).Body);
        Assert.Equal("Titration with **phenolphthalein**", Assert.Single(other.SearchNotes("Third")).Body);
        Assert.Equal(id, Assert.Single(other.GetNotes("third line")).Id);
        Assert.Equal(body, other.GetNote(id)!.Body);
    }

    [Fact]
    public async Task Notes_from_a_file_are_written_on_the_server_and_saved_with_suggestions()
    {
        INotesData notes = Pc().Notes;
        var started = notes.StartFromFile(new FileText("BIO 101 week 3.txt", "Cells are the unit of life."), null, false, DateTime.Now);
        Assert.Null(started.Problem);

        var outcome = await notes.WriteNotesAsync(started.NoteId, null, replaceEdits: false, DateTime.Now);

        Assert.Equal(NoteWriteStatus.Written, outcome.Status);
        Assert.Contains("Cells are the unit of life.", Assert.Single(_haiku.Inputs));
        Assert.Equal("# Cells\nThe cell is the unit of life.", notes.GetNote(started.NoteId)!.Body);
        Assert.True(notes.GetNoteGeneration(started.NoteId)!.WasGenerated);
        var suggestion = Assert.Single(notes.GetNoteSuggestions(started.NoteId));
        Assert.Equal("Midterm", suggestion.Item.Title);
        Assert.True(notes.SkipNoteSuggestion(suggestion.Id));

        // The user's own edit is never replaced without a yes.
        notes.SaveNote(started.NoteId, started.Title, "my own words", DateTime.Now);
        var again = await notes.WriteNotesAsync(started.NoteId, "shorter", replaceEdits: false, DateTime.Now);
        Assert.Equal(NoteWriteStatus.NeedsConfirm, again.Status);
        Assert.Equal("my own words", notes.GetNote(started.NoteId)!.Body);
    }

    [Fact]
    public async Task Only_two_jobs_run_at_once_and_a_third_is_told_to_wait()
    {
        INotesData notes = Pc().Notes;
        var ids = new[] { "a", "b", "c" }.Select(name => notes.StartFromFile(new FileText($"{name}.txt", "Cells."), null, false, DateTime.Now).NoteId).ToList();
        _haiku.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Paired();
        var body = new WriteNotesRequest(null, true, null);

        var first = await client.PostAsJsonAsync($"/api/notes/{ids[0]}/write", body, RemoteJson.Options);
        var second = await client.PostAsJsonAsync($"/api/notes/{ids[1]}/write", body, RemoteJson.Options);
        var deadline = DateTime.UtcNow + InMemoryServer.Background;
        while (_haiku.Inputs.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        var third = await client.PostAsJsonAsync($"/api/notes/{ids[2]}/write", body, RemoteJson.Options);
        var fromPc = await notes.WriteNotesAsync(ids[2], null, replaceEdits: true, DateTime.Now);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(NoteWriteStatus.Failed, fromPc.Status);
        Assert.Contains("already writing", fromPc.Message);

        // Once they finish, there's room again.
        _haiku.Hold.SetResult();
        _haiku.Hold = null;
        HttpResponseMessage again;
        do
        {
            await Task.Delay(20);
            again = await client.PostAsJsonAsync($"/api/notes/{ids[2]}/write", body, RemoteJson.Options);
        }
        while (again.StatusCode == HttpStatusCode.TooManyRequests && DateTime.UtcNow < deadline);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        foreach (var started in new[] { first, second, again })
        {
            var job = (await started.Content.ReadFromJsonAsync<NoteJobStarted>(RemoteJson.Options))!.Id;
            NoteJobState state;
            do
            {
                state = (await client.GetFromJsonAsync<NoteJobState>($"/api/notes/jobs/{job}", RemoteJson.Options))!;
                await Task.Delay(20);
            }
            while (!state.Done && !state.Gone && DateTime.UtcNow < deadline);

            Assert.Equal(NoteJobOutcome.Succeeded, state.Outcome);
        }
    }

    [Fact]
    public async Task After_a_restart_a_finished_job_is_still_told_and_a_running_one_is_interrupted()
    {
        INotesData notes = Pc().Notes;
        var finishedNote = notes.StartFromFile(new FileText("week 1.txt", "Cells."), null, false, DateTime.Now).NoteId;
        var heldNote = notes.StartFromFile(new FileText("week 2.txt", "Tissues."), null, false, DateTime.Now).NoteId;
        var client = Paired();
        var body = new WriteNotesRequest(null, true, null);
        var deadline = DateTime.UtcNow + InMemoryServer.Background;

        var finished = (await (await client.PostAsJsonAsync($"/api/notes/{finishedNote}/write", body, RemoteJson.Options))
            .Content.ReadFromJsonAsync<NoteJobStarted>(RemoteJson.Options))!.Id;
        NoteJobState state;
        do
        {
            await Task.Delay(20);
            state = (await client.GetFromJsonAsync<NoteJobState>($"/api/notes/jobs/{finished}", RemoteJson.Options))!;
        }
        while (state.Kind() == NoteJobOutcome.Running && DateTime.UtcNow < deadline);

        _haiku.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = (await (await client.PostAsJsonAsync($"/api/notes/{heldNote}/write", body, RemoteJson.Options))
            .Content.ReadFromJsonAsync<NoteJobStarted>(RemoteJson.Options))!.Id;
        while (_haiku.Inputs.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        // A new ServerNotes over the same database is the server after a restart: nothing kept in memory.
        var restarted = ActivatorUtilities.CreateInstance<ServerNotes>(_factory.Services);

        var told = restarted.State(finished);
        Assert.Equal(NoteJobOutcome.Succeeded, told.Kind());
        Assert.Equal(NoteWriteStatus.Written, told.Notes!.Status);
        var cut = restarted.State(held);
        Assert.Equal(NoteJobOutcome.Lost, cut.Kind());
        Assert.True(cut.Interrupted);
        Assert.Equal(NoteJobOutcome.Lost, restarted.State("made-up-job").Kind());
        Assert.False(restarted.State("made-up-job").Interrupted);

        // The run from before the restart ends here, and its job stays told as interrupted.
        _haiku.Hold.SetResult();
        _haiku.Hold = null;
        do
        {
            await Task.Delay(20);
            state = (await client.GetFromJsonAsync<NoteJobState>($"/api/notes/jobs/{held}", RemoteJson.Options))!;
        }
        while (state.Kind() == NoteJobOutcome.Running && DateTime.UtcNow < deadline);

        Assert.True(state.Interrupted);
    }

    [Fact]
    public void A_recordings_transcript_goes_to_the_server_as_note_text()
    {
        INotesData notes = Pc().Notes;
        var started = notes.StartForRecording(@"C:\Users\me\Lecture 4.m4a", null, false, DateTime.Now);

        var attached = notes.AttachTranscript(started.NoteId, "Lecture 4.m4a", "Today we cover mitosis.", "[]", DateTime.Now);

        Assert.Null(attached.Problem);
        var source = Assert.Single(notes.GetNoteSources(started.NoteId));
        Assert.Equal(NoteSourceKind.Transcript, source.Kind);
        Assert.Equal("Today we cover mitosis.", source.Text);
        Assert.Equal("[]", notes.GetSourceTimestamps(source.Id));
        Assert.Equal("Lecture 4", notes.GetNote(started.NoteId)!.Title);
    }

    [Fact]
    public async Task A_file_kept_with_a_note_goes_up_and_comes_back_the_same()
    {
        var notes = Pc().Notes;
        var id = notes.StartForRecording("Lecture 5.m4a", null, false, DateTime.Now).NoteId;
        notes.AttachTranscript(id, "Lecture 5.m4a", "Meiosis.", null, DateTime.Now);
        var source = Assert.Single(notes.GetNoteSources(id));
        var audio = Path.Combine(_pc, "lecture.m4a");
        var bytes = Enumerable.Range(0, (int)AttachmentLimit).Select(n => (byte)n).ToArray();
        await File.WriteAllBytesAsync(audio, bytes);

        Assert.Equal(FileSendResult.Sent, await notes.KeepFileAsync(id, source.Id, audio));
        Assert.NotNull(Assert.Single(notes.GetNoteSources(id)).FilePath);

        var back = Path.Combine(_pc, "back.m4a");
        Assert.True(await notes.FetchFileAsync(id, source.Id, back));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(back));

        notes.DropFile(id, source.Id);
        Assert.Null(Assert.Single(notes.GetNoteSources(id)).FilePath);
        Assert.False(await notes.FetchFileAsync(id, source.Id, Path.Combine(_pc, "gone.m4a")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "attachments")));
    }

    [Fact]
    public async Task A_file_over_the_limit_is_refused_and_nothing_is_kept()
    {
        var notes = Pc().Notes;
        var id = notes.StartForRecording("Lecture 6.m4a", null, false, DateTime.Now).NoteId;
        notes.AttachTranscript(id, "Lecture 6.m4a", "Genetics.", null, DateTime.Now);
        var source = Assert.Single(notes.GetNoteSources(id));
        var audio = Path.Combine(_pc, "big.m4a");
        await File.WriteAllBytesAsync(audio, new byte[AttachmentLimit + 1]);

        Assert.Equal(FileSendResult.TooLarge, await notes.KeepFileAsync(id, source.Id, audio));

        // Sent without saying its size first, it's still stopped at the limit.
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/notes/{id}/sources/{source.Id}/file")
        {
            Content = new PushStreamContent(new byte[AttachmentLimit * 3]),
        };
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Paired().SendAsync(request)).StatusCode);

        Assert.Null(Assert.Single(notes.GetNoteSources(id)).FilePath);
        Assert.Empty(Directory.Exists(Path.Combine(_folder, "attachments")) ? Directory.GetFiles(Path.Combine(_folder, "attachments")) : []);
    }

    [Fact]
    public async Task Two_files_sent_at_once_for_one_source_each_arrive_whole_and_leave_nothing_behind()
    {
        var notes = Pc().Notes;
        var id = notes.StartForRecording("Lecture 7.m4a", null, false, DateTime.Now).NoteId;
        notes.AttachTranscript(id, "Lecture 7.m4a", "Osmosis.", null, DateTime.Now);
        var source = Assert.Single(notes.GetNoteSources(id));
        var path = $"/api/notes/{id}/sources/{source.Id}/file";
        var first = Enumerable.Repeat((byte)1, 600).ToArray();
        var second = Enumerable.Repeat((byte)2, 600).ToArray();
        var release = new TaskCompletionSource();
        var a = new GatedContent(first, release.Task);
        var b = new GatedContent(second, release.Task);

        // Both are part way through before either finishes.
        var sendA = Paired().PutAsync(path, a);
        var sendB = Paired().PutAsync(path, b);
        await Task.WhenAll(a.Halfway, b.Halfway).WaitAsync(InMemoryServer.Background);
        await Task.Delay(100);
        release.SetResult();
        var responses = await Task.WhenAll(sendA, sendB);

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));
        var attachments = Path.Combine(_folder, "attachments");
        var kept = Assert.Single(Directory.GetFiles(attachments));
        var bytes = await File.ReadAllBytesAsync(kept);
        Assert.True(bytes.SequenceEqual(first) || bytes.SequenceEqual(second));
        Assert.Equal(kept, Assert.Single(notes.GetNoteSources(id)).FilePath);
    }

    [Fact]
    public async Task A_file_for_a_source_on_another_note_is_turned_down()
    {
        var notes = Pc().Notes;
        var first = notes.StartFromFile(new FileText("a.txt", "one"), null, false, DateTime.Now).NoteId;
        var second = notes.StartFromFile(new FileText("b.txt", "two"), null, false, DateTime.Now).NoteId;
        var source = Assert.Single(notes.GetNoteSources(first));

        var response = await Paired().PutAsync($"/api/notes/{second}/sources/{source.Id}/file", new ByteArrayContent([1]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Typing_while_the_server_is_away_is_kept_and_saved_when_it_is_back()
    {
        var (notes, client, line) = Pc();
        var id = notes.CreateNote("Groceries", "milk", DateTime.Now);
        notes.GetNote(id);
        notes.GetNotes();

        line.Down = true;
        Assert.True(notes.SaveNote(id, "Groceries", "milk and eggs", DateTime.Now));

        // The page shows what was typed, not the server's last text, and changes that need the server say so.
        Assert.Equal("milk and eggs", notes.GetNote(id)!.Body);
        Assert.Equal("milk and eggs", Assert.Single(notes.GetNotes()).Body);
        Assert.Throws<ServerUnavailableException>(() => notes.DeleteNote(id, DateTime.Now));
        Assert.Throws<ServerUnavailableException>(() => notes.CreateNoteFolder("Home", null, DateTime.Now));
        Assert.Equal("milk", Elsewhere().GetNote(id)!.Body);
        Assert.Equal(1, notes.WaitingToSave);

        line.Down = false;
        await client.CheckAsync();
        var sent = await notes.SendOfflineAsync();

        Assert.False(Assert.Single(sent).KeptBoth);
        Assert.Equal("milk and eggs", Elsewhere().GetNote(id)!.Body);
        Assert.Equal(0, notes.WaitingToSave);
        Assert.Single(Elsewhere().GetNotes());
    }

    [Fact]
    public async Task A_note_changed_on_the_server_while_away_keeps_both_versions()
    {
        var (notes, client, line) = Pc();
        var id = notes.CreateNote("Groceries", "milk", DateTime.Now);
        notes.GetNote(id);

        line.Down = true;
        notes.SaveNote(id, "Groceries", "milk from the pc", DateTime.Now);
        Elsewhere().SaveNote(id, "Groceries", "milk from the phone", DateTime.Now);

        line.Down = false;
        await client.CheckAsync();
        var sent = Assert.Single(await notes.SendOfflineAsync());

        Assert.True(sent.KeptBoth);
        Assert.Equal("milk from the phone", Elsewhere().GetNote(id)!.Body);
        var copy = Elsewhere().GetNote(sent.NoteId)!;
        Assert.Equal("Groceries" + OfflineNotes.KeptBothSuffix, copy.Title);
        Assert.Equal("milk from the pc", copy.Body);
        Assert.Equal("milk from the pc", notes.GetNote(sent.NoteId)!.Body);
    }

    [Fact]
    public async Task Saving_the_open_note_after_both_were_kept_goes_to_the_copy_until_the_page_follows()
    {
        var (notes, client, line) = Pc();
        var id = notes.CreateNote("Groceries", "milk", DateTime.Now);
        notes.GetNote(id);

        line.Down = true;
        notes.SaveNote(id, "Groceries", "milk from the pc", DateTime.Now);
        Elsewhere().SaveNote(id, "Groceries", "milk from the phone", DateTime.Now);
        line.Down = false;
        await client.CheckAsync();
        var sent = Assert.Single(await notes.SendOfflineAsync());
        Assert.True(sent.KeptBoth);

        // The editor still has the note open by its first id, and saves what was typed since.
        Assert.True(notes.SaveNote(id, "Groceries", "milk from the pc, and eggs", DateTime.Now));

        Assert.Equal("milk from the phone", Elsewhere().GetNote(id)!.Body);
        Assert.Equal("milk from the pc, and eggs", Elsewhere().GetNote(sent.NoteId)!.Body);

        // Once the page has moved over to the copy, the first note can be opened and saved as usual.
        notes.Followed(id);
        Assert.True(notes.SaveNote(id, "Groceries", "milk from the phone, and bread", DateTime.Now));
        Assert.Equal("milk from the phone, and bread", Elsewhere().GetNote(id)!.Body);
        Assert.Equal("milk from the pc, and eggs", Elsewhere().GetNote(sent.NoteId)!.Body);
    }

    [Fact]
    public async Task A_note_deleted_on_the_server_while_away_is_kept_as_a_new_note()
    {
        var (notes, client, line) = Pc();
        var id = notes.CreateNote("Groceries", "milk", DateTime.Now);
        notes.GetNote(id);

        line.Down = true;
        notes.SaveNote(id, "Groceries", "milk, typed here", DateTime.Now);
        Elsewhere().DeleteNote(id, DateTime.Now);

        line.Down = false;
        await client.CheckAsync();
        var sent = Assert.Single(await notes.SendOfflineAsync());

        Assert.True(sent.KeptBoth);
        Assert.Equal("milk, typed here", Elsewhere().GetNote(sent.NoteId)!.Body);
    }

    [Fact]
    public async Task A_new_note_made_while_away_is_made_on_the_server_after_a_restart()
    {
        var offline = new OfflineNotes(Path.Combine(_pc, "server-notes.json"));
        var (notes, _, line) = Pc(offline);
        line.Down = true;

        var id = notes.CreateNote("Ideas", "a tray clock", DateTime.Now);
        Assert.True(id < 0);
        Assert.True(notes.SaveNote(id, "Ideas", "a tray clock that dims", DateTime.Now));
        Assert.Equal("a tray clock that dims", notes.GetNote(id)!.Body);
        Assert.Contains(notes.GetNotes(), note => note.Id == id);

        // Waypoint closed and started again, with the server back.
        var (later, client, _) = Pc(new OfflineNotes(Path.Combine(_pc, "server-notes.json")));
        await client.CheckAsync();
        var sent = Assert.Single(await later.SendOfflineAsync());

        Assert.Equal("a tray clock that dims", Elsewhere().GetNote(sent.NoteId)!.Body);
        Assert.Equal(sent.NoteId, later.GetNote(id)!.Id);
        Assert.False(File.Exists(Path.Combine(_pc, "server-notes.json")));
    }

    [Fact]
    public async Task What_was_saved_last_is_shown_once_the_server_goes_away()
    {
        var (notes, client, line) = Pc();
        var id = notes.CreateNote("Groceries", "milk", DateTime.Now);
        notes.GetNote(id);
        notes.GetNotes();
        Assert.True(notes.SaveNote(id, "Groceries", "milk and eggs", DateTime.Now));
        var made = notes.CreateNote("Ideas", "a tray clock", DateTime.Now);

        line.Down = true;

        // The editor would go back to old text, or close, if the last read were shown instead.
        Assert.Equal("milk and eggs", notes.GetNote(id)!.Body);
        Assert.Equal("milk and eggs", notes.GetNotes().Single(note => note.Id == id).Body);
        Assert.Equal("a tray clock", notes.GetNote(made)!.Body);
        Assert.Contains(notes.GetNotes(), note => note.Id == made);

        // Typing from there is based on what was saved, so it isn't taken for a change made elsewhere.
        notes.SaveNote(id, "Groceries", "milk, eggs, and bread", DateTime.Now);
        line.Down = false;
        await client.CheckAsync();
        var sent = Assert.Single(await notes.SendOfflineAsync());

        Assert.False(sent.KeptBoth);
        Assert.Equal("milk, eggs, and bread", Elsewhere().GetNote(id)!.Body);
    }

    [Fact]
    public async Task A_note_the_server_turns_down_is_kept_in_a_file_and_the_rest_are_still_saved()
    {
        var (notes, client, line) = Pc();
        var big = notes.CreateNote("Pasted", "short", DateTime.Now);
        var small = notes.CreateNote("Groceries", "milk", DateTime.Now);
        notes.GetNote(big);
        notes.GetNote(small);

        line.Down = true;
        var huge = new string('x', (int)RequestLimit * 2);
        notes.SaveNote(big, "Pasted", huge, DateTime.Now);
        notes.SaveNote(small, "Groceries", "milk and eggs", DateTime.Now);
        var made = notes.CreateNote("Also huge", huge, DateTime.Now);

        line.Down = false;
        await client.CheckAsync();
        var sent = await notes.SendOfflineAsync();

        // Too big for the server every time, so they stop waiting, and what was typed is kept in a file.
        Assert.Equal(3, sent.Count);
        Assert.Equal(2, sent.Count(note => note.Problem is not null));
        Assert.Equal("milk and eggs", Elsewhere().GetNote(small)!.Body);
        Assert.Equal("short", Elsewhere().GetNote(big)!.Body);
        Assert.Equal(0, notes.WaitingToSave);
        var unsaved = Path.Combine(_pc, "server-notes-unsaved.json");
        Assert.Equal(2, notes.NotSaved);
        var kept = File.ReadAllText(unsaved);
        Assert.Contains(huge, kept);
        Assert.Contains("Also huge", kept);
        Assert.Empty(await notes.SendOfflineAsync());

        // Open shows them as plain text, and the kept file stays as it was.
        var copy = notes.WriteUnsavedCopy();
        Assert.Equal(Path.Combine(_pc, Core.Remote.OfflineNotes.UnsavedCopyName), copy);
        var text = File.ReadAllText(copy!);
        Assert.StartsWith(Core.Remote.KeptCopy.Intro, text);
        Assert.Contains("Also huge, typed ", text);
        Assert.Contains(huge, text);
        Assert.Equal(kept, File.ReadAllText(unsaved));
    }

    [Fact]
    public async Task A_new_note_whose_answer_was_lost_is_made_once()
    {
        var (notes, client, line) = Pc();
        line.LoseAnswer = Is(HttpMethod.Post, "/api/notes");

        // Made on the server, but the PC never heard, so it's kept here to be sent again.
        var id = notes.CreateNote("Ideas", "a tray clock", DateTime.Now);
        Assert.True(id < 0);
        notes.SaveNote(id, "Ideas", "a tray clock that dims", DateTime.Now);

        await client.CheckAsync();
        var sent = Assert.Single(await notes.SendOfflineAsync());

        var made = Assert.Single(Elsewhere().GetNotes());
        Assert.Equal(sent.NoteId, made.Id);
        Assert.Equal("a tray clock that dims", made.Body);
    }

    [Fact]
    public async Task A_note_sent_twice_with_the_same_id_is_made_once_unless_it_was_deleted_since()
    {
        var client = Paired();
        async Task<long> Make() =>
            (await (await client.PostAsJsonAsync("/api/notes", new NewNoteRequest("Ideas", "a tray clock", null, "pc:1"), RemoteJson.Options))
                .Content.ReadFromJsonAsync<Found<long>>(RemoteJson.Options))!.Value;

        var first = await Make();
        Assert.Equal(first, await Make());
        Elsewhere().DeleteNote(first, DateTime.Now);

        var again = await Make();
        Assert.NotEqual(first, again);
        Assert.Equal(again, Assert.Single(Elsewhere().GetNotes()).Id);
    }

    [Fact]
    public async Task A_note_made_while_away_whose_answer_was_lost_is_sent_once()
    {
        var (notes, client, line) = Pc();
        line.Down = true;
        var id = notes.CreateNote("Ideas", "a tray clock", DateTime.Now);
        line.Down = false;
        await client.CheckAsync();

        line.LoseAnswer = Is(HttpMethod.Post, "/api/notes");
        Assert.Empty(await notes.SendOfflineAsync());
        notes.SaveNote(id, "Ideas", "a tray clock that dims", DateTime.Now);
        await client.CheckAsync();
        var sent = Assert.Single(await notes.SendOfflineAsync());

        Assert.False(sent.KeptBoth);
        var made = Assert.Single(Elsewhere().GetNotes());
        Assert.Equal("a tray clock that dims", made.Body);
    }

    [Fact]
    public async Task A_transcript_whose_answer_was_lost_is_kept_once()
    {
        var (notes, client, line) = Pc();
        var id = notes.StartForRecording("Lecture 7.m4a", null, false, DateTime.Now).NoteId;
        line.LoseAnswer = Is(HttpMethod.Post, $"/api/notes/{id}/transcript");

        Assert.Throws<ServerUnavailableException>(() => notes.AttachTranscript(id, "Lecture 7.m4a", "Osmosis.", null, DateTime.Now));
        await client.CheckAsync();
        Assert.Null(notes.AttachTranscript(id, "Lecture 7.m4a", "Osmosis.", null, DateTime.Now).Problem);

        Assert.Equal("Osmosis.", Assert.Single(Elsewhere().GetNoteSources(id)).Text);
    }

    [Fact]
    public void A_transcript_goes_through_the_client_for_long_requests()
    {
        var (notes, client, _) = Pc();
        var id = notes.StartForRecording("Lecture 8.m4a", null, false, DateTime.Now).NoteId;
        var used = new List<string>();
        client.FileHttp = new HttpClient(new Watch(_factory.Server.CreateHandler(), used)) { BaseAddress = _factory.Server.BaseAddress };

        notes.AttachTranscript(id, "Lecture 8.m4a", "Diffusion.", null, DateTime.Now);

        Assert.Contains($"/api/notes/{id}/transcript", used);
    }

    private sealed class Watch(HttpMessageHandler inner, List<string> used) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            used.Add(request.RequestUri!.AbsolutePath);
            return base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task A_file_whose_note_was_made_but_not_recorded_is_imported_once()
    {
        var root = Path.Combine(_pc, "Waypoint Notes");
        var (notes, client, line) = Pc();
        var files = new NoteFiles(notes.ForSync(), root);
        WriteOutside(Path.Combine(root, "Groceries.md"), "milk");
        line.Cut = Is(HttpMethod.Put, "/api/notes/files");

        Assert.Throws<ServerUnavailableException>(() => files.Sync(DateTime.Now));
        await client.CheckAsync();
        files.Sync(DateTime.Now);
        files.Sync(DateTime.Now);

        var note = Assert.Single(Elsewhere().GetNotes());
        Assert.Equal("milk", note.Body);
        Assert.Single(Directory.GetFiles(root, "*.md"));
    }

    [Fact]
    public async Task A_note_made_and_deleted_while_away_stays_deleted_and_can_be_brought_back()
    {
        var (notes, client, line) = Pc();
        line.Down = true;
        var id = notes.CreateNote("Scratch", "nothing to keep", DateTime.Now);

        notes.DeleteNote(id, DateTime.Now);

        Assert.Null(notes.GetNote(id));
        Assert.DoesNotContain(notes.GetNotes(), note => note.Id == id);
        Assert.Equal(0, notes.WaitingToSave);

        // Undo brings it back, still waiting to be sent.
        notes.RestoreNote(id, DateTime.Now);
        Assert.Equal("nothing to keep", notes.GetNote(id)!.Body);
        notes.DeleteNote(id, DateTime.Now);

        line.Down = false;
        await client.CheckAsync();
        Assert.Empty(await notes.SendOfflineAsync());
        Assert.Empty(Elsewhere().GetNotes());
    }

    [Fact]
    public async Task Writing_notes_while_the_server_is_away_says_so_and_loses_nothing()
    {
        var (notes, _, line) = Pc();
        var started = notes.StartFromFile(new FileText("week 4.txt", "Photosynthesis."), null, false, DateTime.Now);
        line.Down = true;
        notes.GetNoteSources(started.NoteId);

        var outcome = await notes.WriteNotesAsync(started.NoteId, null, false, DateTime.Now);

        Assert.Equal(NoteWriteStatus.Failed, outcome.Status);
        Assert.Contains("couldn't be reached", outcome.Message);
        Assert.Equal("Photosynthesis.", Assert.Single(Elsewhere().GetNoteSources(started.NoteId)).Text);
    }

    [Fact]
    public void The_notes_folder_follows_the_servers_notes_both_ways()
    {
        var root = Path.Combine(_pc, "Waypoint Notes");
        var (notes, _, _) = Pc();
        var files = new NoteFiles(notes.ForSync(), root);
        var folder = notes.CreateNoteFolder("CS 220", null, DateTime.Now).Id;
        var id = notes.CreateNote("Lecture 1", "Graphs", DateTime.Now, folder);

        files.Sync(DateTime.Now);
        Assert.Equal("Graphs", File.ReadAllText(Path.Combine(root, "CS 220", "Lecture 1.md")));

        WriteOutside(Path.Combine(root, "CS 220", "Lecture 1.md"), "Graphs and trees");
        WriteOutside(Path.Combine(root, "Groceries.md"), "milk");
        files.Sync(DateTime.Now);

        Assert.Equal("Graphs and trees", Elsewhere().GetNote(id)!.Body);
        Assert.Contains(Elsewhere().GetNotes(), note => note.Title == "Groceries" && note.Body == "milk");
    }

    [Fact]
    public void A_second_pc_with_its_own_notes_folder_never_takes_its_missing_files_as_deleted_notes()
    {
        var (notes, _, _) = Pc(clientId: "desk");
        var desk = Path.Combine(_pc, "Desk notes");
        var id = notes.CreateNote("Groceries", "milk", DateTime.Now);
        new NoteFiles(notes.ForSync(), desk).Sync(DateTime.Now);

        // The laptop's folder is there but has none of the desk's files.
        var laptop = Path.Combine(_pc, "Laptop notes");
        WriteOutside(Path.Combine(laptop, "Ideas.md"), "a tray clock");
        new NoteFiles(Pc(clientId: "laptop").Notes.ForSync(), laptop).Sync(DateTime.Now);

        Assert.Equal("milk", Elsewhere().GetNote(id)!.Body);
        Assert.Equal("milk", File.ReadAllText(Path.Combine(laptop, "Groceries.md")));

        // Each keeps its own records, so the desk's next sync only brings in the laptop's new note.
        var again = new NoteFiles(notes.ForSync(), desk).Sync(DateTime.Now);
        Assert.Equal(0, again.Removed);
        Assert.Equal(2, Elsewhere().GetNotes().Count);
        Assert.Equal(2, Directory.GetFiles(desk, "*.md").Length);
    }

    [Fact]
    public async Task Records_from_before_each_pc_kept_its_own_go_to_the_first_pc_to_sync()
    {
        var (notes, _, _) = Pc();
        var id = notes.CreateNote("Groceries", "milk", DateTime.Now);

        // Written the old way, with no PC named.
        var record = new NoteFileRecord(id, "Groceries.md", NoteFiles.Hash("milk"), 4, 1, "Groceries", null, NoteFiles.Hash("milk"));
        Assert.Equal(HttpStatusCode.NoContent, (await Paired().PutAsJsonAsync("/api/notes/files", record, RemoteJson.Options)).StatusCode);

        Assert.Equal(id, Assert.Single(Pc(clientId: "desk").Notes.GetNoteFileRecords()).NoteId);
        Assert.Equal(id, Assert.Single(Pc(clientId: "desk").Notes.GetNoteFileRecords()).NoteId);
        Assert.Empty(Pc(clientId: "laptop").Notes.GetNoteFileRecords());
    }

    [Fact]
    public void A_note_changed_on_the_server_and_in_its_file_keeps_both()
    {
        var root = Path.Combine(_pc, "Waypoint Notes");
        var (notes, _, _) = Pc();
        var files = new NoteFiles(notes.ForSync(), root);
        var id = notes.CreateNote("Groceries", "milk", DateTime.Now);
        files.Sync(DateTime.Now);

        Elsewhere().SaveNote(id, "Groceries", "milk from the phone", DateTime.Now);
        WriteOutside(Path.Combine(root, "Groceries.md"), "milk from obsidian");
        files.Sync(DateTime.Now);

        Assert.Equal("milk from the phone", Elsewhere().GetNote(id)!.Body);
        Assert.Equal("milk from the phone", File.ReadAllText(Path.Combine(root, "Groceries.md")));
        Assert.Contains(Elsewhere().GetNotes(), note => note.Title == "Groceries (edited elsewhere)" && note.Body == "milk from obsidian");
    }

    [Fact]
    public async Task The_notes_folder_is_left_alone_while_the_server_is_away()
    {
        var root = Path.Combine(_pc, "Waypoint Notes");
        var (notes, client, line) = Pc();
        var files = new NoteFiles(notes.ForSync(), root);
        var id = notes.CreateNote("Groceries", "milk", DateTime.Now);
        files.Sync(DateTime.Now);
        WriteOutside(Path.Combine(root, "Groceries.md"), "milk and bread");

        line.Down = true;
        Assert.Throws<ServerUnavailableException>(() => files.Sync(DateTime.Now));
        Assert.Equal("milk and bread", File.ReadAllText(Path.Combine(root, "Groceries.md")));

        // Back again, the edit made to the file meanwhile comes in.
        line.Down = false;
        await client.CheckAsync();
        files.Sync(DateTime.Now);
        Assert.Equal("milk and bread", Elsewhere().GetNote(id)!.Body);
    }

    [Fact]
    public void The_sync_never_uses_old_answers_kept_for_the_page()
    {
        var (notes, _, line) = Pc();
        notes.CreateNote("Groceries", "milk", DateTime.Now);
        notes.GetNoteFolders();

        line.Down = true;

        // The page still shows its last answer, but the sync refuses to act on it.
        Assert.Empty(notes.GetNoteFolders());
        Assert.Throws<ServerUnavailableException>(() => notes.ForSync().GetNoteFolders());
        Assert.Throws<ServerUnavailableException>(() => notes.ForSync().GetNoteFileStates());
    }

    [Fact]
    public void After_a_restore_on_the_server_each_pcs_notes_folder_is_rewritten_from_the_restored_notes()
    {
        var (notes, _, _) = Pc(clientId: "desk");
        var desk = Path.Combine(_pc, "Desk notes");
        var laptop = Path.Combine(_pc, "Laptop notes");
        var id = notes.CreateNote("Groceries", "milk", DateTime.Now);
        new NoteFiles(notes.ForSync(), desk).Sync(DateTime.Now);
        new NoteFiles(Pc(clientId: "laptop").Notes.ForSync(), laptop).Sync(DateTime.Now);
        var database = _factory.Services.GetRequiredService<WaypointServer>().Database;
        var backup = database.BackUpNow(DateTime.Now);

        WriteOutside(Path.Combine(desk, "Groceries.md"), "milk and bread");
        new NoteFiles(notes.ForSync(), desk).Sync(DateTime.Now);
        Assert.Equal("milk and bread", Elsewhere().GetNote(id)!.Body);

        Assert.True(database.RestoreBackup(backup, DateTime.Now).Restored);
        new NoteFiles(notes.ForSync(), desk).Sync(DateTime.Now);
        new NoteFiles(Pc(clientId: "laptop").Notes.ForSync(), laptop).Sync(DateTime.Now);

        Assert.Equal("milk", Elsewhere().GetNote(id)!.Body);
        Assert.Equal("milk", File.ReadAllText(Path.Combine(desk, "Groceries.md")));
        Assert.Equal("milk", File.ReadAllText(Path.Combine(laptop, "Groceries.md")));
        var aside = Assert.Single(Directory.GetFiles(Path.Combine(desk, NoteFiles.PutAsideFolder), "*.md", SearchOption.AllDirectories));
        Assert.Equal("milk and bread", File.ReadAllText(aside));

        // Each PC rewrites once, then syncs the usual way.
        Assert.False(new NoteFiles(notes.ForSync(), desk).Sync(DateTime.Now).NotesChanged);
        Assert.Equal("milk", Elsewhere().GetNote(id)!.Body);
        Assert.Single(Elsewhere().GetNotes());
    }

    private static void WriteOutside(string full, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddMinutes(1));
    }

    /// <summary>A body sent in pieces with no length given up front, as a client streaming a file might.</summary>
    /// <summary>Sends half its bytes, then waits to be let go before sending the rest.</summary>
    private sealed class GatedContent(byte[] bytes, Task release) : HttpContent
    {
        private readonly TaskCompletionSource _halfway = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Halfway => _halfway.Task;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var half = bytes.Length / 2;
            await stream.WriteAsync(bytes.AsMemory(0, half));
            await stream.FlushAsync();
            _halfway.SetResult();
            await release;
            await stream.WriteAsync(bytes.AsMemory(half));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class PushStreamContent(byte[] bytes) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            for (var offset = 0; offset < bytes.Length; offset += 256)
            {
                await stream.WriteAsync(bytes.AsMemory(offset, Math.Min(256, bytes.Length - offset)));
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
