using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>
/// A forum post or a notice in one is noted before it's made, so a crash between making it and saving it leaves exactly
/// one, which works as usual afterwards. A failing trigger stands in for the crash. No network.
/// </summary>
public sealed partial class DiscordThreadTests
{
    [Fact]
    public async Task A_note_post_made_just_before_a_crash_is_saved_on_the_next_start_and_not_made_again()
    {
        var id = _database.CreateNote("Made up field trip", "Bring the made up permit slip", _now);

        // The post is made and noted, then Waypoint stops before the post is saved.
        FailPartway("BEFORE INSERT ON discord_posts");
        var first = MakeWorker(lists: null);
        first.StartDiscord();
        await Record.ExceptionAsync(() => first.SyncDiscordThreadsAsync());
        first.StopDiscord();
        StopFailingPartway();
        Assert.Null(_database.GetDiscordThread(WaypointDatabase.ThreadNote, id.ToString()));
        Assert.NotNull(Assert.Single(_database.GetDiscordSends("post:")).MessageId);

        await StartedAgainAndTyped(id);
    }

    [Fact]
    public async Task A_note_post_made_but_never_heard_back_about_is_found_in_the_forum_instead_of_made_again()
    {
        var id = _database.CreateNote("Made up field trip", "Bring the made up permit slip", _now);

        // Discord makes the post, then Waypoint stops before it hears which post it is.
        FailPartway("BEFORE UPDATE OF message_id ON discord_sends");
        var first = MakeWorker(lists: null);
        first.StartDiscord();
        await Record.ExceptionAsync(() => first.SyncDiscordThreadsAsync());
        first.StopDiscord();
        StopFailingPartway();
        Assert.Single(_discord.PostsIn(NotesForum));

        await StartedAgainAndTyped(id);
    }

    [Fact]
    public async Task A_notice_posted_just_before_a_crash_is_kept_and_cleared_away_like_any_other()
    {
        _database.CreateList("Made up packing", _now);
        var worker = await Started(notes: null);
        var post = PostForList("Made up packing");
        var typed = _discord.UserMessage(post, "", files: ["made-up-map.pdf"]);

        FailPartway("BEFORE INSERT ON discord_post_notices");
        await Record.ExceptionAsync(() => Event(worker, "MESSAGE_CREATE", MessageEvent(post, typed)));
        StopFailingPartway();
        Assert.Single(post.Messages, message => message.Bot && message.Body.Contains("Files stay out of lists"));
        Assert.Empty(_database.GetDiscordNotices(post.Id));

        // The same notice again finds the one already there.
        await Record.ExceptionAsync(() => Event(worker, "MESSAGE_CREATE", MessageEvent(post, typed)));
        Assert.Single(post.Messages, message => message.Bot && message.Body.Contains("Files stay out of lists"));

        _now = _now.AddMinutes(61);
        await worker.SyncDiscordThreadsAsync();
        Assert.DoesNotContain(post.Messages, message => message.Bot && message.Body.Contains("Files stay out of lists"));
        Assert.Empty(_database.GetDiscordSends("notice:"));
    }

    /// <summary>
    /// A fresh start saves the post already made, makes no other, and typing in it adds to the note once.
    /// </summary>
    private async Task StartedAgainAndTyped(long id)
    {
        var second = MakeWorker(lists: null);
        second.StartDiscord();
        second.Discord!.DeferAfter = TimeSpan.FromMinutes(1);
        await second.SyncDiscordThreadsAsync();
        await second.SyncDiscordThreadsAsync();

        var post = Assert.Single(_discord.PostsIn(NotesForum));
        Assert.Same(post, PostFor(id));
        Assert.Single(_discord.All, call => call.Method == HttpMethod.Post && call.Path.EndsWith("/threads", StringComparison.Ordinal));
        Assert.Empty(_database.GetDiscordSends("post:"));

        var typed = _discord.UserMessage(post, "Pack a made up lunch");
        await Event(second, "MESSAGE_CREATE", MessageEvent(post, typed));
        await Event(second, "MESSAGE_CREATE", MessageEvent(post, typed));
        Assert.Equal("Bring the made up permit slip\n\nPack a made up lunch", _database.GetNote(id)!.Body);
    }

    private void FailPartway(string when)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TRIGGER fail_partway {when} BEGIN SELECT RAISE(ABORT, 'made to fail partway'); END";
        command.ExecuteNonQuery();
    }

    private void StopFailingPartway()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TRIGGER fail_partway";
        command.ExecuteNonQuery();
    }
}
