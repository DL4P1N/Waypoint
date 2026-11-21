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
        Assert.Contains(post.Messages, message => message.Bot && message.Says("couldn't be added after several tries"));
    }
}
