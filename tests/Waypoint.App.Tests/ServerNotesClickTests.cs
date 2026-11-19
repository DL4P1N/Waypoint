using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Files;
using Waypoint.Core.Notes;
using Waypoint.Core.Remote;

namespace Waypoint.App.Tests;

/// <summary>Notes and lists in server mode with a server slow to answer: clicks never hold the window, and go in order.</summary>
public sealed class ServerNotesClickTests
{
    /// <summary>Two notes and the Shopping list. Saves, reads of a note, and list changes can each be held.</summary>
    private sealed class NotesServer : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly Dictionary<long, (string Title, string Body)> _notes = new() { [7] = ("Seed list", "radish"), [8] = ("Trip plan", "tent") };
        private readonly List<ListEntry> _entries =
        [
            new(1, "Shopping", "oat milk", false, new DateTime(2030, 4, 1, 9, 0, 0)),
            new(2, "Shopping", "eggs", false, new DateTime(2030, 4, 1, 9, 0, 0)),
        ];

        /// <summary>Each save and list change, as it arrived.</summary>
        public List<string> Changes { get; } = [];

        public int NotesMade { get; private set; }

        public ManualResetEventSlim ReleaseChanges { get; } = new(initialState: true);

        public ManualResetEventSlim ReleaseReads { get; } = new(initialState: true);

        public ManualResetEventSlim ChangeArrived { get; } = new();

        /// <summary>While unset, saves of a note that's there wait for it, after any hold on changes.</summary>
        public ManualResetEventSlim ReleaseSaves { get; } = new(initialState: true);

        /// <summary>Set once a save of a note that's there has arrived.</summary>
        public ManualResetEventSlim SaveArrived { get; } = new();

        /// <summary>While unset, reads of the lists wait for it.</summary>
        public ManualResetEventSlim ReleaseListReads { get; } = new(initialState: true);

        /// <summary>Set once a read of the lists has arrived.</summary>
        public ManualResetEventSlim ListReadArrived { get; } = new();

        /// <summary>Something added to Shopping from another PC.</summary>
        public void AddElsewhere(ListEntry entry)
        {
            lock (_gate)
            {
                _entries.Add(entry);
            }
        }

        /// <summary>A class is on, so a new note started from All goes in its folder, titled with it.</summary>
        public bool ClassOn { get; set; }

        /// <summary>The next note save is turned down, as a server does with a request it won't take.</summary>
        public bool RefuseNextSave { get; set; }

        public void ChangeElsewhere(long id, string body)
        {
            lock (_gate)
            {
                _notes[id] = (_notes[id].Title, body);
            }
        }

        public string Title(long id)
        {
            lock (_gate)
            {
                return _notes[id].Title;
            }
        }

        public string Body(long id)
        {
            lock (_gate)
            {
                return _notes[id].Body;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var sent = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            object body = path is "/api/notes/folders" or "/api/notes/spots" or "/api/notes/classes"
                || path.EndsWith("/sources", StringComparison.Ordinal) || path.EndsWith("/suggestions", StringComparison.Ordinal)
                ? Array.Empty<object>()
                : new { };
            if (request.Method != HttpMethod.Get && (path.StartsWith("/api/notes/", StringComparison.Ordinal) || path.StartsWith("/api/lists/entries", StringComparison.Ordinal)))
            {
                ChangeArrived.Set();
                await Task.Run(() => ReleaseChanges.Wait(TimeSpan.FromSeconds(10)), cancellationToken);
            }

            if (request.Method == HttpMethod.Put && path.StartsWith("/api/notes/", StringComparison.Ordinal))
            {
                SaveArrived.Set();
                await Task.Run(() => ReleaseSaves.Wait(TimeSpan.FromSeconds(10)), cancellationToken);
            }

            lock (_gate)
            {
                if (request.Method == HttpMethod.Put && RefuseNextSave)
                {
                    RefuseNextSave = false;
                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                }

                if (path == "/api/notes/7/files")
                {
                    var file = JsonSerializer.Deserialize<FileText>(sent, RemoteJson.Options)!;
                    Changes.Add($"attach 7 {file.Name}");
                    body = new StartedNote(7, "Seed list", null, null);
                }
                else if (path == "/api/notes/7/generation")
                {
                    body = new Found<NoteGeneration>(new NoteGeneration(null, false, Changes.Count(change => change.StartsWith("attach", StringComparison.Ordinal))));
                }
                else if (path.StartsWith("/api/notes/", StringComparison.Ordinal) && long.TryParse(path["/api/notes/".Length..], out var id))
                {
                    if (request.Method == HttpMethod.Put)
                    {
                        var save = JsonSerializer.Deserialize<SaveNoteRequest>(sent, RemoteJson.Options)!;
                        Changes.Add($"save {id} {save.Body}");
                        _notes[id] = (save.Title, save.Body);
                        body = new FlagValue(true);
                    }
                    else
                    {
                        body = new Found<NoteDetail>(Note(id));
                    }
                }
                else if (path == "/api/notes" && request.Method == HttpMethod.Post)
                {
                    NotesMade++;
                    var made = JsonSerializer.Deserialize<NewNoteRequest>(sent, RemoteJson.Options)!;
                    Changes.Add($"make {100 + NotesMade} {made.Title}: {made.Body} in {made.FolderId}");
                    _notes[100 + NotesMade] = (made.Title, made.Body);
                    body = new Found<long>(100 + NotesMade);
                }
                else if (path == "/api/notes/place")
                {
                    body = ClassOn ? new NotePlacement(null, "Biology", "Biology 1 Apr") : new NotePlacement(null);
                }
                else if (path == "/api/notes/folder-for")
                {
                    Changes.Add("class folder");
                    body = new Found<long?>(50);
                }
                else if (path is "/api/notes" or "/api/notes/in" || path.StartsWith("/api/notes/search", StringComparison.Ordinal))
                {
                    body = _notes.Keys.Order().Select(Note).ToList();
                }
                else if (path == "/api/lists/entries/done")
                {
                    var done = JsonSerializer.Deserialize<EntryDoneRequest>(sent, RemoteJson.Options)!;
                    Changes.Add($"{done.Entry.Text} {(done.Done ? "done" : "open")}");
                    var at = _entries.FindIndex(entry => entry.Id == done.Entry.Id);
                    _entries[at] = _entries[at] with { Done = done.Done };
                    body = new { };
                }
                else if (path.StartsWith("/api/preferences/", StringComparison.Ordinal))
                {
                    body = new PreferenceValue(null);
                }
                else if (path == "/api/lists")
                {
                    body = new[] { new ListOverview("Shopping", _entries.ToList()) };
                }
            }

            if (request.Method == HttpMethod.Get && path == "/api/lists")
            {
                ListReadArrived.Set();
                await Task.Run(() => ReleaseListReads.Wait(TimeSpan.FromSeconds(10)), cancellationToken);
            }

            if (request.Method == HttpMethod.Get && path is "/api/notes/7" or "/api/notes/8")
            {
                await Task.Run(() => ReleaseReads.Wait(TimeSpan.FromSeconds(10)), cancellationToken);
            }

            var json = JsonSerializer.Serialize(body, body.GetType(), RemoteJson.Options);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        private NoteDetail Note(long id) => new(id, _notes[id].Title, _notes[id].Body, null, new DateTime(2030, 4, 1, 9, 0, 0));
    }

    private static ServerClient Client(NotesServer server) =>
        new(new HttpClient(server) { BaseAddress = new Uri("http://server/") }, "token");

    private static void Type(MarkdownBox body, string text)
    {
        body.SelectAll();
        body.Selection.Text = text;
        body.CaretIndex = body.Text.Length;
    }

    private static (NotesView View, PageWork Work, MarkdownBox Body, ServerClient Client) OpenNotes(UiTest ui, NotesServer server, bool withLists = false)
    {
        var client = Client(server);
        var work = new PageWork(client);
        var view = new NotesView { Work = work };
        view.Connect(new ServerNotesData(client), withLists ? new ServerListsData(client) : null);
        ui.Host(view);
        work.Show(() => view.Refresh(force: true));
        var body = UiTest.Named<MarkdownBox>(view, "BodyBox");
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && Rows(view).Count == 2, "the notes");
        Pick(view, 7);
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && body.Text == "radish", "the note to open");
        return (view, work, body, client);
    }

    private static List<object> Rows(NotesView view) => UiTest.Named<ListBox>(view, "NotesList").Items.Cast<object>().ToList();

    private static void Pick(NotesView view, long id)
    {
        var list = UiTest.Named<ListBox>(view, "NotesList");
        list.SelectedItem = list.Items.Cast<object>().Single(row => UiTest.Prop<long>(row, "Id") == id);
    }

    [Fact]
    public void Picking_another_note_returns_at_once_while_the_last_ones_save_is_held_and_it_saves_once() => Ui.Run(ui =>
    {
        var server = new NotesServer();
        var (view, work, body, _) = OpenNotes(ui, server);

        // The save once typing stops is held. Picking another note saves the same text, which is already on its way,
        // so it isn't sent twice.
        Type(body, "radish, kale");
        server.ChangeArrived.Reset();
        server.ReleaseChanges.Reset();
        UiTest.Call(view, "SaveSoon");
        Assert.True(server.ChangeArrived.Wait(TimeSpan.FromSeconds(5)));
        Pick(view, 8);

        // It came back while the server still held the save.
        Assert.Empty(server.Changes);

        server.ReleaseChanges.Set();
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && body.Text == "tent", "the other note");

        Assert.Equal(["save 7 radish, kale"], server.Changes);
        Assert.Equal("radish, kale", server.Body(7));
        Assert.Equal(0, server.NotesMade);
    });

    [Fact]
    public void A_new_notes_first_save_and_its_class_folder_return_at_once_and_the_note_is_made_once() => Ui.Run(ui =>
    {
        var server = new NotesServer { ClassOn = true };
        var (view, work, body, _) = OpenNotes(ui, server);
        server.ChangeArrived.Reset();
        server.ReleaseChanges.Reset();

        // Where it goes is read while the first words are typed, then the first save places it and makes it, and
        // more typed meanwhile is saved to it after. Each came back while the server still held the first.
        UiTest.Call(view, "NewNote");
        Assert.True(server.ChangeArrived.Wait(TimeSpan.FromSeconds(5)));
        Type(body, "cell walls");
        UiTest.Call(view, "SaveSoon");
        Type(body, "cell walls, mitochondria");
        UiTest.Call(view, "SaveSoon");
        Assert.Empty(server.Changes);
        Assert.True(work.ChangesWaiting);
        Assert.Equal("cell walls, mitochondria", body.Text);

        server.ReleaseChanges.Set();
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && server.Changes.Count == 3 && UiTest.Named<TextBlock>(view, "SavedLabel").Text == "Saved", "the saves");
        Assert.Equal(["class folder", "make 101 Biology 1 Apr: cell walls in 50", "save 101 cell walls, mitochondria"], server.Changes);
        Assert.Equal(1, server.NotesMade);
        Assert.Equal("Biology 1 Apr", server.Title(101));
        Assert.Equal("Biology 1 Apr", UiTest.Named<TextBox>(view, "TitleBox").Text);
        Assert.Equal("cell walls, mitochondria", body.Text);

        // Typed on, it goes on as that note.
        Type(body, "cell walls, mitochondria, ribosomes");
        UiTest.Call(view, "SaveSoon");
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && server.Changes.Count == 4, "the next save");
        Assert.Equal("save 101 cell walls, mitochondria, ribosomes", server.Changes[^1]);
        Assert.Equal(1, server.NotesMade);
    });

    [Fact]
    public void A_class_note_opened_again_while_a_later_save_is_held_keeps_its_class_title() => Ui.Run(ui =>
    {
        var server = new NotesServer { ClassOn = true };
        var (view, work, body, _) = OpenNotes(ui, server);
        var title = UiTest.Named<TextBox>(view, "TitleBox");

        // Made with no title typed, it's given the class title. More is typed while that's held, and its save goes
        // after it and is held in turn.
        server.ReleaseChanges.Reset();
        server.ReleaseSaves.Reset();
        UiTest.Call(view, "NewNote");
        Type(body, "cell walls");
        UiTest.Call(view, "SaveSoon");
        Type(body, "cell walls, mitochondria");
        UiTest.Call(view, "SaveSoon");
        server.ReleaseChanges.Set();
        ServerToDoClickTests.PumpUntil(ui, () => server.SaveArrived.IsSet && title.Text == "Biology 1 Apr", "the note to be made");
        Assert.Equal(1, server.NotesMade);

        // Opened again from a read of it while the later save is still held, as a page drawn again does.
        var read = new NoteDetail(101, "Biology 1 Apr", "cell walls", null, new DateTime(2030, 4, 1, 9, 0, 0));
        UiTest.Call(view, "Open", UiTest.Call(view, "WithPendingSave", read), false);
        Assert.Equal("cell walls, mitochondria", body.Text);
        Assert.Equal("Biology 1 Apr", title.Text);

        server.ReleaseSaves.Set();
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && server.Changes.Count >= 3, "the held save");
        Assert.Equal(["class folder", "make 101 Biology 1 Apr: cell walls in 50", "save 101 cell walls, mitochondria"], server.Changes);
        Assert.Equal("Biology 1 Apr", title.Text);

        // Typed on, the title it saves is still the class title.
        Type(body, "cell walls, mitochondria, ribosomes");
        UiTest.Call(view, "SaveSoon");
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && server.Changes.Count == 4, "the next save");
        Assert.Equal("save 101 cell walls, mitochondria, ribosomes", server.Changes[^1]);
        Assert.Equal("Biology 1 Apr", server.Title(101));
        Assert.Equal("Biology 1 Apr", title.Text);
    });

    [Fact]
    public void Adding_a_file_to_a_note_returns_at_once_while_the_server_holds_it_and_says_what_to_do_next() => Ui.Run(ui =>
    {
        var server = new NotesServer();
        var (view, work, body, _) = OpenNotes(ui, server);
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid():N}.txt");
        System.IO.File.WriteAllText(file, "Sow radish in early spring.");
        try
        {
            server.ChangeArrived.Reset();
            server.ReleaseChanges.Reset();
            UiTest.Call(view, "AttachFiles", 7L, new List<string> { file });

            // The window carries on while the server holds the file.
            ServerToDoClickTests.PumpUntil(ui, () => server.ChangeArrived.IsSet, "the file to be sent");
            ui.Pump();
            Assert.Empty(server.Changes);
            Assert.True(work.ChangesWaiting);

            server.ReleaseChanges.Set();
            var message = UiTest.Named<TextBlock>(UiTest.Named<UndoBar>(view, "Bar"), "Message");
            ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && message.Text.Contains("Choose Write notes", StringComparison.Ordinal), "what to do next");
            Assert.Equal([$"attach 7 {System.IO.Path.GetFileName(file)}"], server.Changes);
            Assert.Equal("radish", body.Text);
        }
        finally
        {
            System.IO.File.Delete(file);
        }
    });

    [Fact]
    public void Find_anywhere_jumping_to_a_list_entry_returns_at_once_and_shows_the_entry_once_read() => Ui.Run(ui =>
    {
        var server = new NotesServer();
        var (view, work, _, client) = OpenNotes(ui, server, withLists: true);

        // Added from another PC, so it isn't in what was kept, and the lists have to be read again to show it.
        var chia = new ListEntry(3, "Shopping", "chia seeds", false, new DateTime(2030, 4, 2, 9, 0, 0));
        server.AddElsewhere(chia);
        client.Send(HttpMethod.Post, "api/elsewhere");
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy, "the page to settle");
        server.ListReadArrived.Reset();
        server.ReleaseListReads.Reset();

        view.RevealListEntry(chia);

        // It came back while the server still held the read.
        Assert.True(server.ListReadArrived.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(work.IsBusy);
        var lists = UiTest.Named<ListsView>(view, "ListsPanel");
        var entries = UiTest.Named<ItemsControl>(lists, "Entries");
        bool Shown() => entries.Items.Cast<object>().Any(row => UiTest.Prop<ListEntry>(row, "Entry")!.Id == chia.Id);
        Assert.False(Shown());

        server.ReleaseListReads.Set();
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && Shown(), "the entry");
        Assert.Equal("Shopping", lists.Selected);
    });

    [Fact]
    public void Saves_typed_while_one_is_held_reach_the_server_in_order() => Ui.Run(ui =>
    {
        var server = new NotesServer();
        var (view, work, body, _) = OpenNotes(ui, server);

        server.ChangeArrived.Reset();
        server.ReleaseChanges.Reset();
        Type(body, "radish, kale");
        UiTest.Call(view, "SaveSoon");
        Type(body, "radish, kale, beets");
        UiTest.Call(view, "SaveSoon");

        // Both came back while the server still held the first save.
        Assert.Empty(server.Changes);

        server.ReleaseChanges.Set();
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && UiTest.Named<TextBlock>(view, "SavedLabel").Text == "Saved", "the saves");

        // The second only goes over what the first saved, so nothing is taken for a change made elsewhere.
        Assert.Equal(["save 7 radish, kale", "save 7 radish, kale, beets"], server.Changes);
        Assert.Equal(0, server.NotesMade);
        Assert.Equal("radish, kale, beets", body.Text);
    });

    [Fact]
    public void A_read_that_lands_late_never_replaces_what_is_being_typed() => Ui.Run(ui =>
    {
        var server = new NotesServer();
        var (view, work, body, client) = OpenNotes(ui, server);

        // Changed on another PC. The page is drawn again from what was kept, and read again, held here.
        client.Send(HttpMethod.Post, "api/elsewhere");
        server.ChangeElsewhere(7, "radish from the phone");
        server.ReleaseReads.Reset();
        ui.Pump();
        work.Show(() => view.Refresh(), () => view.Refresh());
        Type(body, "radish, typed here");

        server.ReleaseReads.Set();
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy, "the read");
        Assert.Equal("radish, typed here", body.Text);

        // Saved over the other version, which is kept as its own note, so neither is lost.
        view.Leave();
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy, "the save");
        Assert.Equal("radish, typed here", server.Body(7));
        Assert.Equal(1, server.NotesMade);
        Assert.Equal("radish, typed here", body.Text);
    });

    [Fact]
    public void Closing_waits_for_a_note_save_still_on_its_way() => Ui.Run(ui =>
    {
        var server = new NotesServer();
        var (view, work, body, _) = OpenNotes(ui, server);

        Type(body, "radish, kale");
        server.ChangeArrived.Reset();
        server.ReleaseChanges.Reset();
        UiTest.Call(view, "SaveSoon");
        Assert.True(server.ChangeArrived.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(["Saving a note"], work.Finish(TimeSpan.FromMilliseconds(100)));

        _ = Task.Delay(200).ContinueWith(_ => server.ReleaseChanges.Set());
        Assert.Empty(work.Finish(TimeSpan.FromSeconds(5)));

        // What closing saves after that is the same text, so it isn't sent again.
        view.Leave();
        Assert.Equal(["save 7 radish, kale"], server.Changes);
        ui.Pump();
    });

    [Fact]
    public void List_clicks_return_at_once_and_reach_the_server_in_order() => Ui.Run(ui =>
    {
        var server = new NotesServer();
        var client = Client(server);
        var work = new PageWork(client);
        var view = new ListsView { Work = work };
        view.Connect(new ServerListsData(client));
        ui.Host(view);
        work.Show(() => view.Open("Shopping", ""));
        var entries = UiTest.Named<ItemsControl>(view, "Entries");
        CheckBox Box(string text) => UiTest.Descendants<CheckBox>(entries).Single(box => UiTest.Prop<string>(box.DataContext, "Text") == text);
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && UiTest.Descendants<CheckBox>(entries).Count() == 2, "the entries");

        server.ChangeArrived.Reset();
        server.ReleaseChanges.Reset();
        Box("eggs").IsChecked = true;
        Box("eggs").IsChecked = false;
        Box("oat milk").IsChecked = true;

        // All three came back while the server still held the first change.
        Assert.Empty(server.Changes);
        Assert.True(server.ChangeArrived.Wait(TimeSpan.FromSeconds(5)));

        server.ReleaseChanges.Set();
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && server.Changes.Count == 3, "the changes");
        Assert.Equal(["eggs done", "eggs open", "oat milk done"], server.Changes);
        Assert.False(Box("eggs").IsChecked);
        Assert.True(Box("oat milk").IsChecked);
    });

    [Fact]
    public void Switching_back_to_a_note_whose_save_is_held_shows_what_was_typed_and_loses_nothing() => Ui.Run(ui =>
    {
        var server = new NotesServer();
        var (view, work, body, _) = OpenNotes(ui, server);

        Type(body, "radish, kale");
        server.ChangeArrived.Reset();
        server.ReleaseChanges.Reset();
        UiTest.Call(view, "SaveSoon");
        Assert.True(server.ChangeArrived.Wait(TimeSpan.FromSeconds(5)));

        // Away to the other note and straight back while the save is still held.
        Pick(view, 8);
        ui.Pump();
        Pick(view, 7);
        ui.Pump();
        Assert.Equal("radish, kale", body.Text);

        // Typing goes on from what the editor shows, and saves over what the held save sends.
        Type(body, body.Text + ", beets");
        UiTest.Call(view, "SaveSoon");

        server.ReleaseChanges.Set();
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && server.Changes.Count == 2, "the saves");
        Assert.Equal(["save 7 radish, kale", "save 7 radish, kale, beets"], server.Changes);
        Assert.Equal("radish, kale, beets", server.Body(7));
        Assert.Equal(0, server.NotesMade);
        Assert.Equal("radish, kale, beets", body.Text);
    });

    [Fact]
    public void Closing_after_a_note_save_the_server_turned_down_still_saves_what_was_typed() => Ui.Run(ui =>
    {
        var server = new NotesServer();
        var (view, work, body, _) = OpenNotes(ui, server);

        Type(body, "radish, kale");
        server.ChangeArrived.Reset();
        server.ReleaseChanges.Reset();
        server.RefuseNextSave = true;
        UiTest.Call(view, "SaveSoon");
        Assert.True(server.ChangeArrived.Wait(TimeSpan.FromSeconds(5)));
        server.ReleaseChanges.Set();
        Assert.Empty(work.Finish(TimeSpan.FromSeconds(5)));

        // The turned down save doesn't stop closing, and what it sent is saved again.
        view.Leave();
        Assert.Equal(["save 7 radish, kale"], server.Changes);
        Assert.Equal("radish, kale", server.Body(7));
        ui.Pump();
    });

    [Fact]
    public void A_click_on_a_list_row_drawn_again_while_the_last_click_is_on_its_way_is_its_own_click() => Ui.Run(ui =>
    {
        var server = new NotesServer();
        var client = Client(server);
        var work = new PageWork(client);
        var view = new ListsView { Work = work };
        view.Connect(new ServerListsData(client));
        ui.Host(view);
        work.Show(() => view.Open("Shopping", ""));
        var entries = UiTest.Named<ItemsControl>(view, "Entries");
        CheckBox Box(string text) => UiTest.Descendants<CheckBox>(entries).Single(box => UiTest.Prop<string>(box.DataContext, "Text") == text);
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && UiTest.Descendants<CheckBox>(entries).Count() == 2, "the entries");
        Box("oat milk").IsChecked = true;
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && server.Changes.Count == 1, "the first change");

        // Unchecked while the server is held. Meanwhile the list is drawn again from what was kept, where it's still
        // done, and that new row is unchecked too. It goes by what it shows, so that click isn't dropped.
        server.ReleaseChanges.Reset();
        server.ChangeArrived.Reset();
        Box("oat milk").IsChecked = false;
        Assert.True(server.ChangeArrived.Wait(TimeSpan.FromSeconds(5)));
        work.Show(() => view.Refresh(force: true), () => { });
        ui.Pump();
        Assert.True(Box("oat milk").IsChecked);
        Box("oat milk").IsChecked = false;

        server.ReleaseChanges.Set();
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && server.Changes.Count == 3, "the click on the new row");
        Assert.Equal(["oat milk done", "oat milk open", "oat milk open"], server.Changes);
        Assert.False(Box("oat milk").IsChecked);
    });
}
