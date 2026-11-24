using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>
/// "Not added" in a post being retired and "Moved" in a replaced one are each said once, with Discord's nonce, even
/// when the post can't be archived for a few rounds. No network.
/// </summary>
public sealed partial class DiscordThreadTests
{
    private const string StaysHere = "It stays in this post.";

    [Fact]
    public async Task Not_added_in_a_post_that_cant_be_archived_yet_is_said_once_over_several_rounds()
    {
        var id = _database.CreateNote("Made up retiring note", "Made up words", _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);
        _discord.UserMessage(post, "Made up words typed before it went");
        _discord.FailingArchives = true;
        _database.DeleteNote(id, _now);

        await worker.SyncDiscordThreadsAsync();
        _now = _now.AddMinutes(5);
        await worker.SyncDiscordThreadsAsync();
        Assert.Single(post.Messages, message => message.Bot && message.Says(StaysHere));
        Assert.True(_database.FindDiscordThread(post.Id)!.Live);

        _discord.FailingArchives = false;
        _now = _now.AddMinutes(5);
        await worker.SyncDiscordThreadsAsync();

        var said = Assert.Single(post.Messages, message => message.Bot && message.Says(StaysHere));
        Assert.Contains("nonce", said.Body);
        Assert.True(post.Archived);
        Assert.Equal(WaypointDatabase.PostRetired, _database.FindDiscordThread(post.Id)!.State);
        Assert.Empty(_database.GetDiscordSends("retire:"));
    }

    [Fact]
    public async Task Moved_in_a_replaced_post_goes_with_a_nonce_and_leaves_no_note_behind()
    {
        var id = _database.CreateNote("Made up keep", "Made up words", _now);
        var worker = await Started(lists: null);
        var old = PostFor(id);
        lock (_discord.Posts)
        {
            old.Messages.RemoveAll(message => message.Id == old.Id);
        }

        _database.SaveNote(id, "Made up keep", "Made up words and more", _now);
        await worker.SyncDiscordThreadsAsync();

        var again = PostFor(id);
        var moved = Assert.Single(old.Messages, message => message.Bot && message.Says($"<#{again.Id}>"));
        Assert.Contains("nonce", moved.Body);
        Assert.Empty(_database.GetDiscordSends("moved:"));
    }
}
