using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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
/// The server and the PC's own client classes together, across a real restart of the server (the host stopped and a
/// new one started over the same data folder) and connections that drop partway. The PC reaches whichever server
/// is running through <see cref="Network"/>, so it can be unplugged, cut partway through a request, or have an
/// answer lost on the way back, without the PC's side knowing anything about the tests.
/// </summary>
public sealed class ServerAndPcTests : IDisposable
{
    private const long AttachmentLimit = 100_000;
    private static readonly TimeSpan Wait = InMemoryServer.Background;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-server-{Guid.NewGuid()}");
    private readonly string _pc = Path.Combine(Path.GetTempPath(), $"waypoint-pc-{Guid.NewGuid()}");
    private readonly string _token;
    private readonly FakeHaiku _haiku = new();
    private readonly Network _network = new();
    private WebApplicationFactory<Program> _server;

    public ServerAndPcTests()
    {
        Directory.CreateDirectory(_folder);
        Directory.CreateDirectory(_pc);
        _token = ServerToken.Create(_folder);
        _server = StartServer();
        _network.Connect(_server.Server);
    }

    public void Dispose()
    {
        _network.Unplug();
        _server.Dispose();
        for (var attempt = 0; ; attempt++)
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_folder, recursive: true);
                Directory.Delete(_pc, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>A server over the test's data folder. A second one is the same server after a restart.</summary>
    private WebApplicationFactory<Program> StartServer() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Waypoint:DataDirectory", _folder);
        builder.UseSetting("Waypoint:RunBackground", "false");
        builder.UseSetting(NotesRoutes.MaxAttachmentSetting, AttachmentLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(new WorkerParts(_ => new NoTokens(), CaptureSorter: new NeverSorts()));
            services.AddSingleton<IClaudeRunner>(_haiku);
        });
    });

    /// <summary>Stops the running server the way a restart does, while the PC can't reach anything, and starts it again.</summary>
    private void Restart()
    {
        _network.Unplug();
        _server.Dispose();
        SqliteConnection.ClearAllPools();
        _server = StartServer();
        _network.Connect(_server.Server);
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

    /// <summary>Writes the same notes every time, remembers each run, and can be held so a job stays running.</summary>
    private sealed class FakeHaiku : IClaudeRunner
    {
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public TaskCompletionSource? Hold { get; set; }

        public async Task<string> RunAsync(string systemPrompt, string schema, string input, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);
            if (Hold is { } hold)
            {
                await hold.Task.WaitAsync(cancellationToken);
            }

            return """{"is_error": false, "structured_output": {"notes": "# Tides\nThe moon pulls the sea.", "suggestions": []}}""";
        }
    }

    /// <summary>
    /// The network between the PC and whichever server is running. Unplugged, nothing answers, as when the server is
    /// stopped. A request can be cut partway through its body, or reach the server and lose its answer on the way back.
    /// </summary>
    private sealed class Network : HttpMessageHandler
    {
        private HttpMessageInvoker? _server;

        public void Connect(TestServer server) => _server = new HttpMessageInvoker(server.CreateHandler());

        public void Unplug() => _server = null;

        /// <summary>The next request this matches sends this many bytes of its body, and then the connection drops.</summary>
        public (Func<HttpRequestMessage, bool> Matches, int Bytes)? CutBody { get; set; }

        /// <summary>The next request this matches is done on the server, but its answer never reaches the PC.</summary>
        public Func<HttpRequestMessage, bool>? LoseAnswer { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var server = _server ?? throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
            if (CutBody is { } cut && request.Content is { } content && cut.Matches(request))
            {
                CutBody = null;
                request.Content = new CutContent(content, cut.Bytes);
                try
                {
                    using var ignored = await server.SendAsync(request, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // The server saw the body stop partway. Either way the PC only sees the connection go.
                }

                throw new HttpRequestException("The connection was reset.", new IOException("The connection was reset partway through sending."));
            }

            HttpResponseMessage response;
            try
            {
                response = await server.SendAsync(request, cancellationToken);
            }
            catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
            {
                // A request caught by the server stopping is a dropped connection to the PC.
                throw new HttpRequestException("The connection was reset.", exception);
            }

            if (LoseAnswer is { } lose && lose(request))
            {
                LoseAnswer = null;
                response.Dispose();
                throw new TaskCanceledException("The wait ran out.");
            }

            return response;
        }
    }

    /// <summary>A body that sends its first bytes and then fails, the way a dropped connection ends an upload.</summary>
    private sealed class CutContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly int _keep;

        public CutContent(HttpContent inner, int keep)
        {
            _inner = inner;
            _keep = keep;
            foreach (var header in inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            await using var source = await _inner.ReadAsStreamAsync();
            var buffer = new byte[_keep];
            var read = 0;
            while (read < _keep && await source.ReadAsync(buffer.AsMemory(read)) is > 0 and var count)
            {
                read += count;
            }

            await stream.WriteAsync(buffer.AsMemory(0, read));
            await stream.FlushAsync();
            throw new IOException("The connection was reset partway through sending.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? 0;
            return _inner.Headers.ContentLength is not null;
        }
    }

    private static Func<HttpRequestMessage, bool> Is(HttpMethod method, string path) =>
        request => request.Method == method && request.RequestUri!.AbsolutePath == path;

    /// <summary>The PC's side as server mode makes it: one client, with its notes typed while away kept in a file.</summary>
    private sealed record Pc(ServerClient Client, ServerNotesData Notes, IListsData Lists, IToDoData ToDo, CaptureQueue Queue);

    private Pc StartPc()
    {
        var client = InMemoryServer.Client(new HttpClient(_network) { BaseAddress = new Uri("http://localhost/") }, _token, "made-up-pc");
        var notes = new ServerNotesData(client, new OfflineNotes(Path.Combine(_pc, "server-notes.json")))
        {
            JobPollInterval = TimeSpan.FromMilliseconds(20),
        };
        return new Pc(client, notes, new ServerListsData(client), new ServerToDoData(client), new CaptureQueue(Path.Combine(_pc, "server-queue.json")));
    }

    /// <summary>The server's own view, read straight from the running server rather than through the PC.</summary>
    private HttpClient Direct()
    {
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return client;
    }

    private async Task<List<string>> PendingCaptures() =>
        (await Direct().GetFromJsonAsync<List<PendingCapture>>("/api/captures/pending", RemoteJson.Options))!.Select(capture => capture.Text).ToList();

    private static async Task Until(Func<bool> done)
    {
        var until = DateTime.UtcNow + Wait;
        while (!done() && DateTime.UtcNow < until)
        {
            await Task.Delay(10);
        }

        Assert.True(done());
    }

    /// <summary>
    /// Starts writing notes from the PC, and restarts the server while the job is running there. The PC keeps
    /// waiting through it, sees the server as away meanwhile, and asks again once it's back.
    /// </summary>
    private async Task<(Pc Pc, long NoteId, string Before, NoteWriteOutcome Outcome)> NotesCutOffByRestart()
    {
        var pc = StartPc();
        var started = pc.Notes.StartFromFile(new FileText("Made up tides.txt", "The moon pulls the sea."), null, false, DateTime.Now);
        Assert.Null(started.Problem);
        var before = pc.Notes.GetNote(started.NoteId)!.Body;
        _haiku.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var writing = pc.Notes.WriteNotesAsync(started.NoteId, null, replaceEdits: false, DateTime.Now);
        await Until(() => _haiku.Runs == 1);

        _network.Unplug();
        await Until(() => pc.Client.State == ServerState.Offline);
        Assert.False(writing.IsCompleted);
        _server.Dispose();
        SqliteConnection.ClearAllPools();
        _haiku.Hold = null;
        _server = StartServer();
        _network.Connect(_server.Server);
        await pc.Client.CheckAsync();

        return (pc, started.NoteId, before, await writing.WaitAsync(Wait));
    }

    [Fact]
    public async Task A_note_job_cut_off_by_a_server_restart_offers_try_again_and_is_only_run_again_when_asked()
    {
        var (pc, id, before, outcome) = await NotesCutOffByRestart();

        Assert.Equal(NoteWriteStatus.Failed, outcome.Status);
        Assert.Contains("try again", outcome.Message);
        Assert.Equal(1, _haiku.Runs);
        Assert.Equal(before, pc.Notes.GetNote(id)!.Body);

        // Try again runs it once, on the new server, and the sources were kept for it.
        var again = await pc.Notes.WriteNotesAsync(id, null, replaceEdits: false, DateTime.Now);
        Assert.Equal(NoteWriteStatus.Written, again.Status);
        Assert.Equal(2, _haiku.Runs);
        Assert.Equal("# Tides\nThe moon pulls the sea.", pc.Notes.GetNote(id)!.Body);
        Assert.Single(pc.Notes.GetNoteSources(id));
    }

    [Fact]
    public async Task A_note_job_cut_off_by_a_server_restart_is_told_as_the_server_restarting()
    {
        var (_, _, _, outcome) = await NotesCutOffByRestart();

        Assert.Equal(NoteJobText.NotesLost().Message, outcome.Message);
    }

    [Fact]
    public async Task An_upload_cut_off_partway_keeps_the_last_whole_copy_and_goes_up_whole_once_the_server_is_back()
    {
        var pc = StartPc();
        var id = pc.Notes.StartForRecording("Made up lecture.m4a", null, false, DateTime.Now).NoteId;
        pc.Notes.AttachTranscript(id, "Made up lecture.m4a", "Waves and tides.", null, DateTime.Now);
        var source = Assert.Single(pc.Notes.GetNoteSources(id));
        var first = Path.Combine(_pc, "first.m4a");
        var second = Path.Combine(_pc, "second.m4a");
        var firstBytes = Enumerable.Range(0, 40_000).Select(n => (byte)(n % 7)).ToArray();
        var secondBytes = Enumerable.Range(0, 60_000).Select(n => (byte)(n % 11)).ToArray();
        await File.WriteAllBytesAsync(first, firstBytes);
        await File.WriteAllBytesAsync(second, secondBytes);
        Assert.Equal(FileSendResult.Sent, await pc.Notes.KeepFileAsync(id, source.Id, first));

        // The connection drops with half of the new copy sent.
        _network.CutBody = (Is(HttpMethod.Put, $"/api/notes/{id}/sources/{source.Id}/file"), secondBytes.Length / 2);
        var cut = await pc.Notes.KeepFileAsync(id, source.Id, second);

        Assert.Equal(FileSendResult.Away, cut);
        Assert.Equal(ServerState.Offline, pc.Client.State);
        Assert.True(File.Exists(second));
        var attachments = Path.Combine(_folder, "attachments");
        Assert.Empty(Directory.GetFiles(attachments, "*.part"));
        await pc.Client.CheckAsync();
        var kept = Path.Combine(_pc, "kept.m4a");
        Assert.True(await pc.Notes.FetchFileAsync(id, source.Id, kept));
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(kept));

        // Sent again once the server is back, it arrives whole, and only one copy is kept.
        Assert.Equal(FileSendResult.Sent, await pc.Notes.KeepFileAsync(id, source.Id, second));
        Assert.True(await pc.Notes.FetchFileAsync(id, source.Id, kept));
        Assert.Equal(secondBytes, await File.ReadAllBytesAsync(kept));
        Assert.Single(Directory.GetFiles(attachments));
    }

    [Fact]
    public async Task A_note_save_cut_off_partway_is_kept_on_the_pc_and_saved_once_when_the_server_is_back()
    {
        var pc = StartPc();
        var id = pc.Notes.CreateNote("Made up packing", "socks", DateTime.Now);
        pc.Notes.GetNote(id);

        _network.CutBody = (Is(HttpMethod.Put, $"/api/notes/{id}"), 10);
        Assert.True(pc.Notes.SaveNote(id, "Made up packing", "socks and a made up lantern", DateTime.Now));

        Assert.Equal(ServerState.Offline, pc.Client.State);
        Assert.Equal(1, pc.Notes.WaitingToSave);
        Assert.Equal("socks and a made up lantern", pc.Notes.GetNote(id)!.Body);

        // The app closes and starts again before the server is back. What was typed is still waiting.
        var later = StartPc();
        await later.Client.CheckAsync();
        var sent = Assert.Single(await later.Notes.SendOfflineAsync());

        Assert.False(sent.KeptBoth);
        Assert.Equal(id, sent.NoteId);
        Assert.Equal(0, later.Notes.WaitingToSave);
        var notes = await Direct().GetFromJsonAsync<List<NoteDetail>>("/api/notes", RemoteJson.Options);
        Assert.Equal("socks and a made up lantern", Assert.Single(notes!).Body);
        Assert.Empty(await later.Notes.SendOfflineAsync());
    }

    [Fact]
    public async Task A_capture_whose_answer_was_lost_stays_waiting_and_is_kept_once_after_the_server_restarts()
    {
        var pc = StartPc();
        pc.Queue.Add("buy a made up stamp", DateTimeOffset.Now);
        pc.Queue.Add("water the made up fern", DateTimeOffset.Now);
        _network.LoseAnswer = Is(HttpMethod.Post, "/api/captures");

        // The first reached the server, but the PC never heard, so it stays first in line and nothing after it goes.
        Assert.Equal(0, await pc.Queue.SendAsync(capture => pc.Client.SendCaptureAsync(capture)));
        Assert.Equal(2, pc.Queue.Count);
        Assert.Equal(ServerState.Offline, pc.Client.State);

        Restart();
        await pc.Client.CheckAsync();
        Assert.Equal(2, await pc.Queue.SendAsync(capture => pc.Client.SendCaptureAsync(capture)));

        Assert.Equal(0, pc.Queue.Count);
        Assert.Equal(["buy a made up stamp", "water the made up fern"], await PendingCaptures());
    }

    [Fact]
    public async Task A_task_added_by_hand_whose_answer_was_lost_is_kept_once_when_added_again()
    {
        var pc = StartPc();
        var task = new HandAddedItem(ItemKind.Task, "Return the made up library book", DateOnly.FromDateTime(DateTime.Now));
        _network.LoseAnswer = Is(HttpMethod.Post, "/api/items");

        Assert.Throws<ServerUnavailableException>(() => pc.ToDo.AddByHand(task, DateTime.Now));
        await pc.Client.CheckAsync();
        pc.ToDo.AddByHand(task, DateTime.Now);

        Assert.Single(pc.ToDo.GetOpenItems(), item => item.Title == "Return the made up library book");
    }

    [Fact]
    public async Task A_list_entry_whose_answer_was_lost_is_kept_once_when_added_again()
    {
        var pc = StartPc();
        pc.Lists.CreateList("Made up camping", DateTime.Now);
        _network.LoseAnswer = Is(HttpMethod.Post, "/api/lists/entries");

        Assert.Throws<ServerUnavailableException>(() => pc.Lists.AddListEntry("Made up camping", "Tent", DateTime.Now));
        await pc.Client.CheckAsync();
        pc.Lists.AddListEntry("Made up camping", "Tent", DateTime.Now);

        var list = pc.Lists.GetListOverview().Single(list => list.Name == "Made up camping");
        Assert.Equal("Tent", Assert.Single(list.Entries).Text);
    }

    [Fact]
    public async Task Captures_notes_lists_and_tasks_from_the_pc_survive_restarts_and_typing_while_away_is_saved_once()
    {
        var pc = StartPc();
        pc.Queue.Add("book a made up dentist", DateTimeOffset.Now);
        Assert.Equal(1, await pc.Queue.SendAsync(capture => pc.Client.SendCaptureAsync(capture)));
        var note = pc.Notes.CreateNote("Made up trip", "a lake", DateTime.Now);
        Assert.True(pc.Notes.SaveNote(note, "Made up trip", "a lake and a hill", DateTime.Now));
        pc.Notes.GetNote(note);
        Assert.Null(pc.Lists.CreateList("Made up camping", DateTime.Now).Problem);
        pc.Lists.AddListEntry("Made up camping", "Tent", DateTime.Now);
        var task = pc.ToDo.AddByHand(new HandAddedItem(ItemKind.Task, "Fix the made up bike", DateOnly.FromDateTime(DateTime.Now)), DateTime.Now);
        Assert.Null(task.Problem);
        pc.Lists.GetListOverview();

        // The server stops. Typing goes on, and changes that need the server say so rather than pretending.
        _network.Unplug();
        _server.Dispose();
        SqliteConnection.ClearAllPools();
        Assert.True(pc.Notes.SaveNote(note, "Made up trip", "a lake, a hill, and a river", DateTime.Now));
        pc.Queue.Add("call a made up plumber", DateTimeOffset.Now);
        Assert.Equal(0, await pc.Queue.SendAsync(capture => pc.Client.SendCaptureAsync(capture)));
        Assert.Throws<ServerUnavailableException>(() => pc.Lists.AddListEntry("Made up camping", "Stove", DateTime.Now));
        Assert.Equal("Tent", Assert.Single(pc.Lists.GetListOverview().Single(list => list.Name == "Made up camping").Entries).Text);

        // It starts again, and the PC sends what waited, once.
        _server = StartServer();
        _network.Connect(_server.Server);
        await pc.Client.CheckAsync();
        Assert.Single(await pc.Notes.SendOfflineAsync());
        Assert.Equal(1, await pc.Queue.SendAsync(capture => pc.Client.SendCaptureAsync(capture)));
        Assert.Empty(await pc.Notes.SendOfflineAsync());
        Assert.Equal(0, await pc.Queue.SendAsync(capture => pc.Client.SendCaptureAsync(capture)));
        pc.Lists.AddListEntry("Made up camping", "Stove", DateTime.Now);

        // Another restart, and a PC that starts fresh with nothing kept, reads everything back from the server.
        Restart();
        var fresh = StartPc();
        Assert.Equal("a lake, a hill, and a river", fresh.Notes.GetNote(note)!.Body);
        Assert.Single(fresh.Notes.GetNotes());
        Assert.Equal(["Stove", "Tent"], fresh.Lists.GetListOverview().Single(list => list.Name == "Made up camping").Entries.Select(entry => entry.Text).Order(StringComparer.Ordinal));
        Assert.Single(fresh.ToDo.GetOpenItems(), item => item.Id == task.Item!.Id && item.Title == "Fix the made up bike");
        Assert.Equal(["book a made up dentist", "call a made up plumber"], await PendingCaptures());
        Assert.Equal(2, (await fresh.Client.CheckAsync())!.PendingCaptures);
    }
}
