using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Email;

namespace Waypoint.Core.Tests;

/// <summary>Discord as the phone app: pinned views edited in place, menus on them, and the manual-control commands. No network.</summary>
public sealed partial class DiscordPhoneTests : IDisposable
{
    // A Monday morning, outside any quiet time.
    private static readonly DateTime Morning = new(2026, 11, 9, 10, 0, 0);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Morning);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-phone-{Guid.NewGuid()}");
    private readonly WaypointDatabase _database;
    private readonly FakeDiscord _discord = new();
    private DateTime _now = Morning;

    public DiscordPhoneTests()
    {
        Directory.CreateDirectory(_folder);
        _database = new WaypointDatabase(Path.Combine(_folder, "waypoint.db"));
    }

    /// <summary>Every worker started here, so Dispose can stop its background work before the folder goes.</summary>
    private readonly List<WaypointWorker> _workers = [];

    /// <summary>
    /// Stops each worker and waits for its Discord work, so nothing recreates the folder after it's deleted. A view
    /// refresh can still be finishing, so the folder is then retried for a moment.
    /// </summary>
    public void Dispose()
    {
        foreach (var worker in _workers)
        {
            // On the thread pool, so this wait can't deadlock against the test's own synchronization context.
            Task.Run(async () =>
            {
                await worker.StopDiscordAsync(TimeSpan.FromSeconds(5));
                await Task.WhenAny(worker.ThreadsSettledAsync(), Task.Delay(TimeSpan.FromSeconds(5)));
            }).GetAwaiter().GetResult();
        }

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

    /// <summary>Gives each posted message the next ID, answers 404 for messages in Missing, and keeps every request.</summary>
    private sealed class FakeDiscord : HttpMessageHandler
    {
        private int _posts;

        public List<(HttpMethod Method, string Url, string Body)> Requests { get; } = [];

        public HashSet<string> Missing { get; } = [];

        /// <summary>A copy, since background refreshes can still be adding to the list.</summary>
        public List<(HttpMethod Method, string Url, string Body)> All
        {
            get
            {
                lock (Requests)
                {
                    return Requests.ToList();
                }
            }
        }

        public HttpStatusCode PinStatus { get; set; } = HttpStatusCode.NoContent;

        public HttpStatusCode? EditStatus { get; set; }

        /// <summary>When set, every request fails with this text, the way a dropped connection does.</summary>
        public string? FailWith { get; set; }

        /// <summary>When set, reading a channel's messages fails, the way an outage does.</summary>
        public bool ListFails { get; set; }

        /// <summary>When set, reading a channel's messages gets this answer instead, such as a channel that's gone.</summary>
        public HttpStatusCode? ListStatus { get; set; }

        public IEnumerable<(HttpMethod Method, string Url, string Body)> Of(HttpMethod method)
        {
            lock (Requests)
            {
                return Requests.Where(request => request.Method == method).ToList();
            }
        }

        private readonly List<(Func<(HttpMethod Method, string Url, string Body), bool> Match, int Count, TaskCompletionSource Done)> _waiting = [];

        /// <summary>
        /// Finishes once that many matching requests have been made, counting those made already. It's woken by the
        /// request itself, not by checking again and again, so a slow machine only makes it later, never wrong.
        /// </summary>
        public Task Made(Func<(HttpMethod Method, string Url, string Body), bool> match, int count = 1)
        {
            // Its continuation runs elsewhere, never inside the request that finished it.
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Requests)
            {
                if (Requests.Count(match) >= count)
                {
                    done.SetResult();
                }
                else
                {
                    _waiting.Add((match, count, done));
                }
            }

            // Only so a request that never comes fails the test rather than hanging it.
            return done.Task.WaitAsync(TimeSpan.FromMinutes(2));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var url = request.RequestUri!.ToString();
            lock (Requests)
            {
                Requests.Add((request.Method, url, body));
                foreach (var waiter in _waiting.Where(waiter => Requests.Count(waiter.Match) >= waiter.Count).ToList())
                {
                    _waiting.Remove(waiter);
                    waiter.Done.SetResult();
                }
            }

            if (FailWith is not null)
            {
                throw new HttpRequestException(FailWith);
            }

            if (request.Method == HttpMethod.Put && url.Contains("/pins/"))
            {
                return new HttpResponseMessage(PinStatus) { Content = new StringContent("{}") };
            }

            if (Missing.Any(id => url.EndsWith("/messages/" + id)))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"code":10008}""") };
            }

            if (request.Method == HttpMethod.Patch && EditStatus is { } status)
            {
                return new HttpResponseMessage(status) { Content = new StringContent("""{"code":50035,"message":"Invalid Form Body"}""") };
            }

            if (request.Method == HttpMethod.Get && url.EndsWith("/users/@me", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"bot"}""") };
            }

            if (request.Method == HttpMethod.Get && (ListFails || ListStatus is not null) && ListedChannel(url) is not null)
            {
                return new HttpResponseMessage(ListStatus ?? HttpStatusCode.ServiceUnavailable) { Content = new StringContent("") };
            }

            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ListMade && ListedChannel(url) is { } listed ? Listing(listed) : "[]") };
            }

            var id = request.Method == HttpMethod.Post ? Make(url, body) : "x";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"id":"{{id}}"}""") };
        }

        /// <summary>Each message made, by channel, with what was sent. A post sent again with its nonce isn't made twice.</summary>
        public List<(string Channel, string Id, string Body)> MadeMessages { get; } = [];

        /// <summary>When set, reading a channel's messages lists the ones made in it, as the bot's.</summary>
        public bool ListMade { get; set; }

        /// <summary>Discord only keeps to a nonce for a few minutes. Setting this is those minutes passing.</summary>
        public void ForgetNonces()
        {
            lock (_nonces)
            {
                _nonces.Clear();
            }
        }

        private readonly Dictionary<string, string> _nonces = [];

        private string Make(string url, string body)
        {
            var channel = System.Text.RegularExpressions.Regex.Match(url, "/channels/([^/]+)/messages$") is { Success: true } match ? match.Groups[1].Value : null;
            string? nonce = null;
            if (channel is not null && body.Length > 0)
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("enforce_nonce", out var enforce) && enforce.ValueKind == JsonValueKind.True && json.RootElement.TryGetProperty("nonce", out var sent))
                {
                    nonce = sent.GetString();
                }
            }

            lock (_nonces)
            {
                if (nonce is not null && _nonces.TryGetValue(nonce, out var made))
                {
                    return made;
                }

                var id = $"m{Interlocked.Increment(ref _posts)}";
                if (nonce is not null)
                {
                    _nonces[nonce] = id;
                }

                if (channel is not null)
                {
                    lock (MadeMessages)
                    {
                        MadeMessages.Add((channel, id, body));
                    }
                }

                return id;
            }
        }

        private static string? ListedChannel(string url) =>
            System.Text.RegularExpressions.Regex.Match(url, "/channels/([^/]+)/messages[?]") is { Success: true } match ? match.Groups[1].Value : null;

        /// <summary>The messages made in a channel as Discord lists them: the bot's, without the nonce.</summary>
        private string Listing(string channel)
        {
            lock (MadeMessages)
            {
                return "[" + string.Join(",", MadeMessages.Where(made => made.Channel == channel).Select(made =>
                {
                    using var json = JsonDocument.Parse(made.Body);
                    var embeds = json.RootElement.TryGetProperty("embeds", out var sent) ? sent.GetRawText() : "[]";
                    return $$"""{"id":"{{made.Id}}","channel_id":"{{channel}}","author":{"id":"bot","bot":true},"embeds":{{embeds}}}""";
                })) + "]";
            }
        }
    }

    /// <summary>The PC each worker here runs on, so what it showed is seen across restarts.</summary>
    private readonly Host _pc = new();

    private sealed class Host : IWorkerHost
    {
        /// <summary>The titles of the notices and reminders shown on the PC, in order.</summary>
        public List<string> Shown { get; } = [];

        public List<string> ShownCopy()
        {
            lock (Shown)
            {
                return Shown.ToList();
            }
        }

        public void ShowNotice(string title, string text)
        {
            lock (Shown)
            {
                Shown.Add(title);
            }
        }

        public void ShowReminder(DueReminder reminder, Action closed)
        {
            lock (Shown)
            {
                Shown.Add(reminder.Item.Title);
            }
        }

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

    private WaypointWorker Worker(string? toDo = "todo-ch", string? lists = "lists-ch", ICaptureSorter? sorter = null, Schedule.ClassSchedule? classes = null)
    {
        var settings = new WaypointSettings(Schedule: classes, Discord: new DiscordSettings("token", "owner", "capture", "alerts", "budget", toDo, lists));
        var worker = new WaypointWorker(_database, settings, _folder, _pc, exception => throw exception, new WorkerParts(
            _ => new NoTokens(),
            Clock: () => _now,
            Http: () => new HttpClient(_discord),
            CaptureSorter: sorter ?? new NullSorter(),
            ConnectGateway: false))
        {
            RetryWait = _ => Task.CompletedTask,
        };
        _workers.Add(worker);
        worker.StartDiscord();

        // These check the answer that comes straight away. On a busy machine running every test at once, the 2 second
        // default can pass first, and then the bridge rightly defers instead, which other tests cover.
        if (worker.Discord is { } bridge)
        {
            bridge.DeferAfter = TimeSpan.FromMinutes(1);
        }

        return worker;
    }

    /// <summary>Waits for the starting poll's background refresh to finish, then clears what it sent.</summary>
    private async Task<WaypointWorker> Started(string? toDo = "todo-ch", string? lists = "lists-ch")
    {
        var worker = Worker(toDo, lists);
        await worker.RefreshDiscordViewsAsync();
        await Task.Delay(50);
        return worker;
    }

    private StoredItem AddTask(string title, DateOnly? date) =>
        _database.AddByHand(new HandAddedItem(ItemKind.Task, title, date), _now).Item!;

    private static Dictionary<string, string> Options(params (string Name, string Value)[] options) =>
        options.ToDictionary(option => option.Name, option => option.Value);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static IEnumerable<string> FilesWithOneUnreadable(string good)
    {
        yield return good;
        throw new IOException("The file is open in another program.");
    }

    [Fact]
    public void Importing_several_files_says_what_went_in_even_when_one_could_not_be_read()
    {
        var worker = Worker();
        const string csv = "Posted On,Purchase Date,Category,Description,Amount\n10/02/2026,10/01/2026,Restaurants,LUNCH SPOT,12.00\n";

        var reply = worker.ImportStatementsReply(FilesWithOneUnreadable(csv));

        Assert.Equal("Added 1 purchase to Budget. 1 file couldn't be imported. The file is open in another program.", reply);
        Assert.StartsWith("Couldn't import that file. That file is empty.", worker.ImportStatementsReply([""]));
    }

    [Fact]
    public void Importing_a_file_with_rows_that_cannot_be_read_says_how_many_were_left_out()
    {
        var worker = Worker();
        const string csv = "Posted On,Purchase Date,Category,Description,Amount\n"
            + "10/02/2026,10/01/2026,Restaurants,LUNCH SPOT,12.00\n10/02/2026,soon,Restaurants,DINER,8.00\n10/03/2026,10/03/2026,Shopping,KIOSK,n/a\n";

        Assert.Equal("Added 1 purchase to Budget. 2 rows couldn't be read, so they were left out.", worker.ImportStatementsReply([csv]));
    }

    [Fact]
    public async Task Views_are_posted_and_pinned_once_then_only_edited_when_they_change()
    {
        AddTask("Read chapter 4", Today);
        _database.AddListEntry("Groceries", "eggs", _now);
        var worker = Worker();

        await worker.RefreshDiscordViewsAsync();
        await Task.Delay(50);

        // To do and Waiting on in one channel, then Shopping and Groceries in the lists channel.
        var posts = _discord.Of(HttpMethod.Post).ToList();
        Assert.Contains(posts, post => post.Url.EndsWith("/channels/todo-ch/messages") && post.Body.Contains("Read chapter 4") && post.Body.Contains("waypoint:view-done"));
        Assert.Contains(posts, post => post.Url.EndsWith("/channels/todo-ch/messages") && post.Body.Contains("Waiting on"));
        Assert.Contains(posts, post => post.Url.EndsWith("/channels/lists-ch/messages") && post.Body.Contains("eggs") && post.Body.Contains("waypoint:view-check"));
        Assert.Equal(posts.Count, _discord.Of(HttpMethod.Put).Count(put => put.Url.Contains("/messages/pins/")));
        Assert.All(posts, post => Assert.Contains("\"parse\":[]", post.Body));

        // Nothing changed, so a later round sends nothing.
        var before = _discord.All.Count;
        _now = _now.AddMinutes(2);
        await worker.RefreshDiscordViewsAsync();
        Assert.Equal(before, _discord.All.Count);

        // One change edits only the view it's in.
        AddTask("Email advisor", Today);
        _now = _now.AddMinutes(2);
        await worker.RefreshDiscordViewsAsync();
        var edit = Assert.Single(_discord.Of(HttpMethod.Patch));
        Assert.Contains("Email advisor", edit.Body);
        Assert.Contains("/channels/todo-ch/messages/", edit.Url);
        Assert.Equal(posts.Count, _discord.Of(HttpMethod.Post).Count());
    }

    [Fact]
    public async Task Views_wait_a_minute_unless_something_changed_from_Discord()
    {
        var worker = await Started();
        AddTask("Laundry", null);
        var before = _discord.All.Count;

        _now = _now.AddSeconds(20);
        await worker.RefreshDiscordViewsAsync();
        Assert.Equal(before, _discord.All.Count);

        _now = _now.AddSeconds(50);
        await worker.RefreshDiscordViewsAsync();
        Assert.Single(_discord.Of(HttpMethod.Patch));
    }

    [Fact]
    public async Task A_view_deleted_by_hand_is_sent_again_and_a_removed_list_is_taken_away()
    {
        _database.AddListEntry("Packing", "charger", _now);
        var worker = await Started(toDo: null);
        var listPosts = _discord.Of(HttpMethod.Post).Count();

        // Every message so far is gone, and Packing is removed.
        foreach (var number in Enumerable.Range(1, listPosts))
        {
            _discord.Missing.Add($"m{number}");
        }

        _database.AddListEntry(WaypointDatabase.ShoppingList, "milk", _now);
        _database.DeleteList("Packing", _now);
        _now = _now.AddMinutes(2);
        await worker.RefreshDiscordViewsAsync();

        Assert.Contains(_discord.Of(HttpMethod.Post).Skip(listPosts), post => post.Body.Contains("milk"));
        Assert.Contains(_discord.Of(HttpMethod.Delete), delete => delete.Url.Contains("/channels/lists-ch/messages/"));
    }

    [Fact]
    public async Task Without_pin_permission_the_view_still_works_and_says_why()
    {
        _discord.PinStatus = HttpStatusCode.Forbidden;
        var worker = await Started(lists: null);

        Assert.Contains(_discord.Of(HttpMethod.Post), post => post.Body.Contains("To do"));
        Assert.Contains("Pin Messages", worker.Discord!.PinProblem);
        Assert.Contains("Pin Messages", worker.DescribeConnections()[0].State);
    }

    [Fact]
    public async Task No_view_channels_means_no_calls()
    {
        AddTask("Read chapter 4", Today);
        var worker = Worker(toDo: null, lists: null);

        await worker.RefreshDiscordViewsAsync();

        // Polling still reads the capture and alerts channels, but nothing is sent, edited, or pinned.
        Assert.Empty(_discord.All.Where(request => request.Method != HttpMethod.Get));
    }

    [Fact]
    public async Task Pinhere_and_unpin_choose_where_a_view_goes()
    {
        var worker = await Started(toDo: null, lists: null);

        var pinned = worker.OnDiscordCommand(new SlashCommand("pinhere", Options(("view", "budget")), "money-ch"));
        Assert.True(pinned.Private);
        _now = _now.AddMinutes(2);
        await worker.RefreshDiscordViewsAsync();
        var budget = Assert.Single(_discord.Of(HttpMethod.Post), post => post.Url.Contains("/channels/money-ch/"));
        Assert.Contains("Budget", budget.Body);

        worker.OnDiscordCommand(new SlashCommand("unpin", Options(("view", "budget"))));
        _now = _now.AddMinutes(2);
        await worker.RefreshDiscordViewsAsync();
        Assert.Contains(_discord.Of(HttpMethod.Delete), delete => delete.Url.Contains("/channels/money-ch/messages/"));
    }

    [Fact]
    public async Task Marking_done_from_the_view_menu_redraws_it_in_place()
    {
        var item = AddTask("Read chapter 4", Today);
        AddTask("Email advisor", Today);
        var worker = await Started(lists: null);
        var toDoMessage = "m1";

        var card = worker.OnViewPicked(DiscordViews.DoneMenu, toDoMessage, [ItemRef.Format(item)]);

        Assert.DoesNotContain(_database.GetOpenItems(), open => open.Id == item.Id);
        Assert.DoesNotContain(card!.Fields, field => field.Value.Contains("Read chapter 4"));

        // The redraw was noted, so the next round doesn't edit it again for nothing.
        var before = _discord.Of(HttpMethod.Patch).Count();
        _now = _now.AddMinutes(2);
        await worker.RefreshDiscordViewsAsync();
        Assert.Equal(before, _discord.Of(HttpMethod.Patch).Count());
    }

    [Fact]
    public async Task Checking_off_from_a_list_view_menu()
    {
        _database.AddListEntry("Groceries", "eggs", _now);
        _database.AddListEntry("Groceries", "bread", _now);
        var worker = await Started(toDo: null);
        var eggs = _database.GetListEntries("Groceries").Single(entry => entry.Text == "eggs");

        // Shopping comes first, so Groceries is the second message.
        Assert.Equal(PinnedViews.ListPrefix + "Groceries", worker.Views.ViewFor("m2"));
        var card = worker.OnViewPicked(DiscordViews.CheckMenu, "m2", [eggs.Id.ToString()]);

        Assert.True(_database.GetListEntries("Groceries").Single(entry => entry.Text == "eggs").Done);
        Assert.Contains("~~eggs~~", card!.Description);
        Assert.DoesNotContain(card.Menu!.Choices, choice => choice.Label == "eggs");
    }

    [Fact]
    public async Task A_menu_pick_answers_by_updating_the_message_and_only_for_the_owner()
    {
        var bridge = Worker(toDo: null, lists: null).Discord!;
        string? picked = null;
        bridge.Picked = (menu, message, values) =>
        {
            picked = $"{menu} {message} {values.Single()}";
            return DiscordText.Notice("Groceries", "bread");
        };

        await bridge.HandleInteractionAsync(Json("""
            {"id":"9","token":"tok","type":3,"data":{"custom_id":"waypoint:view-check","component_type":3,"values":["42"]},"member":{"user":{"id":"owner"}},"message":{"id":"m7"}}
            """), (_, _) => null);
        await bridge.HandleInteractionAsync(Json("""
            {"id":"9","token":"tok","type":3,"data":{"custom_id":"waypoint:view-check","component_type":3,"values":["43"]},"member":{"user":{"id":"stranger"}},"message":{"id":"m7"}}
            """), (_, _) => null);

        Assert.Equal("waypoint:view-check m7 42", picked);
        var callbacks = _discord.All.Where(request => request.Url.Contains("/callback")).ToList();
        Assert.Contains("\"type\":7", callbacks[0].Body);
        Assert.Contains("bread", callbacks[0].Body);
        Assert.Contains("Only the owner", callbacks[1].Body);
    }

    [Fact]
    public async Task Suggestions_answer_while_typing_and_only_for_the_owner()
    {
        var item = AddTask("Read chapter 4", Today);
        AddTask("Laundry", null);
        var worker = Worker(toDo: null, lists: null);

        await worker.Discord!.HandleInteractionAsync(Json("""
            {"id":"9","token":"tok","type":4,"channel_id":"c","data":{"name":"done","options":[{"name":"item","type":3,"value":"chap","focused":true}]},"member":{"user":{"id":"owner"}}}
            """), (_, _) => null);
        await worker.Discord.HandleInteractionAsync(Json("""
            {"id":"9","token":"tok","type":4,"channel_id":"c","data":{"name":"done","options":[{"name":"item","type":3,"value":"","focused":true}]},"member":{"user":{"id":"stranger"}}}
            """), (_, _) => null);

        var callbacks = _discord.All.Where(request => request.Url.Contains("/callback")).ToList();
        Assert.Contains("\"type\":8", callbacks[0].Body);
        Assert.Contains(ItemRef.Format(item), callbacks[0].Body);
        Assert.DoesNotContain("Laundry", callbacks[0].Body);
        Assert.Contains("\"choices\":[]", callbacks[1].Body);
    }

    [Fact]
    public void Add_done_and_move_use_the_same_controls_as_the_app()
    {
        var worker = Worker(toDo: null, lists: null);

        var added = worker.OnDiscordCommand(new SlashCommand("add", Options(("title", "Lab report"), ("date", "friday"), ("kind", "deadline"))));
        Assert.True(added.Private);
        var report = Assert.Single(_database.GetOpenItems(), open => open.Title == "Lab report");
        Assert.Equal(new DateOnly(2026, 11, 13), report.DueDate);
        Assert.Equal(ItemKind.Deadline, report.Kind);

        var moved = worker.OnDiscordCommand(new SlashCommand("move", Options(("item", ItemRef.Format(report)), ("date", "11/16"))));
        Assert.Contains("Moved Lab report", moved.Card.Title);
        Assert.Equal(new DateOnly(2026, 11, 16), _database.GetOpenItems().Single(open => open.Title == "Lab report").DueDate);

        // The whole title typed out works without picking a suggestion, and the reply can undo it.
        var done = worker.OnDiscordCommand(new SlashCommand("done", Options(("item", "lab report"))));
        Assert.Equal("Finished Lab report", done.Card.Title);
        Assert.DoesNotContain(_database.GetOpenItems(), open => open.Title == "Lab report");
        Assert.Equal("Undo", Assert.Single(done.Card.Actions).Label);
    }

    [Fact]
    public async Task Part_of_a_title_asks_first_and_a_finish_can_be_undone()
    {
        var report = AddTask("Lab report", Today);
        var worker = Worker(toDo: null, lists: null);

        var asked = worker.OnDiscordCommand(new SlashCommand("done", Options(("item", "lab rep"))));

        Assert.True(asked.Private);
        Assert.Contains(_database.GetOpenItems(), open => open.Title == "Lab report");
        var pick = Assert.Single(asked.Card.Actions);
        Assert.Equal("Finish Lab report", pick.Label);

        var finished = await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + pick.Id, null, "m1", "c"));
        Assert.Equal("Finished Lab report", finished!.Card!.Title);
        Assert.DoesNotContain(_database.GetOpenItems(), open => open.Title == "Lab report");

        var undo = finished.Card.Actions.Single(action => action.Label == "Undo");
        var undone = await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + undo.Id, null, "m1", "c"));
        Assert.Equal("Lab report is back on the list", undone!.Card!.Title);
        Assert.Contains(_database.GetOpenItems(), open => open.Id == report.Id);
    }

    [Fact]
    public async Task Several_matches_are_listed_to_pick_from_instead_of_couldnt_find()
    {
        AddTask("Read chapter 4", Today);
        AddTask("Read chapter 5", Today.AddDays(1));
        var worker = Worker(toDo: null, lists: null);

        var asked = worker.OnDiscordCommand(new SlashCommand("start", Options(("item", "read"))));
        Assert.Contains("Read chapter 4", asked.Card.Description);
        Assert.DoesNotContain("couldn't find", asked.Card.Title + asked.Card.Description);
        Assert.Equal(["Start Read chapter 4", "Start Read chapter 5"], asked.Card.Actions.Select(action => action.Label));
        Assert.Null(_database.GetActiveSession(_now));

        var started = await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + asked.Card.Actions[1].Id, null, "m1", "c"));
        Assert.Equal("Read chapter 5", started!.Card!.Title);
        Assert.True(started.Card.HasSessionButtons);

        var move = worker.OnDiscordCommand(new SlashCommand("move", Options(("item", "read"), ("date", "friday"))));
        Assert.Contains("Read chapter 4", move.Card.Description);
    }

    [Fact]
    public async Task Listadd_to_a_list_that_isnt_there_asks_before_starting_one()
    {
        _database.AddListEntry("Groceries", "milk", _now);
        var worker = Worker(toDo: null, lists: null);

        var typo = worker.OnDiscordCommand(new SlashCommand("listadd", Options(("list", "Grocries"), ("entry", "eggs"))));
        Assert.Contains("Groceries", typo.Card.Description);
        Assert.DoesNotContain("Grocries", _database.GetListNames());

        var same = worker.OnDiscordCommand(new SlashCommand("listadd", Options(("list", "groceries"), ("entry", "eggs"))));
        Assert.Equal("Added eggs to Groceries", same.Card.Title);

        // Several at once, with commas between, the same as typing in a list's post.
        worker.OnDiscordCommand(new SlashCommand("listadd", Options(("list", "Groceries"), ("entry", "bread, butter"))));
        Assert.Contains(_database.GetListEntries("Groceries"), entry => entry.Text == "bread");
        Assert.Contains(_database.GetListEntries("Groceries"), entry => entry.Text == "butter");

        var start = Assert.Single(typo.Card.Actions);
        var made = await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + start.Id, null, "m1", "c"));
        Assert.Contains("Started a list called Grocries", made!.Outcome);
        Assert.Contains("Grocries", _database.GetListNames());
    }

    [Fact]
    public void A_check_that_misses_doesnt_look_like_it_worked()
    {
        _database.AddListEntry("Groceries", "milk", _now);
        var worker = Worker(toDo: null, lists: null);

        var missed = worker.OnDiscordCommand(new SlashCommand("check", Options(("entry", "bread"))));
        var hit = worker.OnDiscordCommand(new SlashCommand("check", Options(("entry", "milk"))));

        Assert.Equal(CardTone.Attention, missed.Card.Tone);
        Assert.Equal(CardTone.Finished, hit.Card.Tone);
    }

    [Fact]
    public void Snooze_grays_the_reminder_card_it_snoozed()
    {
        var item = AddTask("Call mom", Today);
        _database.TrackSentReminder("m42", item.Id);
        var worker = Worker(toDo: null, lists: null);

        var reply = worker.OnDiscordCommand(new SlashCommand("snooze", Options()));

        Assert.Contains("Call mom", reply.Card.Title);
        Assert.Null(_database.LatestSentReminderMessage());
        SpinWait.SpinUntil(() => _discord.Of(HttpMethod.Patch).Any(), 2000);
        Assert.Contains(_discord.Of(HttpMethod.Patch), patch => patch.Url.EndsWith("/channels/alerts/messages/m42"));
    }

    [Fact]
    public void A_time_with_no_kind_makes_a_reminder_for_the_next_time_it_comes()
    {
        var worker = Worker(toDo: null, lists: null);

        worker.OnDiscordCommand(new SlashCommand("add", Options(("title", "Call the landlord"), ("time", "9am"))));

        var reminder = Assert.Single(_database.GetOpenItems());
        Assert.Equal(ItemKind.Reminder, reminder.Kind);
        Assert.Equal(Today.AddDays(1), reminder.DueDate);
    }

    [Fact]
    public void Bad_dates_and_amounts_are_turned_away_without_adding_anything()
    {
        var worker = Worker(toDo: null, lists: null);

        var date = worker.OnDiscordCommand(new SlashCommand("add", Options(("title", "x"), ("date", "someday"))));
        Assert.True(date.Private);
        Assert.Contains("couldn't read that date", date.Card.Description);
        Assert.True(worker.OnDiscordCommand(new SlashCommand("spent", Options(("amount", "-3"), ("what", "coffee")))).Private);
        Assert.Empty(_database.GetOpenItems());
    }

    [Fact]
    public void Spent_check_and_listadd_reach_the_budget_and_lists()
    {
        var worker = Worker(toDo: null, lists: null);

        var spent = worker.OnDiscordCommand(new SlashCommand("spent", Options(("amount", "12.5"), ("what", "Lunch"))));
        Assert.Equal("Added $12.50 for Lunch", spent.Card.Title);

        _database.AddListEntry("Groceries", "milk", _now);
        worker.OnDiscordCommand(new SlashCommand("listadd", Options(("list", "Groceries"), ("entry", "eggs"))));
        Assert.Single(_database.GetListEntries("Groceries"), entry => entry.Text == "eggs" && !entry.Done);

        var suggested = worker.OnDiscordSuggest(new SlashCommand("check", Options(("entry", "eg"))), "entry");
        Assert.Equal("eggs", Assert.Single(suggested).Value);

        worker.OnDiscordCommand(new SlashCommand("check", Options(("entry", "eggs"))));
        Assert.True(_database.GetListEntries("Groceries").Single(entry => entry.Text == "eggs").Done);
    }

    [Fact]
    public void Start_with_no_item_picks_the_most_pressing_one()
    {
        AddTask("Read chapter 4", Today);
        var worker = Worker(toDo: null, lists: null);

        var started = worker.OnDiscordCommand(new SlashCommand("start", Options()));

        // The first step comes with the session's buttons, right in the reply.
        Assert.Equal("Read chapter 4", started.Card.Title);
        Assert.False(string.IsNullOrWhiteSpace(started.Card.Description));
        Assert.True(started.Card.HasSessionButtons);
        Assert.NotNull(_database.GetActiveSession(_now));
    }

    private async Task WaitFor(Func<bool> condition)
    {
        for (var tries = 0; tries < 300 && !condition(); tries++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task A_session_button_answers_on_its_own_card_and_not_in_the_capture_channel()
    {
        AddTask("Read chapter 4", Today);
        var worker = await Started(toDo: null, lists: null);
        worker.Captures.Sorted += outcome => worker.ReplyInDiscord(outcome, "said again");
        worker.OnDiscordCommand(new SlashCommand("start", Options()));

        var result = await worker.OnButtonActedAsync(new ButtonPress("waypoint:not-now", AlertActionKind.NotNow, "m5", "c"));

        Assert.NotNull(result!.Card);
        Assert.False(result.Card!.HasSessionButtons);
        await Task.Delay(100);
        Assert.DoesNotContain(_discord.Of(HttpMethod.Post), post => post.Url.Contains("/channels/capture/") && post.Body.Contains("said again"));
    }

    [Fact]
    public async Task Start_from_now_answers_on_the_card_with_the_first_step()
    {
        AddTask("Read chapter 4", Today);
        var worker = Worker(toDo: null, lists: null);

        var result = await worker.OnButtonActedAsync(new ButtonPress("waypoint:start-next", AlertActionKind.StartNext, "m5", "c"));

        Assert.Equal("Read chapter 4", result!.Card!.Title);
        Assert.True(result.Card.HasSessionButtons);
    }

    [Fact]
    public async Task The_wait_list_card_has_bought_and_skip_buttons_that_settle_it_there()
    {
        _database.AddWaitItem("Headphones", 8000, _now.AddHours(-50), 48);
        var worker = await Started(toDo: null, lists: null);

        worker.CheckAlerts();
        await WaitFor(() => _discord.Of(HttpMethod.Post).Any(post => post.Url.Contains("/channels/budget/") && post.Body.Contains("Headphones")));

        var card = _discord.Of(HttpMethod.Post).Single(post => post.Url.Contains("/channels/budget/") && post.Body.Contains("Headphones")).Body;
        Assert.DoesNotContain("Type bought", card);
        var id = _database.GetWaitList().Single().Id;
        Assert.Contains($"\"waypoint:act:wait-bought:{id}\"", card);
        Assert.Contains($"\"waypoint:act:wait-skip:{id}\"", card);

        var result = await worker.OnButtonActedAsync(new ButtonPress($"waypoint:act:wait-bought:{id}", null, "m9", "budget"));

        Assert.Equal("Headphones is off the wait list", result!.Outcome);
        Assert.Empty(_database.GetWaitList());
    }

    [Fact]
    public async Task A_carrying_button_is_answered_by_the_app_on_its_card()
    {
        var bridge = Worker(toDo: null, lists: null).Discord!;
        bridge.Acted = press => Task.FromResult<ButtonResult?>(new ButtonResult(Outcome: $"did {press.CustomId}"));

        await bridge.HandleInteractionAsync(Json("""
            {"id":"9","token":"tok","type":3,"channel_id":"budget","data":{"custom_id":"waypoint:act:wait-skip:4"},"member":{"user":{"id":"owner"}},"message":{"id":"m7","embeds":[{"title":"Wait's over: Headphones"}]}}
            """), (_, _) => null);

        var callback = Assert.Single(_discord.All, request => request.Url.Contains("/callback"));
        Assert.Contains("\"type\":7", callback.Body);
        Assert.Contains("Headphones", callback.Body);
        Assert.Contains("did waypoint:act:wait-skip:4", callback.Body);
    }

    [Fact]
    public void The_new_commands_are_registered_with_suggestions_where_they_name_something()
    {
        var json = JsonSerializer.Serialize(DiscordBridge.CommandList);

        foreach (var name in new[] { "todo", "add", "done", "move", "start", "spent", "lists", "check", "listadd", "waiting", "pinhere", "unpin", "now", "save-spot", "notes", "history", "search", "new", "settings", "order", "purchases" })
        {
            Assert.Contains($"\"name\":\"{name}\"", json);
        }

        Assert.Contains("\"autocomplete\":true", json);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetArrayLength() <= 100);
        foreach (var command in document.RootElement.EnumerateArray())
        {
            Assert.True(command.GetProperty("description").GetString()!.Length <= 100);
        }
    }

    [Fact]
    public void Help_lists_the_commands_and_an_old_command_points_to_it()
    {
        var worker = Worker(toDo: null, lists: null);
        var json = JsonSerializer.Serialize(DiscordBridge.CommandList);
        Assert.Contains("\"name\":\"help\"", json);
        Assert.Contains("Leave empty to pick one automatically", json);
        Assert.DoesNotContain("Left out", json);

        var help = worker.OnDiscordCommand(new SlashCommand("help", Options()));
        Assert.True(help.Private);
        Assert.Contains("/done", help.Card.Description);
        Assert.Contains("capture channel", help.Card.Description);

        var old = worker.OnDiscordCommand(new SlashCommand("oldthing", Options()));
        Assert.Contains("/todo", old.Card.Description);
        Assert.DoesNotContain("older version", old.Card.Title + old.Card.Description);
    }

    [Fact]
    public void Past_items_are_overdue_in_the_view_its_menu_and_suggestions()
    {
        AddTask("Old reading", Today.AddDays(-3));
        var worker = Worker(toDo: null, lists: null);

        var card = DiscordViews.ToDoCard(_database.GetOpenItems(), Today);
        Assert.Equal("Overdue", card.Fields[0].Name);
        Assert.Equal("Overdue", card.Menu!.Choices.Single().Description);

        var suggested = Assert.Single(worker.OnDiscordSuggest(new SlashCommand("done", Options(("item", "old"))), "item"));
        Assert.EndsWith("· Overdue", suggested.Label);

        var everything = JsonSerializer.Serialize(card) + suggested.Label;
        Assert.DoesNotContain("Behind", everything);
        Assert.DoesNotContain("Earlier", everything);
    }

    [Fact]
    public void Replies_have_a_real_headline_and_keep_sentences_whole()
    {
        var card = DiscordText.Reply("Added Call mom for today\nNote: bring the card");

        Assert.Equal("Added Call mom for today", card.Title);
        Assert.Equal("Note: bring the card", card.Description);
        Assert.Empty(card.Fields);
        Assert.Equal("Answer", DiscordText.Reply(new string('x', 200)).Title);
        Assert.NotEqual(0xFFFFFF, (int)CardTone.Reminder);
    }

    [Fact]
    public void Add_says_the_day_the_way_a_sentence_would()
    {
        var worker = Worker(toDo: null, lists: null);

        var tomorrow = worker.OnDiscordCommand(new SlashCommand("add", Options(("title", "Call the landlord"), ("date", "tomorrow"), ("time", "3pm"))));
        var friday = worker.OnDiscordCommand(new SlashCommand("add", Options(("title", "Lab report"), ("date", "friday"))));

        Assert.Equal("Added Call the landlord to To do, for tomorrow at 3:00 PM", tomorrow.Card.Title);
        Assert.Equal("Added Lab report to To do, for Friday", friday.Card.Title);
    }

    [Fact]
    public void An_empty_list_says_empty_and_a_cut_list_says_where_the_rest_is()
    {
        var lists = new[]
        {
            new ListOverview("Packing", []),
            new ListOverview("Errands", [new ListEntry(1, "Errands", "stamps", true, Morning)]),
        };

        var card = DiscordViews.ListsCard(lists);
        Assert.Equal("Empty", card.Fields[0].Value);
        Assert.Equal("All done", card.Fields[1].Value);

        var long_ = new ListOverview("Groceries", Enumerable.Range(0, 400).Select(number => new ListEntry(number, "Groceries", $"Entry {number} with a long name to fill the space", false, Morning)).ToList());
        Assert.Contains(DiscordViews.ListCard(long_).MoreRows.Single(), button => button.Id == "page:list:Groceries:1");
        Assert.DoesNotContain("in Ask", DiscordViews.BudgetCard([]).Description);
    }

    [Fact]
    public void Move_can_change_only_the_time()
    {
        var report = AddTask("Lab report", Today.AddDays(2));
        var worker = Worker(toDo: null, lists: null);

        worker.OnDiscordCommand(new SlashCommand("move", Options(("item", ItemRef.Format(report)), ("time", "3pm"))));

        var moved = _database.GetOpenItems().Single(open => open.Title == "Lab report");
        Assert.Equal(Today.AddDays(2), moved.DueDate);
        Assert.Equal(15, moved.DueAt!.Value.LocalDateTime.Hour);

        var neither = worker.OnDiscordCommand(new SlashCommand("move", Options(("item", ItemRef.Format(report)))));
        Assert.Contains("new day, a new time", neither.Card.Description);
    }

    [Theory]
    [InlineData("today", "2026-11-09")]
    [InlineData("Tomorrow", "2026-11-10")]
    [InlineData("monday", "2026-11-09")]
    [InlineData("next monday", "2026-11-16")]
    [InlineData("fri", "2026-11-13")]
    [InlineData("in 3 days", "2026-11-12")]
    [InlineData("11/14", "2026-11-14")]
    [InlineData("1/5", "2027-01-05")]
    [InlineData("11/1", "2026-11-01")]
    [InlineData("12/25/27", "2027-12-25")]
    [InlineData("Nov 20", "2026-11-20")]
    [InlineData("2026-12-01", "2026-12-01")]
    public void Typed_dates_are_read(string text, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), TypedDates.ParseDate(text, Today));

    [Theory]
    [InlineData("someday")]
    [InlineData("2/30")]
    [InlineData("13/1")]
    public void Unreadable_dates_are_null(string text) => Assert.Null(TypedDates.ParseDate(text, Today));

    [Theory]
    [InlineData("3pm", 15, 0)]
    [InlineData("3:30 PM", 15, 30)]
    [InlineData("9am", 9, 0)]
    [InlineData("12am", 0, 0)]
    [InlineData("15:30", 15, 30)]
    [InlineData("3", 15, 0)]
    [InlineData("noon", 12, 0)]
    public void Typed_times_are_read(string text, int hour, int minute) =>
        Assert.Equal(new TimeOnly(hour, minute), TypedDates.ParseTime(text));

    [Fact]
    public void Long_views_stay_inside_discords_limits()
    {
        for (var number = 0; number < 120; number++)
        {
            AddTask($"Problem set question {number} with a fairly long title to fill space", Today);
        }

        var card = DiscordViews.ToDoCard(_database.GetOpenItems(), Today);

        Assert.All(card.Fields, field => Assert.True(field.Value.Length <= 1024));
        Assert.Contains("page 1 of", card.Title);
        Assert.True(card.Menu!.Choices.Count <= 25);
        Assert.Contains(card.MoreRows.Single(), button => button.Label == "Next");
        var body = JsonSerializer.Serialize(card);
        Assert.Contains("Problem set question 0", body);
    }

    /// <summary>Discord counts the title, description, and every field name and value against one 6000-character total.</summary>
    private static int EmbedLength(DiscordCard card) =>
        card.Title.Length + (card.Description?.Length ?? 0) + card.Fields.Sum(field => field.Name.Length + field.Value.Length);

    [Fact]
    public void A_full_week_stays_inside_discords_total_for_one_card()
    {
        // Something behind, every day of the week, and undated, each with plenty of items.
        foreach (var offset in Enumerable.Range(-1, 8))
        {
            for (var number = 0; number < 30; number++)
            {
                AddTask($"Problem set question {number} with a fairly long title to fill space", Today.AddDays(offset));
            }
        }

        for (var number = 0; number < 30; number++)
        {
            AddTask($"Someday question {number} with a fairly long title to fill space", null);
        }

        // Every page fits, and every item is on exactly one of them.
        var shown = new List<string>();
        for (var page = 0; ; page++)
        {
            var card = DiscordViews.ToDoCard(_database.GetOpenItems(), Today, page);
            Assert.True(EmbedLength(card) <= 6000, $"Page {page} is {EmbedLength(card)} characters.");
            Assert.All(card.Fields, field => Assert.True(field.Value.Length <= 1024));
            Assert.True(card.Menu!.Choices.Count <= 25);
            shown.AddRange(card.Menu.Choices.Select(choice => choice.Value));
            if (!card.MoreRows.SelectMany(row => row).Any(button => button.Label == "Next"))
            {
                break;
            }
        }

        Assert.Equal(270, shown.Distinct().Count());
        Assert.Equal(270, shown.Count);
    }

    [Fact]
    public void Many_long_lists_stay_inside_discords_total_for_one_card()
    {
        var lists = Enumerable.Range(0, 25)
            .Select(number => new ListOverview($"List {number}", Enumerable.Range(0, 40)
                .Select(entry => new ListEntry(entry, $"List {number}", $"Entry {entry} with a fairly long name to fill space", false, Morning))
                .ToList()))
            .ToList();

        var card = DiscordViews.ListsCard(lists);

        Assert.True(EmbedLength(card) <= 6000, $"The card is {EmbedLength(card)} characters.");
        Assert.Contains(card.MoreRows.Single(), button => button.Id == "page:lists:1");
        var last = DiscordViews.ListsCard(lists, 99);
        Assert.Contains("List 24", last.Fields.Select(field => field.Name));
        Assert.Equal("Back", Assert.Single(last.MoreRows.Single()).Label);
    }

    [Fact]
    public async Task A_view_discord_turns_down_isnt_sent_again_until_it_changes()
    {
        var worker = await Started(lists: null);
        _discord.EditStatus = HttpStatusCode.BadRequest;

        AddTask("Read chapter 4", Today);
        _now = _now.AddMinutes(2);
        await worker.RefreshDiscordViewsAsync();
        Assert.Single(_discord.Of(HttpMethod.Patch));
        Assert.Contains("turned down", worker.DescribeConnections()[0].State);

        // The same content isn't tried again every minute.
        _now = _now.AddMinutes(2);
        await worker.RefreshDiscordViewsAsync();
        Assert.Single(_discord.Of(HttpMethod.Patch));

        // A change is tried, and once it goes through the problem is cleared.
        _discord.EditStatus = null;
        AddTask("Email advisor", Today);
        _now = _now.AddMinutes(2);
        await worker.RefreshDiscordViewsAsync();
        Assert.Equal(2, _discord.Of(HttpMethod.Patch).Count());
        Assert.DoesNotContain("turned down", worker.DescribeConnections()[0].State);
    }
}
