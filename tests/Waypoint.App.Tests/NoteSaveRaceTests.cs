using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;
using Waypoint.Core.Remote;

namespace Waypoint.App.Tests;

/// <summary>
/// A reply added from Discord, or an edit from another PC, landing after the note editor loaded a note and just
/// before its save arrives. It's kept as a note of its own rather than overwritten, on this PC and on a server.
/// </summary>
public sealed class NoteSaveRaceTests
{
    private const long NoteId = 7;

    private const string Kept = "Seed list" + NoteDraft.ElsewhereSuffix;

    private static void Type(MarkdownBox body, string text)
    {
        body.SelectAll();
        body.Selection.Text = text;
        body.CaretIndex = body.Text.Length;
    }

    private static MarkdownBox Open(UiTest ui, NotesView view, long id)
    {
        ui.Host(view);
        view.Refresh(force: true);
        ui.Pump();
        var notes = UiTest.Named<ListBox>(view, "NotesList");
        notes.SelectedItem = notes.Items.Cast<object>().Single(row => UiTest.Prop<long>(row, "Id") == id);
        ui.Pump();
        return UiTest.Named<MarkdownBox>(view, "BodyBox");
    }

    /// <summary>
    /// Waits for the save once typing stops, then for the reads the page makes again after it, however long a busy
    /// PC takes, so none of them is still on its way when a test goes on to count what's asked of the server next.
    /// </summary>
    private static void WaitForSaved(UiTest ui, NotesView view, NotesServer server)
    {
        var label = UiTest.Named<TextBlock>(view, "SavedLabel");
        ServerToDoClickTests.PumpUntil(ui, () => label.Text == "Saved", "the note to be saved");

        // Asked while the page's own reads after the save are on their way, this is the same round of them.
        var reading = server.Client!.RefreshReadsAsync();
        ServerToDoClickTests.PumpUntil(ui, () => reading.IsCompleted, "the reads after the save");
        Assert.Equal("Saved", label.Text);
    }

    /// <summary>This PC's notes, with a reply from Discord added the way the Discord worker adds one, just before a save arrives.</summary>
    public class AppendBeforeSave : DispatchProxy
    {
        public INotesData Inner { get; set; } = null!;

        public WaypointDatabase Database { get; set; } = null!;

        public string? Reply { get; set; }

        /// <summary>When set, a reply lands before every save.</summary>
        public bool KeepsReplying { get; set; }

        private int _replies;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name is nameof(INotesData.SaveNote) or nameof(INotesData.SaveNoteIfUnchanged)
                && (Reply ?? (KeepsReplying ? $"reply {++_replies}" : null)) is { } reply)
            {
                Reply = null;
                var id = (long)args![0]!;
                var note = Database.GetNote(id)!;
                Assert.True(Database.SaveNoteIfUnchanged(id, note.Title, $"{note.Body}\n\n{reply}", note.Body, DateTime.Now));
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is { } inner)
            {
                throw inner;
            }
        }
    }

    [Fact]
    public void On_this_PC_a_reply_landing_just_before_the_save_is_kept() => Ui.Run(ui =>
    {
        var id = ui.Database.CreateNote("Seed list", "radish", DateTime.Now);
        var notes = DispatchProxy.Create<INotesData, AppendBeforeSave>();
        var line = (AppendBeforeSave)(object)notes;
        line.Inner = new LocalNotesData(ui.Database);
        line.Database = ui.Database;
        var view = new NotesView();
        view.Connect(notes);
        var body = Open(ui, view, id);

        Type(body, "radish, kale");
        line.Reply = "beets too";
        view.Leave();

        Assert.Equal("radish, kale", ui.Database.GetNote(id)!.Body);
        var kept = Assert.Single(ui.Database.GetNotes(), note => note.Id != id);
        Assert.Equal(Kept, kept.Title);
        Assert.Equal("radish\n\nbeets too", kept.Body);
    });

    private const string AllReplies = "radish\n\nreply 1\n\nreply 2\n\nreply 3";

    [Fact]
    public void On_this_PC_a_note_changed_again_before_every_try_keeps_both_texts() => Ui.Run(ui =>
    {
        var id = ui.Database.CreateNote("Seed list", "radish", DateTime.Now);
        var notes = DispatchProxy.Create<INotesData, AppendBeforeSave>();
        var line = (AppendBeforeSave)(object)notes;
        line.Inner = new LocalNotesData(ui.Database);
        line.Database = ui.Database;
        var view = new NotesView();
        view.Connect(notes);
        var body = Open(ui, view, id);

        Type(body, "radish, kale");
        line.KeepsReplying = true;
        view.Leave();
        line.KeepsReplying = false;

        // The note keeps every reply, and what was typed is a note of its own beside it.
        Assert.Equal(AllReplies, ui.Database.GetNote(id)!.Body);
        var typed = Assert.Single(ui.Database.GetNotes(), note => note.Id != id && note.Title == "Seed list");
        Assert.Equal("radish, kale", typed.Body);
        Assert.Equal(3, ui.Database.GetNotes().Count(note => note.Title == Kept));
    });

    /// <summary>
    /// One note on a server that checks a save against the text expected, the way the server does, or, when
    /// <see cref="Old"/>, a server from before that saves whatever it's sent. A reply can be added just before a save.
    /// </summary>
    private sealed class NotesServer : HttpMessageHandler
    {
        private readonly object _gate = new();

        public bool Old { get; init; }

        /// <summary>The page's client for this server, set as the page is opened.</summary>
        public ServerClient? Client { get; set; }

        public string Body { get; set; } = "radish";

        /// <summary>Added to the note the next time a save arrives, after anything the PC read before it.</summary>
        public string? Reply { get; set; }

        /// <summary>When set, a reply is added before every save.</summary>
        public bool KeepsReplying { get; set; }

        private int _replies;

        public List<NoteDetail> Made { get; } = [];

        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            object body = new { };
            lock (_gate)
            {
                Requests.Add($"{request.Method} {path}");
            }

            if (path == $"/api/notes/{NoteId}" && request.Method == HttpMethod.Put)
            {
                var sent = JsonSerializer.Deserialize<SaveNoteRequest>(await request.Content!.ReadAsStringAsync(cancellationToken), RemoteJson.Options)!;
                lock (_gate)
                {
                    if ((Reply ?? (KeepsReplying ? $"reply {++_replies}" : null)) is { } reply)
                    {
                        Reply = null;
                        Body = $"{Body}\n\n{reply}";
                    }

                    if (Old)
                    {
                        Body = sent.Body;
                        body = new FlagValue(true);
                    }
                    else if (sent.ExpectedBody is { } expected && expected != Body)
                    {
                        body = new NoteSaveReply(false, Checked: true, Note());
                    }
                    else
                    {
                        Body = sent.Body;
                        body = new NoteSaveReply(true, Checked: sent.ExpectedBody is not null);
                    }
                }
            }
            else if (path == $"/api/notes/{NoteId}")
            {
                body = new Found<NoteDetail>(Note());
            }
            else if (path == "/api/notes" && request.Method == HttpMethod.Post)
            {
                var sent = JsonSerializer.Deserialize<NewNoteRequest>(await request.Content!.ReadAsStringAsync(cancellationToken), RemoteJson.Options)!;
                lock (_gate)
                {
                    Made.Add(new NoteDetail(NoteId + Made.Count + 1, sent.Title, sent.Body, null, DateTime.Now, sent.FolderId));
                    body = new Found<long>(Made[^1].Id);
                }
            }
            else if (Made.FirstOrDefault(made => path == $"/api/notes/{made.Id}") is { } made)
            {
                body = new Found<NoteDetail>(made);
            }
            else if (path is "/api/notes" or "/api/notes/in" || path.StartsWith("/api/notes/search", StringComparison.Ordinal))
            {
                lock (_gate)
                {
                    body = Made.Prepend(Note()).ToArray();
                }
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

        /// <summary>The note's reads and saves and the notes made, up to the save numbered <paramref name="puts"/>, leaving out the page's other reads.</summary>
        public List<string> UpToSaved(int puts)
        {
            lock (_gate)
            {
                var asked = new List<string>();
                foreach (var entry in Requests.Where(entry => entry == $"GET /api/notes/{NoteId}" || entry == $"PUT /api/notes/{NoteId}" || entry == "POST /api/notes"))
                {
                    asked.Add(entry);
                    if (entry.StartsWith("PUT", StringComparison.Ordinal) && --puts == 0)
                    {
                        break;
                    }
                }

                return asked;
            }
        }
    }

    private static (NotesView View, MarkdownBox Body) Open(UiTest ui, NotesServer server)
    {
        var view = new NotesView();
        server.Client = new ServerClient(new HttpClient(server) { BaseAddress = new Uri("http://server/") }, "token");
        view.Connect(new ServerNotesData(server.Client));
        return (view, Open(ui, view, NoteId));
    }

    [Fact]
    public void With_a_server_a_reply_landing_just_before_the_save_once_typing_stops_is_kept() => Ui.Run(ui =>
    {
        var server = new NotesServer();
        var (view, body) = Open(ui, server);

        Type(body, "radish, kale");
        server.Reply = "beets too";
        UiTest.Call(view, "SaveSoon");
        WaitForSaved(ui, view, server);

        Assert.Equal("radish, kale", server.Body);
        var kept = Assert.Single(server.Made);
        Assert.Equal(Kept, kept.Title);
        Assert.Equal("radish\n\nbeets too", kept.Body);

        // Once the server has said it checks, saves go straight to it, and it still catches a reply landing first.
        Type(body, "radish, kale, leeks");
        server.Reply = "and onions";
        server.Requests.Clear();
        view.Leave();

        Assert.Equal("radish, kale, leeks", server.Body);
        Assert.Equal("radish, kale\n\nand onions", server.Made[^1].Body);
        Assert.Equal([$"PUT /api/notes/{NoteId}", "POST /api/notes", $"PUT /api/notes/{NoteId}"], server.UpToSaved(2));
        ui.Pump();
        Assert.Equal("radish, kale, leeks", body.Text);
    });

    [Fact]
    public void With_a_server_a_reply_landing_just_before_leaving_saves_is_kept() => Ui.Run(ui =>
    {
        var server = new NotesServer();
        var (view, body) = Open(ui, server);

        Type(body, "radish, kale");
        server.Reply = "beets too";
        view.Leave();

        Assert.Equal("radish, kale", server.Body);
        Assert.Equal(("radish\n\nbeets too", Kept), (Assert.Single(server.Made).Body, server.Made[0].Title));
    });

    [Fact]
    public void With_a_server_a_note_changed_again_before_every_try_keeps_both_texts() => Ui.Run(ui =>
    {
        var server = new NotesServer();
        var (view, body) = Open(ui, server);

        Type(body, "radish, kale");
        server.KeepsReplying = true;
        UiTest.Call(view, "SaveSoon");
        WaitForSaved(ui, view, server);
        server.KeepsReplying = false;

        // The note keeps every reply, each version seen was kept, and what was typed is a note of its own.
        Assert.Equal(AllReplies, server.Body);
        Assert.Equal(3, server.Made.Count(note => note.Title == Kept));
        Assert.Equal("radish, kale", Assert.Single(server.Made, note => note.Title == "Seed list").Body);
        Assert.Equal(3, server.UpToSaved(int.MaxValue).Count(entry => entry == $"PUT /api/notes/{NoteId}"));
        ui.Pump();
        Assert.Equal("radish, kale", body.Text);
    });

    [Fact]
    public void A_server_from_before_is_checked_first_and_saved_over_as_it_used_to_be() => Ui.Run(ui =>
    {
        var server = new NotesServer { Old = true };
        var (view, body) = Open(ui, server);

        // Changed elsewhere after the editor loaded it, before the save: still noticed and kept, by reading it first.
        Type(body, "radish, kale");
        server.Body = "radish\n\nbeets too";
        server.Requests.Clear();
        view.Leave();

        Assert.Equal("radish, kale", server.Body);
        Assert.Equal(("radish\n\nbeets too", Kept), (Assert.Single(server.Made).Body, server.Made[0].Title));
        Assert.Equal([$"GET /api/notes/{NoteId}", "POST /api/notes", $"GET /api/notes/{NoteId}", $"PUT /api/notes/{NoteId}"], server.UpToSaved(1));

        // Each save reads the note first, since the server never says it checks.
        Type(body, "radish, kale, leeks");
        server.Requests.Clear();
        UiTest.Call(view, "SaveSoon");
        WaitForSaved(ui, view, server);
        Assert.Equal("radish, kale, leeks", server.Body);
        Assert.Single(server.Made);
        Assert.Equal([$"GET /api/notes/{NoteId}", $"PUT /api/notes/{NoteId}"], server.UpToSaved(1));
    });
}
