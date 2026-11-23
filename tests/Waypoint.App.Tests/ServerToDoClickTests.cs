using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Remote;
using Waypoint.Core.Schedule;

namespace Waypoint.App.Tests;

/// <summary>To do in server mode with a server slow to answer: clicks never hold the window, and go in order.</summary>
public sealed class ServerToDoClickTests
{
    /// <summary>The To do page's reads and its done boxes. Changes, and the open items read, can each be held.</summary>
    internal sealed class ToDoServer : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, bool> _items = new() { ["Email advisor"] = false, ["Repot the basil"] = false };
        private readonly Dictionary<string, string?> _preferences = new() { ["todo_setup_card"] = "done" };

        /// <summary>Each This didn't help kept, then each note added to one, as they arrived.</summary>
        public List<string> Feedback { get; } = [];

        /// <summary>Each done box change, as it arrived: the title, then done or not.</summary>
        public List<string> Changes { get; } = [];

        public int ItemReads { get; private set; }

        /// <summary>While set, a done box change can't reach the server.</summary>
        public bool Away { get; set; }

        /// <summary>While unset, changes wait for it.</summary>
        public ManualResetEventSlim ReleaseChanges { get; } = new(initialState: true);

        /// <summary>While unset, reads of the open items wait for it.</summary>
        public ManualResetEventSlim ReleaseReads { get; } = new(initialState: true);

        /// <summary>Set once a change has arrived and is waiting.</summary>
        public ManualResetEventSlim ChangeArrived { get; } = new();

        /// <summary>Something added from another PC.</summary>
        public void Add(string title)
        {
            lock (_gate)
            {
                _items[title] = false;
            }
        }

        public bool IsDone(string title)
        {
            lock (_gate)
            {
                return _items[title];
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            object body;
            if (path == "/api/items/done" && Away)
            {
                throw new HttpRequestException("No route to host");
            }

            if (path == "/api/items/done")
            {
                var sent = JsonSerializer.Deserialize<DoneRequest>(await request.Content!.ReadAsStringAsync(cancellationToken), RemoteJson.Options)!;
                ChangeArrived.Set();
                await Task.Run(() => ReleaseChanges.Wait(TimeSpan.FromSeconds(10)), cancellationToken);
                lock (_gate)
                {
                    Changes.Add($"{sent.Item.Title} {(sent.Done ? "done" : "open")}");
                    _items[sent.Item.Title] = sent.Done;
                }

                body = new { };
            }
            else if (path.StartsWith("/api/feedback", StringComparison.Ordinal))
            {
                var sent = await request.Content!.ReadAsStringAsync(cancellationToken);
                ChangeArrived.Set();
                await Task.Run(() => ReleaseChanges.Wait(TimeSpan.FromSeconds(10)), cancellationToken);
                lock (_gate)
                {
                    if (request.Method == HttpMethod.Post)
                    {
                        Feedback.Add($"kept {JsonSerializer.Deserialize<FeedbackRequest>(sent, RemoteJson.Options)!.Feature}");
                        body = new FeedbackKept(Feedback.Count);
                    }
                    else
                    {
                        Feedback.Add($"{path} {JsonSerializer.Deserialize<TextValue>(sent, RemoteJson.Options)!.Value}");
                        body = new FlagValue(true);
                    }
                }
            }
            else if (path.StartsWith("/api/preferences/", StringComparison.Ordinal))
            {
                var key = Uri.UnescapeDataString(path["/api/preferences/".Length..]);
                var put = request.Method == HttpMethod.Put
                    ? JsonSerializer.Deserialize<PreferenceValue>(await request.Content!.ReadAsStringAsync(cancellationToken), RemoteJson.Options)!
                    : null;
                lock (_gate)
                {
                    if (put is not null)
                    {
                        _preferences[key] = put.Value;
                    }

                    body = new PreferenceValue(_preferences.GetValueOrDefault(key));
                }
            }
            else if (path is "/api/items/open" or "/api/items/finished")
            {
                if (path == "/api/items/open")
                {
                    await Task.Run(() => ReleaseReads.Wait(TimeSpan.FromSeconds(10)), cancellationToken);
                }

                var done = path == "/api/items/finished";
                lock (_gate)
                {
                    ItemReads += done ? 0 : 1;
                    body = _items.Where(pair => pair.Value == done)
                        .Select(pair => new StoredItem(Id(pair.Key), ItemKind.Task, pair.Key, DateOnly.FromDateTime(DateTime.Now).AddDays(1), null, done))
                        .ToList();
                }
            }
            else
            {
                body = path switch
                {
                    "/api/filters" => new[] { new ToDoFilter(1, "Personal", FilterSource.Personal, null, 0) },
                    "/api/money" => new SpendSummary(null, null),
                    "/api/activation/session" => new TextValue(null),
                    "/api/suggestion-habits" => SuggestionHabits.Defaults,
                    _ => Array.Empty<object>(),
                };
            }

            var json = JsonSerializer.Serialize(body, body.GetType(), RemoteJson.Options);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        private static long Id(string title) => title switch
        {
            "Email advisor" => 1,
            "Repot the basil" => 2,
            _ => 3,
        };
    }

    internal static void PumpUntil(UiTest ui, Func<bool> done, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        ui.Pump();
        Assert.True(done(), $"Timed out waiting for {what}");
    }

    private static (TodayView View, PageWork Work) Open(UiTest ui, ToDoServer server) => Open(ui, server, out _);

    private static (TodayView View, PageWork Work) Open(UiTest ui, ToDoServer server, out ServerClient opened)
    {
        var client = new ServerClient(new HttpClient(server) { BaseAddress = new Uri("http://server/") }, "token");
        opened = client;
        var work = new PageWork(client);
        var view = new TodayView { Work = work, FeedbackData = new ServerFeedbackData(client) };
        view.Connect(new ServerToDoData(client), () => { }, _ => { }, _ => { }, () => { });
        ui.Host(view);
        work.Show(() => view.Refresh(null));
        PumpUntil(ui, () => !work.IsBusy && Rows(view).Count == 2, "the rows");
        return (view, work);
    }

    private static List<CheckBox> Rows(TodayView view) =>
        UiTest.Descendants<CheckBox>(UiTest.Named<ScrollViewer>(view, "ItemsScroll")).ToList();

    private static CheckBox Box(TodayView view, string title) =>
        Rows(view).Single(box => AutomationProperties.GetName(box).StartsWith(title, StringComparison.Ordinal));

    /// <summary>A click on a done box, the way it changes for the page: the box's own state, which raises Checked or Unchecked.</summary>
    private static void Click(CheckBox box) => box.IsChecked = box.IsChecked != true;

    [Fact]
    public void A_click_returns_at_once_while_the_server_holds_the_change_and_shows_it_once_saved() => Ui.Run(ui =>
    {
        var server = new ToDoServer();
        var (view, work) = Open(ui, server);
        server.ReleaseChanges.Reset();

        Click(Box(view, "Email advisor"));

        // It came back while the server still held the change.
        Assert.Empty(server.Changes);
        Assert.True(server.ChangeArrived.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(work.ChangesWaiting);
        Assert.True(Box(view, "Email advisor").IsChecked);

        server.ReleaseChanges.Set();
        PumpUntil(ui, () => !work.IsBusy, "the change");
        Assert.Equal(["Email advisor done"], server.Changes);
        Assert.True(Box(view, "Email advisor").IsChecked);
        Assert.False(Box(view, "Repot the basil").IsChecked);
    });

    [Fact]
    public void A_click_while_the_server_is_away_goes_back_and_says_why() => Ui.Run(ui =>
    {
        var server = new ToDoServer();
        var (view, work) = Open(ui, server);
        server.Away = true;

        Click(Box(view, "Email advisor"));
        PumpUntil(ui, () => !work.IsBusy, "the change");

        Assert.False(server.IsDone("Email advisor"));
        Assert.False(Box(view, "Email advisor").IsChecked);
        Assert.Equal(PageMessages.CantChangeYet, UiTest.Named<TextBlock>(UiTest.Named<UndoBar>(view, "Bar"), "Message").Text);
        Assert.Empty(Ui.TakeUnhandled());
    });

    [Fact]
    public void This_didnt_help_and_its_note_return_at_once_while_the_server_holds_them() => Ui.Run(ui =>
    {
        var server = new ToDoServer();
        var (view, work) = Open(ui, server);
        var prompt = UiTest.Named<FeedbackPrompt>(view, "SuggestionFeedback");
        var link = UiTest.Descendants<Button>(prompt).First(button => button.Content as string == CardLabels.DidntHelp);
        server.ReleaseChanges.Reset();

        // A second press while the first is on its way keeps nothing more.
        link.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent, link));
        Assert.True(server.ChangeArrived.Wait(TimeSpan.FromSeconds(5)));
        Assert.Empty(server.Feedback);
        Assert.True(work.ChangesWaiting);
        Assert.False(link.IsVisible);
        link.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent, link));

        server.ReleaseChanges.Set();
        var note = UiTest.Descendants<TextBox>(prompt).Single();
        PumpUntil(ui, () => !work.IsBusy && note.IsVisible, "the line for a note");
        Assert.Equal([$"kept {Feedback.Suggestion}"], server.Feedback);

        server.ChangeArrived.Reset();
        server.ReleaseChanges.Reset();
        note.Text = "Already emailed them";
        var add = UiTest.Descendants<Button>(prompt).First(button => button.Content as string == "Add");
        add.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent, add));

        // It came back while the server still held the note, and the line can't be sent again meanwhile.
        Assert.True(server.ChangeArrived.Wait(TimeSpan.FromSeconds(5)));
        Assert.Single(server.Feedback);
        Assert.False(note.IsVisible);

        server.ReleaseChanges.Set();
        PumpUntil(ui, () => !work.IsBusy && server.Feedback.Count == 2, "the note");
        Assert.Equal([$"kept {Feedback.Suggestion}", "/api/feedback/1/note Already emailed them"], server.Feedback);
        Assert.Contains(UiTest.Descendants<TextBlock>(prompt), block => block.IsVisible && block.Text.StartsWith("Thanks.", StringComparison.Ordinal));
    });

    [Fact]
    public void Two_quick_clicks_reach_the_server_in_the_order_they_were_made() => Ui.Run(ui =>
    {
        var server = new ToDoServer();
        var (view, work) = Open(ui, server);
        server.ReleaseChanges.Reset();

        // Checked, then straight back, before the first is saved. The second must not be taken for the row filling in.
        Click(Box(view, "Email advisor"));
        Click(Box(view, "Email advisor"));
        Click(Box(view, "Repot the basil"));
        server.ReleaseChanges.Set();
        PumpUntil(ui, () => !work.IsBusy, "the changes");

        Assert.Equal(["Email advisor done", "Email advisor open", "Repot the basil done"], server.Changes);
        Assert.False(server.IsDone("Email advisor"));
        Assert.False(Box(view, "Email advisor").IsChecked);
        Assert.True(Box(view, "Repot the basil").IsChecked);
    });

    [Fact]
    public void A_read_from_before_a_click_never_undoes_the_click_on_screen() => Ui.Run(ui =>
    {
        var server = new ToDoServer();
        var (view, work) = Open(ui, server, out var client);

        // Something changed from here elsewhere, so opening the page again draws it from what was kept and reads
        // again, held here. Then the box is clicked.
        client.Send(HttpMethod.Post, "api/elsewhere");
        server.ReleaseReads.Reset();
        server.ReleaseChanges.Reset();
        var reads = server.ItemReads;
        ui.Pump();
        work.Show(() => view.Refresh(null));
        Click(Box(view, "Email advisor"));

        // The read lands first, with the box still open on the server and something new from another PC, so the rows
        // would be built again from it. The page waits for the change before drawing.
        server.Add("Call the dentist");
        server.ReleaseReads.Set();
        PumpUntil(ui, () => server.ItemReads > reads && server.ChangeArrived.IsSet, "the read");
        ui.Pump();
        Assert.True(Box(view, "Email advisor").IsChecked);

        server.ReleaseChanges.Set();
        PumpUntil(ui, () => !work.IsBusy && Rows(view).Count == 3, "the change");
        Assert.True(Box(view, "Email advisor").IsChecked);
        Assert.Equal(["Email advisor done"], server.Changes);
    });

    [Fact]
    public void Closing_waits_for_a_change_still_on_its_way_and_names_one_it_cant_wait_for() => Ui.Run(ui =>
    {
        var server = new ToDoServer();
        var (view, work) = Open(ui, server);
        server.ReleaseChanges.Reset();
        Click(Box(view, "Email advisor"));
        Assert.True(server.ChangeArrived.Wait(TimeSpan.FromSeconds(5)));

        Assert.Equal(["OnItemChecked"], work.Finish(TimeSpan.FromMilliseconds(100)));

        _ = Task.Delay(200).ContinueWith(_ => server.ReleaseChanges.Set());
        Assert.Empty(work.Finish(TimeSpan.FromSeconds(5)));
        Assert.Equal(["Email advisor done"], server.Changes);

        // Once closing, a change is made straight away, so it can't be left behind.
        Click(Box(view, "Repot the basil"));
        Assert.True(server.IsDone("Repot the basil"));
        ui.Pump();
    });

    [Fact]
    public void On_this_pcs_database_a_click_is_saved_before_it_returns() => Ui.Run(ui =>
    {
        ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, "Email advisor", DateOnly.FromDateTime(DateTime.Now).AddDays(1)), DateTime.Now);
        var view = new TodayView();
        Assert.True(view.Work.RunsNow);
        view.Connect(ui.Database, () => { }, _ => { }, _ => { }, () => { });
        ui.Host(view);
        view.Refresh(null);
        ui.Pump();

        Click(Box(view, "Email advisor"));

        Assert.DoesNotContain(ui.Database.GetOpenItems(), item => item.Title == "Email advisor" && !item.Done);
        Assert.False(view.Work.IsBusy);
    });
}
