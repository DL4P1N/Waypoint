using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Notes;
using Waypoint.Core.Remote;

namespace Waypoint.App.Tests;

/// <summary>
/// Studying from notes, and the open note's status and suggestions, in server mode with a server slow to answer:
/// the window never waits on it, a second press meanwhile does nothing, and an answer for a note or page the user
/// has left is dropped.
/// </summary>
public sealed class ServerStudyClickTests
{
    /// <summary>Two notes. Reads of the study folder, of note 7's suggestions, and starting a session can each be held.</summary>
    private sealed class StudyServer : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly Dictionary<long, (string Title, string Body)> _notes = new() { [7] = ("Study: Cell quiz", "1. What holds a plant cell's shape?"), [8] = ("Pond walk", "frogs") };
        private readonly List<string> _asked = [];

        /// <summary>What note 7's suggestions are, as the server has them now.</summary>
        public string? Suggestion { get; set; }

        /// <summary>Note 7 was written as practice questions for an exam on To do.</summary>
        public bool SevenIsStudy { get; set; }

        public ManualResetEventSlim ReleaseStudyFolder { get; } = new(initialState: true);

        public ManualResetEventSlim ReleaseSuggestions { get; } = new(initialState: true);

        public ManualResetEventSlim ReleaseStart { get; } = new(initialState: true);

        /// <summary>Each request but the plain reads of notes and folders, as it arrived.</summary>
        public List<string> Asked
        {
            get
            {
                lock (_gate)
                {
                    return _asked.ToList();
                }
            }
        }

        public int Count(string path) => Asked.Count(asked => asked == path);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var post = request.Method == HttpMethod.Post;
            object body = new { };
            lock (_gate)
            {
                if (path.StartsWith("/api/notes/study", StringComparison.Ordinal) || path.StartsWith("/api/activation", StringComparison.Ordinal) || path == "/api/notes/7/suggestions")
                {
                    _asked.Add(post ? $"POST {path}" : path);
                }

                if (path is "/api/notes/folders" or "/api/notes/spots" or "/api/notes/classes" || path.EndsWith("/sources", StringComparison.Ordinal))
                {
                    body = Array.Empty<object>();
                }
                else if (path.EndsWith("/suggestions", StringComparison.Ordinal))
                {
                    body = path == "/api/notes/7/suggestions" && Suggestion is { } title
                        ? new[] { new NoteSuggestion(1, 7, new SuggestedItem(ItemKind.Task, title, null)) }
                        : Array.Empty<NoteSuggestion>();
                }
                else if (path == "/api/notes/7/generation" && SevenIsStudy)
                {
                    body = new Found<NoteGeneration>(new NoteGeneration(new DateTime(2030, 4, 1, 9, 0, 0), false, 0));
                }
                else if (path == "/api/notes/7/study-exam" && SevenIsStudy)
                {
                    body = new Found<StudyExam>(new StudyExam("Cell quiz", "BIO 101", null, "item:5"));
                }
                else if (path.EndsWith("/generation", StringComparison.Ordinal) || path.EndsWith("/study-exam", StringComparison.Ordinal))
                {
                    body = new Found<object>(null);
                }
                else if (path == "/api/notes/study/folder")
                {
                    body = new Found<long?>(50);
                }
                else if (path == "/api/notes/study/find")
                {
                    body = new Found<long?>(null);
                }
                else if (path == "/api/activation/start")
                {
                    body = new OpReply("Started on Cell quiz. Answer question 1 first.");
                }
                else if (path.StartsWith("/api/notes/", StringComparison.Ordinal) && long.TryParse(path["/api/notes/".Length..], out var id))
                {
                    body = new Found<NoteDetail>(Note(id));
                }
                else if (path is "/api/notes" or "/api/notes/in" || path.StartsWith("/api/notes/search", StringComparison.Ordinal))
                {
                    body = _notes.Keys.Order().Select(Note).ToList();
                }
                else if (path.StartsWith("/api/preferences/", StringComparison.Ordinal))
                {
                    body = new PreferenceValue(null);
                }
            }

            var hold = path switch
            {
                "/api/notes/study/folder" => ReleaseStudyFolder,
                "/api/notes/7/suggestions" => ReleaseSuggestions,
                "/api/activation/start" => ReleaseStart,
                _ => null,
            };
            if (hold is not null)
            {
                await Task.Run(() => hold.Wait(TimeSpan.FromSeconds(10)), cancellationToken);
            }

            var json = JsonSerializer.Serialize(body, body.GetType(), RemoteJson.Options);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        private NoteDetail Note(long id) => new(id, _notes[id].Title, _notes[id].Body, null, new DateTime(2030, 4, 1, 9, 0, 0));
    }

    private sealed class NoAudio : INoteAudio
    {
        public IReadOnlyList<NoteRecording> For(long noteId) => [];

        public Task<NoteAudioResult> KeepAsync(long noteId, CancellationToken cancellationToken = default) => Task.FromResult(new NoteAudioResult(false, ""));

        public NoteAudioResult Delete(long noteId) => new(false, "");
    }

    private static (NotesView View, PageWork Work, MarkdownBox Body, ServerClient Client) OpenNotes(UiTest ui, StudyServer server)
    {
        var client = new ServerClient(new HttpClient(server) { BaseAddress = new Uri("http://server/") }, "token");
        var work = new PageWork(client);
        var view = new NotesView { Work = work };
        view.Connect(new ServerNotesData(client));
        view.ConnectStudy(new NoAudio());
        ui.Host(view);
        work.Show(() => view.Refresh(force: true));
        var body = UiTest.Named<MarkdownBox>(view, "BodyBox");
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && UiTest.Named<ListBox>(view, "NotesList").Items.Count == 2, "the notes");
        Pick(view, 7);
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && body.Text.Contains("plant cell", StringComparison.Ordinal), "the note to open");
        return (view, work, body, client);
    }

    private static void Pick(NotesView view, long id)
    {
        var list = UiTest.Named<ListBox>(view, "NotesList");
        list.SelectedItem = list.Items.Cast<object>().Single(row => UiTest.Prop<long>(row, "Id") == id);
    }

    private static string BarText(NotesView view) => UiTest.Named<TextBlock>(UiTest.Named<UndoBar>(view, "Bar"), "Message").Text;

    [Fact]
    public void Studying_for_an_exam_returns_at_once_a_second_press_does_nothing_and_a_late_answer_after_another_note_opens_is_dropped() => Ui.Run(ui =>
    {
        var server = new StudyServer();
        var (view, work, body, _) = OpenNotes(ui, server);
        var exam = new StoredItem(5, ItemKind.Exam, "Cell quiz", null, null, false);
        var before = BarText(view);

        // Where to study from is held. Both presses came back at once, and the second asked nothing more.
        server.ReleaseStudyFolder.Reset();
        view.StudyForExam(exam);
        view.StudyForExam(exam);
        ServerToDoClickTests.PumpUntil(ui, () => server.Count("/api/notes/study/folder") == 1, "where to study from to be asked");
        Assert.True(work.IsBusy);

        // The user opens another note before the answer comes.
        Pick(view, 8);
        server.ReleaseStudyFolder.Set();
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && body.Text == "frogs", "the other note");
        ui.Pump();

        // Nothing came of the answer: no study note was looked for or asked to be written.
        Assert.Equal(["/api/notes/study/folder"], server.Asked.Where(asked => !asked.EndsWith("/suggestions", StringComparison.Ordinal)));
        Assert.Equal(before, BarText(view));
        Assert.Equal("frogs", body.Text);
    });

    [Fact]
    public void While_a_study_press_waits_on_the_server_the_status_button_of_an_ordinary_note_still_writes_its_notes() => Ui.Run(ui =>
    {
        var server = new StudyServer();
        var (view, work, body, _) = OpenNotes(ui, server);

        // A study press is held on the server.
        server.ReleaseStudyFolder.Reset();
        view.StudyForExam(new StoredItem(5, ItemKind.Exam, "Cell quiz", null, null, false));
        ServerToDoClickTests.PumpUntil(ui, () => server.Count("/api/notes/study/folder") == 1, "where to study from to be asked");

        // Note 7 isn't a study note, so its Write notes or Try again isn't taken for a second study press.
        var handled = (Task<bool>)UiTest.Call(view, "StudyStatusClicked", 7L)!;
        Assert.True(handled.IsCompleted);
        Assert.False(handled.Result);

        // Another note opens, so the study answer is dropped once it comes.
        Pick(view, 8);
        server.ReleaseStudyFolder.Set();
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && body.Text == "frogs", "the other note");
    });

    [Fact]
    public void Starting_a_study_session_returns_at_once_and_a_second_press_while_it_is_on_its_way_starts_nothing_more() => Ui.Run(ui =>
    {
        var server = new StudyServer { SevenIsStudy = true };
        var (view, work, _, _) = OpenNotes(ui, server);
        var start = UiTest.Named<Button>(view, "StudyStartButton");
        ServerToDoClickTests.PumpUntil(ui, () => start.Visibility == Visibility.Visible, "the start button");
        var replies = new List<string>();
        view.Replied += replies.Add;

        server.ReleaseStart.Reset();
        UiTest.Call(view, "OnStudyStartClicked", start, new RoutedEventArgs());
        ServerToDoClickTests.PumpUntil(ui, () => server.Count("POST /api/activation/start") == 1, "the session to be started");
        UiTest.Call(view, "OnStudyStartClicked", start, new RoutedEventArgs());
        ui.Pump();
        Assert.True(work.ChangesWaiting);
        Assert.Empty(replies);

        server.ReleaseStart.Set();
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && replies.Count == 1, "the reply");
        ui.Pump();
        Assert.Equal(1, server.Count("POST /api/activation/start"));
        Assert.Equal(["Started on Cell quiz. Answer question 1 first."], replies);
        Assert.Equal("Started on Cell quiz. Answer question 1 first.", BarText(view));
    });

    [Fact]
    public void A_note_opened_from_elsewhere_shows_its_suggestions_once_read_and_a_late_answer_for_a_note_no_longer_open_is_ignored() => Ui.Run(ui =>
    {
        var server = new StudyServer();
        var (view, work, _, client) = OpenNotes(ui, server);
        var suggestions = UiTest.Named<ItemsControl>(view, "SuggestionsList");
        var panel = UiTest.Named<FrameworkElement>(view, "SuggestionsPanel");
        List<string> Shown() => suggestions.Items.Cast<object>().Select(row => UiTest.Prop<string>(row, "Title")!).ToList();
        Assert.Empty(Shown());
        var seven = new NoteDetail(7, "Study: Cell quiz", "1. What holds a plant cell's shape?", null, new DateTime(2030, 4, 1, 9, 0, 0));

        // A suggestion found on the server, and a change from here since, so what was kept is read again.
        server.Suggestion = "Review the cell wall";
        client.Send(HttpMethod.Post, "api/elsewhere");
        server.ReleaseSuggestions.Reset();
        var asked = server.Count("/api/notes/7/suggestions");

        // Opened as it is after an await, outside a drawing of the page: it came back while the read was held.
        UiTest.Call(view, "Open", seven, false);
        ServerToDoClickTests.PumpUntil(ui, () => server.Count("/api/notes/7/suggestions") == asked + 1, "the suggestions to be read");
        Assert.True(work.IsBusy);
        Assert.Empty(Shown());

        server.ReleaseSuggestions.Set();
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && Shown().Count == 1, "the suggestion");
        Assert.Equal(["Review the cell wall"], Shown());
        Assert.Equal(Visibility.Visible, panel.Visibility);

        // Asked again and held, then another note opens before the answer comes.
        server.Suggestion = "Draw a plant cell";
        client.Send(HttpMethod.Post, "api/elsewhere");
        server.ReleaseSuggestions.Reset();
        asked = server.Count("/api/notes/7/suggestions");
        UiTest.Call(view, "Open", seven, false);
        ServerToDoClickTests.PumpUntil(ui, () => server.Count("/api/notes/7/suggestions") == asked + 1, "the suggestions to be read again");
        Pick(view, 8);
        server.ReleaseSuggestions.Set();
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && UiTest.Named<MarkdownBox>(view, "BodyBox").Text == "frogs", "the other note");
        ui.Pump();

        // The other note shows its own, none, rather than the late answer for note 7.
        Assert.Empty(Shown());
        Assert.Equal(Visibility.Collapsed, panel.Visibility);
    });
}
