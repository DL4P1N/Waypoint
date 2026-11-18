using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Email;
using Waypoint.Core.Files;

namespace Waypoint.Core.Tests;

/// <summary>
/// Messages sent from Discord are never lost or done twice: one whose work was cut off, by a crash or a close, is
/// handed over again later, and handing it over again saves nothing a second time. No network, made-up data.
/// </summary>
public sealed class DiscordInboxTests : IDisposable
{
    private static readonly DateTime Morning = new(2026, 11, 9, 10, 0, 0);
    private const string FileUrl = "https://cdn.discordapp.com/attachments/1/2/made-up-list.txt";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-inbox-{Guid.NewGuid()}");
    private readonly WaypointDatabase _database;
    private readonly Channel _discord = new();
    private DateTime _now = Morning;

    public DiscordInboxTests()
    {
        Directory.CreateDirectory(_folder);
        _database = new WaypointDatabase(Path.Combine(_folder, "waypoint.db"));
    }

    public void Dispose()
    {
        for (var attempt = 0; ; attempt++)
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_folder, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// The capture and notes channels: the owner's messages by ID, read as a list after a place or one at a time,
    /// and the made-up file they carry. Anything else sent gets the next ID.
    /// </summary>
    private sealed class Channel : HttpMessageHandler
    {
        private int _posts;

        public Dictionary<string, string> Messages { get; } = [];

        public List<string> Asked { get; } = [];

        public List<string> Posted { get; } = [];

        /// <summary>Messages Discord won't let the bot read back, the way a lost permission does.</summary>
        public HashSet<string> Forbidden { get; } = [];

        public void Add(string id, string text, bool withFile = false, string channel = "capture") => Messages[id] = $$"""
            {"id":"{{id}}","channel_id":"{{channel}}","content":"{{text}}","author":{"id":"owner"},"timestamp":"2026-11-09T17:00:00+00:00"{{(withFile ? $$""","attachments":[{"id":"f{{id}}","filename":"made-up-list.txt","size":40,"url":"{{FileUrl}}","content_type":"text/plain"}]""" : "")}}}
            """;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Asked)
            {
                Asked.Add($"{request.Method} {url}");
                if (request.Method == HttpMethod.Post)
                {
                    Posted.Add(body);
                }
            }

            if (url == FileUrl)
            {
                return Reply(HttpStatusCode.OK, "made up thing one\nmade up thing two");
            }

            if (request.Method == HttpMethod.Get && Listed(url) is { } listed)
            {
                var after = url.Contains("after=") ? ulong.Parse(url[(url.IndexOf("after=", StringComparison.Ordinal) + 6)..]) : 0;
                var newer = Messages.Where(message => ulong.Parse(message.Key) > after && message.Value.Contains($"\"channel_id\":\"{listed}\"")).Select(message => message.Value);
                return Reply(HttpStatusCode.OK, $"[{string.Join(",", newer)}]");
            }

            if (request.Method == HttpMethod.Get && Forbidden.Any(id => url.EndsWith("/messages/" + id)))
            {
                return Reply(HttpStatusCode.Forbidden, """{"code":50001}""");
            }

            if (request.Method == HttpMethod.Get && (url.Contains("/channels/capture/messages/") || url.Contains("/channels/notes/messages/")))
            {
                return Messages.TryGetValue(url[(url.LastIndexOf('/') + 1)..], out var one)
                    ? Reply(HttpStatusCode.OK, one)
                    : Reply(HttpStatusCode.NotFound, """{"code":10008}""");
            }

            if (request.Method == HttpMethod.Get)
            {
                return Reply(HttpStatusCode.OK, "[]");
            }

            return Reply(HttpStatusCode.OK, $$"""{"id":"m{{Interlocked.Increment(ref _posts)}}"}""");
        }

        /// <summary>The channel a request lists the messages of, if it's one of these.</summary>
        private static string? Listed(string url) =>
            new[] { "capture", "notes" }.FirstOrDefault(channel => url.Contains($"/channels/{channel}/messages?"));

        private static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body) };
    }

    /// <summary>Runs posted work straight away, or holds it the way the window thread does until it gets to it.</summary>
    private sealed class Host(bool hold = false) : IWorkerHost
    {
        public List<Action> Held { get; } = [];

        public void ShowNotice(string title, string text)
        {
        }

        public void ShowReminder(DueReminder reminder, Action closed)
        {
        }

        public void ShowWeeklyReset(string text)
        {
        }

        public void Changed()
        {
        }

        public void Post(Action action)
        {
            if (hold)
            {
                Held.Add(action);
            }
            else
            {
                action();
            }
        }
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

    /// <summary>A fresh start of Waypoint on the same data, as after a crash or a close.</summary>
    private WaypointWorker Start(Host host, string? notes = null)
    {
        var settings = new WaypointSettings(Discord: new DiscordSettings("token", "owner", "capture", "alerts", NotesChannelId: notes));
        var worker = new WaypointWorker(_database, settings, _folder, host, _ => { }, new WorkerParts(
            _ => new NoTokens(),
            Clock: () => _now,
            Http: () => new HttpClient(_discord),
            CaptureSorter: new NullSorter(),
            ReadFile: path => new FileText("made-up-list.txt", File.ReadAllText(path)),
            ConnectGateway: false))
        {
            RetryWait = _ => Task.CompletedTask,
        };
        worker.StartDiscord();
        return worker;
    }

    /// <summary>Polls until the check holds, since the start's own poll may still be running and a poll then does nothing.</summary>
    private static async Task PollUntil(WaypointWorker worker, Func<bool> done)
    {
        for (var attempt = 0; attempt < 100 && !done(); attempt++)
        {
            await worker.Discord!.PollAsync();
            await Task.Delay(20);
        }
    }

    private DiscordBridge Bridge(string? notes = null) => new(new HttpClient(_discord), _database, new DiscordSettings("token", "owner", "capture", NotesChannelId: notes))
    {
        Clock = () => _now,
        Delay = (_, _) => Task.CompletedTask,
    };

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private List<string> Posted()
    {
        lock (_discord.Asked)
        {
            return _discord.Posted.ToList();
        }
    }

    private List<string> PendingTexts() => _database.GetPendingCaptures().Select(capture => capture.Text).ToList();

    [Fact]
    public async Task A_message_whose_work_was_never_finished_is_handed_over_again_once_it_has_waited_long_enough()
    {
        _database.SetPreference(DiscordBridge.LastMessageKey, "5");
        _discord.Add("7", "made up errand");
        var bridge = Bridge();
        var received = new List<string>();
        bridge.Captured = message => received.Add(message.Id);

        await bridge.HandleEventAsync("MESSAGE_CREATE", Json(_discord.Messages["7"]));
        await bridge.PollAsync();
        Assert.Equal(["7"], received);

        // Still being worked on as far as anyone knows, so it waits.
        _now = _now.AddMinutes(2);
        await bridge.PollAsync();
        Assert.Equal(["7"], received);

        _now = _now.AddMinutes(2);
        await bridge.PollAsync();
        Assert.Equal(["7", "7"], received);
        Assert.Contains("GET https://discord.com/api/v10/channels/capture/messages/7", _discord.Asked);

        // Once its work is saved, it's never handed over again.
        _database.FinishDiscordMessage("7", _now);
        _now = _now.AddHours(1);
        await bridge.PollAsync();
        Assert.Equal(["7", "7"], received);
    }

    [Fact]
    public async Task A_message_deleted_before_its_work_could_be_done_again_is_let_go()
    {
        Assert.True(_database.ClaimDiscordMessage("9", "capture", _now, DiscordBridge.UnfinishedAfter));
        _database.SetPreference(DiscordBridge.LastMessageKey, "9");
        var bridge = Bridge();
        var received = new List<string>();
        bridge.Captured = message => received.Add(message.Id);

        _now = _now.AddMinutes(5);
        await bridge.PollAsync();

        Assert.Empty(received);
        Assert.True(_database.IsDiscordMessageFinished("9"));
    }

    [Fact]
    public async Task An_unfinished_message_that_no_longer_asks_for_anything_is_let_go_after_one_look()
    {
        // Claimed as the owner's, but read back it's one Waypoint doesn't act on, as after an edit or a settings change.
        _database.SetPreference(DiscordBridge.LastMessageKey, "50");
        _discord.Messages["21"] = """{"id":"21","channel_id":"capture","content":"made up","author":{"id":"someone"}}""";
        Assert.True(_database.ClaimDiscordMessage("21", "capture", _now, DiscordBridge.UnfinishedAfter));
        var bridge = Bridge();
        bridge.Captured = _ => throw new InvalidOperationException("not the owner's");

        for (var poll = 0; poll < 10; poll++)
        {
            _now = _now.AddMinutes(4);
            await bridge.PollAsync();
        }

        Assert.Single(_discord.Asked, asked => asked.EndsWith("/channels/capture/messages/21"));
        Assert.True(_database.IsDiscordMessageFinished("21"));
    }

    [Fact]
    public async Task An_unfinished_message_that_cant_be_read_back_is_only_tried_a_few_times()
    {
        _database.SetPreference(DiscordBridge.LastMessageKey, "50");
        _discord.Forbidden.Add("22");
        Assert.True(_database.ClaimDiscordMessage("22", "capture", _now, DiscordBridge.UnfinishedAfter));
        var bridge = Bridge();

        for (var poll = 0; poll < 10; poll++)
        {
            _now = _now.AddMinutes(4);
            await bridge.PollAsync();
        }

        Assert.InRange(_discord.Asked.Count(asked => asked == "GET https://discord.com/api/v10/channels/capture/messages/22"), 1, 4);
    }

    [Fact]
    public async Task A_message_that_never_finishes_is_answered_once_when_it_is_given_up_on()
    {
        _database.SetPreference(DiscordBridge.LastMessageKey, "50");
        _discord.Add("23", "made up errand");

        // Handed over again and again without its work ever being saved.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.True(_database.ClaimDiscordMessage("23", "capture", _now, DiscordBridge.UnfinishedAfter));
            _now = _now.AddMinutes(4);
        }

        var bridge = Bridge();
        bridge.Captured = _ => { };
        await bridge.PollAsync();
        _now = _now.AddMinutes(10);
        await bridge.PollAsync();

        var told = Assert.Single(Posted());
        Assert.Equal(DiscordBridge.GaveUp, JsonDocument.Parse(told).RootElement.GetProperty("embeds")[0].GetProperty("description").GetString());
        Assert.Contains("\"message_id\":\"23\"", told);
        Assert.True(_database.IsDiscordMessageFinished("23"));
    }

    [Fact]
    public async Task A_typed_done_handed_over_again_never_finishes_a_reminder_sent_after_it_was_typed()
    {
        var older = _database.AddByHand(new HandAddedItem(ItemKind.Task, "Made up older", null), _now).Item!;
        var newer = _database.AddByHand(new HandAddedItem(ItemKind.Task, "Made up newer", null), _now).Item!;
        _database.TrackSentReminder("100", older.Id);
        var bridge = Bridge();
        bridge.AlertActed = action => _database.ApplyAlertAction(action, _now, 15);

        // Typed as message 150 under no card. The older card is answered, then a newer one goes out at 200.
        await bridge.ActOnAlertAsync(new AlertAction("100", AlertActionKind.Done));
        _database.TrackSentReminder("200", newer.Id);
        Assert.True(_database.ClaimDiscordMessage("150", "alerts", _now, DiscordBridge.UnfinishedAfter));

        await bridge.ActOnAlertAsync(new AlertAction(null, AlertActionKind.Done, "150"));
        Assert.Null(_database.ApplyAlertAction(new AlertAction(null, AlertActionKind.Done, "151"), _now, 15));

        Assert.Equal(["Made up newer"], _database.GetOpenItems().Select(item => item.Title));
        Assert.True(_database.IsDiscordMessageFinished("150"));
    }

    [Fact]
    public void Messages_seen_before_the_upgrade_still_count_as_done()
    {
        _database.SetPreference("discord_seen", "made-up-1 made-up-2");

        Assert.False(_database.ClaimDiscordMessage("made-up-2", "capture", _now, DiscordBridge.UnfinishedAfter));
        Assert.True(_database.ClaimDiscordMessage("made-up-3", "capture", _now, DiscordBridge.UnfinishedAfter));
        Assert.False(_database.ClaimDiscordMessage("made-up-3", "capture", _now, DiscordBridge.UnfinishedAfter));
    }

    [Fact]
    public async Task A_capture_lost_to_a_crash_before_it_was_saved_is_saved_once_on_the_next_start()
    {
        _database.SetPreference(DiscordBridge.LastMessageKey, "5");
        _discord.Add("7", "buy made up stamps");

        // The message is taken, then Waypoint stops before the window thread gets to it.
        var crashed = new Host(hold: true);
        var first = Start(crashed);
        await PollUntil(first, () => crashed.Held.Count > 0);
        first.StopDiscord();
        Assert.Empty(PendingTexts());

        _now = _now.AddMinutes(5);
        var second = Start(new Host());
        await PollUntil(second, () => PendingTexts().Count > 0);
        Assert.Equal(["buy made up stamps"], PendingTexts());
        Assert.True(_database.IsDiscordMessageFinished("7"));

        // Later polls and a further start never save it again.
        _now = _now.AddMinutes(10);
        await second.Discord!.PollAsync();
        second.StopDiscord();
        var third = Start(new Host());
        await third.Discord!.PollAsync();
        Assert.Equal(["buy made up stamps"], PendingTexts());
    }

    [Fact]
    public async Task A_file_skipped_while_closing_is_read_on_the_next_start_and_the_words_are_saved_once()
    {
        _database.SetPreference(DiscordBridge.LastMessageKey, "5");
        _discord.Add("7", "made up words", withFile: true);

        // Closing runs what was left for the window thread after Discord has stopped, so the file can't be fetched.
        var closing = new Host(hold: true);
        var first = Start(closing);
        await PollUntil(first, () => closing.Held.Count > 0);
        first.StopDiscord();
        closing.Held.ForEach(work => work());
        Assert.Equal(["made up words"], PendingTexts());
        Assert.False(_database.IsDiscordMessageFinished("7"));

        _now = _now.AddMinutes(5);
        var second = Start(new Host());
        await PollUntil(second, () => _database.IsDiscordMessageFinished("7"));

        Assert.Equal(["made up words", "made up thing one\nmade up thing two"], PendingTexts());
        Assert.Single(_discord.Asked, asked => asked == $"GET {FileUrl}");

        _now = _now.AddMinutes(10);
        await second.Discord!.PollAsync();
        Assert.Equal(2, PendingTexts().Count);
    }

    [Fact]
    public async Task A_file_still_being_read_after_a_few_minutes_is_not_read_a_second_time()
    {
        _database.SetPreference(DiscordBridge.LastMessageKey, "5");
        _discord.Add("7", "made up words", withFile: true);
        var reads = 0;
        using var reading = new SemaphoreSlim(0);
        var settings = new WaypointSettings(Discord: new DiscordSettings("token", "owner", "capture", "alerts"));
        var worker = new WaypointWorker(_database, settings, _folder, new Host(), _ => { }, new WorkerParts(
            _ => new NoTokens(),
            Clock: () => _now,
            Http: () => new HttpClient(_discord),
            CaptureSorter: new NullSorter(),
            ReadFile: path =>
            {
                Interlocked.Increment(ref reads);
                var text = File.ReadAllText(path);
                reading.Wait(TimeSpan.FromSeconds(20));
                return new FileText("made-up-list.txt", text);
            },
            ConnectGateway: false))
        {
            RetryWait = _ => Task.CompletedTask,
        };
        worker.StartDiscord();
        await PollUntil(worker, () => Volatile.Read(ref reads) >= 1);

        // A slow read is still going when the claim would count as cut off.
        for (var poll = 0; poll < 3; poll++)
        {
            _now = _now.AddMinutes(4);
            await worker.Discord!.PollAsync();
        }

        reading.Release(5);
        await PollUntil(worker, () => _database.IsDiscordMessageFinished("7"));

        Assert.Equal(1, Volatile.Read(ref reads));
        Assert.Equal(["made up words", "made up thing one\nmade up thing two"], PendingTexts());
        Assert.DoesNotContain(_discord.Asked, asked => asked.EndsWith("/channels/capture/messages/7"));
        worker.StopDiscord();
    }

    [Fact]
    public async Task A_reply_to_a_capture_saved_before_a_restart_still_goes_under_its_message()
    {
        _database.SetPreference(DiscordBridge.LastMessageKey, "5");
        _discord.Add("7", "made up question");
        var crashed = new Host(hold: true);
        var first = Start(crashed);
        await PollUntil(first, () => crashed.Held.Count > 0);
        crashed.Held.ForEach(work => work());
        first.StopDiscord();
        var id = _database.GetPendingCaptures().Single().Id;

        var second = Start(new Host());
        second.ReplyInDiscord(new CaptureOutcome([], [], "Made up answer", Origin: CaptureOrigin.Discord) { CaptureId = id }, "Made up answer");

        await PollUntil(second, () => Posted().Any(body => body.Contains("Made up answer")));
        Assert.Contains("\"message_id\":\"7\"", Posted().Single(body => body.Contains("Made up answer")));
    }

    [Fact]
    public void A_typed_done_handed_over_twice_finishes_only_one_reminder()
    {
        var first = _database.AddByHand(new HandAddedItem(ItemKind.Task, "Made up first", null), _now).Item!;
        var second = _database.AddByHand(new HandAddedItem(ItemKind.Task, "Made up second", null), _now).Item!;
        _database.TrackSentReminder("card-1", first.Id);
        Assert.True(_database.ClaimDiscordMessage("typed-1", "alerts", _now, DiscordBridge.UnfinishedAfter));

        Assert.Equal("Done: Made up first", _database.ApplyAlertAction(new AlertAction(null, AlertActionKind.Done, "typed-1"), _now, 15));

        // A newer card goes out, and the same typed reply comes round again after a restart.
        _database.TrackSentReminder("card-2", second.Id);
        Assert.Null(_database.ApplyAlertAction(new AlertAction(null, AlertActionKind.Done, "typed-1"), _now, 15));

        Assert.Equal(["Made up second"], _database.GetOpenItems().Select(item => item.Title));
    }

    [Fact]
    public async Task A_notes_channel_message_sent_while_waypoint_was_off_is_handed_over_once_when_it_is_back()
    {
        _database.SetPreference(DiscordBridge.LastMessageKey, "5");
        _database.SetPreference(DiscordBridge.LastNotesMessageKey, "5");
        _discord.Add("8", "made up notes question", channel: "notes");
        _discord.Messages["9"] = """{"id":"9","channel_id":"notes","content":"made up","author":{"id":"someone"}}""";
        var bridge = Bridge(notes: "notes");
        var captured = new List<string>();
        var notes = new List<string>();
        bridge.Captured = message => captured.Add(message.Id);
        bridge.NotesSent = message => notes.Add(message.Id);

        await bridge.PollAsync();
        await bridge.PollAsync();

        Assert.Equal(["8"], notes);
        Assert.Empty(captured);
        Assert.Equal("9", _database.GetPreference(DiscordBridge.LastNotesMessageKey));

        // One sent later is read on the next poll, and the one before isn't handed over again.
        _discord.Add("12", "made up later", channel: "notes");
        await bridge.PollAsync();
        Assert.Equal(["8", "12"], notes);
    }

    [Fact]
    public async Task The_notes_channel_is_only_marked_on_its_first_poll_and_never_read_as_captures()
    {
        _database.SetPreference(DiscordBridge.LastMessageKey, "5");
        _discord.Add("8", "made up older note", channel: "notes");
        var bridge = Bridge(notes: "notes");
        var notes = new List<string>();
        bridge.NotesSent = message => notes.Add(message.Id);

        await bridge.PollAsync();

        Assert.Empty(notes);
        Assert.Equal("8", _database.GetPreference(DiscordBridge.LastNotesMessageKey));
        Assert.Empty(PendingTexts());
    }

    [Fact]
    public async Task A_notes_message_taken_before_a_crash_is_answered_exactly_once_after_the_next_start()
    {
        _database.SetPreference(DiscordBridge.LastMessageKey, "5");
        _database.SetPreference(DiscordBridge.LastNotesMessageKey, "5");
        _discord.Add("8", "made up words with no file", channel: "notes");

        // The poll takes the message, then Waypoint stops before the window thread gets to it.
        var crashed = new Host(hold: true);
        var first = Start(crashed, notes: "notes");
        await PollUntil(first, () => crashed.Held.Count > 0);
        first.StopDiscord();
        Assert.False(_database.IsDiscordMessageFinished("8"));
        Assert.DoesNotContain(Posted(), body => body.Contains("Send files here"));

        _now = _now.AddMinutes(5);
        var second = Start(new Host(), notes: "notes");
        await PollUntil(second, () => _database.IsDiscordMessageFinished("8"));
        await PollUntil(second, () => Posted().Any(body => body.Contains("Send files here")));

        // Later polls and a further start never answer it again.
        _now = _now.AddMinutes(10);
        await second.Discord!.PollAsync();
        second.StopDiscord();
        var third = Start(new Host(), notes: "notes");
        await third.Discord!.PollAsync();
        third.StopDiscord();

        var answer = Assert.Single(Posted(), body => body.Contains("Send files here"));
        Assert.Contains("\"message_id\":\"8\"", answer);
        Assert.Empty(PendingTexts());
    }
}
