using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Remote;
using Waypoint.Core.Schedule;

namespace Waypoint.App.Tests;

/// <summary>Settings in server mode with a server slow to answer: saving a field never holds the window, and saves go in order.</summary>
public sealed class ServerSettingsClickTests
{
    private sealed class SettingsServer : HttpMessageHandler
    {
        private readonly object _gate = new();
        private decimal _rate = 20.00m;
        private int _commute = 15;

        public List<string> Saves { get; } = [];

        public ManualResetEventSlim Release { get; } = new(initialState: true);

        public ManualResetEventSlim SaveArrived { get; } = new();

        /// <summary>While unset, reads of the Discord form wait for it.</summary>
        public ManualResetEventSlim ReleaseDiscordReads { get; } = new(initialState: true);

        public ManualResetEventSlim DiscordReadArrived { get; } = new();

        private bool _hasToken;

        private List<FeedbackEntry> _feedback =
        [
            new(1, new DateTime(2030, 4, 1, 9, 0, 0), Feedback.Suggestion, null, null, "picked an old one"),
            new(2, new DateTime(2030, 4, 1, 9, 5, 0), Feedback.Row, null, null, null),
        ];

        private ClassesForm _classes = new(null, null,
        [
            new("Art 101", "", [DayOfWeek.Monday], new TimeOnly(9, 0), new TimeOnly(10, 0)),
            new("Bio 102", "", [DayOfWeek.Tuesday], new TimeOnly(11, 0), new TimeOnly(12, 0)),
            new("Chem 103", "", [DayOfWeek.Friday], new TimeOnly(13, 0), new TimeOnly(14, 0)),
        ]);

        /// <summary>While set, each read of the classes waits until <see cref="LetClassReadsGo"/>.</summary>
        public bool HoldClassReads { get; set; }

        public int ClassReadsWaiting => Volatile.Read(ref _classReadsWaiting);

        private int _classReadsWaiting;

        private readonly SemaphoreSlim _classReads = new(0);

        public void LetOneClassReadGo() => _classReads.Release();

        public void LetClassReadsGo()
        {
            HoldClassReads = false;
            _classReads.Release(100);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/settings/classes")
            {
                if (request.Method == HttpMethod.Put)
                {
                    var sent = JsonSerializer.Deserialize<ClassesForm>(await request.Content!.ReadAsStringAsync(cancellationToken), RemoteJson.Options)!;
                    SaveArrived.Set();
                    await Task.Run(() => Release.Wait(TimeSpan.FromMinutes(1)), cancellationToken);
                    lock (_gate)
                    {
                        _classes = sent;
                        Saves.Add($"classes {string.Join(", ", sent.Classes.Select(meeting => meeting.Course))}");
                    }

                    return Json(new TextValue("Saved. Waypoint uses these classes straight away."));
                }

                if (HoldClassReads)
                {
                    Interlocked.Increment(ref _classReadsWaiting);
                    await _classReads.WaitAsync(TimeSpan.FromMinutes(1), cancellationToken);
                    Interlocked.Decrement(ref _classReadsWaiting);
                }

                lock (_gate)
                {
                    return Json(_classes);
                }
            }

            if (path == "/api/settings/discord")
            {
                if (request.Method == HttpMethod.Put)
                {
                    lock (_gate)
                    {
                        _hasToken = true;
                        Saves.Add("discord");
                    }

                    return Json(new TextValue("Saved. Discord is starting."));
                }

                DiscordReadArrived.Set();
                await Task.Run(() => ReleaseDiscordReads.Wait(TimeSpan.FromMinutes(1)), cancellationToken);
                lock (_gate)
                {
                    return Json(new DiscordForm("123", null, null, null, _hasToken));
                }
            }

            if (path == "/api/feedback")
            {
                if (request.Method == HttpMethod.Delete)
                {
                    SaveArrived.Set();
                    await Task.Run(() => Release.Wait(TimeSpan.FromMinutes(1)), cancellationToken);
                    lock (_gate)
                    {
                        Saves.Add($"clear {_feedback.Count}");
                        var count = _feedback.Count;
                        _feedback = [];
                        return Json(new FeedbackKept(count));
                    }
                }

                lock (_gate)
                {
                    return Json(_feedback.ToList());
                }
            }

            if (request.Method == HttpMethod.Put && path is "/api/settings/rate" or "/api/settings/commute")
            {
                var sent = await request.Content!.ReadAsStringAsync(cancellationToken);
                SaveArrived.Set();
                await Task.Run(() => Release.Wait(TimeSpan.FromMinutes(1)), cancellationToken);
                lock (_gate)
                {
                    if (path == "/api/settings/rate")
                    {
                        _rate = JsonSerializer.Deserialize<RateRequest>(sent, RemoteJson.Options)!.Dollars ?? 0;
                        Saves.Add($"rate {_rate:0.00}");
                    }
                    else
                    {
                        _commute = int.Parse(JsonSerializer.Deserialize<TextValue>(sent, RemoteJson.Options)!.Value!, System.Globalization.CultureInfo.InvariantCulture);
                        Saves.Add($"commute {_commute}");
                    }
                }

                return Json(new OpReply("Saved."));
            }

            lock (_gate)
            {
                return Json(path switch
                {
                    "/api/settings/rate" => new NumberValue(_rate),
                    "/api/settings/commute" => new NumberValue(_commute),
                    "/api/settings/tonight" => new TextValue("Bed by 11:30 PM"),
                    "/api/settings/weekly" or "/api/settings/dates" => Array.Empty<object>(),
                    _ => new { },
                });
            }
        }

        private static HttpResponseMessage Json(object body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), RemoteJson.Options), Encoding.UTF8, "application/json") };
    }

    [Fact]
    public void Saving_fields_returns_at_once_and_the_saves_reach_the_server_in_order() => Ui.Run(ui =>
    {
        var server = new SettingsServer();
        var client = new ServerClient(new HttpClient(server) { BaseAddress = new Uri("http://server/") }, "token");
        var work = new PageWork(client);
        var view = new SettingsView { Work = work };
        view.Connect(new ServerSettingsData(client), ui.Database, null, new BedtimeSettings(), () => [], () => { });
        ui.Host(view);
        view.LoadSoon();
        var rate = UiTest.Named<TextBox>(view, "HourlyRate");
        var commute = UiTest.Named<TextBox>(view, "Commute");
        ServerToDoClickTests.PumpUntil(ui, () => ServerToDoClickTests.Settled(work) && rate.Text == "20.00" && commute.Text == "15", "the settings");

        server.Release.Reset();
        rate.Text = "21.50";
        commute.Text = "25";
        UiTest.Call(view, "OnHourlyRateSaved", view, new RoutedEventArgs());
        UiTest.Call(view, "OnCommuteSaved", view, new RoutedEventArgs());

        // Both came back while the server still held the first save.
        Assert.Empty(server.Saves);
        Assert.True(server.SaveArrived.Wait(TimeSpan.FromSeconds(30)));

        server.Release.Set();
        ServerToDoClickTests.PumpUntil(ui, () => ServerToDoClickTests.Settled(work) && server.Saves.Count == 2, "the saves");
        Assert.Equal(["rate 21.50", "commute 25"], server.Saves);
        Assert.Equal("21.50", rate.Text);
        Assert.Equal("25", commute.Text);
    });

    private static (SettingsView View, PageWork Work) Open(UiTest ui, SettingsServer server)
    {
        var client = new ServerClient(new HttpClient(server) { BaseAddress = new Uri("http://server/") }, "token");
        var work = new PageWork(client);
        var view = new SettingsView { Work = work, FeedbackData = new ServerFeedbackData(client) };
        view.Connect(new ServerSettingsData(client), ui.Database, null, new BedtimeSettings(), () => [], () => { });
        ui.Host(view);
        view.LoadSoon();
        ServerToDoClickTests.PumpUntil(ui, () => ServerToDoClickTests.Settled(work) && UiTest.Named<ItemsControl>(view, "FeedbackRows").Items.Count == 2, "the settings");
        return (view, work);
    }

    [Fact]
    public void After_a_discord_save_whether_a_token_is_kept_is_read_without_holding_the_window() => Ui.Run(ui =>
    {
        var server = new SettingsServer();
        var (view, work) = Open(ui, server);
        var note = UiTest.Named<TextBlock>(view, "DiscordTokenNote");
        Assert.Equal(Visibility.Collapsed, note.Visibility);
        server.DiscordReadArrived.Reset();
        server.ReleaseDiscordReads.Reset();

        UiTest.Named<PasswordBox>(view, "DiscordToken").Password = "made-up-token";
        UiTest.Call(view, "OnDiscordSaved", view, new RoutedEventArgs());

        // The window carries on while the server holds the read after the save.
        ServerToDoClickTests.PumpUntil(ui, () => server.DiscordReadArrived.IsSet, "the read after the save");
        ui.Pump();
        Assert.True(work.IsBusy);
        Assert.Equal(["discord"], server.Saves);
        Assert.Equal(Visibility.Collapsed, note.Visibility);

        server.ReleaseDiscordReads.Set();
        ServerToDoClickTests.PumpUntil(ui, () => ServerToDoClickTests.Settled(work) && note.Visibility == Visibility.Visible, "the token note");
        Assert.Equal("", UiTest.Named<PasswordBox>(view, "DiscordToken").Password);
    });

    [Fact]
    public void Clearing_the_feedback_list_returns_at_once_and_can_be_pressed_only_once_while_it_is_on_its_way() => Ui.Run(ui =>
    {
        var server = new SettingsServer();
        var (view, work) = Open(ui, server);
        var clear = UiTest.Named<Button>(view, "FeedbackClear");
        server.SaveArrived.Reset();
        server.Release.Reset();

        UiTest.Call(view, "OnFeedbackCleared", view, new RoutedEventArgs());

        // It came back while the server still held the clear.
        Assert.True(server.SaveArrived.Wait(TimeSpan.FromSeconds(30)));
        Assert.Empty(server.Saves);
        Assert.True(work.ChangesWaiting);
        Assert.False(clear.IsEnabled);

        server.Release.Set();
        var message = UiTest.Named<TextBlock>(UiTest.Named<UndoBar>(view, "FeedbackBar"), "Message");
        ServerToDoClickTests.PumpUntil(ui, () => ServerToDoClickTests.Settled(work) && message.Text == "Cleared 2 entries.", "the clear");
        Assert.Equal(["clear 2"], server.Saves);
        Assert.True(clear.IsEnabled);
        Assert.Empty(UiTest.Named<ItemsControl>(view, "FeedbackRows").Items);
        Assert.Equal(Visibility.Visible, UiTest.Named<TextBlock>(view, "NoFeedback").Visibility);
    });

    private static List<string> ClassLines(SettingsView view) =>
        ((System.Collections.IEnumerable)UiTest.Named<ItemsControl>(view, "ClassRows").ItemsSource).Cast<object>().Select(row => row.ToString()!.Split("  ")[0]).ToList();

    private static void RemoveClass(UiTest ui, SettingsView view, string course)
    {
        ui.Pump();
        var remove = UiTest.Descendants<Button>(view).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == $"Remove {course}");
        UiTest.Call(view, "OnClassRemoved", remove, new RoutedEventArgs());
    }

    [Fact]
    public void Two_quick_class_removals_both_stay_removed_even_when_a_read_from_between_them_comes_back_late() => Ui.Run(ui =>
    {
        var server = new SettingsServer();
        var (view, work) = Open(ui, server);
        view.ShowSection("classes");
        ServerToDoClickTests.PumpUntil(ui, () => ServerToDoClickTests.Settled(work) && ClassLines(view).Count == 3, "the classes");

        // The first removal is held by the server, and the list shows it gone straight away.
        server.Release.Reset();
        server.SaveArrived.Reset();
        RemoveClass(ui, view, "Art 101");
        Assert.True(server.SaveArrived.Wait(TimeSpan.FromSeconds(30)));
        Assert.Equal(["Bio 102", "Chem 103"], ClassLines(view));

        // Once it's saved, the read of the classes after it is held too. Reads made as part of the save itself go on.
        server.HoldClassReads = true;
        server.Release.Set();
        ServerToDoClickTests.PumpUntil(ui, () =>
        {
            if (server.ClassReadsWaiting > 0 && work.ChangesWaiting)
            {
                server.LetOneClassReadGo();
            }

            return server.ClassReadsWaiting > 0 && !work.ChangesWaiting;
        }, "the read after the first removal");

        // The second removal is asked for while that read is still out, and starts from the first.
        server.Release.Reset();
        server.SaveArrived.Reset();
        RemoveClass(ui, view, "Bio 102");
        Assert.Equal(["Chem 103"], ClassLines(view));

        // The read from before the second removal comes back, and doesn't bring Bio 102 back.
        server.LetClassReadsGo();
        Assert.True(server.SaveArrived.Wait(TimeSpan.FromSeconds(30)));
        ui.Pump();
        Assert.Equal(["Chem 103"], ClassLines(view));

        server.Release.Set();
        ServerToDoClickTests.PumpUntil(ui, () => ServerToDoClickTests.Settled(work) && server.Saves.Count == 2, "the removals");
        Assert.Equal(["classes Bio 102, Chem 103", "classes Chem 103"], server.Saves);
        Assert.Equal(["Chem 103"], ClassLines(view));
    });

    [Fact]
    public void Without_the_page_queue_as_on_this_pcs_database_a_field_is_saved_before_the_click_returns() => Ui.Run(ui =>
    {
        var server = new SettingsServer();
        var view = new SettingsView();
        Assert.True(view.Work.RunsNow);
        view.Connect(new ServerSettingsData(new ServerClient(new HttpClient(server) { BaseAddress = new Uri("http://server/") }, "token")), ui.Database, null, new BedtimeSettings(), () => [], () => { });
        ui.Host(view);
        view.Load();
        ui.Pump();

        UiTest.Named<TextBox>(view, "Commute").Text = "25";
        UiTest.Call(view, "OnCommuteSaved", view, new RoutedEventArgs());

        Assert.Equal(["commute 25"], server.Saves);
        ui.Pump();
    });
}
