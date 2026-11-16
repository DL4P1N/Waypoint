using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Email;

namespace Waypoint.Core.Tests;

/// <summary>Only one copy uses the bot at a time, checked against a fake alerts channel that several copies share.</summary>
public sealed class DiscordClaimTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 17, 0, 0, TimeSpan.Zero);
    private static readonly DiscordSettings Settings = new("token", "owner", "capture", "alerts", "budget");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-claim-{Guid.NewGuid()}");
    private readonly WaypointDatabase _database;
    private DateTimeOffset _now = Start;

    public DiscordClaimTests()
    {
        Directory.CreateDirectory(_folder);
        _database = new WaypointDatabase(Path.Combine(_folder, "waypoint.db"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    /// <summary>The alerts channel: its messages, which are pinned, and every call made to it.</summary>
    private sealed class FakeChannel : HttpMessageHandler
    {
        private long _nextId = 1000;

        public Dictionary<string, string> Messages { get; } = [];

        public HashSet<string> Pinned { get; } = [];

        public List<(HttpMethod Method, string Path)> Calls { get; } = [];

        public bool CanPin { get; set; } = true;

        public bool CanReadPins { get; set; } = true;

        /// <summary>Anything sent other than the note, such as an alert.</summary>
        public int OtherPosts { get; private set; }

        public string Add(string content, bool pinned = true)
        {
            var id = (_nextId++).ToString();
            Messages[id] = content;
            if (pinned)
            {
                Pinned.Add(id);
            }

            return id;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Replace("/api/v10", "");
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Calls)
            {
                Calls.Add((request.Method, path));
            }

            var parts = path.Trim('/').Split('/');
            if (request.Method == HttpMethod.Get && path.EndsWith("/messages/pins", StringComparison.Ordinal))
            {
                if (!CanReadPins)
                {
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                }

                var items = Pinned.Select(id => new { pinned_at = "2026-11-13T00:00:00Z", message = new { id, content = Messages[id] } });
                return Json(new { items, has_more = false });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/messages", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(body);
                var content = document.RootElement.TryGetProperty("content", out var text) ? text.GetString() ?? "" : "";
                if (!content.Contains("waypoint-claim", StringComparison.Ordinal))
                {
                    OtherPosts++;
                }

                return Json(new { id = Add(content, pinned: false) });
            }

            if (request.Method == HttpMethod.Put && parts is [.., "pins", var pinId])
            {
                if (!CanPin)
                {
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                }

                Pinned.Add(pinId);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Patch && parts is [.., "messages", var editId])
            {
                if (!Messages.ContainsKey(editId))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                using var document = JsonDocument.Parse(body);
                Messages[editId] = document.RootElement.GetProperty("content").GetString()!;
                return Json(new { id = editId });
            }

            if (request.Method == HttpMethod.Delete && parts is [.., "messages", var deleteId])
            {
                Messages.Remove(deleteId);
                Pinned.Remove(deleteId);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            // Anything else the worker asks for, such as reading messages, finds an empty channel.
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        }

        private static HttpResponseMessage Json(object value) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    }

    private DiscordClaim Claim(FakeChannel channel, string host, DiscordRunner runner) =>
        new(new HttpClient(channel), Settings, host, runner, () => _now);

    private string Note(string host, DiscordRunner runner, DateTimeOffset at) => DiscordClaim.NoteText(host, runner, at);

    [Fact]
    public async Task The_first_copy_pins_a_note_and_a_second_PC_then_sends_nothing()
    {
        var channel = new FakeChannel();

        var first = await Claim(channel, "DESK", DiscordRunner.Pc).CheckAsync();
        var second = await Claim(channel, "LAPTOP", DiscordRunner.Pc).CheckAsync();

        Assert.Equal(ClaimState.Here, first.State);
        var note = Assert.Single(channel.Pinned);
        Assert.Contains("Waypoint is running on DESK, a PC.", channel.Messages[note]);
        Assert.Equal(new ClaimCheck(ClaimState.Elsewhere, "DESK"), second);
        Assert.Single(channel.Messages);
    }

    [Fact]
    public async Task The_server_takes_over_from_a_PC_and_the_PC_then_steps_aside()
    {
        var channel = new FakeChannel();
        var pc = Claim(channel, "DESK", DiscordRunner.Pc);
        await pc.CheckAsync();

        var server = await Claim(channel, "homeserver", DiscordRunner.Server).CheckAsync();
        _now += DiscordClaim.CheckEvery;
        var pcAfter = await pc.CheckAsync();

        Assert.Equal(ClaimState.Here, server.State);
        Assert.Equal(new ClaimCheck(ClaimState.Elsewhere, "homeserver"), pcAfter);
        var note = Assert.Single(channel.Pinned);
        Assert.Contains("homeserver, the server", channel.Messages[note]);
    }

    [Fact]
    public async Task A_note_left_by_a_copy_that_stopped_is_taken_over_and_cleared_away()
    {
        var channel = new FakeChannel();
        var old = channel.Add(Note("homeserver", DiscordRunner.Server, Start - DiscordClaim.Stale - TimeSpan.FromMinutes(1)));

        var check = await Claim(channel, "DESK", DiscordRunner.Pc).CheckAsync();

        Assert.Equal(ClaimState.Here, check.State);
        Assert.DoesNotContain(old, channel.Messages.Keys);
        Assert.Contains("DESK, a PC", channel.Messages[Assert.Single(channel.Pinned)]);
    }

    [Fact]
    public async Task After_a_restart_a_copy_keeps_its_own_note_instead_of_waiting_on_it()
    {
        var channel = new FakeChannel();
        var before = channel.Add(Note("homeserver", DiscordRunner.Server, Start - TimeSpan.FromMinutes(1)));

        var check = await Claim(channel, "HOMESERVER", DiscordRunner.Server).CheckAsync();

        Assert.Equal(ClaimState.Here, check.State);
        Assert.Equal([before], channel.Pinned);
        Assert.Contains($"at={Start.ToUnixTimeSeconds()}", channel.Messages[before]);
        Assert.DoesNotContain(channel.Calls, call => call.Method == HttpMethod.Post);
    }

    [Fact]
    public void Of_two_servers_or_two_PCs_the_one_there_first_keeps_it()
    {
        IReadOnlyList<ClaimNote> notes =
        [
            new("200", "box-b", DiscordRunner.Server, Start),
            new("100", "box-a", DiscordRunner.Server, Start),
            new("50", "desk", DiscordRunner.Pc, Start),
        ];

        Assert.Equal("box-a", DiscordClaim.RunsElsewhere(notes, "box-b", DiscordRunner.Server, Start));
        Assert.Null(DiscordClaim.RunsElsewhere(notes, "box-a", DiscordRunner.Server, Start));
        Assert.Equal("box-a", DiscordClaim.RunsElsewhere(notes, "desk", DiscordRunner.Pc, Start));
        Assert.Equal("desk", DiscordClaim.RunsElsewhere([new("50", "desk", DiscordRunner.Pc, Start)], "laptop", DiscordRunner.Pc, Start));
    }

    [Fact]
    public void A_note_reads_back_the_way_it_was_written()
    {
        var note = DiscordClaim.ReadNote("7", DiscordClaim.NoteText("my pc", DiscordRunner.Pc, Start));

        Assert.Equal(new ClaimNote("7", "my pc", DiscordRunner.Pc, Start), note);
        Assert.Null(DiscordClaim.ReadNote("8", "Some other pinned message"));
        Assert.DoesNotContain("—", DiscordClaim.NoteText("my pc", DiscordRunner.Pc, Start));
    }

    [Fact]
    public async Task Pins_that_cannot_be_read_leave_it_unknown_and_say_which_permission_is_missing()
    {
        var channel = new FakeChannel { CanReadPins = false };

        var check = await Claim(channel, "DESK", DiscordRunner.Pc).CheckAsync();

        Assert.Equal(ClaimState.Unknown, check.State);
        Assert.Contains("Read Message History", check.Problem);
    }

    [Fact]
    public async Task A_note_that_cannot_be_pinned_is_kept_and_updated_rather_than_sent_again()
    {
        var channel = new FakeChannel { CanPin = false };
        var claim = Claim(channel, "DESK", DiscordRunner.Pc);

        var first = await claim.CheckAsync();
        _now += DiscordClaim.CheckEvery;
        var second = await claim.CheckAsync();

        Assert.Contains("Pin Messages", first.Problem);
        Assert.Contains("Pin Messages", second.Problem);
        Assert.Single(channel.Calls, call => call.Method == HttpMethod.Post);
        Assert.Contains($"at={_now.ToUnixTimeSeconds()}", Assert.Single(channel.Messages.Values));
    }

    // The worker

    private sealed class Host : IWorkerHost
    {
        public List<string> Notices { get; } = [];

        public int Reminders { get; private set; }

        public void ShowNotice(string title, string text) => Notices.Add(title);

        public void ShowReminder(DueReminder reminder, Action closed) => Reminders++;

        public void ShowWeeklyReset(string text)
        {
        }

        public void Changed()
        {
        }

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

    private WaypointWorker Worker(FakeChannel channel, Host host, DiscordRunner runner = DiscordRunner.Pc) =>
        new(_database, new WaypointSettings(Discord: Settings), _folder, host, exception => throw exception, new WorkerParts(
            _ => new NoTokens(),
            Clock: () => _now.LocalDateTime,
            Http: () => new HttpClient(channel),
            CaptureSorter: new NullSorter(),
            ConnectGateway: false,
            Runner: runner,
            HostName: "DESK"))
        {
            RetryWait = _ => Task.CompletedTask,
        };

    [Fact]
    public async Task A_PC_in_local_mode_sends_nothing_while_the_server_runs_and_takes_over_once_it_stops()
    {
        var channel = new FakeChannel();
        channel.Add(Note("homeserver", DiscordRunner.Server, Start));
        var due = _now.LocalDateTime.AddMinutes(-1);
        _database.CompleteCapture(_database.AddCapture("Call the bank"), [new SortedItem(ItemKind.Reminder, "Call the bank", DateOnly.FromDateTime(due), null, TimeOnly.FromDateTime(due))]);
        var host = new Host();
        var worker = Worker(channel, host);

        worker.StartDiscord();
        await worker.CheckDiscordClaimAsync();
        worker.CheckAlerts();

        Assert.Null(worker.Discord);
        Assert.Equal("homeserver", worker.DiscordRunsOn);
        Assert.Equal(0, channel.OtherPosts);
        Assert.Equal(0, host.Reminders);
        Assert.StartsWith("Running on homeserver", worker.DescribeConnections()[0].State);

        _now += DiscordClaim.Stale + TimeSpan.FromMinutes(1);
        await worker.CheckDiscordClaimAsync();

        Assert.NotNull(worker.Discord);
        Assert.Null(worker.DiscordRunsOn);
    }

    [Fact]
    public async Task The_server_connects_even_with_a_fresh_note_from_a_PC()
    {
        var channel = new FakeChannel();
        channel.Add(Note("DESK-PC", DiscordRunner.Pc, Start));
        var worker = Worker(channel, new Host(), DiscordRunner.Server);

        worker.StartDiscord();
        await worker.CheckDiscordClaimAsync();

        Assert.NotNull(worker.Discord);
        Assert.Null(worker.DiscordRunsOn);
    }

    [Fact]
    public async Task Without_the_check_turned_on_Discord_connects_straight_away_as_before()
    {
        var channel = new FakeChannel();
        channel.Add(Note("homeserver", DiscordRunner.Server, Start));
        var worker = new WaypointWorker(_database, new WaypointSettings(Discord: Settings), _folder, new Host(), exception => throw exception, new WorkerParts(
            _ => new NoTokens(), Clock: () => _now.LocalDateTime, Http: () => new HttpClient(channel), CaptureSorter: new NullSorter(), ConnectGateway: false));

        worker.StartDiscord();
        await worker.CheckDiscordClaimAsync();

        Assert.NotNull(worker.Discord);
        Assert.DoesNotContain(channel.Calls, call => call.Path.Contains("/pins", StringComparison.Ordinal));
    }

    [Fact]
    public void Discord_trouble_counts_only_once_it_has_lasted_ten_minutes_and_a_send_that_works_clears_it()
    {
        var health = new DiscordHealth();
        var at = Start.DateTime;

        health.Sent(false, at);
        Assert.Null(health.Problem(at.AddMinutes(9), WaypointWorker.DiscordTroubleAfter));
        Assert.Contains("failed for 11 minutes", health.Problem(at.AddMinutes(11), WaypointWorker.DiscordTroubleAfter));

        health.Sent(true, at.AddMinutes(12));
        health.Connection(false, at.AddMinutes(12));
        Assert.Null(health.Problem(at.AddMinutes(13), WaypointWorker.DiscordTroubleAfter));
        Assert.Contains("down for 15 minutes", health.Problem(at.AddMinutes(27), WaypointWorker.DiscordTroubleAfter));

        health.Connection(true, at.AddMinutes(28));
        Assert.Null(health.Problem(at.AddMinutes(40), WaypointWorker.DiscordTroubleAfter));
    }

    [Fact]
    public async Task A_notice_that_fails_to_send_is_Discord_trouble_once_it_lasts()
    {
        var host = new Host();
        var worker = new WaypointWorker(_database, new WaypointSettings(Discord: Settings), _folder, host, exception => throw exception, new WorkerParts(
            _ => new NoTokens(), Clock: () => _now.LocalDateTime, Http: () => new HttpClient(new Refuses()), CaptureSorter: new NullSorter(), ConnectGateway: false))
        {
            RetryWait = _ => Task.CompletedTask,
        };
        worker.StartDiscord();

        await worker.NotifyAsync("Heads up", "Something", CardTone.Attention);

        Assert.Null(worker.DiscordTrouble(_now.LocalDateTime.AddMinutes(5)));
        Assert.Contains("Sending to Discord has failed", worker.DiscordTrouble(_now.LocalDateTime.AddMinutes(11)));
        Assert.Null(new WaypointWorker(_database, new WaypointSettings(), _folder, host, exception => throw exception, new WorkerParts(_ => new NoTokens(), ConnectGateway: false))
            .DiscordTrouble(_now.LocalDateTime.AddHours(1)));
    }

    private sealed class Refuses : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }
}
