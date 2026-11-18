using System.Diagnostics;
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

        /// <summary>While unset, saves wait for it. Kept well inside the client's wait, so a held save still goes through.</summary>
        public ManualResetEventSlim Release { get; } = new(initialState: true);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            object body = new { };
            if (path == $"/api/notes/{NoteId}" && request.Method == HttpMethod.Put)
            {
                var sent = JsonSerializer.Deserialize<SaveNoteRequest>(await request.Content!.ReadAsStringAsync(cancellationToken), RemoteJson.Options)!;
                SaveArrived.Set();
                await Task.Run(() => Release.Wait(TimeSpan.FromSeconds(2)), cancellationToken);
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
                await Task.Run(() => Release.Wait(TimeSpan.FromSeconds(2)), cancellationToken);
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
        var watch = Stopwatch.StartNew();
        UiTest.Call(view, "SaveSoon");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"The window waited {watch.Elapsed.TotalMilliseconds:0} ms for the server");
        Assert.True(server.SaveArrived.Wait(TimeSpan.FromSeconds(5)));

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
    });

    [Fact]
    public void A_save_that_finished_in_the_background_shows_as_saved_and_isnt_sent_again() => Ui.Run(ui =>
    {
        var server = new SlowNotesServer();
        var (view, body) = Open(ui, server);

        Type(body, "radish and leeks");
        UiTest.Call(view, "SaveSoon");
        for (var tries = 0; tries < 50 && UiTest.Named<TextBlock>(view, "SavedLabel").Text != "Saved"; tries++)
        {
            Thread.Sleep(20);
            ui.Pump();
        }

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
        var watch = Stopwatch.StartNew();
        UiTest.Call(view, "SearchSoon");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"The window waited {watch.Elapsed.TotalMilliseconds:0} ms for the server");
        Assert.Empty(results.Items);

        server.Release.Set();
        for (var tries = 0; tries < 100 && results.Items.Count == 0; tries++)
        {
            Thread.Sleep(20);
            ui.Pump();
        }

        Assert.Equal(["Repot the basil"], results.Items.Cast<FoundThing>().Select(thing => thing.Title));
    });
}
