using System.Net;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Email;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Tests;

/// <summary>The background work moved out of the PC app. It must do the same thing whichever host runs it.</summary>
public sealed class WorkerTests : IDisposable
{
    // A Wednesday morning, outside any quiet time.
    private static readonly DateTime Morning = new(2026, 11, 4, 10, 0, 0);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-worker-{Guid.NewGuid()}");
    private readonly WaypointDatabase _database;
    private readonly FakeHost _host = new();
    private DateTime _now = Morning;

    public WorkerTests()
    {
        Directory.CreateDirectory(_folder);
        _database = new WaypointDatabase(Path.Combine(_folder, "waypoint.db"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private sealed class FakeHost : IWorkerHost
    {
        public List<(string Title, string Text)> Notices { get; } = [];

        public List<(DueReminder Reminder, Action Closed)> Reminders { get; } = [];

        public List<string> Resets { get; } = [];

        public int Changes { get; private set; }

        public void ShowNotice(string title, string text)
        {
            lock (Notices)
            {
                Notices.Add((title, text));
            }
        }

        public void ShowReminder(DueReminder reminder, Action closed) => Reminders.Add((reminder, closed));

        public void ShowWeeklyReset(string text) => Resets.Add(text);

        public void Changed() => Changes++;

        public void Post(Action action) => action();
    }

    private sealed class NoTokens : ITokenStore
    {
        public string? Load() => null;

        public void Save(string token)
        {
        }
    }

    private sealed class NullSorter : ICaptureSorter
    {
        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<CaptureResult?>(null);
    }

    /// <summary>Stands in for Discord. Every message posted gets the next ID, or fails if told to.</summary>
    private sealed class FakeDiscord(bool works) : HttpMessageHandler
    {
        public int Posts { get; private set; }

        public List<string> PostedTo { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Posts++;
                lock (PostedTo)
                {
                    PostedTo.Add(request.RequestUri!.ToString());
                }
            }

            return Task.FromResult(works
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"id":"m{{Posts}}"}""") }
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    private WaypointWorker Worker(WaypointSettings? settings = null, HttpMessageHandler? http = null) =>
        new(_database, settings ?? new WaypointSettings(), _folder, _host, exception => throw exception, new WorkerParts(
            _ => new NoTokens(),
            Clock: () => _now,
            Http: () => new HttpClient(http ?? new FakeDiscord(works: false)),
            CaptureSorter: new NullSorter(),
            NudgePlanner: null,
            ConnectGateway: false))
        {
            RetryWait = _ => Task.CompletedTask,
        };

    private static readonly WaypointSettings WithDiscord = new(Discord: new DiscordSettings("token", "owner", "capture", "alerts", "budget"));

    private void AddReminder(string title, DateTime at) =>
        _database.CompleteCapture(_database.AddCapture(title), [new SortedItem(ItemKind.Reminder, title, DateOnly.FromDateTime(at), null, TimeOnly.FromDateTime(at))]);

    [Fact]
    public void Without_Discord_a_due_reminder_shows_on_the_host_once_until_it_is_closed()
    {
        AddReminder("call the bank", Morning.AddMinutes(-5));
        var worker = Worker();

        worker.CheckAlerts();
        worker.CheckAlerts();

        var shown = Assert.Single(_host.Reminders);
        Assert.Equal("call the bank", shown.Reminder.Item.Title);

        // Closed without being answered, so it's still due and comes back.
        shown.Closed();
        worker.CheckAlerts();
        Assert.Equal(2, _host.Reminders.Count);
    }

    [Fact]
    public async Task With_Discord_a_reminder_goes_there_and_is_settled()
    {
        AddReminder("call the bank", Morning.AddMinutes(-5));
        var discord = new FakeDiscord(works: true);
        var worker = Worker(WithDiscord, discord);
        worker.StartDiscord();

        worker.CheckAlerts();
        await WaitFor(() => _database.GetDueReminders(_now).Count == 0);

        Assert.Empty(_host.Reminders);
        Assert.True(discord.Posts >= 1);
    }

    [Fact]
    public async Task When_Discord_cannot_be_reached_the_reminder_falls_back_to_the_host()
    {
        AddReminder("call the bank", Morning.AddMinutes(-5));
        var worker = Worker(WithDiscord, new FakeDiscord(works: false));
        worker.StartDiscord();

        worker.CheckAlerts();
        await WaitFor(() => _host.Reminders.Count == 1);

        Assert.Equal("call the bank", _host.Reminders[0].Reminder.Item.Title);
    }

    [Fact]
    public void Time_to_leave_goes_out_once_per_class()
    {
        var meeting = new ClassMeeting("Biology", "B 2", [DayOfWeek.Wednesday], new TimeOnly(10, 30), new TimeOnly(11, 45), 15);
        var schedule = new ClassSchedule(new DateOnly(2026, 9, 14), new DateOnly(2026, 12, 18), [meeting], []);
        var worker = Worker(new WaypointSettings(Schedule: schedule));

        _now = new DateTime(2026, 11, 4, 10, 15, 0);
        worker.CheckAlerts();
        _now = _now.AddMinutes(2);
        worker.CheckAlerts();

        var leave = Assert.Single(_host.Notices, notice => notice.Title == "Time to leave");
        Assert.Contains("Biology", leave.Text);
    }

    [Fact]
    public void The_briefing_goes_out_once_a_day_and_not_before_its_time()
    {
        var worker = Worker();

        _now = new DateTime(2026, 11, 4, 7, 0, 0);
        worker.CheckAlerts();
        Assert.DoesNotContain(_host.Notices, notice => notice.Title == "Today");

        _now = new DateTime(2026, 11, 4, 8, 0, 0);
        worker.CheckAlerts();
        worker.CheckAlerts();
        Assert.Single(_host.Notices, notice => notice.Title == "Today");

        // A second worker, as after a restart, still knows it went out today.
        Worker().CheckAlerts();
        Assert.Single(_host.Notices, notice => notice.Title == "Today");
    }

    [Fact]
    public async Task The_morning_briefing_is_one_ping_with_safe_to_spend_in_it()
    {
        _database.SetMonthlyBudget(60000);
        var discord = new FakeDiscord(works: true);
        var worker = Worker(WithDiscord, discord);
        worker.StartDiscord();
        _now = new DateTime(2026, 11, 4, 8, 0, 0);

        worker.CheckAlerts();
        await WaitFor(() => discord.PostedTo.Any(url => url.Contains("/channels/alerts/")));
        await Task.Delay(100);

        lock (discord.PostedTo)
        {
            Assert.Single(discord.PostedTo, url => url.Contains("/channels/alerts/"));
            Assert.DoesNotContain(discord.PostedTo, url => url.Contains("/channels/budget/"));
        }
    }

    [Theory]
    [InlineData(2026, 11, 4, 8, "Today")]
    [InlineData(2026, 11, 8, 18, "Your week")]
    public void A_briefing_that_fails_to_build_is_not_marked_sent_and_goes_out_once_it_can(int year, int month, int day, int hour, string title)
    {
        // Problems are kept rather than thrown, as the app logs them, so every check still runs each round.
        var problems = new List<Exception>();
        var worker = new WaypointWorker(_database, new WaypointSettings(), _folder, _host, problems.Add, new WorkerParts(
            _ => new NoTokens(),
            Clock: () => _now,
            Http: () => new HttpClient(new FakeDiscord(works: false)),
            CaptureSorter: new NullSorter(),
            NudgePlanner: null,
            ConnectGateway: false));
        _now = new DateTime(year, month, day, hour, 30, 0);
        _database.AddByHand(new HandAddedItem(ItemKind.Task, "Made up essay", new DateOnly(2026, 11, 20)), _now);
        SetDueDate("not a date");

        worker.CheckAlerts();
        Assert.DoesNotContain(_host.Notices, notice => notice.Title == title);
        Assert.NotEmpty(problems);

        SetDueDate("2026-11-20");
        worker.CheckAlerts();
        worker.CheckAlerts();
        Assert.Single(_host.Notices, notice => notice.Title == title);

        void SetDueDate(string value)
        {
            using var connection = new SqliteConnection($"Data Source={Path.Combine(_folder, "waypoint.db")};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE items SET due_date = $value";
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
    }

    [Fact]
    public void The_weekly_reset_shows_on_the_host_and_goes_out_on_Sunday_evening()
    {
        var worker = Worker();
        _now = new DateTime(2026, 11, 8, 18, 30, 0);

        worker.CheckAlerts();
        worker.CheckAlerts();

        Assert.Single(_host.Resets);
        Assert.Single(_host.Notices, notice => notice.Title == "Your week");
    }

    [Fact]
    public void A_daily_backup_is_made_on_the_first_check()
    {
        var worker = Worker();

        worker.CheckAlerts();

        Assert.NotEmpty(_database.GetBackups());
    }

    [Fact]
    public void A_session_button_is_typed_as_its_words()
    {
        var worker = Worker(WithDiscord, new FakeDiscord(works: true));
        worker.StartDiscord();

        Assert.Equal("Got it", worker.OnButtonPressed("m1", AlertActionKind.DidIt));

        Assert.Contains(_database.GetPendingCaptures(), capture => capture.Text == "did it");
    }

    [Fact]
    public void Discord_waits_for_the_host_to_start_it()
    {
        var worker = Worker(WithDiscord, new FakeDiscord(works: true));

        Assert.NotNull(worker.DiscordSettings);
        Assert.Null(worker.Discord);
        worker.StartDiscord();
        Assert.NotNull(worker.Discord);
        worker.StopDiscord();
        Assert.Null(worker.Discord);
    }

    [Fact]
    public void A_message_from_Discord_is_captured_as_from_Discord()
    {
        var worker = Worker(WithDiscord, new FakeDiscord(works: true));
        worker.StartDiscord();

        worker.OnDiscordMessage(new DiscordMessage("123", "buy stamps", DateTimeOffset.Now, []));

        var capture = Assert.Single(_database.GetPendingCaptures());
        Assert.Equal("buy stamps", capture.Text);
        Assert.Equal(CaptureOrigin.Discord, capture.Origin);
    }

    [Fact]
    public void The_now_command_offers_the_most_pressing_thing_with_a_start_button()
    {
        AddReminder("call the bank", Morning.AddHours(2));
        var worker = Worker();

        var reply = worker.OnDiscordCommand(new SlashCommand("now", new Dictionary<string, string>()));

        Assert.True(reply.Card.HasStartNext);
        Assert.Contains("call the bank", reply.Card.Title);
    }

    [Fact]
    public void An_unknown_command_answers_privately()
    {
        Assert.True(Worker().OnDiscordCommand(new SlashCommand("nope", new Dictionary<string, string>())).Private);
    }

    [Fact]
    public void Saving_Discord_with_a_bad_id_changes_nothing()
    {
        var worker = Worker();

        var reply = worker.SaveDiscord("not an id", null, null, null, "token");

        Assert.Null(worker.DiscordSettings);
        Assert.DoesNotContain("Saved", reply);
    }

    [Fact]
    public void Connections_say_what_is_not_set_up()
    {
        var connections = Worker().DescribeConnections();

        Assert.Equal(["Discord", "Calendar feed", "Alert email"], connections.Select(connection => connection.Name));
        Assert.All(connections, connection => Assert.StartsWith("Not set up", connection.State));

        // The server describes these for the PC's Settings page too, so they point at the page, not a file.
        Assert.Equal("Not set up. Fill it in below.", connections[0].State);
        Assert.Equal("Not set up. Add its app ID below.", connections[2].State);
    }

    [Fact]
    public void Alert_email_set_up_on_the_settings_page_is_used_straight_away()
    {
        var worker = Worker();
        Assert.Null(worker.Outlook);

        var reply = worker.SaveAlertEmail("12345678-abcd-1234-abcd-1234567890ab", "me@example.com");

        Assert.Equal("Saved. Choose Connect alert email in the tray menu to sign in.", reply);
        Assert.NotNull(worker.Outlook);
        Assert.Equal("me@example.com", worker.OutlookSettings?.OwnAddress);
        Assert.StartsWith("Set up, but not signed in", worker.DescribeConnections()[2].State);

        // A worker started later finds it too, laid over settings.json.
        Assert.Equal("12345678-abcd-1234-abcd-1234567890ab", Worker(new WaypointSettings(Outlook: new OutlookSettings("from-the-file"))).OutlookSettings?.ClientId);
    }

    [Fact]
    public void A_bad_alert_email_app_id_or_address_is_turned_down()
    {
        var worker = Worker();

        Assert.Contains("Application (client) ID", worker.SaveAlertEmail("not an id", null));
        Assert.Contains("email address", worker.SaveAlertEmail("12345678-abcd-1234-abcd-1234567890ab", "not an address"));
        Assert.Null(worker.Outlook);

        // Cleared on the page, settings.json is used again.
        Assert.Equal("from-the-file", Worker(new WaypointSettings(Outlook: new OutlookSettings("from-the-file"))).OutlookSettings?.ClientId);
    }

    [Fact]
    public async Task Email_is_read_only_once_it_is_set_up()
    {
        var worker = Worker();

        await worker.PollEmailAsync();
        Assert.Contains("Settings page", await worker.CheckEmailNowAsync());
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var tries = 0; tries < 200 && !condition(); tries++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
