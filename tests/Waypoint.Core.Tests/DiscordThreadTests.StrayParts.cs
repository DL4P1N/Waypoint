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
    public async Task A_waiting_notice_goes_after_the_parts_and_never_hides_a_part_sent_unheard()
    {
        var id = _database.CreateNote("Made up long reading", LongReading, _now);
        var post = await SecondPartSentButNotKept(id, "BEFORE UPDATE OF message_id ON discord_sends WHEN NEW.send_key LIKE 'part:%'");
        var parts = post.Messages.Where(message => message.Bot).Select(message => message.Id).ToList();

        // A notice is also waiting to be tried again in the same post.
        var key = $"notice:{post.Id}:madeupwaiting";
        _database.BeginDiscordSend(key, post.Id, _now);
        _database.SetDiscordSendCard(key, """{"Title":"Made up heads up","Text":"Made up words for the post","Tone":0}""");

        var second = MakeWorker(lists: null);
        second.StartDiscord();
        await second.Discord!.HandleEventAsync("READY", Json("""{"user":{"id":"bot"}}"""));
        await second.SyncDiscordThreadsAsync();

        var bots = post.Messages.Where(message => message.Bot).ToList();
        Assert.Equal(3, bots.Count);
        Assert.Equal(parts, bots.Take(2).Select(message => message.Id));
        Assert.True(bots[2].Says("Made up words for the post"));
        Assert.Equal(parts, _database.FindDiscordThread(post.Id)!.Parts.Select(part => part.Message));
        Assert.Empty(_database.GetDiscordSends(""));
    }

    [Fact]
    public async Task A_stray_part_after_many_messages_in_the_post_is_still_found_by_reading_from_the_last_part()
    {
        var id = _database.CreateNote("Made up long reading", "Made up first words", _now);
        await Started(lists: null);
        var post = PostFor(id);

        // Far more than one look reads, typed before the note grew, and left in the post.
        lock (_discord.Posts)
        {
            for (var line = 0; line < 600; line++)
            {
                post.Messages.Add(new Message { Id = _discord.NextId(), Author = "made-up-friend", Content = $"Made up chat {line}" });
            }
        }

        // The note grows to three parts. The second is kept, and the third goes out unheard before Waypoint stops.
        var longer = string.Join("\n\n", Enumerable.Range(1, 200).Select(line => $"Made up line {line} about a made up topic that runs on for a while."));
        Assert.True(_database.SaveNote(id, "Made up long reading", longer, _now));
        FailPartway("BEFORE UPDATE OF message_id ON discord_sends WHEN NEW.send_key LIKE 'part:%:2'");
        var first = MakeWorker(lists: null);
        first.StartDiscord();
        await Record.ExceptionAsync(() => first.SyncDiscordThreadsAsync());
        first.StopDiscord();
        StopFailingPartway();
        Assert.Equal(3, post.Messages.Count(message => message.Bot));
        Assert.Equal(2, _database.FindDiscordThread(post.Id)!.Parts.Count);

        // Then it gets shorter again, to two parts.
        Assert.True(_database.SaveNote(id, "Made up long reading", LongReading, _now));
        var second = MakeWorker(lists: null);
        second.StartDiscord();
        await second.Discord!.HandleEventAsync("READY", Json("""{"user":{"id":"bot"}}"""));
        await second.SyncDiscordThreadsAsync();

        Assert.Equal(_database.FindDiscordThread(post.Id)!.Parts.Select(part => part.Message), post.Messages.Where(message => message.Bot).Select(message => message.Id));
        Assert.Equal(2, post.Messages.Count(message => message.Bot));
        Assert.Empty(_database.GetDiscordSends("part:"));
    }

    [Fact]
    public async Task A_stray_part_that_cant_be_taken_away_is_let_go_after_a_day_so_the_rounds_settle()
    {
        var id = _database.CreateNote("Made up long reading", LongReading, _now);
        var post = await SecondPartSentButNotKept(id, "BEFORE UPDATE OF parts ON discord_posts");
        var stray = Assert.Single(_database.GetDiscordSends("part:")).MessageId!;
        _discord.FailingDeletes.Add(stray);

        Assert.True(_database.SaveNote(id, "Made up long reading", "Made up short words", _now));
        var second = MakeWorker(lists: null);
        second.StartDiscord();
        await second.SyncDiscordThreadsAsync();
        Assert.Single(_database.GetDiscordSends("part:"));
        Assert.False(second.NotePostsSettled);

        _now = _now.AddDays(1).AddMinutes(1);
        await second.SyncDiscordThreadsAsync();
        Assert.Empty(_database.GetDiscordSends("part:"));
        Assert.Contains(post.Messages, message => message.Id == stray);
        await second.SyncDiscordThreadsAsync();
        Assert.True(second.NotePostsSettled);
    }

    [Fact]
    public async Task A_part_noted_for_a_post_that_was_retired_is_let_go()
    {
        var id = _database.CreateNote("Made up long reading", LongReading, _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);
        _database.BeginDiscordSend($"part:{post.Id}:5", post.Id, _now);

        _database.DeleteNote(id, _now);
        await worker.SyncDiscordThreadsAsync();
        await worker.SyncDiscordThreadsAsync();

        Assert.False(_database.FindDiscordThread(post.Id)!.Live);
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
