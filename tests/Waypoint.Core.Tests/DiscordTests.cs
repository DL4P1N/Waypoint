using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Background;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Tests;

/// <summary>The live connection, replies, commands, and rate limits, all against a fake Discord with no network.</summary>
public sealed class DiscordTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public DiscordTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    /// <summary>Answers each request with what the script says, and keeps every request it saw.</summary>
    private sealed class FakeDiscord(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)>? script = null) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.ToString(), body));
            var (status, reply) = script?.Invoke(request) ?? (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(reply) };
        }
    }

    private DiscordBridge Bridge(FakeDiscord fake, List<TimeSpan>? waits = null) =>
        new(new HttpClient(fake), _database, new DiscordSettings("token", "owner", "100", "200", "300"))
        {
            // Tests that look for the immediate answer shouldn't race the 2 second deferral on a busy machine. The
            // ones about deferring set their own.
            DeferAfter = TimeSpan.FromMinutes(1),
            Delay = (wait, _) =>
            {
                waits?.Add(wait);
                return Task.CompletedTask;
            },
        };

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static JsonElement Message(string id, string channel, string author, string content = "hi", string extra = "") =>
        Json($$"""{"id":"{{id}}","channel_id":"{{channel}}","content":"{{content}}","author":{"id":"{{author}}"}{{extra}}}""");

    [Fact]
    public void Gateway_identifies_with_message_intents_then_resumes_after_a_drop()
    {
        var session = new GatewaySession("token");

        var hello = session.Read(Json("""{"op":10,"d":{"heartbeat_interval":41250}}"""));
        Assert.Equal(TimeSpan.FromMilliseconds(41250), hello.HeartbeatEvery);
        var identify = JsonSerializer.Serialize(hello.Send);
        Assert.Contains("\"op\":2", identify);
        Assert.Contains($"\"intents\":{GatewaySession.MessageIntents}", identify);

        var ready = session.Read(Json("""{"op":0,"t":"READY","s":1,"d":{"session_id":"abc","resume_gateway_url":"wss://resume.discord.gg"}}"""));
        Assert.Equal("READY", ready.Event);
        session.Read(Json("""{"op":0,"t":"MESSAGE_CREATE","s":5,"d":{}}"""));

        Assert.True(session.Read(Json("""{"op":7,"d":null}""")).Reconnect);
        Assert.Null(session.Closed(1006));
        Assert.StartsWith("wss://resume.discord.gg", session.Url);

        var resume = JsonSerializer.Serialize(session.Read(Json("""{"op":10,"d":{"heartbeat_interval":41250}}""")).Send);
        Assert.Contains("\"op\":6", resume);
        Assert.Contains("\"session_id\":\"abc\"", resume);
        Assert.Contains("\"seq\":5", resume);
    }

    [Fact]
    public void Gateway_starts_fresh_when_the_session_cannot_be_resumed()
    {
        var session = new GatewaySession("token");
        session.Read(Json("""{"op":0,"t":"READY","s":3,"d":{"session_id":"abc","resume_gateway_url":"wss://resume.discord.gg"}}"""));

        Assert.True(session.Read(Json("""{"op":9,"d":false}""")).Reconnect);

        Assert.False(session.CanResume);
        Assert.Contains("\"op\":2", JsonSerializer.Serialize(session.Read(Json("""{"op":10,"d":{"heartbeat_interval":1000}}""")).Send));
        Assert.StartsWith("wss://gateway.discord.gg", session.Url);
    }

    [Fact]
    public void Gateway_notices_an_unanswered_heartbeat_and_answers_one_asked_for()
    {
        var session = new GatewaySession("token");
        session.Read(Json("""{"op":0,"t":"READY","s":8,"d":{"session_id":"abc"}}"""));

        Assert.Contains("\"d\":8", JsonSerializer.Serialize(session.NextHeartbeat()));
        Assert.Null(session.NextHeartbeat());

        session.Read(Json("""{"op":11}"""));
        Assert.NotNull(session.NextHeartbeat());
        Assert.Contains("\"op\":1", JsonSerializer.Serialize(session.Read(Json("""{"op":1,"d":null}""")).Send));
    }

    [Fact]
    public void Gateway_falls_back_to_buttons_only_without_message_content_and_stops_on_a_bad_token()
    {
        var session = new GatewaySession("token");

        Assert.Null(session.Closed(4014));
        Assert.Equal(0, session.Intents);
        Assert.NotNull(session.Closed(4014));
        Assert.Equal("Discord rejected the bot token.", new GatewaySession("token").Closed(4004));
    }

    [Fact]
    public async Task Live_messages_are_only_taken_from_the_owner_in_the_capture_channel()
    {
        var bridge = Bridge(new FakeDiscord());
        var received = new List<string>();
        bridge.Captured = message => received.Add(message.Text);

        await bridge.HandleEventAsync("MESSAGE_CREATE", Message("1", "100", "owner", "buy milk"));
        await bridge.HandleEventAsync("MESSAGE_CREATE", Message("2", "100", "stranger", "spam"));
        await bridge.HandleEventAsync("MESSAGE_CREATE", Json("""{"id":"4","channel_id":"100","content":"bot","author":{"id":"owner","bot":true}}"""));
        await bridge.HandleEventAsync("MESSAGE_CREATE", Message("5", "999", "owner", "other channel"));

        Assert.Equal(["buy milk"], received);
    }

    [Fact]
    public async Task A_message_taken_live_is_not_captured_again_by_the_next_poll()
    {
        _database.SetPreference("discord_last_message", "5");
        var fake = new FakeDiscord(_ => (HttpStatusCode.OK, """
            [{"id":"7","content":"later","author":{"id":"owner"}},
             {"id":"6","content":"live","author":{"id":"owner"}}]
            """));
        var bridge = Bridge(fake);
        var received = new List<string>();
        bridge.Captured = message => received.Add(message.Text);

        await bridge.HandleEventAsync("MESSAGE_CREATE", Message("6", "100", "owner", "live"));
        await bridge.PollAsync();
        await bridge.PollAsync();

        Assert.Equal(["live", "later"], received);
        Assert.Equal("7", _database.GetPreference("discord_last_message"));
    }

    [Fact]
    public async Task Files_come_with_the_message_even_without_text()
    {
        var bridge = Bridge(new FakeDiscord());
        var received = new List<DiscordMessage>();
        bridge.Captured = received.Add;

        await bridge.HandleEventAsync("MESSAGE_CREATE", Message("1", "100", "owner", "", ""","attachments":[{"id":"a1","filename":"syllabus.pdf","size":2048,"url":"https://cdn.discordapp.com/attachments/1/2/syllabus.pdf","content_type":"application/pdf"}]"""));
        await bridge.HandleEventAsync("MESSAGE_CREATE", Message("2", "100", "owner", "  "));

        var message = Assert.Single(received);
        Assert.Equal("", message.Text);
        Assert.Equal(new DiscordAttachment("a1", "syllabus.pdf", 2048, "https://cdn.discordapp.com/attachments/1/2/syllabus.pdf", "application/pdf"), Assert.Single(message.Attachments));
    }

    /// <summary>Answers each link with a redirect or a file, and can act like a client that followed a redirect itself.</summary>
    private sealed class Redirects(Func<Uri, (HttpStatusCode Status, string? Location, Uri? EndedAt)> script) : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!);
            var (status, location, endedAt) = script(request.RequestUri!);
            if (endedAt is not null)
            {
                request.RequestUri = endedAt;
            }

            var response = new HttpResponseMessage(status) { Content = new StringContent("file text"), RequestMessage = request };
            if (location is not null)
            {
                response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            }

            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task A_download_redirect_is_only_followed_to_discords_own_hosts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"waypoint-download-{Guid.NewGuid()}.txt");
        var redirects = new Redirects(url => url.AbsolutePath switch
        {
            "/away.txt" => (HttpStatusCode.Found, "https://169.254.169.254/latest/meta-data", null),
            "/plain.txt" => (HttpStatusCode.Found, "http://cdn.discordapp.com/x.txt", null),
            "/moved.txt" => (HttpStatusCode.MovedPermanently, "https://media.discordapp.net/here.txt", null),
            "/relative.txt" => (HttpStatusCode.Found, "/here.txt", null),
            "/loop.txt" => (HttpStatusCode.Found, "/loop.txt", null),
            "/followed.txt" => (HttpStatusCode.OK, null, new Uri("https://evil.test/file.txt")),
            _ => (HttpStatusCode.OK, null, null),
        });
        var bridge = new DiscordBridge(new HttpClient(new FakeDiscord()), _database, new DiscordSettings("token", "owner", "100")) { Downloads = new HttpClient(redirects) };

        try
        {
            Assert.Contains("didn't come from Discord", await bridge.DownloadAsync(new DiscordAttachment("1", "a.txt", 9, "https://cdn.discordapp.com/away.txt", null), path, 1000));
            Assert.DoesNotContain(redirects.Asked, url => url.Host == "169.254.169.254");
            Assert.Contains("didn't come from Discord", await bridge.DownloadAsync(new DiscordAttachment("2", "b.txt", 9, "https://cdn.discordapp.com/plain.txt", null), path, 1000));
            Assert.Contains("didn't come from Discord", await bridge.DownloadAsync(new DiscordAttachment("3", "c.txt", 9, "https://cdn.discordapp.com/followed.txt", null), path, 1000));
            Assert.Contains("too many", await bridge.DownloadAsync(new DiscordAttachment("4", "d.txt", 9, "https://cdn.discordapp.com/loop.txt", null), path, 1000));
            Assert.False(File.Exists(path));

            Assert.Null(await bridge.DownloadAsync(new DiscordAttachment("5", "e.txt", 9, "https://cdn.discordapp.com/moved.txt", null), path, 1000));
            Assert.Equal("file text", File.ReadAllText(path));
            Assert.Null(await bridge.DownloadAsync(new DiscordAttachment("6", "f.txt", 9, "https://cdn.discordapp.com/relative.txt", null), path, 1000));
            Assert.Contains(new Uri("https://cdn.discordapp.com/here.txt"), redirects.Asked);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Downloads_only_follow_discords_own_links_and_respect_the_size_limit()
    {
        var fake = new FakeDiscord(_ => (HttpStatusCode.OK, "file text"));
        var bridge = Bridge(fake);
        var path = Path.Combine(Path.GetTempPath(), $"waypoint-download-{Guid.NewGuid()}.txt");

        try
        {
            Assert.Contains("didn't come from Discord", await bridge.DownloadAsync(new DiscordAttachment("1", "a.txt", 9, "https://example.com/a.txt", null), path, 1000));
            Assert.Contains("too big", await bridge.DownloadAsync(new DiscordAttachment("2", "b.txt", 5000, "https://cdn.discordapp.com/b.txt", null), path, 1000));
            Assert.Empty(fake.Requests);

            Assert.Null(await bridge.DownloadAsync(new DiscordAttachment("3", "c.txt", 9, "https://cdn.discordapp.com/c.txt", null), path, 1000));
            Assert.Equal("file text", File.ReadAllText(path));

            // Discord's stated size can be wrong, so the real length is checked too.
            Assert.Contains("too big", await bridge.DownloadAsync(new DiscordAttachment("4", "d.txt", 1, "https://cdn.discordapp.com/d.txt", null), path, 4));
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Replies_point_at_the_message_they_answer_without_pinging()
    {
        var fake = new FakeDiscord(_ => (HttpStatusCode.OK, """{"id":"55"}"""));

        Assert.Equal("55", await Bridge(fake).ReplyAsync("42", DiscordText.Notice("Waypoint", "Added: milk")));

        var (_, url, body) = fake.Requests.Single();
        Assert.Contains("/channels/100/messages", url);
        Assert.Contains("\"message_reference\":{\"message_id\":\"42\",\"fail_if_not_exists\":false}", body);
        Assert.Contains("\"replied_user\":false", body);
    }

    [Fact]
    public async Task A_rate_limit_is_waited_out_and_tried_again()
    {
        var tries = 0;
        var fake = new FakeDiscord(_ => ++tries == 1
            ? ((HttpStatusCode)429, """{"message":"You are being rate limited.","retry_after":0.5,"global":false}""")
            : (HttpStatusCode.OK, """{"id":"9"}"""));
        var waits = new List<TimeSpan>();

        var delivery = await Bridge(fake, waits).DeliverAsync(DiscordText.Notice("Leave for class", ""));

        Assert.True(delivery.Sent);
        Assert.Equal("9", delivery.MessageId);
        Assert.Equal([TimeSpan.FromSeconds(0.5)], waits);
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task A_long_rate_limit_fails_the_send_so_the_alert_retry_takes_over()
    {
        var fake = new FakeDiscord(_ => ((HttpStatusCode)429, """{"retry_after":120}"""));
        var waits = new List<TimeSpan>();
        var bridge = Bridge(fake, waits);

        var delivery = await bridge.DeliverAsync(DiscordText.Notice("Leave for class", ""));

        Assert.False(delivery.Sent);
        Assert.Empty(waits);
        Assert.Single(fake.Requests);
        Assert.NotNull(bridge.LastError);
    }

    [Fact]
    public async Task Rate_limits_stop_after_a_few_tries()
    {
        var fake = new FakeDiscord(_ => ((HttpStatusCode)429, """{"retry_after":0.1}"""));

        Assert.False((await Bridge(fake).DeliverAsync(DiscordText.Notice("x", ""))).Sent);
        Assert.Equal(3, fake.Requests.Count);
    }

    private static JsonElement Command(string name, string user, string options = "[]") =>
        Json($$"""{"id":"9","token":"tok","type":2,"data":{"name":"{{name}}","options":{{options}}},"member":{"user":{"id":"{{user}}" """ + "}}}");

    [Fact]
    public async Task Slash_commands_reach_the_app_with_their_options()
    {
        var fake = new FakeDiscord();
        SlashCommand? used = null;

        await Bridge(fake).HandleInteractionAsync(
            Command("afford", "owner", """[{"name":"amount","type":10,"value":65.5}]"""),
            (_, _) => null,
            command =>
            {
                used = command;
                return new CommandReply(DiscordText.Notice("Can I afford it?", "Yes"), Private: true);
            });

        Assert.Equal("afford", used!.Name);
        Assert.Equal("65.5", used.Options["amount"]);
        var (_, url, body) = fake.Requests.Single();
        Assert.Contains("/interactions/9/tok/callback", url);
        Assert.Contains("\"type\":4", body);
        Assert.Contains("\"flags\":64", body);
        Assert.Contains("Can I afford it?", body);
    }

    [Fact]
    public async Task Only_the_owner_can_use_a_slash_command()
    {
        var fake = new FakeDiscord();
        var used = false;

        await Bridge(fake).HandleInteractionAsync(Command("budget", "stranger"), (_, _) => null, _ =>
        {
            used = true;
            return new CommandReply(DiscordText.Notice("x", ""));
        });

        Assert.False(used);
        Assert.Contains("Only the owner", fake.Requests.Single().Body);
    }

    [Fact]
    public async Task A_command_that_breaks_still_answers()
    {
        var fake = new FakeDiscord();

        await Bridge(fake).HandleInteractionAsync(Command("today", "owner"), (_, _) => null, _ => throw new InvalidOperationException("database is locked"));

        // The error's own words are for the log, not the phone.
        var body = fake.Requests.Single().Body;
        Assert.DoesNotContain("database is locked", body);
        Assert.Contains("nothing changed", body);
    }

    /// <summary>
    /// Holds the work until the bridge has promised Discord an answer, so it is slow however late the deferral timer
    /// fires. A fixed sleep could end first on a busy machine running every test at once, and then the answer rightly
    /// went straight back with no promise.
    /// </summary>
    private static void StaysSlowUntilPromised(FakeDiscord fake) =>
        Assert.True(SpinWait.SpinUntil(() => fake.Requests.Count > 0, TimeSpan.FromSeconds(30)), "The slow answer was never promised");

    [Fact]
    public async Task A_slow_command_is_promised_first_then_filled_in()
    {
        var fake = new FakeDiscord();
        var bridge = Bridge(fake);
        bridge.DeferAfter = TimeSpan.FromMilliseconds(50);
        var slow = Json("""{"id":"9","token":"tok","application_id":"app1","type":2,"data":{"name":"today","options":[]},"member":{"user":{"id":"owner"}}}""");

        await bridge.HandleInteractionAsync(slow, (_, _) => null, _ =>
        {
            StaysSlowUntilPromised(fake);
            return new CommandReply(DiscordText.Notice("Today", "Quiz at 9"));
        });

        Assert.Equal(2, fake.Requests.Count);
        Assert.Contains("/interactions/9/tok/callback", fake.Requests[0].Url);
        Assert.Contains("\"type\":5", fake.Requests[0].Body);
        Assert.Equal(HttpMethod.Patch, fake.Requests[1].Method);
        Assert.EndsWith("/webhooks/app1/tok/messages/@original", fake.Requests[1].Url);
        Assert.Contains("Quiz at 9", fake.Requests[1].Body);
    }

    [Fact]
    public async Task A_slow_pick_that_answers_privately_is_promised_as_a_private_reply()
    {
        var fake = new FakeDiscord();
        var bridge = Bridge(fake);
        bridge.DeferAfter = TimeSpan.FromMilliseconds(50);
        bridge.PressWillReply = (_, _, _) => PressReply.Private;
        bridge.Picked = (_, _, _) =>
        {
            StaysSlowUntilPromised(fake);
            return DiscordText.Notice("Purchases", "Coffee") with { Private = true };
        };

        await bridge.HandleInteractionAsync(Json("""
            {"id":"9","token":"tok","application_id":"app1","type":3,"data":{"custom_id":"waypoint:view-open","component_type":3,"values":["note:1"]},"member":{"user":{"id":"owner"}},"message":{"id":"m7"}}
            """), (_, _) => null);

        // A reply of its own, so the pinned message is never the one edited.
        Assert.Equal(2, fake.Requests.Count);
        Assert.Contains("\"type\":5", fake.Requests[0].Body);
        Assert.Contains("\"flags\":64", fake.Requests[0].Body);
        Assert.Contains("Coffee", fake.Requests[1].Body);
    }

    [Fact]
    public async Task A_slow_press_that_only_opens_a_form_still_shows_it()
    {
        var fake = new FakeDiscord();
        var bridge = Bridge(fake);
        bridge.DeferAfter = TimeSpan.FromMilliseconds(50);
        bridge.PressWillReply = (_, _, _) => PressReply.Form;
        bridge.Acted = async _ =>
        {
            await Task.Delay(400);
            return new ButtonResult { Form = new CardForm("x", "Search", [new FormBox("words", "Words", Required: true)]) };
        };

        await bridge.HandleInteractionAsync(Json("""
            {"id":"9","token":"tok","application_id":"app1","type":3,"data":{"custom_id":"waypoint:act:nsearch","component_type":2},"member":{"user":{"id":"owner"}},"message":{"id":"m7"}}
            """), (_, _) => null);

        var request = Assert.Single(fake.Requests);
        Assert.Contains("\"type\":9", request.Body);
    }

    [Fact]
    public async Task Live_buttons_use_the_apps_handler()
    {
        var bridge = Bridge(new FakeDiscord());
        AlertActionKind? pressed = null;
        bridge.Pressed = (_, kind) =>
        {
            pressed = kind;
            return "Started";
        };

        await bridge.HandleEventAsync("INTERACTION_CREATE", Json("""
            {"id":"9","token":"tok","type":3,"data":{"custom_id":"waypoint:start-next"},"member":{"user":{"id":"owner"}},"message":{"id":"700","embeds":[{"title":"Next: Paper"}]}}
            """));

        Assert.Equal(AlertActionKind.StartNext, pressed);
    }

    [Fact]
    public async Task Commands_are_set_for_the_server_once_on_connect()
    {
        var fake = new FakeDiscord(request => request.Method == HttpMethod.Get
            ? (HttpStatusCode.OK, """{"id":"100","guild_id":"777"}""")
            : (HttpStatusCode.OK, "[]"));
        var bridge = Bridge(fake);
        var connected = 0;
        bridge.Connected += () => connected++;

        await bridge.HandleEventAsync("READY", Json("""{"session_id":"s","application":{"id":"app1"}}"""));
        await bridge.HandleEventAsync("READY", Json("""{"session_id":"s","application":{"id":"app1"}}"""));

        Assert.Equal(2, connected);
        var put = Assert.Single(fake.Requests, request => request.Method == HttpMethod.Put);
        Assert.EndsWith("/applications/app1/guilds/777/commands", put.Url);
        foreach (var name in new[] { "now", "today", "budget", "afford", "snooze", "save-spot" })
        {
            Assert.Contains($"\"name\":\"{name}\"", put.Body);
        }
    }

    [Fact]
    public async Task A_reaction_on_an_alert_is_applied_and_grays_the_card()
    {
        var fake = new FakeDiscord(request => request.Method == HttpMethod.Get
            ? (HttpStatusCode.OK, """{"id":"700","embeds":[{"title":"Call mom"}]}""")
            : (HttpStatusCode.OK, "{}"));
        var bridge = Bridge(fake);
        var acted = new List<AlertAction>();
        bridge.AlertActed = action =>
        {
            acted.Add(action);
            return "Done: Call mom";
        };

        await bridge.HandleEventAsync("MESSAGE_REACTION_ADD", Json("""{"user_id":"owner","channel_id":"200","message_id":"700","emoji":{"name":"✅"}}"""));
        await bridge.HandleEventAsync("MESSAGE_REACTION_ADD", Json("""{"user_id":"stranger","channel_id":"200","message_id":"700","emoji":{"name":"✅"}}"""));
        await bridge.HandleEventAsync("MESSAGE_REACTION_ADD", Json("""{"user_id":"owner","channel_id":"200","message_id":"700","emoji":{"name":"😀"}}"""));

        Assert.Equal([new AlertAction("700", AlertActionKind.Done)], acted);
        var patch = Assert.Single(fake.Requests, request => request.Method == HttpMethod.Patch);
        Assert.EndsWith("/channels/200/messages/700", patch.Url);
        Assert.Contains("\"components\":[]", patch.Body);
        Assert.Contains("Call mom", patch.Body);
    }

    [Fact]
    public async Task A_typed_reply_taken_live_is_not_applied_again_by_polling()
    {
        _database.SetPreference("discord_last_alert", "5");
        var fake = new FakeDiscord(_ => (HttpStatusCode.OK, """[{"id":"6","content":"done","author":{"id":"owner"}}]"""));
        var bridge = Bridge(fake);
        var acted = new List<AlertAction>();
        bridge.AlertActed = action =>
        {
            acted.Add(action);
            return null;
        };

        await bridge.HandleEventAsync("MESSAGE_CREATE", Message("6", "200", "owner", "done"));
        var polled = await bridge.ReadAlertActionsAsync();

        Assert.Equal([new AlertAction(null, AlertActionKind.Done, "6")], acted);
        Assert.Empty(polled);
    }

    [Fact]
    public async Task A_handled_alert_with_no_card_known_gets_a_short_note()
    {
        var fake = new FakeDiscord();
        var bridge = Bridge(fake);
        bridge.AlertActed = _ => "Back in 15 minutes: call";

        await bridge.ActOnAlertAsync(new AlertAction(null, AlertActionKind.Snooze));

        var (method, url, body) = fake.Requests.Single();
        Assert.Equal(HttpMethod.Post, method);
        Assert.Contains("/channels/200/messages", url);
        Assert.Contains("Back in 15 minutes", body);
    }

    [Fact]
    public async Task The_test_message_reports_why_it_failed()
    {
        Assert.Null(await Bridge(new FakeDiscord()).SendTestAsync());
        Assert.NotNull(await Bridge(new FakeDiscord(_ => (HttpStatusCode.Forbidden, """{"message":"Missing Access"}"""))).SendTestAsync());
    }

    [Fact]
    public void Ids_saved_on_the_page_sit_over_settings_json()
    {
        var file = new DiscordSettings("file-token", "111111111111111111", "222222222222222222", "333333333333333333");

        Assert.Equal(file, DiscordSetup.Combine(file, null, _database));
        Assert.Null(DiscordSetup.SaveIds(_database, null, "444444444444444444", null, null));

        var combined = DiscordSetup.Combine(file, "page-token", _database)!;
        Assert.Equal("page-token", combined.BotToken);
        Assert.Equal("111111111111111111", combined.OwnerUserId);
        Assert.Equal("444444444444444444", combined.CaptureChannelId);
        Assert.Equal("333333333333333333", combined.AlertsChannelId);
    }

    [Fact]
    public void Discord_needs_a_token_and_an_owner_and_ids_are_digits()
    {
        Assert.Null(DiscordSetup.Combine(null, null, _database));
        Assert.Null(DiscordSetup.Combine(null, "token", _database));
        Assert.NotNull(DiscordSetup.SaveIds(_database, "@me", null, null, null));
        Assert.Null(_database.GetPreference(DiscordSetup.OwnerKey));

        Assert.Null(DiscordSetup.SaveIds(_database, "111111111111111111", null, null, null));
        Assert.Equal("111111111111111111", DiscordSetup.Combine(null, "token", _database)!.OwnerUserId);
    }

    [Fact]
    public async Task Gateway_events_are_handled_one_at_a_time_in_the_order_they_came()
    {
        var failures = 0;
        var events = new OrderedWork(_ => Interlocked.Increment(ref failures));
        var order = new List<int>();
        var running = 0;
        var overlapped = false;
        var last = new TaskCompletionSource();

        // The earlier ones take longer, as a slow close would, so side by side they'd be saved out of order.
        for (var index = 0; index < 10; index++)
        {
            var number = index;
            events.Post(async () =>
            {
                overlapped |= Interlocked.Increment(ref running) > 1;
                await Task.Delay(10 - number);
                order.Add(number);
                Interlocked.Decrement(ref running);
            });
        }

        events.Post(() => throw new InvalidOperationException("broken"));
        events.Post(() => last.SetResult());
        await last.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(Enumerable.Range(0, 10), order);
        Assert.False(overlapped);
        Assert.Equal(1, failures);
    }

    [Fact]
    public async Task Looking_for_an_earlier_post_takes_only_this_bots_own_and_reads_past_the_first_page()
    {
        var card = new DiscordCard("Made up card", "Made up words", [], CardTone.Reminder);
        var shown = """[{"title":"Made up card","description":"Made up words"}]""";
        var fake = new FakeDiscord(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/users/@me", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, """{"id":"self"}""");
            }

            // A full first page of the bot's other posts, then another bot's look-alike before the bot's own.
            if (url.Contains("after=1000", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, "[" + string.Join(",", Enumerable.Range(1001, 100).Select(id => $$"""{"id":"{{id}}","author":{"id":"self","bot":true},"embeds":[{"title":"Something else"}]}""")) + "]");
            }

            return url.Contains("after=1100", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, $$"""[{"id":"1102","author":{"id":"self","bot":true},"embeds":{{shown}}},{"id":"1101","author":{"id":"another-bot","bot":true},"embeds":{{shown}}}]""")
                : (HttpStatusCode.OK, "[]");
        });

        var found = await Bridge(fake).FindSentAfterAsync("200", "1000", "made-up-nonce", card, new HashSet<string>());

        Assert.Equal(new SentSearch(SentLookup.Found, "1102"), found);
    }

    [Fact]
    public async Task Looking_for_an_earlier_post_says_it_isnt_known_when_the_channel_or_the_bot_cant_be_read()
    {
        var card = new DiscordCard("Made up card", null, [], CardTone.Reminder);
        var channelDown = new FakeDiscord(request => request.RequestUri!.ToString().EndsWith("/users/@me", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """{"id":"self"}""")
            : (HttpStatusCode.InternalServerError, ""));
        var nobody = new FakeDiscord(_ => (HttpStatusCode.Unauthorized, ""));
        var empty = new FakeDiscord(request => request.RequestUri!.ToString().EndsWith("/users/@me", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """{"id":"self"}""")
            : (HttpStatusCode.OK, "[]"));

        Assert.Equal(SentLookup.Unknown, (await Bridge(channelDown).FindSentAfterAsync("200", "1", "n", card, new HashSet<string>())).Result);
        Assert.Equal(SentLookup.Unknown, (await Bridge(nobody).FindSentAfterAsync("200", "1", "n", card, new HashSet<string>())).Result);
        Assert.Equal(SentLookup.NotFound, (await Bridge(empty).FindSentAfterAsync("200", "1", "n", card, new HashSet<string>())).Result);
    }
}
