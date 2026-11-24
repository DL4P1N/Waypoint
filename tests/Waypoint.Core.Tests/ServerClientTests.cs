using System.Net;
using System.Text.Json;
using Waypoint.Core.Capture;
using Waypoint.Core.Remote;

namespace Waypoint.Core.Tests;

/// <summary>Server mode on the PC: what's shown and what's kept while the server can't be reached.</summary>
public sealed class ServerClientTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-client-{Guid.NewGuid()}");
    private readonly FakeServer _fake = new();

    public ServerClientTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>Answers with the open items, or acts like a server that's switched off.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public bool Up { get; set; } = true;

        public HttpStatusCode Answer { get; set; } = HttpStatusCode.OK;

        public List<string> Items { get; } = ["Read chapter 4"];

        public List<string> Received { get; } = [];

        /// <summary>A capture with this in it is turned down as a bad request, as one the server can never take.</summary>
        public string? Refuse { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!Up)
            {
                throw new HttpRequestException("No route to host");
            }

            if (request.Content is not null)
            {
                var sent = await request.Content.ReadAsStringAsync(cancellationToken);
                Received.Add(sent);
                if (Refuse is not null && sent.Contains(Refuse))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }
            }

            var body = request.RequestUri!.AbsolutePath switch
            {
                "/api/items/open" => JsonSerializer.Serialize(Items.Select((title, index) => new StoredItem(index + 1, ItemKind.Task, title, null, null, false)), RemoteJson.Options),
                "/api/captures" => JsonSerializer.Serialize(new CaptureAccepted(false), RemoteJson.Options),
                var path when path.EndsWith("/restore-body", StringComparison.Ordinal) => "",
                _ => "{}",
            };
            return new HttpResponseMessage(Answer) { Content = new StringContent(body) };
        }
    }

    private ServerClient Client() => new(new HttpClient(_fake) { BaseAddress = new Uri("http://server/") }, "token");

    [Fact]
    public void While_the_server_is_away_the_last_read_is_shown()
    {
        var client = Client();
        var data = new ServerToDoData(client);
        Assert.Equal("Read chapter 4", Assert.Single(data.GetOpenItems()).Title);

        _fake.Up = false;
        _fake.Items.Add("Something new");

        Assert.Equal("Read chapter 4", Assert.Single(data.GetOpenItems()).Title);
        Assert.Equal(ServerState.Offline, client.State);
        Assert.Contains("Can't reach the server", client.Problem);
    }

    [Fact]
    public void Undo_of_written_notes_on_an_older_server_that_answers_with_nothing_counts_as_put_back()
    {
        _fake.Answer = HttpStatusCode.NoContent;

        Assert.True(new ServerNotesData(Client()).RestoreNoteBody(5, new Notes.NoteBodyState("Made up text", null, null), DateTime.Now));
    }

    [Fact]
    public void While_the_server_is_away_changes_say_so_instead_of_pretending()
    {
        var client = Client();
        _fake.Up = false;

        var problem = Assert.Throws<ServerUnavailableException>(() => new ServerToDoData(client).SetDone(new StoredItem(1, ItemKind.Task, "x", null, null, false), true));

        Assert.Contains("saved and sent when it's back", problem.Message);
    }

    [Fact]
    public void Once_away_reads_stop_trying_until_a_check_finds_the_server()
    {
        var client = Client();
        var data = new ServerToDoData(client);
        data.GetOpenItems();
        _fake.Up = false;
        data.GetOpenItems();

        // Back up, but nothing has checked yet, so the kept copy is still shown without waiting on the network.
        _fake.Up = true;
        _fake.Items.Add("Something new");
        Assert.Single(data.GetOpenItems());

        Assert.NotNull(client.CheckAsync().GetAwaiter().GetResult());
        Assert.Equal(ServerState.Online, client.State);
        Assert.Equal(2, data.GetOpenItems().Count);
    }

    [Fact]
    public void With_nothing_read_yet_and_no_server_the_page_still_opens_and_the_status_says_why()
    {
        _fake.Up = false;
        var client = Client();

        Assert.Empty(new ServerToDoData(client).GetOpenItems());
        Assert.Throws<ServerUnavailableException>(() => client.Get<List<StoredItem>>("api/items/open"));
        Assert.Contains("Can't reach the server", client.Problem);
    }

    [Fact]
    public void A_refused_token_needs_pairing_again()
    {
        _fake.Answer = HttpStatusCode.Unauthorized;
        var client = Client();

        Assert.Null(client.CheckAsync().GetAwaiter().GetResult());

        Assert.Equal(ServerState.NotPaired, client.State);
    }

    [Fact]
    public async Task Queued_captures_survive_a_restart_and_go_in_order_once_the_server_is_back()
    {
        var path = Path.Combine(_folder, "queue.json");
        var client = Client();
        _fake.Up = false;

        new CaptureQueue(path).Add("first", DateTimeOffset.Now);
        new CaptureQueue(path).Add("second", DateTimeOffset.Now);
        Assert.Equal(0, await new CaptureQueue(path).SendAsync(capture => client.SendCaptureAsync(capture)));

        // As if the app closed and opened again.
        var queue = new CaptureQueue(path);
        Assert.Equal(2, queue.Count);

        _fake.Up = true;
        Assert.Equal(2, await queue.SendAsync(capture => client.SendCaptureAsync(capture)));
        Assert.Equal(0, queue.Count);
        Assert.Contains("first", _fake.Received[0]);
        Assert.Contains("second", _fake.Received[1]);
    }

    [Fact]
    public async Task A_capture_that_does_not_go_keeps_its_place_ahead_of_later_ones()
    {
        var queue = new CaptureQueue(Path.Combine(_folder, "queue.json"));
        queue.Add("first", DateTimeOffset.Now);
        queue.Add("second", DateTimeOffset.Now);
        var tried = new List<string>();

        await queue.SendAsync(capture =>
        {
            tried.Add(capture.Text);
            return Task.FromResult(false);
        });

        Assert.Equal(["first"], tried);
        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void A_capture_keeps_one_id_so_the_server_can_tell_a_resend()
    {
        var queue = new CaptureQueue(Path.Combine(_folder, "queue.json"));
        var capture = queue.Add("first", DateTimeOffset.Now);
        var seen = new List<string>();

        queue.SendAsync(next => { seen.Add(next.Id); return Task.FromResult(false); }).GetAwaiter().GetResult();
        queue.SendAsync(next => { seen.Add(next.Id); return Task.FromResult(false); }).GetAwaiter().GetResult();

        Assert.All(seen, id => Assert.Equal(capture.Id, id));
    }

    [Fact]
    public async Task A_capture_the_server_turns_down_is_set_aside_and_the_rest_still_go()
    {
        var client = Client();
        var queue = new CaptureQueue(Path.Combine(_folder, "queue.json"));
        _fake.Refuse = "never takes this";
        queue.Add("the server never takes this", DateTimeOffset.Now);
        queue.Add("buy milk", DateTimeOffset.Now);

        var sent = await queue.SendAsync(capture => client.SendCaptureAsync(capture));

        Assert.Equal(1, sent);
        Assert.Equal(0, queue.Count);
        Assert.Contains(_fake.Received, body => body.Contains("buy milk"));
        Assert.Equal(1, queue.RefusedCount);
        Assert.Equal("Something you typed couldn't be saved on the server. It's kept on this PC.", queue.Refused);
        Assert.Contains("the server never takes this", File.ReadAllText(queue.RefusedPath));

        // Open shows it as plain text beside the queue. The kept file stays, so nothing is lost by opening it.
        var copy = queue.WriteRefusedCopy();
        Assert.Equal(Path.Combine(_folder, CaptureQueue.RefusedCopyName), copy);
        var text = File.ReadAllText(copy!);
        Assert.StartsWith(KeptCopy.Intro, text);
        Assert.Contains("Typed ", text);
        Assert.Contains("the server never takes this", text);
        Assert.DoesNotContain("buy milk", text);
        Assert.Equal(1, queue.RefusedCount);
        Assert.Equal(ServerState.Online, client.State);
    }

    [Fact]
    public void Several_turned_down_captures_are_counted_without_naming_a_file()
    {
        var queue = new CaptureQueue(Path.Combine(_folder, "queue.json"));
        Assert.Null(queue.Refused);
        Assert.Null(queue.WriteRefusedCopy());

        File.WriteAllText(queue.RefusedPath, System.Text.Json.JsonSerializer.Serialize(
            new[] { new CaptureRequest("a", "one", DateTimeOffset.Now), new CaptureRequest("b", "two", DateTimeOffset.Now, "notes.pdf") }, RemoteJson.Options));

        Assert.Equal("2 things you typed couldn't be saved on the server. They're kept on this PC.", queue.Refused);
        Assert.Contains(", from notes.pdf:", File.ReadAllText(queue.WriteRefusedCopy()!));
    }

    [Fact]
    public async Task A_capture_is_kept_to_try_again_when_the_server_is_only_busy()
    {
        var queue = new CaptureQueue(Path.Combine(_folder, "queue.json"));
        queue.Add("buy milk", DateTimeOffset.Now);
        _fake.Answer = HttpStatusCode.TooManyRequests;

        Assert.Equal(0, await queue.SendAsync(capture => Client().SendCaptureAsync(capture)));
        Assert.Equal(1, queue.Count);
        Assert.Equal(0, queue.RefusedCount);
    }

    private static ServerStatus StatusAt(long lastReply, string boot) =>
        new(DateTime.Now, "America/Chicago", 0, null, null, null, null, [], [], lastReply, boot);

    private static ServerReply Reply(long seq) => new(seq, $"reply {seq}", [], false, false);

    [Fact]
    public void Replies_from_before_the_app_started_are_skipped_but_a_restarted_servers_are_shown()
    {
        var cursor = new ReplyCursor();
        Assert.Equal(40, cursor.After(StatusAt(40, "first")));
        cursor.Saw([Reply(41), Reply(42)]);
        Assert.Equal(42, cursor.After(StatusAt(42, "first")));

        // The server restarted, so its replies count from 1 again.
        Assert.Equal(0, cursor.After(StatusAt(1, "second")));
        cursor.Saw([Reply(1)]);
        Assert.Equal(1, cursor.After(StatusAt(1, "second")));

        // A restarted server that says nothing of its boot is noticed by its count going back.
        var older = new ReplyCursor();
        older.After(StatusAt(9, null!));
        Assert.Equal(0, older.After(StatusAt(2, null!)));
    }

    /// <summary>A stand-in for the Windows user's encryption: reversible, never plain JSON, and it turns away bytes it didn't make.</summary>
    private sealed class Scramble : IFileProtection
    {
        private static readonly byte[] Mark = "WP1"u8.ToArray();

        public byte[] Protect(byte[] plain) => [.. Mark, .. plain.Select(value => (byte)(value ^ 0x5A))];

        public byte[] Unprotect(byte[] stored) => stored.AsSpan().StartsWith(Mark)
            ? stored[Mark.Length..].Select(value => (byte)(value ^ 0x5A)).ToArray()
            : throw new System.Security.Cryptography.CryptographicException("Not protected here");
    }

    private string ReadsFile => Path.Combine(_folder, "server-reads.json");

    [Fact]
    public void The_saved_reads_are_kept_encrypted_and_load_after_a_restart()
    {
        var before = Client();
        before.SaveReadsIn(ReadsFile, new Scramble());
        new ServerToDoData(before).GetOpenItems();
        before.FlushSavedReads();

        var stored = File.ReadAllBytes(ReadsFile);
        Assert.DoesNotContain("Read chapter 4", System.Text.Encoding.UTF8.GetString(stored));
        Assert.Contains("Read chapter 4", System.Text.Encoding.UTF8.GetString(new Scramble().Unprotect(stored)));

        _fake.Up = false;
        var after = Client();
        after.SaveReadsIn(ReadsFile, new Scramble());
        Assert.Equal("Read chapter 4", Assert.Single(new ServerToDoData(after).GetOpenItems()).Title);
    }

    [Fact]
    public void A_plain_file_from_before_is_read_once_and_written_again_encrypted()
    {
        var plain = Client();
        plain.SaveReadsIn(ReadsFile);
        new ServerToDoData(plain).GetOpenItems();
        plain.FlushSavedReads();
        Assert.Contains("Read chapter 4", File.ReadAllText(ReadsFile));

        _fake.Up = false;
        var after = Client();
        after.SaveReadsIn(ReadsFile, new Scramble());

        Assert.Equal("Read chapter 4", Assert.Single(new ServerToDoData(after).GetOpenItems()).Title);
        var stored = File.ReadAllBytes(ReadsFile);
        Assert.DoesNotContain("Read chapter 4", System.Text.Encoding.UTF8.GetString(stored));
        Assert.Contains("Read chapter 4", System.Text.Encoding.UTF8.GetString(new Scramble().Unprotect(stored)));
    }

    [Fact]
    public void A_saved_file_that_cant_be_read_counts_as_empty()
    {
        // Encrypted for someone else, or damaged: nothing is shown from it, and the next read replaces it.
        File.WriteAllBytes(ReadsFile, [1, 0, 0, 0, 0xD0, 0x8C, 0x9D, 0xDF, 0x01, 0x15]);
        _fake.Up = false;
        var client = Client();
        client.SaveReadsIn(ReadsFile, new Scramble());
        Assert.Empty(new ServerToDoData(client).GetOpenItems());

        _fake.Up = true;
        var later = Client();
        later.SaveReadsIn(ReadsFile, new Scramble());
        new ServerToDoData(later).GetOpenItems();
        later.FlushSavedReads();
        Assert.Contains("Read chapter 4", System.Text.Encoding.UTF8.GetString(new Scramble().Unprotect(File.ReadAllBytes(ReadsFile))));
    }

    [Fact]
    public async Task A_run_of_changes_is_written_once_they_stop_rather_than_each_time()
    {
        var client = Client();
        client.SaveReadsIn(ReadsFile);
        for (var n = 0; n < 20; n++)
        {
            client.Keep("api/notes/1", $"Draft {n}");
        }

        Assert.False(File.Exists(ReadsFile));

        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(ReadsFile) && waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(50);
        }

        Assert.Contains("Draft 19", File.ReadAllText(ReadsFile));
        Assert.DoesNotContain("Draft 18", File.ReadAllText(ReadsFile));
    }

    [Fact]
    public void Searches_are_kept_for_now_but_not_saved_in_the_file()
    {
        var client = Client();
        client.SaveReadsIn(ReadsFile);
        client.Keep("api/notes/search?words=mitosis", new[] { "Found by a search" });
        client.Keep("api/notes/search/in?words=&subfolders=True", new[] { "Unfiled list" });
        client.Keep("api/history?filter=All&limit=50&words=", new[] { "History page" });
        client.FlushSavedReads();

        var saved = File.ReadAllText(ReadsFile);
        Assert.DoesNotContain("Found by a search", saved);
        Assert.Contains("Unfiled list", saved);
        Assert.Contains("History page", saved);
        _fake.Up = false;
        Assert.Equal(["Found by a search"], client.Get<string[]>("api/notes/search?words=mitosis"));
    }

    [Fact]
    public void The_saved_file_stays_under_its_size_and_keeps_the_newest()
    {
        var client = Client();
        client.SaveReadsIn(ReadsFile);
        var big = new string('x', 1024 * 1024);
        for (var n = 0; n < 10; n++)
        {
            client.Keep($"api/notes/{n}", $"{n}{big}");
        }

        client.FlushSavedReads();

        Assert.True(new FileInfo(ReadsFile).Length <= ServerClient.MaxSavedReadBytes + 4096);
        _fake.Up = false;
        var after = Client();
        after.SaveReadsIn(ReadsFile);
        Assert.StartsWith("9", after.Get<string>("api/notes/9"));
    }
}
