using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Remote;

namespace Waypoint.App.Tests;

/// <summary>Typing in a note, and in Find, in server mode, with a server slow to answer.</summary>
public sealed class ServerNoteSaveTests
{
    private const long NoteId = 7;

    /// <summary>One note. Saves to it can be held, the way a slow server keeps the caller waiting.</summary>
    private sealed class SlowNotesServer : HttpMessageHandler
    {
        private readonly object _gate = new();

        public string Body { get; private set; } = "radish";

        public List<string> Saves { get; } = [];

        public int NotesMade { get; private set; }

        /// <summary>Set once a save has arrived, while it's held.</summary>
        public ManualResetEventSlim SaveArrived { get; } = new();

        /// <summary>
        /// While unset, saves wait for it, but never past the server's own limit, kept just inside the client's wait so
        /// a held save still goes through. A window that waited on the request could only go on once the server gave up
        /// holding it, which <see cref="GaveUp"/> counts.
        /// </summary>
        public ManualResetEventSlim Release { get; } = new(initialState: true);

        private int _answered;
        private int _gaveUp;
        private int _held;

        /// <summary>How many held requests (saves and Find's search) have been answered.</summary>
        public int Answered => Volatile.Read(ref _answered);

        /// <summary>How many were answered because the server's limit ran out rather than the test letting them go.</summary>
        public int GaveUp => Volatile.Read(ref _gaveUp);

        /// <summary>How many requests have started being held.</summary>
        public int Held => Volatile.Read(ref _held);

        private async Task Hold(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _held);
            var released = await Task.Run(() => Release.Wait(ServerClient.RequestTimeout - TimeSpan.FromMilliseconds(500)), cancellationToken);
            if (!released)
            {
                Interlocked.Increment(ref _gaveUp);
            }

            Interlocked.Increment(ref _answered);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            object body = new { };
            if (path == $"/api/notes/{NoteId}" && request.Method == HttpMethod.Put)
            {
                var sent = JsonSerializer.Deserialize<SaveNoteRequest>(await request.Content!.ReadAsStringAsync(cancellationToken), RemoteJson.Options)!;
                SaveArrived.Set();
                await Hold(cancellationToken);
                lock (_gate)
                {
                    Saves.Add(sent.Body);
                    Body = sent.Body;
                }

                body = new FlagValue(true);
            }
            else if (path == $"/api/notes/{NoteId}")
            {
                body = new Found<NoteDetail>(Note());
            }
            else if (path == "/api/notes" && request.Method == HttpMethod.Post)
            {
                NotesMade++;
                body = new Found<long>(NoteId + NotesMade);
            }
            else if (path is "/api/notes" or "/api/notes/in" || path.StartsWith("/api/notes/search", StringComparison.Ordinal))
            {
                body = new[] { Note() };
            }
            else if (path == "/api/items/open")
            {
                await Hold(cancellationToken);
                body = new[] { new StoredItem(1, ItemKind.Task, "Repot the basil", null, null, false) };
            }
            else if (path is "/api/notes/folders" or "/api/notes/spots" or "/api/notes/classes" or "/api/lists"
                || path.EndsWith("/sources", StringComparison.Ordinal) || path.EndsWith("/suggestions", StringComparison.Ordinal))
            {
                body = Array.Empty<object>();
            }

            var json = JsonSerializer.Serialize(body, body.GetType(), RemoteJson.Options);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        private NoteDetail Note()
        {
            lock (_gate)
            {
                return new NoteDetail(NoteId, "Seed list", Body, null, new DateTime(2030, 4, 1, 9, 0, 0));
            }
        }
    }

    private static void Type(MarkdownBox body, string text)
    {
        body.SelectAll();
        body.Selection.Text = text;
        body.CaretIndex = body.Text.Length;
    }

    private static (NotesView View, MarkdownBox Body) Open(UiTest ui, SlowNotesServer server)
    {
        var client = new ServerClient(new HttpClient(server) { BaseAddress = new Uri("http://server/") }, "token");
        var view = new NotesView();
        view.Connect(new ServerNotesData(client));
        ui.Host(view);
        view.Refresh(force: true);
        ui.Pump();
        var notes = UiTest.Named<ListBox>(view, "NotesList");
        notes.SelectedItem = notes.Items.Cast<object>().Single(row => UiTest.Prop<long>(row, "Id") == NoteId);
        ui.Pump();
        return (view, UiTest.Named<MarkdownBox>(view, "BodyBox"));
    }

    [Fact]
    public void The_save_once_typing_stops_goes_on_without_holding_the_window_and_closing_waits_for_it() => Ui.Run(ui =>
    {
        var server = new SlowNotesServer();
        var (view, body) = Open(ui, server);

        Type(body, "radish, kale");
        server.Release.Reset();
        var answered = server.Answered;
        UiTest.Call(view, "SaveSoon");
        // The server holds the save until it's let go, so had the window waited for it, it would have been answered.
        Assert.Equal(answered, server.Answered);
        Assert.True(server.SaveArrived.Wait(TimeSpan.FromSeconds(30)));
        // And the window went on while the server was still holding it, so it never waited on the save at all.
        Assert.Equal(answered, server.Answered);

        // Typing goes on while that save is on its way. Leaving waits for it, then saves the rest, in order.
        Type(body, "radish, kale, beets");
        server.Release.Set();
        view.Leave();

        Assert.Equal(["radish, kale", "radish, kale, beets"], server.Saves);
        Assert.Equal("radish, kale, beets", server.Body);

        // The note the save made is the editor's own, so nothing was taken for a change made elsewhere.
        Assert.Equal(0, server.NotesMade);
        ui.Pump();
        Assert.Equal("radish, kale, beets", body.Text);
        Assert.Equal(0, server.GaveUp);
    });

    [Fact]
    public void A_save_that_finished_in_the_background_shows_as_saved_and_isnt_sent_again() => Ui.Run(ui =>
    {
        var server = new SlowNotesServer();
        var (view, body) = Open(ui, server);

        Type(body, "radish and leeks");
        UiTest.Call(view, "SaveSoon");
        ServerToDoClickTests.PumpUntil(ui, () => UiTest.Named<TextBlock>(view, "SavedLabel").Text == "Saved", "the save");

        Assert.Equal("Saved", UiTest.Named<TextBlock>(view, "SavedLabel").Text);
        view.Leave();
        Assert.Equal(["radish and leeks"], server.Saves);
        Assert.Equal(0, server.NotesMade);
    });

    [Fact]
    public void Find_searches_off_the_window_thread_once_typing_pauses_and_shows_what_came_back() => Ui.Run(ui =>
    {
        var server = new SlowNotesServer();
        var client = new ServerClient(new HttpClient(server) { BaseAddress = new Uri("http://server/") }, "token");
        var view = new FindView();
        view.Connect(new FindSources(new ServerToDoData(client), null, null, null, null));
        ui.Host(view);
        ui.Pump();
        var results = UiTest.Named<ListBox>(view, "Results");

        server.Release.Reset();
        UiTest.Named<TextBox>(view, "SearchBox").Text = "basil";
        var answered = server.Answered;
        var held = server.Held;
        UiTest.Call(view, "SearchSoon");
        // The search is held until it's let go, so had the window waited for it, it would have been answered.
        Assert.Equal(answered, server.Answered);
        Assert.Empty(results.Items);
        ServerToDoClickTests.PumpUntil(ui, () => server.Held > held, "the search to reach the server");
        Assert.Equal(answered, server.Answered);

        server.Release.Set();
        ServerToDoClickTests.PumpUntil(ui, () => results.Items.Count > 0, "the search");

        Assert.Equal(["Repot the basil"], results.Items.Cast<FoundThing>().Select(thing => thing.Title));
        Assert.Equal(0, server.GaveUp);
    });
}
