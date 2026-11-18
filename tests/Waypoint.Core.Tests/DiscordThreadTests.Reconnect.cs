using System.Text.Json;
using Waypoint.Core.Background;

namespace Waypoint.Core.Tests;

/// <summary>
/// The live connection to Discord dropping and coming back while a round is reading the forum posts. When it comes
/// back, Discord says RESUMED (or READY for a new session) and replays the messages sent meanwhile, so the same
/// messages reach Waypoint twice: once from the round reading the post, and once from the connection. Each has to be
/// added once, nothing typed may be lost, and no post may be made twice.
/// </summary>
public sealed partial class DiscordThreadTests
{
    /// <summary>
    /// Calls <paramref name="reconnect"/> the first time Waypoint makes a request <paramref name="matches"/>, before it's
    /// answered, so the connection comes back while the round is partway through. Events go straight to the bridge,
    /// as the connection passes them on. The check it returns says whether it came back at all.
    /// </summary>
    private Func<bool> ReconnectWhen(Func<HttpMethod, string, bool> matches, Action reconnect)
    {
        var done = 0;
        _discord.Watch = (method, path, _) =>
        {
            if (matches(method, path) && Interlocked.Exchange(ref done, 1) == 0)
            {
                reconnect();
            }
        };
        return () => Volatile.Read(ref done) == 1;
    }

    private static void Deliver(WaypointWorker worker, string type, JsonElement data) =>
        worker.Discord!.HandleEventAsync(type, data).GetAwaiter().GetResult();

    [Theory]
    [InlineData("RESUMED")]
    [InlineData("READY")]
    public async Task Typing_in_a_note_post_while_disconnected_is_added_once_when_the_connection_comes_back_during_a_round(string welcome)
    {
        var id = _database.CreateNote("Made up trip", "Start", _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);
        var waypointsOwn = post.Messages.Count(message => message.Bot);

        // Typed while the connection was down, so only reading the post will find them.
        var typed = new[] { "Pack the made up tent", "Check the made up tide times", "Bring a made up kite" }
            .Select(text => _discord.UserMessage(post, text)).ToList();
        Message? late = null;

        // The round reads the post, and as it does the connection comes back and replays what was typed.
        var reconnected = ReconnectWhen((method, path) => method == HttpMethod.Get && path == $"/channels/{post.Id}/messages", () =>
        {
            Deliver(worker, welcome, Json("{}"));
            foreach (var message in typed)
            {
                Deliver(worker, "MESSAGE_CREATE", MessageEvent(post, message));
            }
        });

        // And one more is typed while the round is folding in what it read.
        var lateDone = 0;
        var watch = _discord.Watch!;
        _discord.Watch = (method, path, body) =>
        {
            watch(method, path, body);
            if (method == HttpMethod.Delete && path.StartsWith($"/channels/{post.Id}/messages/", StringComparison.Ordinal) && Interlocked.Exchange(ref lateDone, 1) == 0)
            {
                late = _discord.UserMessage(post, "Leave the made up stove");
                Deliver(worker, "MESSAGE_CREATE", MessageEvent(post, late));
            }
        };

        _now = _now.AddHours(1);
        await worker.SyncDiscordThreadsAsync();
        await worker.ThreadsSettledAsync();
        await WaitFor(() => _database.GetNote(id)!.Body.Contains("Leave the made up stove"));
        Assert.True(reconnected());
        _discord.Watch = null;
        await worker.SyncDiscordThreadsAsync();
        await worker.ThreadsSettledAsync();

        Assert.NotNull(late);
        Assert.Equal(
            "Start\n\nPack the made up tent\n\nCheck the made up tide times\n\nBring a made up kite\n\nLeave the made up stove",
            _database.GetNote(id)!.Body);
        Assert.DoesNotContain(post.Messages, message => !message.Bot);
        Assert.Single(_discord.PostsIn(NotesForum));
        Assert.Equal(waypointsOwn, post.Messages.Count(message => message.Bot));
        Assert.DoesNotContain(_discord.All, call => call.Method == HttpMethod.Post && call.Path.EndsWith("/threads", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Typing_in_a_list_post_while_disconnected_is_added_once_when_the_connection_comes_back_during_a_round()
    {
        _database.CreateList("Made up camping", _now);
        _database.AddListEntry("Made up camping", "Tent", _now);
        var worker = await Started(notes: null);
        var post = PostForList("Made up camping");
        var typed = new[] { "Stove, lantern", "Tent\nRope" }.Select(text => _discord.UserMessage(post, text)).ToList();

        var reconnected = ReconnectWhen((method, path) => method == HttpMethod.Get && path == $"/channels/{post.Id}/messages", () =>
        {
            Deliver(worker, "RESUMED", Json("{}"));
            foreach (var message in typed.AsEnumerable().Reverse())
            {
                Deliver(worker, "MESSAGE_CREATE", MessageEvent(post, message));
            }
        });

        _now = _now.AddHours(1);
        await worker.SyncDiscordThreadsAsync();
        await worker.ThreadsSettledAsync();
        Assert.True(reconnected());
        _discord.Watch = null;
        await worker.SyncDiscordThreadsAsync();
        await worker.ThreadsSettledAsync();

        Assert.Equal(["Tent", "Stove", "lantern", "Rope"], _database.GetListEntries("Made up camping").Select(entry => entry.Text));
        Assert.DoesNotContain(post.Messages, message => !message.Bot);
        Assert.Single(_discord.PostsIn(ListsForum), found => found.Name == "Made up camping");
        Assert.Contains("lantern", post.Shown);
    }

    [Fact]
    public async Task A_post_started_while_disconnected_makes_one_list_when_its_start_is_replayed_during_a_round()
    {
        var worker = await Started(notes: null);
        var started = _discord.UserPost(ListsForum, "Made up hardware", "Screws\nWood glue");
        var listsBefore = _discord.PostsIn(ListsForum).Count;

        // The round asks which posts are active, and as it does the connection comes back and replays the new post.
        var reconnected = ReconnectWhen((method, path) => method == HttpMethod.Get && path.EndsWith("/threads/active", StringComparison.Ordinal), () =>
        {
            Deliver(worker, "RESUMED", Json("{}"));
            Deliver(worker, "THREAD_CREATE", ThreadEvent(started));
            Deliver(worker, "MESSAGE_CREATE", MessageEvent(started, started.Messages[0]));
        });

        _now = _now.AddHours(1);
        await worker.SyncDiscordThreadsAsync();
        await worker.ThreadsSettledAsync();
        Assert.True(reconnected());
        _discord.Watch = null;
        await worker.SyncDiscordThreadsAsync();
        await worker.ThreadsSettledAsync();

        Assert.Single(_database.GetListNames(), name => name == "Made up hardware");
        Assert.Equal(["Screws", "Wood glue"], _database.GetListEntries("Made up hardware").Select(entry => entry.Text));
        Assert.Equal(listsBefore, _discord.PostsIn(ListsForum).Count);
        Assert.Contains("Wood glue", started.Shown);
    }
}
