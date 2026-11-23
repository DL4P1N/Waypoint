namespace Waypoint.Core.Tests;

/// <summary>A message typed in a note's post that keeps failing is tried a few times, then left, and its files are added once however often it's read.</summary>
public sealed partial class DiscordThreadTests
{
    private const string StuckWords = "made up stuck words";

    [Fact]
    public async Task A_file_in_a_message_read_again_after_a_failure_is_added_to_the_note_once()
    {
        var id = _database.CreateNote("Made up trip", "Pack made up bags", _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);
        var typed = _discord.UserMessage(post, StuckWords, files: ["made-up-map.pdf"]);

        // The file joins the note, then saving the words fails, so the message is read again later.
        FailPartway($"BEFORE UPDATE OF body ON items WHEN NEW.body LIKE '%{StuckWords}%'");
        await Record.ExceptionAsync(() => Event(worker, "MESSAGE_CREATE", MessageEvent(post, typed)));
        StopFailingPartway();
        await Event(worker, "MESSAGE_CREATE", MessageEvent(post, typed));

        Assert.Single(_database.GetNoteSources(id));
        Assert.Equal($"Pack made up bags\n\n{StuckWords}", _database.GetNote(id)!.Body);
    }

    [Fact]
    public async Task A_message_that_keeps_failing_is_left_after_a_few_tries_and_the_ones_after_it_are_still_added()
    {
        var id = _database.CreateNote("Made up trip", "Pack made up bags", _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);
        var stuck = _discord.UserMessage(post, StuckWords);
        var later = _discord.UserMessage(post, "Made up later words");
        FailPartway($"BEFORE UPDATE OF body ON items WHEN NEW.body LIKE '%{StuckWords}%'");

        for (var read = 0; read < 10 && !_database.GetNote(id)!.Body.Contains("Made up later words"); read++)
        {
            await Record.ExceptionAsync(() => Event(worker, "MESSAGE_CREATE", MessageEvent(post, later)));
        }

        StopFailingPartway();
        Assert.Equal("Pack made up bags\n\nMade up later words", _database.GetNote(id)!.Body);
        Assert.Contains(post.Messages, message => message.Id == stuck.Id);
        Assert.Contains(post.Messages, message => message.Bot && message.Says("send the text again"));
    }

    [Fact]
    public async Task A_message_that_keeps_failing_is_still_left_after_a_few_tries_when_Waypoint_restarts_between_them()
    {
        var id = _database.CreateNote("Made up trip", "Pack made up bags", _now);
        (await Started(lists: null)).StopDiscord();
        var post = PostFor(id);
        var stuck = _discord.UserMessage(post, StuckWords);
        FailPartway($"BEFORE UPDATE OF body ON items WHEN NEW.body LIKE '%{StuckWords}%'");

        // Each try is made by a fresh start, the way a message that stops Waypoint partway is.
        for (var start = 0; start < 10 && !post.Messages.Any(message => message.Bot && message.Says("send the text again")); start++)
        {
            var worker = MakeWorker(lists: null);
            worker.StartDiscord();
            await Record.ExceptionAsync(() => Event(worker, "MESSAGE_CREATE", MessageEvent(post, stuck)));
            worker.StopDiscord();
        }

        StopFailingPartway();
        Assert.Single(post.Messages, message => message.Bot && message.Says("send the text again"));
        Assert.Contains(post.Messages, message => message.Id == stuck.Id);
        Assert.Equal(stuck.Id, _database.FindDiscordThread(post.Id)!.Cursor);
        Assert.Equal(0, _database.DiscordMessageTries(post.Id, stuck.Id));
        Assert.Equal("Pack made up bags", _database.GetNote(id)!.Body);

        // Once left, it isn't tried again, and what's typed after it is added.
        var later = _discord.UserMessage(post, "Made up later words");
        var again = MakeWorker(lists: null);
        again.StartDiscord();
        await Event(again, "MESSAGE_CREATE", MessageEvent(post, later));
        Assert.Equal("Pack made up bags\n\nMade up later words", _database.GetNote(id)!.Body);
    }
}
