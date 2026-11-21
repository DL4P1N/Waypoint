using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Email;
using Waypoint.Core.Remote;

namespace Waypoint.Server.Tests;

/// <summary>The private API, run in memory against a database in a temporary folder. Nothing is sorted or sent.</summary>
public sealed class ApiTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-server-{Guid.NewGuid()}");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _token;

    public ApiTests()
    {
        Directory.CreateDirectory(_folder);
        _token = ServerToken.Create(_folder);
        _factory = StartServer();
    }

    /// <summary>A server over the test's folder. A second one stands in for the server after a restart.</summary>
    private WebApplicationFactory<Program> StartServer() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Waypoint:DataDirectory", _folder);
        builder.UseSetting("Waypoint:RunBackground", "false");
        builder.ConfigureServices(services =>
            services.AddSingleton(new WorkerParts(_ => new NoTokens(), CaptureSorter: new NeverSorts())));
    });

    public void Dispose()
    {
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private sealed class NoTokens : ITokenStore
    {
        public string? Load() => null;

        public void Save(string token)
        {
        }
    }

    /// <summary>Leaves every capture waiting, as when sorting isn't available.</summary>
    private sealed class NeverSorts : ICaptureSorter
    {
        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<CaptureResult?>(null);
    }

    private HttpClient Paired()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return client;
    }

    [Fact]
    public async Task The_PC_reports_its_idle_time_and_the_server_keeps_it_in_memory_for_check_ins()
    {
        var refused = await _factory.CreateClient().PostAsJsonAsync("/api/idle", new IdleReport(30), RemoteJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        var idle = _factory.Services.GetRequiredService<ReportedIdle>();
        Assert.Null(idle.IdleFor(DateTime.Now));

        var sent = await Paired().PostAsJsonAsync("/api/idle", new IdleReport(30), RemoteJson.Options);

        Assert.Equal(HttpStatusCode.NoContent, sent.StatusCode);
        Assert.InRange(idle.IdleFor(DateTime.Now)!.Value.TotalSeconds, 30, 60);
    }

    [Fact]
    public void The_worker_reads_the_same_idle_time_the_route_keeps() =>
        Assert.Same(_factory.Services.GetRequiredService<ReportedIdle>(), _factory.Services.GetRequiredService<WaypointServer>().Worker.UserIdle);

    /// <summary>On Windows the default host also writes to the Application event log, which tests and a trial server shouldn't fill.</summary>
    [Fact]
    public void The_server_logs_to_the_console_and_never_to_the_Windows_event_log()
    {
        var providers = _factory.Services.GetServices<Microsoft.Extensions.Logging.ILoggerProvider>().Select(provider => provider.GetType().Name).ToList();

        Assert.DoesNotContain("EventLogLoggerProvider", providers);
        Assert.Contains("ConsoleLoggerProvider", providers);
    }

    [Fact]
    public async Task Health_answers_without_a_token_and_says_nothing_about_the_data()
    {
        var response = await _factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"ok"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_warns_while_plain_http_is_allowed_outside_this_machine_and_tailscale()
    {
        using var exposed = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Configure<ServerOptions>(options =>
            {
                options.Listen = "http://192.168.1.20:5280";
                options.AllowPublicHttp = true;
            })));

        var response = await exposed.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Contains("Waypoint__AllowPublicHttp", body.GetProperty("warning").GetString());
    }

    [Theory]
    [InlineData("/api/status")]
    [InlineData("/api/items/open")]
    [InlineData("/api/captures/pending")]
    [InlineData("/api/lists")]
    [InlineData("/api/notes")]
    [InlineData("/api/budget")]
    public async Task Every_api_route_needs_the_token(string path)
    {
        var none = await _factory.CreateClient().GetAsync(path);

        var wrong = _factory.CreateClient();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token");
        var refused = await wrong.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Paired().GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task The_pairing_file_stays_through_wrong_tokens_and_goes_after_the_first_right_one()
    {
        var file = Path.Combine(_folder, ServerToken.PairingFileName);
        File.WriteAllText(file, _token);
        var wrong = _factory.CreateClient();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token");

        await wrong.GetAsync("/api/status");
        Assert.True(File.Exists(file));

        Assert.Equal(HttpStatusCode.OK, (await Paired().GetAsync("/api/status")).StatusCode);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task The_token_is_checked_before_the_body_is_read()
    {
        var client = _factory.CreateClient();

        var broken = await client.PostAsync("/api/captures", new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"));
        var unknown = await client.GetAsync("/api/no-such-route");
        var upperCase = await client.GetAsync("/API/status");

        Assert.Equal(HttpStatusCode.Unauthorized, broken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, upperCase.StatusCode);
    }

    [Fact]
    public async Task A_body_over_the_limit_is_refused()
    {
        using var small = _factory.WithWebHostBuilder(builder => builder.UseSetting("Waypoint:MaxRequestBytes", "1000"));
        var client = small.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        var big = await client.PostAsJsonAsync("/api/captures", new CaptureRequest("a", new string('x', 5000), DateTimeOffset.Now), RemoteJson.Options);
        var fine = await client.PostAsJsonAsync("/api/captures", new CaptureRequest("b", "buy milk", DateTimeOffset.Now), RemoteJson.Options);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
        Assert.True(fine.IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_capture_posted_without_the_token_is_not_saved()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/captures", new CaptureRequest("a", "buy milk", DateTimeOffset.Now), RemoteJson.Options);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await Paired().GetFromJsonAsync<List<PendingCapture>>("/api/captures/pending", RemoteJson.Options) ?? []);
    }

    [Fact]
    public async Task A_capture_round_trips_and_is_kept_once_when_sent_again()
    {
        var client = Paired();
        var capture = new CaptureRequest(Guid.NewGuid().ToString("N"), "call the dentist friday", DateTimeOffset.Now);

        var first = await client.PostAsJsonAsync("/api/captures", capture, RemoteJson.Options);
        var again = await client.PostAsJsonAsync("/api/captures", capture, RemoteJson.Options);

        Assert.False((await first.Content.ReadFromJsonAsync<CaptureAccepted>(RemoteJson.Options))!.Duplicate);
        Assert.True((await again.Content.ReadFromJsonAsync<CaptureAccepted>(RemoteJson.Options))!.Duplicate);
        var pending = await client.GetFromJsonAsync<List<PendingCapture>>("/api/captures/pending", RemoteJson.Options);
        Assert.Equal("call the dentist friday", Assert.Single(pending!).Text);
    }

    [Fact]
    public async Task A_capture_sent_again_after_a_restart_is_still_kept_once()
    {
        var capture = new CaptureRequest(Guid.NewGuid().ToString("N"), "return the library books", DateTimeOffset.Now);
        await Paired().PostAsJsonAsync("/api/captures", capture, RemoteJson.Options);

        // The server restarted before its answer reached the PC, so the PC sends it again.
        using var restarted = StartServer();
        var client = restarted.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        var again = await client.PostAsJsonAsync("/api/captures", capture, RemoteJson.Options);

        Assert.True((await again.Content.ReadFromJsonAsync<CaptureAccepted>(RemoteJson.Options))!.Duplicate);
        var pending = await client.GetFromJsonAsync<List<PendingCapture>>("/api/captures/pending", RemoteJson.Options);
        Assert.Equal("return the library books", Assert.Single(pending!).Text);
    }

    [Fact]
    public async Task The_pc_client_queues_captures_and_sends_them_through_the_api()
    {
        var server = new ServerClient(Paired(), _token);
        var queue = new CaptureQueue(Path.Combine(_folder, "queue.json"));
        queue.Add("first", DateTimeOffset.Now);
        queue.Add("second", DateTimeOffset.Now);

        var sent = await queue.SendAsync(capture => server.SendCaptureAsync(capture));

        Assert.Equal(2, sent);
        Assert.Equal(0, queue.Count);
        var pending = await Paired().GetFromJsonAsync<List<PendingCapture>>("/api/captures/pending", RemoteJson.Options);
        Assert.Equal(["first", "second"], pending!.Select(capture => capture.Text));
    }

    [Fact]
    public async Task A_task_sent_again_under_the_same_id_is_kept_once_and_one_from_a_pc_from_before_is_added_as_always()
    {
        var http = Paired();
        var task = new HandAddedItem(ItemKind.Task, "Fold the made up laundry", null);

        var first = await (await http.PostAsJsonAsync("/api/items?client=pc-a:1", task, RemoteJson.Options)).Content.ReadFromJsonAsync<HandAddResult>(RemoteJson.Options);
        var again = await (await http.PostAsJsonAsync("/api/items?client=pc-a:1", task, RemoteJson.Options)).Content.ReadFromJsonAsync<HandAddResult>(RemoteJson.Options);
        Assert.Equal(first!.Item!.Id, again!.Item!.Id);

        // Another id is another task, and a PC from before sends none, so each of its adds is one.
        await http.PostAsJsonAsync("/api/items?client=pc-a:2", task, RemoteJson.Options);
        await http.PostAsJsonAsync("/api/items", task, RemoteJson.Options);
        await http.PostAsJsonAsync("/api/items", task, RemoteJson.Options);
        var open = await http.GetFromJsonAsync<List<StoredItem>>("/api/items/open", RemoteJson.Options);
        Assert.Equal(4, open!.Count(item => item.Title == "Fold the made up laundry"));

        // Deleted since, it's added anew rather than lost.
        IToDoData data = new ServerToDoData(new ServerClient(Paired(), _token));
        data.DeleteItem(first.Item, DateTime.Now);
        var anew = await (await http.PostAsJsonAsync("/api/items?client=pc-a:1", task, RemoteJson.Options)).Content.ReadFromJsonAsync<HandAddResult>(RemoteJson.Options);
        Assert.NotEqual(first.Item.Id, anew!.Item!.Id);
    }

    [Fact]
    public void The_to_do_page_works_the_same_through_the_server()
    {
        var server = new ServerClient(Paired(), _token);
        IToDoData data = new ServerToDoData(server);

        var added = data.AddByHand(new HandAddedItem(ItemKind.Task, "Read chapter 4", DateOnly.FromDateTime(DateTime.Now)), DateTime.Now);
        var item = Assert.Single(data.GetOpenItems(), found => found.Title == "Read chapter 4");
        Assert.Equal(added.Item!.Id, item.Id);

        var filter = data.CreateToDoFilter("Work").Filter!;
        Assert.True(data.AssignToFilter(item, filter.Id));
        Assert.Equal(filter.Id, data.GetItemPlacements()[item.Id].FilterId);
        Assert.Null(data.RenameToDoFilter(filter.Id, "Job").Problem);
        Assert.Contains(data.GetToDoFilters(), found => found.Name == "Job");

        data.SetPreference("todo_hide_finished", "1");
        Assert.Equal("1", data.GetPreference("todo_hide_finished"));
        Assert.Null(data.GetPreference("never_set"));

        data.SetDone(item, true);
        Assert.DoesNotContain(data.GetOpenItems(), found => found.Id == item.Id);
        Assert.Contains(data.GetFinishedForList(DateTime.Now), found => found.Id == item.Id);

        var deleted = data.DeleteItem(item, DateTime.Now);
        Assert.NotNull(deleted);
        data.UndoDelete(deleted, DateTime.Now);
        Assert.Contains(data.GetFinishedForList(DateTime.Now), found => found.Id == item.Id);

        Assert.NotNull(data.StartActivation(null, "test", DateTime.Now).Text);
        Assert.Null(data.GetSpendSummary(DateOnly.FromDateTime(DateTime.Now)).SafeToSpend);
        Assert.Empty(data.GetShifts(DateOnly.FromDateTime(DateTime.Now)));
        Assert.Equal(ServerState.Online, server.State);
    }

    [Fact]
    public void A_long_task_brings_its_whole_text_through_the_server()
    {
        var database = _factory.Services.GetRequiredService<WaypointServer>().Database;
        var text = "Ask Morgan Vance whether the book club can meet in the library annex, and if not, find two cafes with a quiet back room before the Saturday meetup.";
        database.CompleteCapture(database.AddCapture(text), [new SortedItem(ItemKind.Task, text, null, null)]);
        IToDoData data = new ServerToDoData(new ServerClient(Paired(), _token));

        var item = Assert.Single(data.GetOpenItems());

        Assert.True(item.Title.Length < text.Length);
        Assert.Equal(text, item.Body);
    }

    [Fact]
    public void Waiting_on_someone_works_the_same_through_the_server()
    {
        IToDoData data = new ServerToDoData(new ServerClient(Paired(), _token));
        var today = DateOnly.FromDateTime(DateTime.Now);

        var added = data.AddWaitingByHand("Professor Quill", "the extension", today.AddDays(3), null, DateTime.Now);
        var item = added.Item!;
        var waiting = Assert.Single(data.GetWaiting());
        Assert.Equal("Professor Quill", waiting.Who);

        // Not due yet, so it's left out of what to do next, and the server agrees with the PC's own reading of it.
        Assert.DoesNotContain(data.WithoutQuietWaiting(data.GetOpenItems(), today), found => found.Id == item.Id);
        Assert.Contains(data.WithoutQuietWaiting(data.GetOpenItems(), today.AddDays(3)), found => found.Id == item.Id);

        Assert.NotNull(data.StillWaiting(item.Id, today.AddDays(5), DateTime.Now).Text);
        Assert.Equal(today.AddDays(5), Assert.Single(data.GetWaiting()).FollowUpOn);
        Assert.True(data.StopWaiting(item.Id));
        Assert.Empty(data.GetWaiting());
    }

    [Fact]
    public void Starting_with_a_first_action_goes_through_the_server()
    {
        IToDoData data = new ServerToDoData(new ServerClient(Paired(), _token));
        var item = data.AddByHand(new HandAddedItem(ItemKind.Task, "Study for the quiz", DateOnly.FromDateTime(DateTime.Now)), DateTime.Now).Item!;

        var reply = data.StartActivation(ItemRef.Format(item), "test", DateTime.Now, "Open the study guide");

        Assert.Contains("Open the study guide", reply.Text);
        Assert.Equal("Study for the quiz", data.GetActiveTaskTitle(DateTime.Now));
    }

    [Fact]
    public void The_suggestion_habits_come_from_the_server()
    {
        IToDoData data = new ServerToDoData(new ServerClient(Paired(), _token));

        var habits = data.GetSuggestionHabits(DateTime.Now);

        // The fallback has no lists at all, so empty ones show the answer came from the server.
        Assert.NotNull(habits.HardBlocks);
        Assert.Null(habits.LearnedTaskMinutes);
        Assert.Equal(NextAction.TaskMinutes, habits.TaskMinutes);
    }

    [Fact]
    public async Task Status_reports_waiting_captures_and_connections()
    {
        var client = Paired();
        await client.PostAsJsonAsync("/api/captures", new CaptureRequest("x1", "buy milk", DateTimeOffset.Now), RemoteJson.Options);

        var status = await new ServerClient(Paired(), _token).CheckAsync();

        Assert.Equal(1, status!.PendingCaptures);
        Assert.False(string.IsNullOrEmpty(status.Boot));
        Assert.Equal(["Discord", "Calendar feed", "Alert email"], status.Connections.Select(connection => connection.Name));
    }

    [Fact]
    public async Task Notices_that_couldnt_reach_discord_are_kept_and_shown_on_the_pc_once()
    {
        var host = _factory.Services.GetRequiredService<WaypointServer>().Host;
        host.ShowNotice("Time to leave", "Leave by 8:10 for Chemistry.");
        host.ShowNotice("Morning briefing", "Three things today.");

        var pc = new ServerClient(Paired(), _token);
        var status = await pc.CheckAsync();
        var shown = await pc.TakeNoticesAsync();

        // Both are kept, the earlier one isn't written over, and they come in the order they were meant to.
        Assert.Equal(["Time to leave", "Morning briefing"], status!.Notices!.Select(notice => notice.Title));
        Assert.Equal(["Time to leave", "Morning briefing"], shown.Select(notice => notice.Title));
        await pc.CheckAsync();
        Assert.Empty(await pc.TakeNoticesAsync());
        Assert.Empty(pc.LastStatus!.Notices!);
    }

    [Fact]
    public async Task A_kept_notice_is_tried_in_discord_again_until_it_goes()
    {
        var host = _factory.Services.GetRequiredService<WaypointServer>().Host;
        host.ShowNotice("Wait list", "Still want the headphones?");
        var tries = new List<string>();

        await host.RetryNoticesAsync((title, _) => { tries.Add(title); return Task.FromResult(false); });
        Assert.Single(_factory.Services.GetRequiredService<WaypointServer>().Database.GetUndeliveredNotices());

        await host.RetryNoticesAsync((title, _) => { tries.Add(title); return Task.FromResult(true); });
        Assert.Empty(_factory.Services.GetRequiredService<WaypointServer>().Database.GetUndeliveredNotices());
        Assert.Equal(["Wait list", "Wait list"], tries);
    }

    [Fact]
    public void A_refused_token_marks_the_pc_as_not_paired()
    {
        var client = _factory.CreateClient();
        var server = new ServerClient(client, "wrong");

        Assert.Empty(new ServerToDoData(server).GetOpenItems());
        Assert.Throws<ServerUnavailableException>(() => new ServerToDoData(server).SetDone(new StoredItem(1, ItemKind.Task, "x", null, null, false), true));
        Assert.Equal(ServerState.NotPaired, server.State);
        Assert.Contains("Pair it again", server.Problem);
    }
}
