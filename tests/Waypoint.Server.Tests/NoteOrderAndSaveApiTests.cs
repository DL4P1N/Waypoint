using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Email;
using Waypoint.Core.Notes;
using Waypoint.Core.Remote;

namespace Waypoint.Server.Tests;

/// <summary>
/// Putting note folders in a new order, and saving a note only if nobody changed it since it was opened, through the
/// server, and the same from a PC talking to a server from before either could be done in one step.
/// </summary>
public sealed class NoteOrderAndSaveApiTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-server-{Guid.NewGuid()}");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _token;

    public NoteOrderAndSaveApiTests()
    {
        Directory.CreateDirectory(_folder);
        _token = ServerToken.Create(_folder);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Waypoint:DataDirectory", _folder);
            builder.UseSetting("Waypoint:RunBackground", "false");
            builder.ConfigureServices(services => services.AddSingleton(new WorkerParts(_ => new NoTokens(), CaptureSorter: new NeverSorts())));
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
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

    /// <summary>
    /// Makes this server answer the way one from before did: it has no route for a folder order, and a note save
    /// neither reads nor sends anything about what the note was expected to be.
    /// </summary>
    private sealed class OldServer(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Requests)
            {
                Requests.Add($"{request.Method} {path}");
            }

            if (request.Method == HttpMethod.Put && path == "/api/notes/folders/order")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var noteSave = request.Method == HttpMethod.Put && IsNote(path);
            if (noteSave)
            {
                var sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                sent.Remove("expectedBody");
                sent.Remove("expectedTitle");
                request.Content = new StringContent(sent.ToJsonString(), Encoding.UTF8, "application/json");
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (noteSave)
            {
                var answer = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
                var flag = JsonSerializer.Serialize(new FlagValue(answer["value"]!.GetValue<bool>()), RemoteJson.Options);
                response.Content = new StringContent(flag, Encoding.UTF8, "application/json");
            }

            return response;
        }

        private static bool IsNote(string path) =>
            path.StartsWith("/api/notes/", StringComparison.Ordinal) && long.TryParse(path["/api/notes/".Length..], out _);
    }

    /// <summary>
    /// Stands between the PC and the server, writing down each request. It can add a reply to a note just before a
    /// save of it reaches the server, the way the Discord worker adds one, after anything the PC read first. It can
    /// also make the server unreachable.
    /// </summary>
    private sealed class Line(HttpMessageHandler inner, WaypointDatabase database) : DelegatingHandler(inner)
    {
        public List<string> Requests { get; } = [];

        public string? Reply { get; set; }

        public bool Down { get; set; }

        /// <summary>When set, the next new note reaches the server and is made, and its answer is lost on the way back.</summary>
        public bool LoseNewNoteAnswer { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Requests)
            {
                Requests.Add($"{request.Method} {path}");
            }

            if (Down)
            {
                throw new HttpRequestException("No route to host");
            }

            if (request.Method == HttpMethod.Put && Reply is { } reply && path.StartsWith("/api/notes/", StringComparison.Ordinal) && long.TryParse(path["/api/notes/".Length..], out var id))
            {
                Reply = null;
                var note = database.GetNote(id)!;
                Assert.True(database.SaveNoteIfUnchanged(id, note.Title, $"{note.Body}\n\n{reply}", note.Body, DateTime.Now));
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (LoseNewNoteAnswer && request.Method == HttpMethod.Post && path == "/api/notes")
            {
                LoseNewNoteAnswer = false;
                response.Dispose();
                throw new HttpRequestException("The connection was reset");
            }

            return response;
        }
    }

    private Line NewLine() => new(_factory.Server.CreateHandler(), Database);

    private ServerClient Client(HttpMessageHandler? handler = null) =>
        new(new HttpClient(handler ?? _factory.Server.CreateHandler()) { BaseAddress = _factory.Server.BaseAddress }, _token, "this-pc");

    /// <summary>A PC talking to a server from before. Old writes down what the PC sent, and Line is next to the server.</summary>
    private (ServerNotesData Notes, OldServer Old, Line Line) OldPc()
    {
        var line = NewLine();
        var old = new OldServer(line);
        return (new ServerNotesData(Client(old)), old, line);
    }

    private WaypointDatabase Database => _factory.Services.GetRequiredService<WaypointServer>().Database;

    private static IReadOnlyList<string> Names(INotesData notes) => notes.GetNoteFolders().Select(folder => folder.Name).ToList();

    [Fact]
    public void Folders_are_put_in_a_new_order_in_one_request()
    {
        var line = NewLine();
        var requests = line.Requests;
        INotesData notes = new ServerNotesData(Client(line));
        var a = notes.CreateNoteFolder("Garden", null, DateTime.Now).Id;
        var b = notes.CreateNoteFolder("Recipes", null, DateTime.Now).Id;
        var c = notes.CreateNoteFolder("Trips", null, DateTime.Now).Id;
        requests.Clear();

        Assert.True(notes.ReorderNoteFolders(null, [c, a, b]));

        Assert.Equal(["Trips", "Garden", "Recipes"], Names(notes));
        Assert.Equal(["PUT /api/notes/folders/order", "GET /api/notes/folders"], requests);
    }

    [Fact]
    public void A_new_order_the_server_cant_all_keep_changes_nothing()
    {
        INotesData notes = new ServerNotesData(Client());
        var a = notes.CreateNoteFolder("Garden", null, DateTime.Now).Id;
        var b = notes.CreateNoteFolder("Recipes", null, DateTime.Now).Id;
        var c = notes.CreateNoteFolder("Trips", null, DateTime.Now).Id;

        // Another PC deleted one of them just before this drag landed.
        Assert.Equal(FolderDeleteResult.Deleted, Database.DeleteNoteFolder(b, NotesInFolder.Refuse));

        Assert.False(notes.ReorderNoteFolders(null, [c, b, a]));
        Assert.Equal(["Garden", "Trips"], Names(notes));
        Assert.Equal([0, 1], notes.GetNoteFolders().Select(folder => folder.Position));
    }

    [Fact]
    public void A_server_from_before_gets_the_new_order_one_folder_at_a_time()
    {
        var (server, line, _) = OldPc();
        INotesData notes = server;
        var a = notes.CreateNoteFolder("Garden", null, DateTime.Now).Id;
        var b = notes.CreateNoteFolder("Recipes", null, DateTime.Now).Id;
        var c = notes.CreateNoteFolder("Trips", null, DateTime.Now).Id;
        line.Requests.Clear();

        Assert.True(notes.ReorderNoteFolders(null, [c, a, b]));

        Assert.Equal(["Trips", "Garden", "Recipes"], Names(notes));
        Assert.Equal(
            ["PUT /api/notes/folders/order", $"PUT /api/notes/folders/{c}/place", $"PUT /api/notes/folders/{a}/place", $"PUT /api/notes/folders/{b}/place", "GET /api/notes/folders"],
            line.Requests);
    }

    [Fact]
    public void A_reply_from_Discord_landing_between_the_pcs_read_and_its_save_is_kept()
    {
        var line = NewLine();
        INotesData notes = new ServerNotesData(Client(line));
        var id = notes.CreateNote("Seed list", "radish", DateTime.Now);
        Assert.Equal("radish", notes.GetNote(id)!.Body);

        // The first save reads the note first, since this PC hasn't heard yet that the server checks. The reply
        // lands after that read, just before the save.
        line.Reply = "beets too";
        line.Requests.Clear();
        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale", "radish", DateTime.Now));

        Assert.Equal("radish, kale", Database.GetNote(id)!.Body);
        var kept = Assert.Single(Database.GetNotes(), note => note.Id != id);
        Assert.Equal("Seed list" + NoteDraft.ElsewhereSuffix, kept.Title);
        Assert.Equal("radish\n\nbeets too", kept.Body);
        Assert.Equal([$"GET /api/notes/{id}", $"PUT /api/notes/{id}", "POST /api/notes", $"PUT /api/notes/{id}"], line.Requests);

        // Now that the server has said it checks, the save goes straight to it, and still catches a reply.
        line.Reply = "and onions";
        line.Requests.Clear();
        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale, leeks", "radish, kale", DateTime.Now));

        Assert.Equal("radish, kale, leeks", Database.GetNote(id)!.Body);
        Assert.Contains(Database.GetNotes(), note => note.Title == "Seed list" + NoteDraft.ElsewhereSuffix && note.Body == "radish, kale\n\nand onions");
        Assert.Equal([$"PUT /api/notes/{id}", "POST /api/notes", $"PUT /api/notes/{id}"], line.Requests);
    }

    [Fact]
    public void A_save_checked_against_text_that_changed_saves_nothing_and_says_what_it_is_now()
    {
        INotesData notes = new ServerNotesData(Client());
        var id = notes.CreateNote("Seed list", "radish", DateTime.Now);
        Database.SaveNote(id, "Seed list", "radish\n\nbeets too", DateTime.Now);

        var result = notes.SaveNoteIfUnchanged(id, "Seed list", "radish, kale", "radish", DateTime.Now);

        Assert.Equal(NoteSaveOutcome.Changed, result.Outcome);
        Assert.Equal("radish\n\nbeets too", result.Stored!.Body);
        Assert.Equal("radish\n\nbeets too", Database.GetNote(id)!.Body);

        Database.DeleteNote(id, DateTime.Now);
        Assert.Equal(NoteSaveOutcome.Gone, notes.SaveNoteIfUnchanged(id, "Seed list", "radish, kale", "radish\n\nbeets too", DateTime.Now).Outcome);
    }

    [Fact]
    public async Task A_pc_from_before_still_saves_to_this_server_and_reads_the_answer_it_always_did()
    {
        var id = Database.CreateNote("Seed list", "radish", DateTime.Now);
        Database.SaveNote(id, "Seed list", "radish\n\nbeets too", DateTime.Now);
        using var http = new HttpClient(_factory.Server.CreateHandler()) { BaseAddress = _factory.Server.BaseAddress };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);

        // What a PC from before sends: no text expected, so it saves as it always has.
        using var response = await http.PutAsync($"/api/notes/{id}", new StringContent("""{"title":"Seed list","body":"radish, kale"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(JsonSerializer.Deserialize<FlagValue>(await response.Content.ReadAsStringAsync(), RemoteJson.Options)!.Value);
        Assert.Equal("radish, kale", Database.GetNote(id)!.Body);
    }

    [Fact]
    public void Saving_to_a_server_from_before_reads_the_note_first_each_time_and_keeps_a_change_found()
    {
        var (notes, old, _) = OldPc();
        var id = notes.CreateNote("Seed list", "radish", DateTime.Now);

        // Changed after the editor loaded it and before it saved: the read before the save finds it, as before.
        Database.SaveNote(id, "Seed list", "radish\n\nbeets too", DateTime.Now);
        old.Requests.Clear();
        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale", "radish", DateTime.Now));

        Assert.Equal("radish, kale", Database.GetNote(id)!.Body);
        Assert.Contains(Database.GetNotes(), note => note.Title == "Seed list" + NoteDraft.ElsewhereSuffix && note.Body == "radish\n\nbeets too");
        Assert.Equal([$"GET /api/notes/{id}", "POST /api/notes", $"GET /api/notes/{id}", $"PUT /api/notes/{id}"], old.Requests);

        // It never says it checks, so the next save reads first too.
        old.Requests.Clear();
        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale, leeks", "radish, kale", DateTime.Now));
        Assert.Equal("radish, kale, leeks", Database.GetNote(id)!.Body);
        Assert.Equal([$"GET /api/notes/{id}", $"PUT /api/notes/{id}"], old.Requests);
        Assert.Equal(2, Database.GetNotes().Count);
    }

    /// <summary>The same server address, answered by this server or by one from before, as when it's changed in place.</summary>
    private sealed class Swapped(HttpMessageHandler now, HttpMessageHandler before) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _now = new(now, disposeHandler: false);
        private readonly HttpMessageInvoker _before = new(before, disposeHandler: false);

        public bool Before { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            (Before ? _before : _now).SendAsync(request, cancellationToken);
    }

    [Fact]
    public async Task A_server_changed_for_one_from_before_while_away_has_the_note_read_first_again()
    {
        var line = NewLine();
        var old = new OldServer(line);
        var swapped = new Swapped(line, old);
        var client = Client(swapped);
        INotesData notes = new ServerNotesData(client);
        var id = notes.CreateNote("Seed list", "radish", DateTime.Now);
        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale", "radish", DateTime.Now));

        // This server said it checks. Then it goes away and comes back as one from before.
        line.Down = true;
        await client.CheckAsync();
        Assert.Equal(ServerState.Offline, client.State);
        swapped.Before = true;
        line.Down = false;
        await client.CheckAsync();
        Assert.Equal(ServerState.Online, client.State);

        // A change made there meanwhile is found by reading first, since that server can't check, and is kept.
        Database.SaveNote(id, "Seed list", "radish, kale\n\nbeets too", DateTime.Now);
        old.Requests.Clear();
        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale, leeks", "radish, kale", DateTime.Now));

        Assert.Equal($"GET /api/notes/{id}", old.Requests[0]);
        Assert.Equal("radish, kale, leeks", Database.GetNote(id)!.Body);
        Assert.Contains(Database.GetNotes(), note => note.Title == "Seed list" + NoteDraft.ElsewhereSuffix && note.Body == "radish, kale\n\nbeets too");
    }

    [Fact]
    public async Task Typing_kept_while_away_remembers_the_text_the_editor_loaded_so_a_reply_meanwhile_is_kept_too()
    {
        var line = NewLine();
        var client = Client(line);
        var notes = new ServerNotesData(client, new OfflineNotes(Path.Combine(_folder, "pc-notes.json")));
        var id = notes.CreateNote("Seed list", "radish", DateTime.Now);

        // A reply is added on the server, and the page reads it, but the editor had typing it hadn't saved, so it
        // still has the text it loaded.
        Assert.True(Database.SaveNoteIfUnchanged(id, "Seed list", "radish\n\nbeets too", "radish", DateTime.Now));
        Assert.Equal("radish\n\nbeets too", notes.GetNote(id)!.Body);

        line.Down = true;
        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale", "radish", DateTime.Now));
        Assert.Equal(1, notes.WaitingToSave);

        line.Down = false;
        await client.CheckAsync();
        var sent = Assert.Single(await notes.SendOfflineAsync());

        Assert.True(sent.KeptBoth);
        Assert.Equal("radish\n\nbeets too", Database.GetNote(id)!.Body);
        Assert.Equal("radish, kale", Database.GetNote(sent.NoteId)!.Body);
    }

    [Fact]
    public async Task A_reply_landing_while_typing_kept_away_is_being_sent_is_kept_beside_it()
    {
        var line = NewLine();
        var client = Client(line);
        var notes = new ServerNotesData(client, new OfflineNotes(Path.Combine(_folder, "pc-notes.json")));
        var id = notes.CreateNote("Seed list", "radish", DateTime.Now);

        line.Down = true;
        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale", "radish", DateTime.Now));
        line.Down = false;
        await client.CheckAsync();

        // The note still has the text the typing started from when it's read, and a reply lands just before the save.
        line.Reply = "beets too";
        var sent = Assert.Single(await notes.SendOfflineAsync());

        Assert.True(sent.KeptBoth);
        Assert.Equal("radish\n\nbeets too", Database.GetNote(id)!.Body);
        Assert.Equal("radish, kale", Database.GetNote(sent.NoteId)!.Body);
        Assert.Equal(0, notes.WaitingToSave);
    }

    [Fact]
    public async Task A_reply_landing_while_a_note_made_away_is_brought_up_to_date_is_kept_beside_it()
    {
        var line = NewLine();
        var client = Client(line);
        var notes = new ServerNotesData(client, new OfflineNotes(Path.Combine(_folder, "pc-notes.json")));

        // The server makes the note, but the answer is lost, and more is typed before it's back.
        line.LoseNewNoteAnswer = true;
        var id = notes.CreateNote("Seed list", "radish", DateTime.Now);
        Assert.True(id < 0);
        line.Down = true;
        Assert.True(notes.SaveNote(id, "Seed list", "radish, kale", DateTime.Now));
        line.Down = false;
        await client.CheckAsync();

        // Sent again, the server gives back the note it made, which is brought up to date, and a reply lands just
        // before that save.
        line.Reply = "beets too";
        var sent = Assert.Single(await notes.SendOfflineAsync());

        Assert.True(sent.KeptBoth);
        Assert.Equal("radish, kale", Database.GetNote(sent.NoteId)!.Body);
        var made = Assert.Single(Database.GetNotes(), note => note.Id != sent.NoteId);
        Assert.Equal("radish\n\nbeets too", made.Body);
        Assert.Equal(0, notes.WaitingToSave);
    }

    [Fact]
    public void A_note_renamed_elsewhere_while_its_editor_is_open_keeps_its_new_title_when_the_text_is_saved()
    {
        var notes = new ServerNotesData(Client());
        var id = notes.CreateNote("Seed list", "radish", DateTime.Now);

        // Renamed on the server, from Discord or another PC, while only the text is typed here.
        Database.SaveNote(id, "Garden seeds", "radish", DateTime.Now);
        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale", "radish", DateTime.Now, "Seed list"));

        var note = Assert.Single(Database.GetNotes());
        Assert.Equal(("Garden seeds", "radish, kale"), (note.Title, note.Body));
        Assert.Equal("Garden seeds", notes.GetNote(id)!.Title);
    }

    [Fact]
    public void A_note_renamed_on_a_server_from_before_is_found_by_the_read_first_and_keeps_its_new_title()
    {
        var (notes, _, _) = OldPc();
        var id = notes.CreateNote("Seed list", "radish", DateTime.Now);

        Database.SaveNote(id, "Garden seeds", "radish", DateTime.Now);
        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale", "radish", DateTime.Now, "Seed list"));

        var note = Assert.Single(Database.GetNotes());
        Assert.Equal(("Garden seeds", "radish, kale"), (note.Title, note.Body));
    }

    [Fact]
    public void A_title_and_text_changed_on_the_server_and_here_keeps_both_versions()
    {
        INotesData notes = new ServerNotesData(Client());
        var id = notes.CreateNote("Seed list", "radish", DateTime.Now);
        Database.SaveNote(id, "Garden seeds", "radish\n\nbeets too", DateTime.Now);

        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "Spring seeds", "radish, kale", "radish", DateTime.Now, "Seed list"));

        var note = Database.GetNote(id)!;
        Assert.Equal(("Spring seeds", "radish, kale"), (note.Title, note.Body));
        var kept = Assert.Single(Database.GetNotes(), other => other.Id != id);
        Assert.Equal(("Garden seeds" + NoteDraft.ElsewhereSuffix, "radish\n\nbeets too"), (kept.Title, kept.Body));
    }

    [Fact]
    public async Task A_pc_from_before_that_sends_only_the_text_it_expects_still_saves_as_it_did()
    {
        var id = Database.CreateNote("Seed list", "radish", DateTime.Now);
        Database.SaveNote(id, "Garden seeds", "radish", DateTime.Now);
        using var http = new HttpClient(_factory.Server.CreateHandler()) { BaseAddress = _factory.Server.BaseAddress };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);

        using var response = await http.PutAsync($"/api/notes/{id}", new StringContent("""{"title":"Seed list","body":"radish, kale","expectedBody":"radish"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reply = JsonSerializer.Deserialize<NoteSaveReply>(await response.Content.ReadAsStringAsync(), RemoteJson.Options)!;
        Assert.True(reply.Value);
        Assert.True(reply.Checked);
        Assert.Equal(("Seed list", "radish, kale"), (Database.GetNote(id)!.Title, Database.GetNote(id)!.Body));
    }

    [Fact]
    public async Task Typing_kept_while_away_keeps_a_title_renamed_on_the_server_meanwhile()
    {
        var line = NewLine();
        var client = Client(line);
        var notes = new ServerNotesData(client, new OfflineNotes(Path.Combine(_folder, "pc-notes.json")));
        var id = notes.CreateNote("Seed list", "radish", DateTime.Now);

        line.Down = true;
        Assert.True(NoteSaving.SaveKeepingOther(notes, id, "Seed list", "radish, kale", "radish", DateTime.Now, "Seed list"));
        Database.SaveNote(id, "Garden seeds", "radish", DateTime.Now);

        line.Down = false;
        await client.CheckAsync();
        var sent = Assert.Single(await notes.SendOfflineAsync());

        Assert.False(sent.KeptBoth);
        var note = Assert.Single(Database.GetNotes());
        Assert.Equal(("Garden seeds", "radish, kale"), (note.Title, note.Body));
    }
}
