using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>
/// A post or a later part that went through unheard is found again rather than made twice, even when the forum can't
/// be read for a while or the note's title changes in between. No network.
/// </summary>
public sealed partial class DiscordThreadTests
{
    private const string ActivePosts = "/guilds/guild/threads/active";

    [Fact]
    public async Task A_note_post_made_unheard_is_not_made_again_while_the_forum_cant_be_read()
    {
        var id = _database.CreateNote("Made up field trip", "Bring the made up permit slip", _now);
        await MadeButNeverHeardBack();

        // Still unreadable on the next start, so whether the post is there isn't known, and it isn't made again.
        var second = MakeWorker(lists: null);
        second.StartDiscord();
        await second.SyncDiscordThreadsAsync();
        Assert.Single(_discord.PostsIn(NotesForum));
        Assert.Null(_database.GetDiscordThread(WaypointDatabase.ThreadNote, id.ToString()));

        // Once they can be read, the post already made is the one kept.
        _discord.FailingReads.Remove(ActivePosts);
        _now = _now.AddMinutes(6);
        await second.SyncDiscordThreadsAsync();
        var post = Assert.Single(_discord.PostsIn(NotesForum));
        Assert.Same(post, PostFor(id));
        Assert.Single(_discord.PostNames);
        Assert.Empty(_database.GetDiscordSends("post:"));
    }

    [Fact]
    public async Task A_note_post_made_unheard_is_found_by_the_name_it_was_made_with_after_the_title_changes()
    {
        var id = _database.CreateNote("Made up field trip", "Bring the made up permit slip", _now);
        await MadeButNeverHeardBack();
        Assert.True(_database.SaveNote(id, "Made up museum trip", "Bring the made up permit slip", _now));
        _discord.FailingReads.Remove(ActivePosts);

        var second = MakeWorker(lists: null);
        second.StartDiscord();
        await second.SyncDiscordThreadsAsync();

        var post = Assert.Single(_discord.PostsIn(NotesForum));
        Assert.Same(post, PostFor(id));
        Assert.Single(_discord.PostNames);
        Assert.Empty(_database.GetDiscordSends("post:"));
    }

    /// <summary>
    /// Discord makes the note's post, then Waypoint stops before it hears which post it is, while the forum's posts
    /// can't be read, so the try straight after can't look for it. They stay unreadable until a test says otherwise.
    /// </summary>
    private async Task MadeButNeverHeardBack()
    {
        _discord.FailingReads.Add(ActivePosts);
        FailPartway("BEFORE UPDATE OF message_id ON discord_sends");
        var first = MakeWorker(lists: null);
        first.StartDiscord();
        await Record.ExceptionAsync(() => first.SyncDiscordThreadsAsync());
        await Record.ExceptionAsync(() => first.SyncDiscordThreadsAsync());
        first.StopDiscord();
        StopFailingPartway();
        Assert.Single(_discord.PostsIn(NotesForum));
    }
}
