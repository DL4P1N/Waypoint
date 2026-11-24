using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Email;
using Waypoint.Core.Files;
using Waypoint.Core.Notes;

namespace Waypoint.Core.Tests;

/// <summary>Notes and lists as forum posts, against a fake Discord that keeps forums, posts, and their messages.</summary>
public sealed partial class DiscordThreadTests : IDisposable
{
    private static readonly DateTime Morning = new(2026, 11, 15, 10, 0, 0);
    private const string NotesForum = "500000000000000001";
    private const string ListsForum = "500000000000000002";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-threads-{Guid.NewGuid()}");
    private readonly WaypointDatabase _database;
    private readonly FakeForums _discord = new();
    private DateTime _now = Morning;

    public DiscordThreadTests()
    {
        Directory.CreateDirectory(_folder);
        _database = new WaypointDatabase(Path.Combine(_folder, "waypoint.db"));
        _discord.Forums[NotesForum] = [];
        _discord.Forums[ListsForum] = [];
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

    private sealed class Message
    {
        public required string Id { get; init; }

        public required string Author { get; init; }

        public bool Bot { get; init; }

        public string Body { get; set; } = "{}";

        /// <summary>Whether what was sent says this, reading the body as text rather than JSON, which escapes &lt; and &gt;.</summary>
        public bool Says(string text) => System.Text.RegularExpressions.Regex.Unescape(Body).Contains(text);

        public string Content { get; init; } = "";

        public int Type { get; init; }

        public List<string> Files { get; init; } = [];

        public List<string> Reactions { get; } = [];
    }

    private sealed class Post
    {
        public required string Id { get; init; }

        public required string Parent { get; init; }

        public required string Owner { get; init; }

        public string Name { get; set; } = "";

        public List<string> Tags { get; set; } = [];

        public bool Archived { get; set; }

        public List<Message> Messages { get; } = [];

        /// <summary>Everything Waypoint's messages in it say, joined, so a test can look for text.</summary>
        public string Shown => string.Join("\n", Messages.Where(message => message.Bot).Select(message => message.Body));
    }

    /// <summary>Forums with tags, posts with messages, and every call made, with a few permissions that can be taken away.</summary>
    private sealed class FakeForums : HttpMessageHandler
    {
        private long _next = 900000000000000000;

        public Dictionary<string, List<(string Id, string Name)>> Forums { get; } = [];

        public Dictionary<string, Post> Posts { get; } = [];

        public List<(HttpMethod Method, string Path, string Body)> Calls { get; } = [];

        /// <summary>Called with each request before it's answered, to see what Waypoint had saved by then.</summary>
        public Action<HttpMethod, string, string>? Watch { get; set; }

        public bool CanDeleteOthers { get; set; } = true;

        public bool CanManageChannels { get; set; } = true;

        /// <summary>Without Manage Threads, a post can't be archived or deleted, though it can still be unarchived.</summary>
        public bool CanManageThreads { get; set; } = true;

        /// <summary>A forum set to need a tag on every post, which turns down a post with none.</summary>
        public bool RequireTags { get; set; }

        /// <summary>Paths whose reads fail, such as a forum or a post's first message, the way an outage does.</summary>
        public HashSet<string> FailingReads { get; } = [];

        /// <summary>Whether asking who the bot is gets an answer, as it does from Discord, rather than nothing.</summary>
        public bool AnswersWhoAmI { get; set; }

        /// <summary>Whether messages sent to posts fail, the way an outage does.</summary>
        public bool FailingSends { get; set; }

        /// <summary>Messages whose deletes fail, the way an outage does.</summary>
        public HashSet<string> FailingDeletes { get; } = [];

        /// <summary>Whether archiving a post fails, the way an outage does.</summary>
        public bool FailingArchives { get; set; }

        /// <summary>Every name a post was made with, to check none is cut in half.</summary>
        public List<string> PostNames { get; } = [];

        /// <summary>The note pinned in the alerts channel saying which copy runs Discord, if any.</summary>
        public string? ClaimNote { get; set; }

        public string NextId() => Interlocked.Increment(ref _next).ToString(System.Globalization.CultureInfo.InvariantCulture);

        public List<(HttpMethod Method, string Path, string Body)> All
        {
            get
            {
                lock (Calls)
                {
                    return Calls.ToList();
                }
            }
        }

        public void Clear()
        {
            lock (Calls)
            {
                Calls.Clear();
            }
        }

        public List<Post> PostsIn(string forum)
        {
            lock (Posts)
            {
                return Posts.Values.Where(post => post.Parent == forum).ToList();
            }
        }

        /// <summary>A post the owner starts by hand, with its first message.</summary>
        public Post UserPost(string forum, string name, string content, string[]? tags = null, string owner = "owner", string[]? files = null)
        {
            var id = NextId();
            var post = new Post { Id = id, Parent = forum, Owner = owner, Name = name, Tags = (tags ?? []).ToList() };
            post.Messages.Add(new Message { Id = id, Author = owner, Content = content, Files = (files ?? []).ToList() });
            lock (Posts)
            {
                Posts[id] = post;
            }

            return post;
        }

        public Message UserMessage(Post post, string content, string author = "owner", int type = 0, string[]? files = null)
        {
            var message = new Message { Id = NextId(), Author = author, Content = content, Type = type, Files = (files ?? []).ToList() };
            lock (Posts)
            {
                post.Messages.Add(message);
            }

            return message;
        }

        private static string MessageJson(Message message, string channel) =>
            JsonSerializer.Serialize(new
            {
                id = message.Id,
                type = message.Type,
                channel_id = channel,
                content = message.Content,
                author = new { id = message.Author, bot = message.Bot },

                // Like Discord, a message read back shows what it was sent with, but never its nonce.
                embeds = SentWith(message, "embeds"),
                components = SentWith(message, "components"),
                attachments = message.Files.Select(file => new { id = "a" + message.Id, filename = file, size = 2048, url = $"https://cdn.discordapp.com/attachments/{message.Id}/a{message.Id}/{file}", content_type = "application/pdf" }),
            });

        /// <summary>Part of what Waypoint sent a message of its own with, or null.</summary>
        private static JsonElement? SentWith(Message message, string name) =>
            message.Bot && DiscordThreadTests.Json(message.Body) is { ValueKind: JsonValueKind.Object } sent && sent.TryGetProperty(name, out var part) ? part : null;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Replace("/api/v10", "");
            var query = request.RequestUri.Query;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Calls)
            {
                Calls.Add((request.Method, path, body));
            }

            Watch?.Invoke(request.Method, path, body);

            if (request.Method == HttpMethod.Get && FailingReads.Contains(path))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") };
            }

            lock (Posts)
            {
                return Answer(request.Method, path.Trim('/').Split('/'), query, body);
            }
        }

        private HttpResponseMessage Answer(HttpMethod method, string[] parts, string query, string body)
        {
            if (parts is ["guilds", _, "threads", "active"])
            {
                var threads = Posts.Values.Where(post => !post.Archived).Select(post => new { id = post.Id, parent_id = post.Parent, owner_id = post.Owner, name = post.Name, applied_tags = post.Tags, last_message_id = post.Messages.LastOrDefault()?.Id });
                return Json(new { threads });
            }

            if (parts is ["users", "@me"] && AnswersWhoAmI)
            {
                return Json(new { id = "bot" });
            }

            if (parts is ["channels", "alerts", "messages", "pins"])
            {
                var items = ClaimNote is null ? [] : new[] { new { pinned_at = "2026-11-15T00:00:00Z", message = new { id = "1", content = ClaimNote } } };
                return Json(new { items, has_more = false });
            }

            // A file can only be downloaded while the message it came on is still there.
            if (parts is ["attachments", var holder, ..])
            {
                return Posts.Values.Any(post => post.Messages.Any(message => message.Id == holder))
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (parts is not ["channels", var channel, ..])
            {
                return method == HttpMethod.Post ? Json(new { id = NextId() }) : new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (Forums.TryGetValue(channel, out var tags))
            {
                return Forum(method, channel, tags, parts, body);
            }

            if (!Posts.TryGetValue(channel, out var post))
            {
                // A post that was deleted is unknown. Any other channel, such as capture or alerts, is empty, and
                // anything sent there is taken.
                if (channel.Length == 18 && channel.StartsWith('9'))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"code":10003}""") };
                }

                return method == HttpMethod.Get ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") } : Json(new { id = NextId() });
            }

            switch (parts)
            {
                case [_, _] when method == HttpMethod.Patch && FailingArchives && JsonNode.Parse(body)!["archived"]?.GetValue<bool>() == true:
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") };
                case [_, _] when method == HttpMethod.Patch && !CanManageThreads && JsonNode.Parse(body)!["archived"]?.GetValue<bool>() == true:
                case [_, _] when method == HttpMethod.Delete && !CanManageThreads:
                    return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"code":50013}""") };
                case [_, _] when method == HttpMethod.Patch:
                    var change = JsonNode.Parse(body)!;
                    if (change["name"] is { } name)
                    {
                        post.Name = name.GetValue<string>();
                    }

                    if (change["applied_tags"] is JsonArray applied)
                    {
                        post.Tags = applied.Select(tag => tag!.GetValue<string>()).ToList();
                    }

                    if (change["archived"] is { } archived)
                    {
                        post.Archived = archived.GetValue<bool>();
                    }

                    return Json(new { id = post.Id });
                case [_, _] when method == HttpMethod.Delete:
                    Posts.Remove(post.Id);
                    return Json(new { id = post.Id });
                case [_, _, "messages"] when method == HttpMethod.Post && FailingSends:
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") };
                case [_, _, "messages"] when method == HttpMethod.Post:
                    post.Archived = false;
                    var sent = new Message { Id = NextId(), Author = "bot", Bot = true, Body = body };
                    post.Messages.Add(sent);
                    return Json(new { id = sent.Id });
                case [_, _, "messages"] when method == HttpMethod.Get:
                    // Like Discord: the messages right after the one asked about, at most the limit, newest first.
                    var after = Query(query, "after") is { } from ? ulong.Parse(from) : 0;
                    var limit = Query(query, "limit") is { } most ? int.Parse(most) : 50;
                    var page = post.Messages.Where(message => ulong.Parse(message.Id) > after).OrderBy(message => ulong.Parse(message.Id)).Take(limit).Reverse();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("[" + string.Join(",", page.Select(message => MessageJson(message, post.Id))) + "]"),
                    };
            }

            var message = parts.Length >= 4 ? post.Messages.FirstOrDefault(found => found.Id == parts[3]) : null;
            if (message is null)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"code":10008}""") };
            }

            switch (parts)
            {
                case [_, _, "messages", _] when method == HttpMethod.Get:
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MessageJson(message, post.Id)) };
                case [_, _, "messages", _] when method == HttpMethod.Patch:
                    if (post.Archived)
                    {
                        return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"code":50083,"message":"Thread is archived"}""") };
                    }

                    message.Body = body;
                    return Json(new { id = message.Id });
                case [_, _, "messages", _] when method == HttpMethod.Delete && FailingDeletes.Contains(message.Id):
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") };
                case [_, _, "messages", _] when method == HttpMethod.Delete:
                    if (!message.Bot && !CanDeleteOthers)
                    {
                        return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"code":50013}""") };
                    }

                    post.Messages.Remove(message);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                case [_, _, "messages", _, "reactions", var emoji, "@me"] when method == HttpMethod.Put:
                    message.Reactions.Add(Uri.UnescapeDataString(emoji));
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static string? Query(string query, string name) =>
            query.TrimStart('?').Split('&').Select(pair => pair.Split('=')).FirstOrDefault(pair => pair.Length == 2 && pair[0] == name)?[1];

        private HttpResponseMessage Forum(HttpMethod method, string forum, List<(string Id, string Name)> tags, string[] parts, string body)
        {
            object Channel() => new { id = forum, type = 15, guild_id = "guild", available_tags = tags.Select(tag => new { id = tag.Id, name = tag.Name }) };
            if (parts.Length == 2 && method == HttpMethod.Get)
            {
                return Json(Channel());
            }

            if (parts.Length == 2 && method == HttpMethod.Patch)
            {
                if (!CanManageChannels)
                {
                    return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"code":50013}""") };
                }

                var wanted = JsonNode.Parse(body)!["available_tags"]!.AsArray();
                Assert.True(wanted.Count <= 20);
                var kept = wanted.Select(tag => (Id: tag!["id"]?.GetValue<string>() ?? NextId(), Name: tag["name"]!.GetValue<string>())).ToList();
                Assert.All(kept, tag => Assert.True(tag.Name.Length <= 20));
                tags.Clear();
                tags.AddRange(kept);
                return Json(Channel());
            }

            if (parts is [_, _, "threads"] && method == HttpMethod.Post)
            {
                var made = JsonNode.Parse(body)!;
                var name = made["name"]!.GetValue<string>();
                var applied = made["applied_tags"]!.AsArray().Select(tag => tag!.GetValue<string>()).ToList();
                PostNames.Add(name);
                if ((RequireTags && applied.Count == 0) || applied.Any(tag => tags.All(known => known.Id != tag)) || name.Any(char.IsSurrogate) && !IsWhole(name))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"code":50035,"message":"Invalid Form Body"}""") };
                }

                var id = NextId();
                var post = new Post
                {
                    Id = id,
                    Parent = forum,
                    Owner = "bot",
                    Name = made["name"]!.GetValue<string>(),
                    Tags = made["applied_tags"]!.AsArray().Select(tag => tag!.GetValue<string>()).ToList(),
                };
                Assert.InRange(post.Name.Length, 1, 100);
                post.Messages.Add(new Message { Id = id, Author = "bot", Bot = true, Body = made["message"]!.ToJsonString() });
                Posts[id] = post;
                return Json(new { id, parent_id = forum, name = post.Name, message = new { id } });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static bool IsWhole(string text)
        {
            for (var index = 0; index < text.Length; index++)
            {
                if (char.IsHighSurrogate(text[index]) && (index + 1 >= text.Length || !char.IsLowSurrogate(text[++index])))
                {
                    return false;
                }

                if (char.IsLowSurrogate(text[index]) && (index == 0 || !char.IsHighSurrogate(text[index - 1])))
                {
                    return false;
                }
            }

            return true;
        }

        private static HttpResponseMessage Json(object value) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    }

    /// <summary>Notices shown on the PC, by any worker made here.</summary>
    private readonly List<(string Title, string Text)> _shownHere = [];

    private sealed class Host(List<(string Title, string Text)> shown) : IWorkerHost
    {
        public void ShowNotice(string title, string text)
        {
            lock (shown)
            {
                shown.Add((title, text));
            }
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

    private sealed class FakeWriter : INoteWriter
    {
        public Task<WrittenNotes> WriteAsync(IReadOnlyList<NoteSource> sources, NoteWriteContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WrittenNotes("## Cell parts\nThe nucleus holds the DNA.", []));
    }

    private static DiscordSettings Settings(string? notes = NotesForum, string? lists = ListsForum, string? notesView = null) =>
        new("token", "owner", "capture", "alerts", "budget", NotesChannelId: notesView, NotesForumChannelId: notes, ListsForumChannelId: lists);

    private WaypointWorker MakeWorker(DiscordRunner? runner = null, string? notes = NotesForum, string? lists = ListsForum, string? notesView = null)
    {
        var worker = new WaypointWorker(_database, new WaypointSettings(Discord: Settings(notes, lists, notesView)), _folder, new Host(_shownHere), exception => throw exception, new WorkerParts(
            _ => new NoTokens(),
            Clock: () => _now,
            Http: () => new HttpClient(_discord),
            CaptureSorter: new NullSorter(),
            ReadFile: path => new FileText(Path.GetFileName(path), "Cells have a nucleus and a membrane."),
            ConnectGateway: false,
            Runner: runner,
            HostName: "DESK"))
        {
            RetryWait = _ => Task.CompletedTask,
        };
        worker.NoteFiles = new LocalNotesData(_database, new NoteMaker(_database, new FakeWriter(), null));
        return worker;
    }

    /// <summary>A started worker whose first round has made its posts, with the calls so far cleared.</summary>
    private async Task<WaypointWorker> Started(string? notes = NotesForum, string? lists = ListsForum, string? notesView = null)
    {
        var worker = MakeWorker(notes: notes, lists: lists, notesView: notesView);
        worker.StartDiscord();
        worker.Discord!.DeferAfter = TimeSpan.FromMinutes(1);
        await worker.SyncDiscordThreadsAsync();
        await Task.Delay(100);
        await worker.SyncDiscordThreadsAsync();
        _discord.Clear();
        return worker;
    }

    /// <summary>Passes on an event as the live connection would, then waits for what it set going.</summary>
    private static async Task Event(WaypointWorker worker, string type, JsonElement data)
    {
        await worker.Discord!.HandleEventAsync(type, data);
        await worker.ThreadsSettledAsync();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var tries = 0; tries < 500 && !condition(); tries++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private Post PostFor(long noteId) => _discord.Posts[_database.GetDiscordThread(WaypointDatabase.ThreadNote, noteId.ToString())!.Thread];

    private Post PostForList(string name) => _discord.Posts[_database.GetDiscordThread(WaypointDatabase.ThreadList, name)!.Thread];

    private static JsonElement MessageEvent(Post post, Message message) => Json(JsonSerializer.Serialize(new
    {
        id = message.Id,
        type = message.Type,
        channel_id = post.Id,
        content = message.Content,
        author = new { id = message.Author },
        attachments = message.Files.Select(file => new { id = "a" + message.Id, filename = file, size = 2048, url = $"https://cdn.discordapp.com/attachments/{message.Id}/a{message.Id}/{file}", content_type = "application/pdf" }),
    }));

    private static JsonElement ThreadEvent(Post post) => Json(JsonSerializer.Serialize(new
    {
        id = post.Id,
        parent_id = post.Parent,
        owner_id = post.Owner,
        name = post.Name,
        applied_tags = post.Tags,
        newly_created = true,
        type = 11,
    }));

    /// <summary>What each of Waypoint's messages in a post says, in order.</summary>
    private static List<string> Descriptions(Post post) =>
        post.Messages.Where(message => message.Bot).Select(message => Json(message.Body).GetProperty("embeds")[0].GetProperty("description").GetString()!).ToList();

    private static string EditId(Message message) =>
        Json(message.Body).GetProperty("components")[0].GetProperty("components")[0].GetProperty("custom_id").GetString()!;

    [Fact]
    public async Task The_first_sync_makes_a_post_for_each_note_tagged_with_its_folder_and_a_post_for_each_list_with_Shopping_first()
    {
        var biology = _database.CreateNoteFolder("Biology", null, _now).Id;
        var labs = _database.CreateNoteFolder("Labs", biology, _now).Id;
        var cells = _database.CreateNote("Cells", "The nucleus holds the DNA.\n\n| Part | Job |\n|---|---|\n| Wall | Shape |", _now, biology);
        _database.CreateNote("Lab 1", "Microscope day", _now, labs);
        var loose = _database.CreateNote("Loose thought", "Something", _now);
        _database.AddNoteSource(cells, "pdf", "slides.pdf", "Slide text that stays on the PC", _now);
        _database.AddNoteSource(cells, "transcript", "lecture", "Transcript text that stays on the PC", _now);
        _database.CreateList("Packing", _now);
        _database.AddListEntry("Packing", "Charger", _now);

        await Started();

        var tags = _discord.Forums[NotesForum];
        Assert.Contains(tags, tag => tag.Name == "Biology");
        Assert.Contains(tags, tag => tag.Name == "Biology / Labs");

        var cellsPost = PostFor(cells);
        Assert.Equal("Cells", cellsPost.Name);
        Assert.Equal([tags.Single(tag => tag.Name == "Biology").Id], cellsPost.Tags);
        var shown = Descriptions(cellsPost).Single();
        Assert.Contains("The nucleus holds the DNA.", shown);
        Assert.Contains("Wall · Shape", shown);
        Assert.Contains("Sources: 2 files, kept on the PC", shown);
        Assert.DoesNotContain("Slide text", cellsPost.Shown);
        Assert.DoesNotContain("Transcript text", cellsPost.Shown);
        Assert.StartsWith("waypoint:act:nedit:", EditId(cellsPost.Messages[0]));

        Assert.Empty(PostFor(loose).Tags);
        Assert.Equal(3, _discord.PostsIn(NotesForum).Count);

        var lists = _discord.PostsIn(ListsForum).OrderBy(post => ulong.Parse(post.Id)).ToList();
        Assert.Equal(["Shopping", "Packing"], lists.Select(post => post.Name));
        Assert.Contains("Charger", lists[1].Shown);
        Assert.Contains("waypoint:view-check:", lists[1].Shown);
    }

    [Fact]
    public async Task A_round_with_nothing_changed_makes_no_calls_and_a_PC_edit_changes_only_the_message_it_touched()
    {
        var body = string.Join("\n", Enumerable.Range(0, 260).Select(number => $"Line {number} of a long note with some words in it"));
        var id = _database.CreateNote("Long", body, _now);
        var worker = await Started(lists: null);

        var post = PostFor(id);
        var parts = post.Messages.Where(message => message.Bot).ToList();
        Assert.True(parts.Count >= 3);
        Assert.All(Descriptions(post), text => Assert.True(text.Length <= 4096));
        Assert.StartsWith("Long, page 1 of", Json(parts[0].Body).GetProperty("embeds")[0].GetProperty("title").GetString());

        _now = _now.AddMinutes(1);
        await worker.SyncDiscordThreadsAsync();
        Assert.DoesNotContain(_discord.All, call => call.Method != HttpMethod.Get);

        // A word changed near the end, in the last part only.
        _database.SaveNote(id, "Long", body.Replace("Line 259 of", "Line 259, the last, of"), _now);
        await worker.SyncDiscordThreadsAsync();
        var edits = _discord.All.Where(call => call.Method == HttpMethod.Patch).ToList();
        var edit = Assert.Single(edits);
        Assert.EndsWith($"/messages/{parts[^1].Id}", edit.Path);
        Assert.Contains("the last", Descriptions(post)[^1]);
    }

    [Fact]
    public async Task Typing_in_a_note_post_adds_a_paragraph_through_the_note_save_and_then_takes_the_message_away()
    {
        var id = _database.CreateNote("Groceries plan", "Buy oats", _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);

        var typed = _discord.UserMessage(post, "Also check the freezer");
        await Event(worker, "MESSAGE_CREATE", MessageEvent(post, typed));

        Assert.Equal("Buy oats\n\nAlso check the freezer", _database.GetNote(id)!.Body);
        Assert.DoesNotContain(post.Messages, message => message.Id == typed.Id);

        await worker.SyncDiscordThreadsAsync();
        Assert.Contains("Also check the freezer", Descriptions(post).Single());

        // The same message passed on again, such as by a catch-up read, isn't added twice.
        await Event(worker, "MESSAGE_CREATE", MessageEvent(post, typed));
        Assert.Equal("Buy oats\n\nAlso check the freezer", _database.GetNote(id)!.Body);
    }

    [Fact]
    public async Task Without_Manage_Messages_a_saved_message_gets_a_check_mark_and_the_post_says_so_once()
    {
        var id = _database.CreateNote("Ideas", "First", _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);
        _discord.CanDeleteOthers = false;

        var first = _discord.UserMessage(post, "Second");
        await Event(worker, "MESSAGE_CREATE", MessageEvent(post, first));
        var second = _discord.UserMessage(post, "Third");
        await Event(worker, "MESSAGE_CREATE", MessageEvent(post, second));

        Assert.Equal("First\n\nSecond\n\nThird", _database.GetNote(id)!.Body);
        Assert.Equal(["✅"], first.Reactions);
        Assert.Equal(["✅"], second.Reactions);
        Assert.Single(post.Messages, message => message.Bot && message.Body.Contains("Manage Messages"));
    }

    [Fact]
    public async Task Edit_opens_that_part_and_saving_replaces_exactly_that_part()
    {
        var first = string.Join("\n", Enumerable.Range(0, 90).Select(number => $"First half line {number} with a few more words"));
        var second = string.Join("\n", Enumerable.Range(0, 20).Select(number => $"Second half line {number}"));
        var body = first + "\n" + second;
        var id = _database.CreateNote("Two parts", body, _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);
        var parts = post.Messages.Where(message => message.Bot).ToList();
        Assert.Equal(2, parts.Count);

        var press = new ButtonPress(EditId(parts[1]), null, parts[1].Id, post.Id);
        var form = (await worker.OnButtonActedAsync(press))!.Form!;
        Assert.Equal("Edit page 2 of 2", form.Title);
        var box = Assert.Single(form.Boxes);
        Assert.Contains("Markdown", box.Label);
        var chunks = WaypointWorker.SplitMarkdown(body, WaypointWorker.PartMarkdown);
        Assert.Equal(chunks[1], box.Value);
        Assert.Equal(4000, box.MaxLength);

        var reply = worker.OnFormSent(form.Id, new Dictionary<string, string> { ["text"] = "Second half, rewritten on the phone" });
        Assert.Equal("Saved", reply.Card.Title);
        Assert.True(reply.Private);
        Assert.Equal(chunks[0] + "Second half, rewritten on the phone", _database.GetNote(id)!.Body);
    }

    [Fact]
    public async Task An_edit_to_a_part_that_changed_meanwhile_is_added_at_the_end_under_From_Discord()
    {
        var id = _database.CreateNote("Plans", "Saturday: hike\nSunday: rest", _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);
        var part = post.Messages.Single(message => message.Bot);

        var form = (await worker.OnButtonActedAsync(new ButtonPress(EditId(part), null, part.Id, post.Id)))!.Form!;

        // Changed on the PC while the form was open.
        _database.SaveNote(id, "Plans", "Saturday: hike with Sam\nSunday: rest", _now);
        var reply = worker.OnFormSent(form.Id, new Dictionary<string, string> { ["text"] = "Saturday: bike\nSunday: rest" });

        Assert.Equal("Saturday: hike with Sam\nSunday: rest\n\nFrom Discord\n\nSaturday: bike", _database.GetNote(id)!.Body);
        Assert.True(reply.Private);
        Assert.Contains("added at the end", reply.Card.Description);
    }

    [Fact]
    public async Task A_post_the_owner_starts_becomes_a_note_in_the_tagged_folder_and_Waypoint_takes_the_post_over()
    {
        var biology = _database.CreateNoteFolder("Biology", null, _now).Id;
        var worker = await Started(lists: null);
        var tag = _discord.Forums[NotesForum].Single(found => found.Name == "Biology").Id;

        var post = _discord.UserPost(NotesForum, "Mitosis", "Prophase, metaphase, anaphase, telophase", [tag]);
        await Event(worker, "THREAD_CREATE", ThreadEvent(post));

        var note = Assert.Single(_database.GetNotes());
        Assert.Equal("Mitosis", note.Title);
        Assert.Equal("Prophase, metaphase, anaphase, telophase", note.Body);
        Assert.Equal(biology, note.FolderId);
        Assert.DoesNotContain(post.Messages, message => !message.Bot);

        await worker.SyncDiscordThreadsAsync();
        Assert.Single(_discord.PostsIn(NotesForum));
        Assert.Contains("Prophase", Descriptions(post).Single());

        // The starter's own message coming through afterwards adds nothing.
        await Event(worker, "MESSAGE_CREATE", Json(JsonSerializer.Serialize(new { id = post.Id, channel_id = post.Id, content = "Prophase, metaphase, anaphase, telophase", author = new { id = "owner" } })));
        Assert.Equal("Prophase, metaphase, anaphase, telophase", _database.GetNotes().Single().Body);
    }

    [Fact]
    public async Task A_first_message_that_cannot_be_taken_away_stays_and_the_note_shows_under_it()
    {
        var worker = await Started(lists: null);
        _discord.CanDeleteOthers = false;

        var post = _discord.UserPost(NotesForum, "Quick idea", "Paint the shelf");
        await Event(worker, "THREAD_CREATE", ThreadEvent(post));
        await worker.SyncDiscordThreadsAsync();

        Assert.Null(_database.GetNotes().Single().FolderId);
        Assert.False(post.Messages[0].Bot);
        Assert.True(post.Messages[1].Bot);
        Assert.Contains("Paint the shelf", Descriptions(post).Single());
    }

    [Fact]
    public async Task A_deleted_post_is_made_again_and_the_note_is_never_touched()
    {
        var id = _database.CreateNote("Keep me", "Important words", _now);
        var worker = await Started(lists: null);
        var old = PostFor(id);

        _discord.Posts.Remove(old.Id);
        await Event(worker, "THREAD_DELETE", Json($$"""{"id":"{{old.Id}}","parent_id":"{{NotesForum}}","type":11}"""));
        await worker.SyncDiscordThreadsAsync();

        var note = _database.GetNote(id)!;
        Assert.Equal("Important words", note.Body);
        var again = PostFor(id);
        Assert.NotEqual(old.Id, again.Id);
        Assert.Contains("Important words", Descriptions(again).Single());
    }

    [Fact]
    public async Task A_post_found_gone_while_editing_is_made_again_too()
    {
        var id = _database.CreateNote("Gone", "Words", _now);
        var worker = await Started(lists: null);
        var old = PostFor(id);
        _discord.Posts.Remove(old.Id);

        _database.SaveNote(id, "Gone", "Words and more", _now);
        await worker.SyncDiscordThreadsAsync();

        Assert.Equal("Words and more", _database.GetNote(id)!.Body);
        Assert.Contains("Words and more", Descriptions(PostFor(id)).Single());
    }

    [Fact]
    public async Task Renaming_moving_and_editing_on_the_PC_update_the_post_and_an_archived_post_is_unarchived_first()
    {
        var biology = _database.CreateNoteFolder("Biology", null, _now).Id;
        var id = _database.CreateNote("Cells", "Nucleus", _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);
        post.Archived = true;

        _database.SaveNote(id, "Cell parts", "Nucleus and wall", _now);
        _database.MoveNoteToFolder(id, biology);
        await worker.SyncDiscordThreadsAsync();

        // The tag and the text change at once. The name waits until the title has stayed the same for a while, so
        // a title still being typed doesn't leave a line in the post for every change.
        Assert.Equal("Cells", post.Name);
        Assert.Equal([_discord.Forums[NotesForum].Single(tag => tag.Name == "Biology").Id], post.Tags);
        Assert.False(post.Archived);
        Assert.Contains("Nucleus and wall", Descriptions(post).Single());

        _now = _now.AddMinutes(3);
        await worker.SyncDiscordThreadsAsync();
        Assert.Equal("Cell parts", post.Name);

        // Only the text changed this time, so the edit itself finds the post archived and unarchives it.
        post.Archived = true;
        _database.SaveNote(id, "Cell parts", "Nucleus, wall, and membrane", _now);
        await worker.SyncDiscordThreadsAsync();
        Assert.False(post.Archived);
        Assert.Contains("membrane", Descriptions(post).Single());
    }

    [Fact]
    public async Task A_note_deleted_on_the_PC_deletes_its_post_unless_something_typed_there_is_still_unread()
    {
        var gone = _database.CreateNote("Old", "Old words", _now);
        var kept = _database.CreateNote("Busy", "Busy words", _now);
        var worker = await Started(lists: null);
        var busy = PostFor(kept);
        var oldPost = PostFor(gone);

        // Typed while Waypoint wasn't listening, so it hasn't been read.
        _discord.UserMessage(busy, "Not read yet");
        _database.DeleteNote(gone, _now);
        _database.DeleteNote(kept, _now);
        await worker.SyncDiscordThreadsAsync();

        Assert.False(_discord.Posts.ContainsKey(oldPost.Id));
        Assert.True(_discord.Posts.ContainsKey(busy.Id));
        Assert.True(busy.Archived);
        Assert.Contains(busy.Messages, message => message.Content == "Not read yet");
        Assert.Empty(_database.GetDiscordThreads(WaypointDatabase.ThreadNote));
    }

    [Fact]
    public async Task What_was_typed_in_a_post_while_away_is_read_on_the_next_catch_up()
    {
        var id = _database.CreateNote("Trip", "Pack bags", _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);

        var typed = _discord.UserMessage(post, "Book the car");
        _now = _now.AddMinutes(6);
        await worker.SyncDiscordThreadsAsync();

        Assert.Equal("Pack bags\n\nBook the car", _database.GetNote(id)!.Body);
        Assert.DoesNotContain(post.Messages, message => message.Id == typed.Id);
    }

    [Fact]
    public async Task A_file_sent_in_a_note_post_joins_that_notes_sources()
    {
        var id = _database.CreateNote("Biology", "My own notes", _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);
        var message = _discord.UserMessage(post, "", files: ["cells.pdf"]);

        await Event(worker, "MESSAGE_CREATE", MessageEvent(post, message));

        Assert.Equal("My own notes", _database.GetNote(id)!.Body);
        Assert.Contains("nucleus", _database.GetNoteSources(id).Single().Text);
        Assert.DoesNotContain(post.Messages, found => found.Id == message.Id);
    }

    [Fact]
    public async Task Only_the_owner_is_listened_to_in_posts()
    {
        var id = _database.CreateNote("Mine", "Only mine", _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);

        var stranger = _discord.UserMessage(post, "Hello from someone else", author: "someone");
        await Event(worker, "MESSAGE_CREATE", MessageEvent(post, stranger));
        var theirs = _discord.UserPost(NotesForum, "Their post", "Not a note", owner: "someone");
        await Event(worker, "THREAD_CREATE", ThreadEvent(theirs));
        _now = _now.AddMinutes(6);
        await worker.SyncDiscordThreadsAsync();

        Assert.Equal("Only mine", _database.GetNote(id)!.Body);
        Assert.Single(_database.GetNotes());
        Assert.Contains(post.Messages, message => message.Id == stranger.Id);
        Assert.DoesNotContain(_discord.All, call => call.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task A_system_message_from_the_owner_such_as_a_rename_is_not_added_to_the_note()
    {
        var id = _database.CreateNote("Plans", "Saturday", _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);

        // Renaming a post leaves a "changed the post title" message by the owner, with the new name as its text.
        var renamed = _discord.UserMessage(post, "New title", type: 4);
        await Event(worker, "MESSAGE_CREATE", MessageEvent(post, renamed));
        var reply = _discord.UserMessage(post, "Sunday", type: 19);
        await Event(worker, "MESSAGE_CREATE", MessageEvent(post, reply));

        Assert.Equal("Saturday\n\nSunday", _database.GetNote(id)!.Body);
    }

    [Fact]
    public async Task Typing_in_a_list_post_adds_each_line_and_the_menu_checks_things_off()
    {
        _database.CreateList("Packing", _now);
        var worker = await Started(notes: null);
        var post = PostForList("Packing");

        var typed = _discord.UserMessage(post, "Charger, socks\nToothbrush");
        await Event(worker, "MESSAGE_CREATE", MessageEvent(post, typed));

        Assert.Equal(["Charger", "socks", "Toothbrush"], _database.GetListEntries("Packing").Select(entry => entry.Text));
        Assert.DoesNotContain(post.Messages, message => message.Id == typed.Id);

        await worker.SyncDiscordThreadsAsync();
        Assert.Contains("Toothbrush", post.Shown);

        var socks = _database.GetListEntries("Packing").Single(entry => entry.Text == "socks");
        var card = worker.OnViewPicked($"{DiscordViews.CheckMenu}:{DiscordViews.ListTarget("Packing")}", post.Messages.First(message => message.Bot).Id, [socks.Id.ToString()])!;
        Assert.True(_database.GetListEntries("Packing").Single(entry => entry.Text == "socks").Done);
        Assert.Contains(card.Actions, action => action.Label == "Start over");

        var startOver = card.Actions.Single(action => action.Label == "Start over");
        var result = await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + startOver.Id, null, "m", post.Id));
        Assert.NotNull(result!.Card);
        Assert.DoesNotContain(_database.GetListEntries("Packing"), entry => entry.Done);

        // Its Undo checks socks again, and only socks.
        var undo = result.Card!.Actions.Single(action => action.Label == "Undo");
        var undone = await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + undo.Id, null, "m", post.Id));
        Assert.Contains(undone!.Card!.Actions, action => action.Label == "Start over");
        Assert.Equal(["socks"], _database.GetListEntries("Packing").Where(entry => entry.Done).Select(entry => entry.Text));
    }

    [Fact]
    public async Task A_new_post_in_the_lists_forum_makes_a_new_list_with_its_lines()
    {
        var worker = await Started(notes: null);

        var post = _discord.UserPost(ListsForum, "Hardware store", "Screws\nWood glue");
        await Event(worker, "THREAD_CREATE", ThreadEvent(post));
        await worker.SyncDiscordThreadsAsync();

        Assert.Contains("Hardware store", _database.GetListNames());
        Assert.Equal(["Screws", "Wood glue"], _database.GetListEntries("Hardware store").Select(entry => entry.Text));
        Assert.Equal(1, _discord.PostsIn(ListsForum).Count(found => found.Name == "Hardware store"));
        Assert.Contains("Wood glue", post.Shown);
    }

    [Fact]
    public async Task Over_twenty_folders_the_rest_get_no_tag_and_Settings_says_so()
    {
        for (var number = 0; number < 22; number++)
        {
            _database.CreateNoteFolder($"Folder {number:00}", null, _now);
        }

        var last = _database.GetNoteFolders().Last().Id;
        var id = _database.CreateNote("In the last folder", "Words", _now, last);
        var worker = await Started(lists: null);

        Assert.Equal(20, _discord.Forums[NotesForum].Count);
        Assert.Empty(PostFor(id).Tags);
        Assert.Contains("only have 20 tags", worker.ForumNotice);
    }

    [Fact]
    public async Task Without_Manage_Channels_posts_are_still_made_and_Settings_says_which_permission_is_missing()
    {
        _database.CreateNoteFolder("Biology", null, _now);
        var id = _database.CreateNote("Cells", "Words", _now, _database.GetNoteFolders()[0].Id);
        _discord.CanManageChannels = false;

        var worker = await Started(lists: null);

        Assert.Empty(PostFor(id).Tags);
        Assert.Contains("Manage Channels", worker.Discord!.ThreadProblem);
    }

    [Fact]
    public async Task Only_the_copy_running_Discord_makes_posts()
    {
        _database.CreateNote("Server note", "Words", _now);
        _discord.ClaimNote = DiscordClaim.NoteText("homeserver", DiscordRunner.Server, new DateTimeOffset(_now));


        var pc = MakeWorker(DiscordRunner.Pc);
        pc.StartDiscord();
        await pc.CheckDiscordClaimAsync();
        await pc.SyncDiscordThreadsAsync();
        await pc.RefreshDiscordViewsAsync();

        Assert.Null(pc.Discord);
        Assert.Empty(_discord.Posts);

        _discord.ClaimNote = DiscordClaim.NoteText("DESK-PC", DiscordRunner.Pc, new DateTimeOffset(_now));
        var server = MakeWorker(DiscordRunner.Server);
        server.StartDiscord();
        await server.CheckDiscordClaimAsync();
        await server.SyncDiscordThreadsAsync();

        Assert.NotNull(server.Discord);
        Assert.Single(_discord.PostsIn(NotesForum));
    }

    [Fact]
    public void Markdown_split_into_parts_puts_back_together_exactly()
    {
        var body = "# Title\r\n\r\n" + string.Join("\r\n", Enumerable.Range(0, 400).Select(number => $"Line {number} with words")) + "\r\n" + new string('x', 9000);
        var parts = WaypointWorker.SplitMarkdown(body, WaypointWorker.PartMarkdown);

        Assert.Equal(body, string.Concat(parts));
        Assert.All(parts, part => Assert.True(part.Length <= WaypointWorker.PartMarkdown));
        Assert.Equal([""], WaypointWorker.SplitMarkdown("", WaypointWorker.PartMarkdown));
        Assert.Equal("Oats\r\n\r\nMilk", WaypointWorker.AppendParagraph("Oats\r\n", "Milk"));
        Assert.Equal(["Eggs", "milk", "Bread"], WaypointWorker.ListLines("Eggs, milk\n- Bread\n"));
    }

    [Fact]
    public void The_forum_IDs_are_saved_from_Settings_and_read_back()
    {
        Assert.Null(DiscordSetup.SaveIds(_database, "111111111111111111", null, null, null, NotesForum, ListsForum));
        var combined = DiscordSetup.Combine(null, "token", _database)!;
        Assert.Equal(NotesForum, combined.NotesForumChannelId);
        Assert.Equal(ListsForum, combined.ListsForumChannelId);
        Assert.NotNull(DiscordSetup.SaveIds(_database, null, null, null, null, "not a forum", null));

        var file = DiscordSetup.Combine(new DiscordSettings("t", "1", NotesForumChannelId: "222222222222222222"), null, new WaypointDatabase(Path.Combine(_folder, "other.db")))!;
        Assert.Equal("222222222222222222", file.NotesForumChannelId);
    }

    [Fact]
    public async Task A_new_list_post_that_differs_only_in_case_adds_to_that_list_and_every_later_round_still_runs()
    {
        _database.CreateList("Groceries", _now);
        var keep = _database.CreateNote("Keep", "Before", _now);
        var worker = await Started();
        var groceries = PostForList("Groceries");

        var post = _discord.UserPost(ListsForum, "groceries", "Milk");
        await Event(worker, "THREAD_CREATE", ThreadEvent(post));

        Assert.Equal(["Shopping", "Groceries"], _database.GetDiscordThreads(WaypointDatabase.ThreadList).Select(row => row.Key));
        Assert.Equal(["Milk"], _database.GetListEntries("Groceries").Select(entry => entry.Text));
        Assert.True(post.Archived);
        Assert.Contains(post.Messages, message => message.Bot && message.Says($"<#{groceries.Id}>"));

        _database.SaveNote(keep, "Keep", "After edit", _now);
        await worker.SyncDiscordThreadsAsync();
        Assert.Contains("After edit", Descriptions(PostFor(keep)).Single());
        Assert.Contains("Milk", groceries.Shown);
    }

    [Fact]
    public async Task Files_in_a_new_post_are_kept_before_its_first_message_is_taken_away()
    {
        var worker = await Started(lists: null);

        var post = _discord.UserPost(NotesForum, "Cells", "My summary", files: ["cells.pdf"]);
        await Event(worker, "THREAD_CREATE", ThreadEvent(post));

        var note = Assert.Single(_database.GetNotes());
        Assert.Equal("My summary", note.Body);
        Assert.Contains("nucleus", Assert.Single(_database.GetNoteSources(note.Id)).Text);
        Assert.DoesNotContain(post.Messages, message => message.Id == post.Id);
    }

    [Fact]
    public async Task A_file_that_cannot_be_downloaded_leaves_the_first_message_in_place()
    {
        var worker = await Started(lists: null);

        var post = _discord.UserPost(NotesForum, "Cells", "My summary", files: ["cells.pdf"]);
        _discord.FailingReads.Add($"/attachments/{post.Id}/a{post.Id}/cells.pdf");
        await Event(worker, "THREAD_CREATE", ThreadEvent(post));

        Assert.Equal("My summary", Assert.Single(_database.GetNotes()).Body);
        Assert.Contains(post.Messages, message => message.Id == post.Id);
        Assert.Contains(post.Messages, message => message.Bot && message.Body.Contains("Try sending it again"));
    }

    [Fact]
    public async Task Waypoints_own_messages_never_move_the_read_point_past_something_the_owner_typed_while_away()
    {
        var id = _database.CreateNote("Trip", "Pack bags", _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);

        _discord.UserMessage(post, "Book the car");
        _database.SaveNote(id, "Trip", "Pack bags\n" + string.Join("\n", Enumerable.Repeat(new string('x', 100), 50)), _now);
        await worker.SyncDiscordThreadsAsync();
        Assert.True(post.Messages.Count(message => message.Bot) > 1);

        _now = _now.AddMinutes(6);
        await worker.SyncDiscordThreadsAsync();

        Assert.EndsWith("Book the car", _database.GetNote(id)!.Body);
    }

    [Fact]
    public async Task Text_typed_in_the_post_of_a_note_deleted_on_the_PC_keeps_the_post_archived_with_it()
    {
        var id = _database.CreateNote("Old", "Old words", _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);

        _database.DeleteNote(id, _now);
        var typed = _discord.UserMessage(post, "Important thing");
        await Event(worker, "MESSAGE_CREATE", MessageEvent(post, typed));
        await worker.SyncDiscordThreadsAsync();

        Assert.True(_discord.Posts.ContainsKey(post.Id));
        Assert.True(post.Archived);
        Assert.Contains(post.Messages, message => message.Id == typed.Id);
        Assert.Contains(post.Messages, message => message.Bot && message.Body.Contains("deleted on the PC"));
        Assert.Equal(WaypointDatabase.PostRetired, _database.FindDiscordThread(post.Id)!.State);
    }

    [Fact]
    public async Task A_post_Waypoint_could_not_delete_or_archive_is_still_never_taken_for_a_new_note()
    {
        var worker = await Started(lists: null);
        var post = _discord.UserPost(NotesForum, "Idea", "Some idea");
        await Event(worker, "THREAD_CREATE", ThreadEvent(post));
        var noteId = long.Parse(_database.FindDiscordThread(post.Id)!.Key);

        _discord.CanManageThreads = false;
        _database.DeleteNote(noteId, _now);
        await worker.SyncDiscordThreadsAsync();
        Assert.Equal(WaypointDatabase.PostRetired, _database.FindDiscordThread(post.Id)!.State);
        Assert.False(post.Archived);

        // However long ago it was seen, it's known by its record, so a catch-up doesn't bring the note back as new.
        _database.SetPreference("discord_seen", string.Join(' ', Enumerable.Range(1, 200).Select(number => "x" + number)));
        _now = _now.AddMinutes(6);
        await worker.SyncDiscordThreadsAsync();
        Assert.Empty(_database.GetNotes());

        // Typing there later says why nothing was added, and the words stay.
        var typed = _discord.UserMessage(post, "Another idea");
        await Event(worker, "MESSAGE_CREATE", MessageEvent(post, typed));
        Assert.Empty(_database.GetNotes());
        Assert.Contains(post.Messages, message => message.Id == typed.Id);
        Assert.Single(post.Messages, message => message.Bot && message.Body.Contains("deleted on the PC"));
    }

    [Fact]
    public async Task A_duplicate_list_post_that_cannot_be_archived_is_not_read_as_a_new_list_again()
    {
        _database.CreateList("Groceries", _now);
        var worker = await Started(notes: null);
        _discord.CanManageThreads = false;

        var post = _discord.UserPost(ListsForum, "Groceries", "Milk");
        await Event(worker, "THREAD_CREATE", ThreadEvent(post));
        _database.SetPreference("discord_seen", string.Join(' ', Enumerable.Range(1, 200).Select(number => "x" + number)));
        _now = _now.AddMinutes(6);
        await worker.SyncDiscordThreadsAsync();

        Assert.Equal(["Milk"], _database.GetListEntries("Groceries").Select(entry => entry.Text));
        Assert.Single(post.Messages, message => message.Bot && message.Body.Contains("already has its own post"));
    }

    [Fact]
    public async Task Two_messages_handled_at_once_both_land_in_the_note()
    {
        var id = _database.CreateNote("Ideas", "First", _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);

        var second = _discord.UserMessage(post, "Second");
        var third = _discord.UserMessage(post, "Third");
        await Task.WhenAll(
            worker.Discord!.HandleEventAsync("MESSAGE_CREATE", MessageEvent(post, second)),
            worker.Discord.HandleEventAsync("MESSAGE_CREATE", MessageEvent(post, third)),
            worker.SyncDiscordThreadsAsync());
        await worker.ThreadsSettledAsync();

        Assert.Equal("First\n\nSecond\n\nThird", _database.GetNote(id)!.Body);
    }

    [Fact]
    public void A_save_from_Discord_never_writes_over_a_change_saved_meanwhile()
    {
        var id = _database.CreateNote("Ideas", "First", _now);
        _database.SaveNote(id, "Ideas", "First, changed on the PC", _now);

        Assert.False(_database.SaveNoteIfUnchanged(id, "Ideas", "First\n\nFrom the phone", "First", _now));
        Assert.Equal("First, changed on the PC", _database.GetNote(id)!.Body);
        Assert.True(_database.SaveNoteIfUnchanged(id, "Ideas", "First, changed on the PC\n\nFrom the phone", "First, changed on the PC", _now));
    }

    [Fact]
    public async Task When_the_forum_cannot_be_read_tags_stay_as_they_are_and_a_tagged_post_waits_for_its_folder()
    {
        var biology = _database.CreateNoteFolder("Biology", null, _now).Id;
        var id = _database.CreateNote("Cells", "Nucleus", _now, biology);
        var first = await Started(lists: null);
        var post = PostFor(id);
        var tag = _discord.Forums[NotesForum].Single().Id;
        first.StopDiscord();

        _discord.FailingReads.Add($"/channels/{NotesForum}");
        var worker = await Started(lists: null);
        Assert.Equal([tag], post.Tags);
        Assert.DoesNotContain(_discord.All, call => call.Method == HttpMethod.Patch && call.Path == $"/channels/{post.Id}");

        var mine = _discord.UserPost(NotesForum, "Mitosis", "Phases", [tag]);
        await Event(worker, "THREAD_CREATE", ThreadEvent(mine));
        Assert.Single(_database.GetNotes());

        _discord.FailingReads.Clear();
        _now = _now.AddMinutes(6);
        await worker.SyncDiscordThreadsAsync();
        Assert.Equal(biology, _database.GetNotes().Single(note => note.Title == "Mitosis").FolderId);
    }

    [Fact]
    public async Task Posts_Discord_turns_down_wait_their_turn_and_never_hold_up_newer_ones()
    {
        var biology = _database.CreateNoteFolder("Biology", null, _now).Id;
        for (var number = 0; number < WaypointWorker.MaxNewPostsPerRound + 2; number++)
        {
            _database.CreateNote($"Loose {number}", "Unfiled", _now);
        }

        var tagged = _database.CreateNote("Tagged", "In a folder", _now, biology);
        _discord.RequireTags = true;
        await Started(lists: null);

        Assert.NotNull(_database.GetDiscordThread(WaypointDatabase.ThreadNote, tagged.ToString()));
        Assert.Single(_discord.PostsIn(NotesForum));
    }

    [Fact]
    public async Task A_tag_deleted_by_hand_is_made_again_after_Discord_turns_a_post_down()
    {
        var biology = _database.CreateNoteFolder("Biology", null, _now).Id;
        _database.CreateNote("Cells", "Nucleus", _now, biology);
        var worker = await Started(lists: null);

        _discord.Forums[NotesForum].Clear();
        var later = _database.CreateNote("Membranes", "Lipids", _now, biology);
        await worker.SyncDiscordThreadsAsync();
        Assert.Null(_database.GetDiscordThread(WaypointDatabase.ThreadNote, later.ToString()));

        _now = _now.AddMinutes(10);
        await worker.SyncDiscordThreadsAsync();
        Assert.Single(_discord.Forums[NotesForum]);
        Assert.Equal([_discord.Forums[NotesForum][0].Id], PostFor(later).Tags);
    }

    [Fact]
    public async Task A_long_title_is_cut_between_whole_characters()
    {
        var title = new string('a', 98) + "\U0001F600\U0001F600";
        var id = _database.CreateNote(title, "Words", _now);
        await Started(lists: null);

        var name = PostFor(id).Name;
        Assert.True(name.Length <= 100);
        Assert.EndsWith("…", name);
        Assert.DoesNotContain(_discord.PostNames, made => made.Length > 0 && char.IsHighSurrogate(made[^2]) && made[^1] == '…');
    }

    [Fact]
    public async Task A_post_that_lost_its_first_message_is_replaced_and_points_to_the_new_one()
    {
        var id = _database.CreateNote("Keep", "Words", _now);
        var worker = await Started(lists: null);
        var old = PostFor(id);
        old.Messages.RemoveAll(message => message.Id == old.Id);

        _database.SaveNote(id, "Keep", "Words and more", _now);
        await worker.SyncDiscordThreadsAsync();

        var again = PostFor(id);
        Assert.NotEqual(old.Id, again.Id);
        Assert.True(old.Archived);
        Assert.Contains(old.Messages, message => message.Bot && message.Says($"<#{again.Id}>"));

        // Typing in the old post still reaches the note, and says where it lives now, once it's saved.
        string? bodyWhenSaid = null;
        _discord.Watch = (method, path, body) =>
        {
            if (method == HttpMethod.Post && path == $"/channels/{old.Id}/messages" && body.Contains("moved"))
            {
                bodyWhenSaid = _database.GetNote(id)!.Body;
            }
        };
        var typed = _discord.UserMessage(old, "From the old post");
        await Event(worker, "MESSAGE_CREATE", MessageEvent(old, typed));
        Assert.EndsWith("From the old post", _database.GetNote(id)!.Body);
        Assert.EndsWith("From the old post", bodyWhenSaid);
    }

    [Fact]
    public async Task A_new_notes_forum_moves_each_post_and_the_old_ones_point_to_the_new()
    {
        var id = _database.CreateNote("Keep", "Words", _now);
        var first = await Started(lists: null);
        var old = PostFor(id);
        first.StopDiscord();

        const string newer = "500000000000000003";
        _discord.Forums[newer] = [];
        await Started(notes: newer, lists: null);

        var moved = PostFor(id);
        Assert.Equal(newer, moved.Parent);
        Assert.True(old.Archived);
        Assert.Contains(old.Messages, message => message.Bot && message.Says($"<#{moved.Id}>"));
    }

    [Fact]
    public async Task A_read_pages_past_other_peoples_messages_to_reach_the_owners()
    {
        var id = _database.CreateNote("Busy", "Start", _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);
        for (var number = 0; number < 130; number++)
        {
            _discord.UserMessage(post, $"Chatter {number}", author: "someone");
        }

        var mine = _discord.UserMessage(post, "Mine at the end");
        _now = _now.AddMinutes(6);
        await worker.SyncDiscordThreadsAsync();

        Assert.Equal("Start\n\nMine at the end", _database.GetNote(id)!.Body);
        Assert.Equal(mine.Id, _database.FindDiscordThread(post.Id)!.Cursor);
    }

    [Fact]
    public async Task A_new_post_whose_first_message_cannot_be_read_is_tried_again_instead_of_making_an_empty_note()
    {
        var worker = await Started(lists: null);
        var post = _discord.UserPost(NotesForum, "Later", "Read me later");
        _discord.FailingReads.Add($"/channels/{post.Id}/messages/{post.Id}");

        await Event(worker, "THREAD_CREATE", ThreadEvent(post));
        Assert.Empty(_database.GetNotes());

        _discord.FailingReads.Clear();
        _now = _now.AddMinutes(6);
        await worker.SyncDiscordThreadsAsync();
        Assert.Equal("Read me later", Assert.Single(_database.GetNotes()).Body);
    }

    [Fact]
    public async Task A_list_post_that_cannot_become_a_list_keeps_its_first_message()
    {
        var worker = await Started(notes: null);

        var post = _discord.UserPost(ListsForum, "my  list", "Milk");
        await Event(worker, "THREAD_CREATE", ThreadEvent(post));

        Assert.Contains(post.Messages, message => message.Id == post.Id);
        Assert.Contains(post.Messages, message => message.Bot && message.Body.Contains("A list needs a name"));
        Assert.Equal(WaypointDatabase.PostRetired, _database.FindDiscordThread(post.Id)!.State);
    }

    [Fact]
    public void List_lines_sent_again_are_never_added_twice()
    {
        _database.CreateList("Packing", _now);
        _database.AddListEntries("Packing", ["Charger", "Socks"], _now);
        _database.SetEntryDone(_database.GetListEntries("Packing").Single(entry => entry.Text == "Socks"), true, _now);

        _database.AddListEntries("Packing", ["charger", "Socks", "Hat"], _now);

        Assert.Equal(["Charger", "Hat", "Socks"], _database.GetListEntries("Packing").Select(entry => entry.Text).OrderBy(text => text));
        Assert.DoesNotContain(_database.GetListEntries("Packing"), entry => entry.Done);
        _database.AddListEntries(WaypointDatabase.ShoppingList, ["Eggs"], _now);
        _database.AddListEntries(WaypointDatabase.ShoppingList, ["Eggs"], _now);
        Assert.Single(_database.GetListEntries(WaypointDatabase.ShoppingList));
    }

    private sealed class SlowWriter(TaskCompletionSource writing, TaskCompletionSource gate) : INoteWriter
    {
        public async Task<WrittenNotes> WriteAsync(IReadOnlyList<NoteSource> sources, NoteWriteContext context, CancellationToken cancellationToken = default)
        {
            writing.TrySetResult();
            await gate.Task;
            return new WrittenNotes("## Written", []);
        }
    }

    [Fact]
    public async Task A_post_still_being_made_into_a_note_is_only_taken_away_once_that_is_done()
    {
        var worker = await Started(lists: null);
        var writing = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        worker.NoteFiles = new LocalNotesData(_database, new NoteMaker(_database, new SlowWriter(writing, gate), null));
        var post = _discord.UserPost(NotesForum, "From a PDF", "", files: ["cells.pdf"]);
        await worker.Discord!.HandleEventAsync("THREAD_CREATE", ThreadEvent(post));
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Deleted on the PC while its notes are still being written: the round leaves the post to a later round.
        var noteId = long.Parse(_database.FindDiscordThread(post.Id)!.Key, CultureInfo.InvariantCulture);
        _database.DeleteNote(noteId, _now);
        await worker.SyncDiscordThreadsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(_discord.Posts.ContainsKey(post.Id));
        Assert.Equal(WaypointDatabase.PostLive, _database.FindDiscordThread(post.Id)!.State);

        gate.SetResult();
        await worker.ThreadsSettledAsync();
        await worker.SyncDiscordThreadsAsync();
        Assert.NotEqual(WaypointDatabase.PostLive, _database.FindDiscordThread(post.Id)!.State);
    }

    [Fact]
    public async Task Notes_being_written_from_a_new_posts_files_never_hold_up_an_Edit_or_the_next_round()
    {
        var id = _database.CreateNote("Edit me", "Line one", _now);
        var worker = await Started(lists: null);
        var writing = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        worker.NoteFiles = new LocalNotesData(_database, new NoteMaker(_database, new SlowWriter(writing, gate), null));

        var post = _discord.UserPost(NotesForum, "From a PDF", "", files: ["cells.pdf"]);
        await worker.Discord!.HandleEventAsync("THREAD_CREATE", ThreadEvent(post));
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // An Edit saves straight away, so what was typed in the form is never turned away.
        var part = PostFor(id).Messages.First(message => message.Bot);
        var form = (await worker.OnButtonActedAsync(new ButtonPress(EditId(part), null, part.Id, PostFor(id).Id)))!.Form!;
        var reply = await Task.Run(() => worker.OnFormSent(form.Id, new Dictionary<string, string> { ["text"] = "Line one changed" })).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Saved", reply.Card.Title);
        Assert.Equal("Line one changed", _database.GetNote(id)!.Body);

        // A round goes on too, and gives a note made meanwhile its post.
        var later = _database.CreateNote("Made meanwhile", "Words", _now);
        await worker.SyncDiscordThreadsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(_database.GetDiscordThread(WaypointDatabase.ThreadNote, later.ToString(CultureInfo.InvariantCulture)));

        gate.SetResult();
        await worker.ThreadsSettledAsync();
        Assert.Contains(_database.GetNotes(), note => note.Title == "From a PDF" && note.Body.Contains("Written"));
    }

    [Fact]
    public async Task An_Edit_that_cannot_be_saved_hands_back_what_was_typed()
    {
        var id = _database.CreateNote("Short lived", "Words", _now);
        var worker = await Started(lists: null);
        var part = PostFor(id).Messages.Single(message => message.Bot);
        var form = (await worker.OnButtonActedAsync(new ButtonPress(EditId(part), null, part.Id, PostFor(id).Id)))!.Form!;

        _database.DeleteNote(id, _now);
        var reply = worker.OnFormSent(form.Id, new Dictionary<string, string> { ["text"] = "Line one: kept\nLine *two*" });

        Assert.Equal("Not saved", reply.Card.Title);
        Assert.True(reply.Private);
        Assert.Contains("Line one: kept\nLine *two*", reply.Card.Description);
    }

    [Fact]
    public void Checkboxes_and_underlines_read_the_way_lists_show_them()
    {
        Assert.Equal("☐ milk\n☑ eggs\n  ☐ bread\nSome __big__ news", DiscordNoteText.Format("- [ ] milk\n- [x] eggs\n  * [ ] bread\nSome <u>big</u> news"));
    }

    [Fact]
    public async Task Back_and_Next_on_a_list_post_keep_Start_over_and_the_line_about_typing()
    {
        _database.CreateList("Packing", _now);
        _database.AddListEntries("Packing", Enumerable.Range(1, 30).Select(number => $"Thing {number}").ToList(), _now);
        _database.SetEntryDone(_database.GetListEntries("Packing").First(), true, _now);
        var worker = await Started(notes: null);
        var post = PostForList("Packing");
        var card = post.Messages.First(message => message.Bot);

        var next = await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + DiscordPages.Id(DiscordViews.ListTarget("Packing"), 1), null, card.Id, post.Id));

        Assert.Contains("Type here to add", next!.Card!.Description);
        Assert.Contains(next.Card.Actions, action => action.Label == "Start over");
    }

    [Fact]
    public async Task Renaming_a_list_on_the_PC_keeps_its_post()
    {
        _database.CreateList("Packing", _now);
        var worker = await Started(notes: null);
        var post = PostForList("Packing");

        _database.RenameList("Packing", "Trip packing", _now);
        await worker.SyncDiscordThreadsAsync();
        _now = _now.AddMinutes(3);
        await worker.SyncDiscordThreadsAsync();

        Assert.Same(post, PostForList("Trip packing"));
        Assert.Equal("Trip packing", post.Name);
        Assert.True(_discord.Posts.ContainsKey(post.Id));
        Assert.Single(_discord.PostsIn(ListsForum), found => found.Name == "Trip packing");
    }

    [Fact]
    public async Task Waypoints_own_notices_in_a_post_are_cleared_away_after_a_while()
    {
        _database.CreateList("Packing", _now);
        var worker = await Started(notes: null);
        var post = PostForList("Packing");
        var typed = _discord.UserMessage(post, "", files: ["cells.pdf"]);
        await Event(worker, "MESSAGE_CREATE", MessageEvent(post, typed));
        Assert.Contains(post.Messages, message => message.Bot && message.Body.Contains("Files stay out of lists"));

        _now = _now.AddMinutes(61);
        await worker.SyncDiscordThreadsAsync();
        Assert.DoesNotContain(post.Messages, message => message.Bot && message.Body.Contains("Files stay out of lists"));
        Assert.Contains(post.Messages, message => message.Bot);
    }

    [Fact]
    public async Task A_notice_in_a_post_is_cleared_before_a_new_part_goes_under_it()
    {
        var id = _database.CreateNote("Growing", "Short", _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);
        var typed = _discord.UserMessage(post, "", files: ["voice.mp3"]);
        await Event(worker, "MESSAGE_CREATE", MessageEvent(post, typed));
        Assert.Contains(post.Messages, message => message.Bot && message.Body.Contains("Not every file was added"));

        // A notice sent just now stays, even with a new part under it, so there's time to read it.
        _database.SaveNote(id, "Growing", string.Join("\n", Enumerable.Range(0, 120).Select(number => $"A long line {number} with plenty of words in it")), _now);
        await worker.SyncDiscordThreadsAsync();
        Assert.Contains(post.Messages, message => message.Bot && message.Body.Contains("Not every file was added"));

        // Once it's a couple of minutes old, the next new part clears it first.
        _now = _now.AddMinutes(2);
        _database.SaveNote(id, "Growing", string.Join("\n", Enumerable.Range(0, 200).Select(number => $"A long line {number} with plenty of words in it")), _now);
        await worker.SyncDiscordThreadsAsync();

        var bots = post.Messages.Where(message => message.Bot).ToList();
        Assert.DoesNotContain(bots, message => message.Body.Contains("Not every file was added"));
        Assert.True(bots.Count >= 3);
    }

    [Fact]
    public async Task A_note_post_says_typing_there_adds_to_the_end()
    {
        var id = _database.CreateNote("Ideas", "One idea", _now);
        await Started(lists: null);

        Assert.Contains("Type here to add to the end.", Descriptions(PostFor(id)).Last());
    }

    [Fact]
    public void Changed_lines_from_separate_paragraphs_stay_separate_paragraphs()
    {
        Assert.Equal("Intro, rewritten\n\nEnd, rewritten", WaypointWorker.ChangedLines("Intro\n\nMiddle\n\nEnd", "Intro, rewritten\n\nMiddle\n\nEnd, rewritten"));
        Assert.Equal("New one\nNew two", WaypointWorker.ChangedLines("a\nb\nc", "a\nNew one\nNew two\nc"));
        Assert.Equal("Changed\n\nAlso changed", WaypointWorker.ChangedLines("a\nb\nc", "Changed\nb\nAlso changed"));
    }

    [Fact]
    public void A_comma_inside_a_number_does_not_split_an_entry()
    {
        Assert.Equal(["1,000 piece puzzle", "$1,000 gift card", "milk", "eggs", "bread"], WaypointWorker.ListLines("1,000 piece puzzle, $1,000 gift card\nmilk,eggs ,bread"));
    }

    [Fact]
    public void A_part_leaves_room_to_add_to_it_in_the_form()
    {
        Assert.True(WaypointWorker.PartMarkdown <= 3000);
    }

    [Fact]
    public async Task A_list_name_in_a_reply_is_escaped_once()
    {
        _database.CreateList("Car_stuff", _now);
        var worker = await Started(notes: null);

        var post = _discord.UserPost(ListsForum, "Car_stuff", "Oil");
        await Event(worker, "THREAD_CREATE", ThreadEvent(post));

        var said = post.Messages.Where(message => message.Bot).Select(message => Json(message.Body).GetProperty("embeds")[0].GetProperty("title").GetString()).ToList();
        Assert.Contains("Added to Car\\_stuff", said);
    }

    [Fact]
    public async Task A_slow_round_of_posts_never_holds_up_the_pinned_views()
    {
        const string notesView = "600000000000000001";
        var worker = await Started(lists: null, notesView: notesView);
        using var held = new ManualResetEventSlim();
        _discord.Watch = (method, path, _) =>
        {
            if (method == HttpMethod.Post && path == $"/channels/{NotesForum}/threads")
            {
                held.Wait(TimeSpan.FromSeconds(20));
            }
        };

        int ViewCalls()
        {
            lock (_discord.Calls)
            {
                return _discord.Calls.Count(call => call.Path.StartsWith($"/channels/{notesView}", StringComparison.Ordinal));
            }
        }

        _database.CreateNote("Slow to post", "Words", _now);
        _now = _now.AddMinutes(2);
        var slow = Task.Run(worker.RefreshDiscordViewsAsync);
        await WaitFor(() =>
        {
            lock (_discord.Calls)
            {
                return _discord.Calls.Any(call => call.Method == HttpMethod.Post && call.Path == $"/channels/{NotesForum}/threads");
            }
        });

        // The notes view is drawn again while that post is still being made.
        var before = ViewCalls();
        _database.CreateNote("Another", "More words", _now);
        _now = _now.AddMinutes(2);
        var next = Task.Run(worker.RefreshDiscordViewsAsync);
        for (var tries = 0; tries < 300 && ViewCalls() == before; tries++)
        {
            await Task.Delay(10);
        }

        var drawn = ViewCalls() > before;
        held.Set();
        await Task.WhenAll(slow, next);
        Assert.True(drawn);
    }

    [Fact]
    public async Task How_far_a_post_was_read_is_saved_along_with_the_text_it_added()
    {
        var id = _database.CreateNote("Ideas", "First", _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);
        var typed = _discord.UserMessage(post, "Second");

        // By the time the message is taken away, the note and how far the post was read are already saved together.
        (string? Read, string Body)? whenTaken = null;
        _discord.Watch = (method, path, _) =>
        {
            if (method == HttpMethod.Delete && path == $"/channels/{post.Id}/messages/{typed.Id}")
            {
                whenTaken = (_database.FindDiscordThread(post.Id)!.Cursor, _database.GetNote(id)!.Body);
            }
        };
        await Event(worker, "MESSAGE_CREATE", MessageEvent(post, typed));

        Assert.NotNull(whenTaken);
        Assert.Equal(typed.Id, whenTaken.Value.Read);
        Assert.EndsWith("Second", whenTaken.Value.Body);
    }

    [Fact]
    public void A_note_save_that_finds_the_note_changed_leaves_how_far_the_post_was_read()
    {
        var id = _database.CreateNote("Ideas", "First", _now);
        _database.AddDiscordThread(new DiscordThreadRow("700", WaypointDatabase.ThreadNote, id.ToString(CultureInfo.InvariantCulture), NotesForum, WaypointDatabase.PostLive, false, null, [], "700"));

        Assert.False(_database.SaveNoteIfUnchanged(id, "Ideas", "First\n\nSecond", "Something else", _now, ("700", "710")));
        Assert.Equal("700", _database.FindDiscordThread("700")!.Cursor);

        Assert.True(_database.SaveNoteIfUnchanged(id, "Ideas", "First\n\nSecond", "First", _now, ("700", "710")));
        Assert.Equal("710", _database.FindDiscordThread("700")!.Cursor);
    }

    [Fact]
    public async Task Typing_in_a_moved_post_whose_note_was_then_deleted_says_so_and_moves_on()
    {
        var id = _database.CreateNote("Keep", "Words", _now);
        var worker = await Started(lists: null);
        var old = PostFor(id);
        old.Messages.RemoveAll(message => message.Id == old.Id);
        _database.SaveNote(id, "Keep", "Words and more", _now);
        await worker.SyncDiscordThreadsAsync();
        _database.DeleteNote(id, _now);
        await worker.SyncDiscordThreadsAsync();

        var typed = _discord.UserMessage(old, "Too late");
        await Event(worker, "MESSAGE_CREATE", MessageEvent(old, typed));

        Assert.Contains(old.Messages, message => message.Bot && message.Body.Contains("Not added"));
        Assert.DoesNotContain(old.Messages, message => message.Bot && message.Body.Contains("Saved, and this post moved"));
        Assert.Contains(old.Messages, message => message.Id == typed.Id);
        Assert.Equal(typed.Id, _database.FindDiscordThread(old.Id)!.Cursor);
    }

    [Fact]
    public async Task After_a_note_comes_back_from_History_its_old_post_points_to_the_new_one()
    {
        var id = _database.CreateNote("Back", "Words", _now);
        var worker = await Started(lists: null);
        var old = PostFor(id);
        _discord.CanManageThreads = false;
        _database.DeleteNote(id, _now);
        await worker.SyncDiscordThreadsAsync();
        Assert.Equal(WaypointDatabase.PostRetired, _database.FindDiscordThread(old.Id)!.State);

        _discord.CanManageThreads = true;
        _database.RestoreNote(id, _now);
        await worker.SyncDiscordThreadsAsync();
        var again = PostFor(id);
        Assert.NotEqual(old.Id, again.Id);

        var typed = _discord.UserMessage(old, "Still here");
        await Event(worker, "MESSAGE_CREATE", MessageEvent(old, typed));

        Assert.EndsWith("Still here", _database.GetNote(id)!.Body);
        Assert.Contains(old.Messages, message => message.Bot && message.Says($"<#{again.Id}>"));
        Assert.DoesNotContain(old.Messages, message => message.Bot && message.Body.Contains("deleted on the PC"));
    }

    [Fact]
    public async Task Something_typed_right_after_starting_a_post_is_added_without_waiting_for_the_catch_up()
    {
        var worker = await Started(lists: null);
        var post = _discord.UserPost(NotesForum, "Quick", "Start");

        // Typed before Waypoint knew the post, so the message itself was passed over.
        var typed = _discord.UserMessage(post, "And more");
        await Event(worker, "MESSAGE_CREATE", MessageEvent(post, typed));
        await Event(worker, "THREAD_CREATE", ThreadEvent(post));

        var note = long.Parse(_database.FindDiscordThread(post.Id)!.Key, CultureInfo.InvariantCulture);
        Assert.EndsWith("And more", _database.GetNote(note)!.Body);
        Assert.DoesNotContain(post.Messages, message => message.Id == typed.Id);
    }
}
