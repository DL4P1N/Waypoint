using Waypoint.Core.Data;
using Waypoint.Core.Discord;

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
        Assert.Equal(DiscordBridge.PostUnchecked, second.Discord!.ThreadProblem);

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

    [Fact]
    public async Task A_later_part_of_a_note_sent_just_before_a_crash_is_kept_and_not_sent_again()
    {
        var body = string.Join("\n\n", Enumerable.Range(1, 90).Select(line => $"Made up line {line} about a made up topic that runs on for a while."));
        var id = _database.CreateNote("Made up long reading", body, _now);

        // The second part is sent, then Waypoint stops before the post's parts are saved.
        FailPartway("BEFORE UPDATE OF parts ON discord_posts");
        var first = MakeWorker(lists: null);
        first.StartDiscord();
        await Record.ExceptionAsync(() => first.SyncDiscordThreadsAsync());
        first.StopDiscord();
        StopFailingPartway();
        var post = PostFor(id);
        Assert.Equal(2, post.Messages.Count(message => message.Bot));

        var second = MakeWorker(lists: null);
        second.StartDiscord();
        await second.SyncDiscordThreadsAsync();
        await second.SyncDiscordThreadsAsync();

        Assert.Equal(2, post.Messages.Count(message => message.Bot));
        Assert.Equal(post.Messages.Where(message => message.Bot).Select(message => message.Id), _database.FindDiscordThread(post.Id)!.Parts.Select(part => part.Message));
        Assert.Empty(_database.GetDiscordSends("part:"));
    }

    [Fact]
    public async Task A_post_the_owner_starts_makes_one_note_even_when_saving_the_post_is_cut_off_and_tried_again()
    {
        var worker = await Started(lists: null);
        var post = _discord.UserPost(NotesForum, "Made up idea", "Paint the made up shelf");

        // The note is made, then saving which note the post shows is cut off.
        FailPartway("BEFORE UPDATE ON discord_posts");
        await Record.ExceptionAsync(() => Event(worker, "THREAD_CREATE", ThreadEvent(post)));
        StopFailingPartway();
        Assert.Equal(WaypointDatabase.PostPending, _database.FindDiscordThread(post.Id)!.State);

        worker.StopDiscord();

        // Started again, the post is caught up on and finished with the note already made.
        var again = MakeWorker(lists: null);
        again.StartDiscord();
        await again.SyncDiscordThreadsAsync();
        await again.ThreadsSettledAsync();

        var note = Assert.Single(_database.GetNotes());
        Assert.Equal("Paint the made up shelf", note.Body);
        Assert.Equal(note.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), _database.FindDiscordThread(post.Id)!.Key);
    }

    [Fact]
    public async Task A_deleted_post_with_a_part_left_noted_from_a_cut_off_try_is_made_again()
    {
        var body = string.Join("\n\n", Enumerable.Range(1, 90).Select(line => $"Made up line {line} about a made up topic that runs on for a while."));
        var id = _database.CreateNote("Made up long reading", body, _now);
        var worker = await Started(lists: null);
        var old = PostFor(id);
        Assert.Equal(2, old.Messages.Count(message => message.Bot));

        // A try at the second part was cut off before it was kept, and then the post was deleted on Discord.
        var row = _database.FindDiscordThread(old.Id)!;
        _database.SetDiscordThreadParts(old.Id, [row.Parts[0]]);
        _database.BeginDiscordSend($"part:{old.Id}:1", old.Id, _now);
        lock (_discord.Posts)
        {
            _discord.Posts.Remove(old.Id);
        }

        // The bot knows who it is, as it does once connected, so the post's messages can be looked through.
        await worker.Discord!.HandleEventAsync("READY", Json("""{"user":{"id":"bot"}}"""));
        await worker.SyncDiscordThreadsAsync();

        var post = Assert.Single(_discord.PostsIn(NotesForum));
        Assert.NotEqual(old.Id, post.Id);
        Assert.Same(post, PostFor(id));
        Assert.Equal(2, post.Messages.Count(message => message.Bot));
        Assert.Empty(_database.GetDiscordSends("part:"));
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
