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

    private ServerClient Client(HttpMessageHandler? handler = null) =>
        new(new HttpClient(handler ?? _factory.Server.CreateHandler()) { BaseAddress = _factory.Server.BaseAddress }, _token, "this-pc");

    private (ServerNotesData Notes, OldServer Line) OldPc()
    {
        var line = new OldServer(_factory.Server.CreateHandler());
        return (new ServerNotesData(Client(line)), line);
    }

    private WaypointDatabase Database => _factory.Services.GetRequiredService<WaypointServer>().Database;

    private static IReadOnlyList<string> Names(INotesData notes) => notes.GetNoteFolders().Select(folder => folder.Name).ToList();

    [Fact]
    public void Folders_are_put_in_a_new_order_in_one_request()
    {
        var requests = new List<string>();
        INotesData notes = new ServerNotesData(Client(new PassThrough(_factory.Server.CreateHandler(), requests)));
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
        var (old, line) = OldPc();
        INotesData notes = old;
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

    /// <summary>Writes down each request on its way to the server, which answers as it is.</summary>
    private sealed class PassThrough(HttpMessageHandler inner, List<string> requests) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (requests)
            {
                requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
