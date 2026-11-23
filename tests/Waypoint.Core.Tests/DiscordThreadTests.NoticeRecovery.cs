namespace Waypoint.Core.Tests;

/// <summary>
/// A notice in a post that went out unheard is found again even after a new part goes in under it, and one that can't
/// go out, because its post is gone or a day has passed, shows on the PC before it's let go. No network.
/// </summary>
public sealed partial class DiscordThreadTests
{
    private const string WaitingCard = """{"Title":"Made up heads up","Text":"Made up words for the post","Tone":0}""";

    [Fact]
    public async Task A_notice_sent_unheard_is_found_and_kept_after_a_new_part_goes_in_under_it()
    {
        var id = _database.CreateNote("Made up long reading", "Made up first words", _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);
        _discord.AnswersWhoAmI = true;

        // A recording sent in the post raises a notice, which goes out before Waypoint stops partway.
        FailPartway("BEFORE UPDATE OF message_id ON discord_sends WHEN NEW.send_key LIKE 'notice:%'");
        var typed = _discord.UserMessage(post, "", files: ["made-up-talk.mp3"]);
        await Record.ExceptionAsync(() => Event(worker, "MESSAGE_CREATE", MessageEvent(post, typed)));
        StopFailingPartway();
        Assert.Single(post.Messages, message => message.Bot && message.Says("Not every file was added"));

        // The note grows, so a new part goes in under the notice on the next round.
        Assert.True(_database.SaveNote(id, "Made up long reading", LongReading, _now));
        _now = _now.AddMinutes(2);
        await worker.SyncDiscordThreadsAsync();

        Assert.Equal(2, _database.FindDiscordThread(post.Id)!.Parts.Count);
        Assert.Single(post.Messages, message => message.Bot && message.Says("Not every file was added"));
        Assert.Single(_database.GetDiscordNotices(post.Id));
        Assert.Empty(_database.GetDiscordSends("notice:"));
    }

    [Fact]
    public async Task A_notice_waiting_in_a_post_that_was_retired_shows_on_the_PC_once_and_is_let_go()
    {
        var id = _database.CreateNote("Made up long reading", "Made up first words", _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);
        var key = $"notice:{post.Id}:madeupwaiting";
        _database.BeginDiscordSend(key, post.Id, _now);
        _database.SetDiscordSendCard(key, WaitingCard);

        _database.DeleteNote(id, _now);
        await worker.SyncDiscordThreadsAsync();
        await worker.SyncDiscordThreadsAsync();

        Assert.False(_database.FindDiscordThread(post.Id)!.Live);
        Assert.Equal("Made up heads up", Assert.Single(_shownHere).Title);
        Assert.Empty(_database.GetDiscordSends("notice:"));
        await worker.SyncDiscordThreadsAsync();
        Assert.True(worker.NotePostsSettled);
    }

    [Fact]
    public async Task A_notice_let_go_after_a_day_shows_on_the_PC_first()
    {
        _database.CreateList("Made up packing", _now);
        var worker = await Started(notes: null);
        var post = PostForList("Made up packing");
        var key = $"notice:{post.Id}:madeupwaiting";
        _database.BeginDiscordSend(key, post.Id, _now.AddDays(-2));
        _database.SetDiscordSendCard(key, WaitingCard);

        await worker.SyncDiscordThreadsAsync();
        await worker.SyncDiscordThreadsAsync();

        Assert.Equal("Made up heads up", Assert.Single(_shownHere).Title);
        Assert.Empty(_database.GetDiscordSends("notice:"));
        Assert.DoesNotContain(post.Messages, message => message.Bot && message.Says("Made up words for the post"));
    }

    [Fact]
    public async Task A_notice_is_still_tried_again_when_the_round_stops_partway_through_the_parts()
    {
        var id = _database.CreateNote("Made up long reading", LongReading, _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);
        await worker.Discord!.HandleEventAsync("READY", Json("""{"user":{"id":"bot"}}"""));
        var key = $"notice:{post.Id}:madeupwaiting";
        _database.BeginDiscordSend(key, post.Id, _now);
        _database.SetDiscordSendCard(key, WaitingCard);

        // The note gets shorter, but its last part can't be taken away, so the round stops there.
        var last = _database.FindDiscordThread(post.Id)!.Parts[^1].Message;
        _discord.FailingDeletes.Add(last);
        Assert.True(_database.SaveNote(id, "Made up long reading", "Made up short words", _now));
        _now = _now.AddMinutes(2);
        await worker.SyncDiscordThreadsAsync();

        Assert.Contains(post.Messages, message => message.Id == last);
        Assert.Single(post.Messages, message => message.Bot && message.Says("Made up words for the post"));
        Assert.Empty(_database.GetDiscordSends("notice:"));
    }
}
