namespace Waypoint.Core.Tests;

/// <summary>
/// A notice in a post whose earlier try can't be checked is never sent again. It's looked for on later rounds, even
/// after a restart, and shows on the PC once it has waited as long as sending is tried. No network.
/// </summary>
public sealed partial class DiscordThreadTests
{
    private const string FilesStayOut = "Files stay out of lists";

    [Fact]
    public async Task A_notice_whose_earlier_try_cant_be_checked_waits_instead_of_being_sent_again()
    {
        _database.CreateList("Made up packing", _now);
        var worker = await Started(notes: null);
        var post = PostForList("Made up packing");
        var typed = _discord.UserMessage(post, "", files: ["made-up-map.pdf"]);

        // The notice goes out, then Waypoint stops before it hears which message it is. Who the bot is isn't known
        // yet, so the post can't be looked through for it.
        FailPartway("BEFORE UPDATE OF message_id ON discord_sends");
        await Record.ExceptionAsync(() => Event(worker, "MESSAGE_CREATE", MessageEvent(post, typed)));
        StopFailingPartway();
        Assert.Single(post.Messages, message => message.Bot && message.Says(FilesStayOut));

        // Read again, and on the rounds after, it isn't sent again while whether it went out can't be told.
        await Event(worker, "MESSAGE_CREATE", MessageEvent(post, typed));
        await worker.SyncDiscordThreadsAsync();
        Assert.Single(post.Messages, message => message.Bot && message.Says(FilesStayOut));
        Assert.Empty(_shownHere);
        worker.StopDiscord();

        // After a restart it's still waiting, and once it has waited as long as sending is tried, it shows on the PC, once.
        var again = MakeWorker(notes: null);
        again.StartDiscord();
        await again.SyncDiscordThreadsAsync();
        Assert.Empty(_shownHere);
        _now = _now.AddMinutes(6);
        await again.SyncDiscordThreadsAsync();
        await again.SyncDiscordThreadsAsync();
        Assert.Equal(FilesStayOut, Assert.Single(_shownHere).Title);
        Assert.Single(post.Messages, message => message.Bot && message.Says(FilesStayOut));

        // Once the post can be looked through, the one already there is kept and cleared away like any other.
        await again.Discord!.HandleEventAsync("READY", Json("""{"user":{"id":"bot"}}"""));
        _now = _now.AddMinutes(5);
        await again.SyncDiscordThreadsAsync();
        Assert.Single(post.Messages, message => message.Bot && message.Says(FilesStayOut));
        Assert.Single(_database.GetDiscordNotices(post.Id));
        Assert.Empty(_database.GetDiscordSends("notice:"));

        _now = _now.AddMinutes(61);
        await again.SyncDiscordThreadsAsync();
        Assert.DoesNotContain(post.Messages, message => message.Bot && message.Says(FilesStayOut));
        Assert.Single(_shownHere);
    }

    [Fact]
    public async Task A_notice_shown_on_the_PC_that_turns_out_never_to_have_gone_out_is_let_go_without_sending_it()
    {
        _database.CreateList("Made up packing", _now);
        var worker = await Started(notes: null);
        var post = PostForList("Made up packing");
        var typed = _discord.UserMessage(post, "", files: ["made-up-map.pdf"]);

        // The notice goes out unheard, and is then taken away by hand, while the post can't be looked through.
        FailPartway("BEFORE UPDATE OF message_id ON discord_sends");
        await Record.ExceptionAsync(() => Event(worker, "MESSAGE_CREATE", MessageEvent(post, typed)));
        StopFailingPartway();
        lock (_discord.Posts)
        {
            post.Messages.RemoveAll(message => message.Bot && message.Says(FilesStayOut));
        }

        _now = _now.AddMinutes(6);
        await worker.SyncDiscordThreadsAsync();
        Assert.Single(_shownHere);

        // It isn't in the post, but it was shown on the PC, so it isn't sent now.
        await worker.Discord!.HandleEventAsync("READY", Json("""{"user":{"id":"bot"}}"""));
        _now = _now.AddMinutes(2);
        await worker.SyncDiscordThreadsAsync();
        Assert.DoesNotContain(post.Messages, message => message.Bot && message.Says(FilesStayOut));
        Assert.Empty(_database.GetDiscordSends("notice:"));
        Assert.Single(_shownHere);
    }

    [Fact]
    public async Task A_notice_that_keeps_failing_to_send_is_tried_less_and_less_often_and_shows_on_the_PC_once()
    {
        _database.CreateList("Made up packing", _now);
        var worker = await Started(notes: null);
        var post = PostForList("Made up packing");
        await worker.Discord!.HandleEventAsync("READY", Json("""{"user":{"id":"bot"}}"""));
        _discord.FailingSends = true;
        var typed = _discord.UserMessage(post, "", files: ["made-up-map.pdf"]);
        await Event(worker, "MESSAGE_CREATE", MessageEvent(post, typed));

        // A round a minute for ten minutes tries it only a few times, and it shows on the PC once it has waited long enough.
        for (var minute = 0; minute < 10; minute++)
        {
            _now = _now.AddMinutes(1);
            await worker.SyncDiscordThreadsAsync();
        }

        var tries = _discord.All.Count(call => call.Method == HttpMethod.Post && call.Path == $"/channels/{post.Id}/messages");
        Assert.InRange(tries, 2, 5);
        Assert.Equal(FilesStayOut, Assert.Single(_shownHere).Title);

        // Once Discord works again, it isn't in the post and it was shown on the PC, so it's let go without sending it.
        _discord.FailingSends = false;
        _now = _now.AddHours(1);
        await worker.SyncDiscordThreadsAsync();
        Assert.DoesNotContain(post.Messages, message => message.Bot && message.Says(FilesStayOut));
        Assert.Empty(_database.GetDiscordSends("notice:"));
        Assert.Single(_shownHere);
    }

    [Fact]
    public async Task The_same_notice_raised_again_after_one_was_shown_on_the_PC_is_sent_to_the_post()
    {
        _database.CreateList("Made up packing", _now);
        var worker = await Started(notes: null);
        var post = PostForList("Made up packing");
        await worker.Discord!.HandleEventAsync("READY", Json("""{"user":{"id":"bot"}}"""));
        _discord.FailingSends = true;
        await Event(worker, "MESSAGE_CREATE", MessageEvent(post, _discord.UserMessage(post, "", files: ["made-up-map.pdf"])));
        _now = _now.AddMinutes(6);
        await worker.SyncDiscordThreadsAsync();
        Assert.Single(_shownHere);

        // Later another file is sent, which raises the same notice, and Discord works again.
        _discord.FailingSends = false;
        await Event(worker, "MESSAGE_CREATE", MessageEvent(post, _discord.UserMessage(post, "", files: ["made-up-plan.pdf"])));

        Assert.Single(post.Messages, message => message.Bot && message.Says(FilesStayOut));
        Assert.Single(_database.GetDiscordNotices(post.Id));
        Assert.Empty(_database.GetDiscordSends("notice:"));
    }

    [Fact]
    public async Task A_waiting_notice_noted_under_an_older_layout_is_sent_once_and_its_own_note_settled()
    {
        _database.CreateList("Made up packing", _now);
        var worker = await Started(notes: null);
        var post = PostForList("Made up packing");
        await worker.Discord!.HandleEventAsync("READY", Json("""{"user":{"id":"bot"}}"""));

        // Noted by a version that laid notices out differently, so its key isn't the one this version would give it.
        var key = $"notice:{post.Id}:madeupolderlayout";
        _database.BeginDiscordSend(key, post.Id, _now);
        _database.SetDiscordSendCard(key, """{"Title":"Made up heads up","Text":"Made up words for the post","Tone":0}""");

        for (var round = 0; round < 3; round++)
        {
            _now = _now.AddMinutes(2);
            await worker.SyncDiscordThreadsAsync();
        }

        Assert.Single(post.Messages, message => message.Bot && message.Says("Made up words for the post"));
        Assert.Single(_database.GetDiscordNotices(post.Id));
        Assert.Empty(_database.GetDiscordSends("notice:"));
    }
}
