namespace Waypoint.Core.Tests;

/// <summary>
/// A later part sent just before a crash, whose note then gets shorter, is past the parts the post has now. It's taken
/// away on the next round, if it's the bot's own, and one already gone is let go. No network.
/// </summary>
public sealed partial class DiscordThreadTests
{
    private static readonly string LongReading = string.Join("\n\n", Enumerable.Range(1, 90).Select(line => $"Made up line {line} about a made up topic that runs on for a while."));

    [Fact]
    public async Task A_later_part_sent_just_before_a_crash_is_taken_away_once_the_note_gets_shorter()
    {
        var id = _database.CreateNote("Made up long reading", LongReading, _now);
        var post = await SecondPartSentButNotKept(id, "BEFORE UPDATE OF parts ON discord_posts");
        Assert.NotNull(Assert.Single(_database.GetDiscordSends("part:")).MessageId);

        Assert.True(_database.SaveNote(id, "Made up long reading", "Made up short words", _now));
        var second = MakeWorker(lists: null);
        second.StartDiscord();
        await second.SyncDiscordThreadsAsync();

        var only = Assert.Single(post.Messages, message => message.Bot);
        Assert.Equal([only.Id], _database.FindDiscordThread(post.Id)!.Parts.Select(part => part.Message));
        Assert.Contains("Made up short words", only.Body);
        Assert.Empty(_database.GetDiscordSends("part:"));
    }

    [Fact]
    public async Task A_later_part_whose_message_was_never_heard_back_about_is_found_by_its_button_and_taken_away()
    {
        var id = _database.CreateNote("Made up long reading", LongReading, _now);
        var post = await SecondPartSentButNotKept(id, "BEFORE UPDATE OF message_id ON discord_sends WHEN NEW.send_key LIKE 'part:%'");
        Assert.Null(Assert.Single(_database.GetDiscordSends("part:")).MessageId);

        // Only the bot's own messages are looked at, so someone else's in the post is never taken.
        var other = _discord.UserMessage(post, "Made up words of a friend", author: "made-up-friend");
        Assert.True(_database.SaveNote(id, "Made up long reading", "Made up short words", _now));
        var second = MakeWorker(lists: null);
        second.StartDiscord();

        // While who the bot is can't be told, the post can't be looked through, so it waits.
        await second.SyncDiscordThreadsAsync();
        Assert.Equal(2, post.Messages.Count(message => message.Bot));
        Assert.Single(_database.GetDiscordSends("part:"));

        await second.Discord!.HandleEventAsync("READY", Json("""{"user":{"id":"bot"}}"""));
        await second.SyncDiscordThreadsAsync();
        Assert.Single(post.Messages, message => message.Bot);
        Assert.Contains(post.Messages, message => message.Id == other.Id);
        Assert.Empty(_database.GetDiscordSends("part:"));
    }

    [Fact]
    public async Task A_later_part_never_heard_back_about_is_found_by_its_button_after_its_words_change_and_not_sent_again()
    {
        var id = _database.CreateNote("Made up long reading", LongReading, _now);
        var post = await SecondPartSentButNotKept(id, "BEFORE UPDATE OF message_id ON discord_sends WHEN NEW.send_key LIKE 'part:%'");
        var sentBefore = post.Messages.Where(message => message.Bot).Select(message => message.Id).ToList();

        // The note's end changes, so the part already sent no longer shows the words it would be found by.
        Assert.True(_database.SaveNote(id, "Made up long reading", LongReading.Replace("Made up line 90 about", "Made up line 90, now changed, about"), _now));
        var second = MakeWorker(lists: null);
        second.StartDiscord();

        // While who the bot is can't be told, the post can't be looked through, so nothing is sent.
        await second.SyncDiscordThreadsAsync();
        Assert.Equal(sentBefore, post.Messages.Where(message => message.Bot).Select(message => message.Id));
        Assert.Single(_database.GetDiscordSends("part:"));

        await second.Discord!.HandleEventAsync("READY", Json("""{"user":{"id":"bot"}}"""));
        await second.SyncDiscordThreadsAsync();
        await second.SyncDiscordThreadsAsync();

        Assert.Equal(sentBefore, post.Messages.Where(message => message.Bot).Select(message => message.Id));
        Assert.Equal(sentBefore, _database.FindDiscordThread(post.Id)!.Parts.Select(part => part.Message));
        Assert.Contains("now changed", Descriptions(post)[^1]);
        Assert.Empty(_database.GetDiscordSends("part:"));
    }

    [Fact]
    public async Task A_stray_part_already_taken_away_by_hand_is_let_go()
    {
        var id = _database.CreateNote("Made up long reading", LongReading, _now);
        var post = await SecondPartSentButNotKept(id, "BEFORE UPDATE OF parts ON discord_posts");
        var stray = Assert.Single(_database.GetDiscordSends("part:")).MessageId;
        lock (_discord.Posts)
        {
            post.Messages.RemoveAll(message => message.Id == stray);
        }

        Assert.True(_database.SaveNote(id, "Made up long reading", "Made up short words", _now));
        var second = MakeWorker(lists: null);
        second.StartDiscord();
        await second.SyncDiscordThreadsAsync();

        Assert.Single(post.Messages, message => message.Bot);
        Assert.Empty(_database.GetDiscordSends("part:"));
        Assert.Contains(_discord.All, call => call.Method == HttpMethod.Delete && call.Path.EndsWith($"/messages/{stray}", StringComparison.Ordinal));
    }

    /// <summary>
    /// The note's post is made, then its second part is sent and Waypoint stops partway, before the post's parts are saved.
    /// </summary>
    private async Task<Post> SecondPartSentButNotKept(long id, string when)
    {
        FailPartway(when);
        var first = MakeWorker(lists: null);
        first.StartDiscord();
        await Record.ExceptionAsync(() => first.SyncDiscordThreadsAsync());
        first.StopDiscord();
        StopFailingPartway();
        var post = PostFor(id);
        Assert.Equal(2, post.Messages.Count(message => message.Bot));
        Assert.Single(_database.FindDiscordThread(post.Id)!.Parts);
        return post;
    }
}
