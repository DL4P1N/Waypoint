namespace Waypoint.Core.Tests;

/// <summary>
/// A file added to a note from a post just before Waypoint stops, before it's remembered as added, is found on the
/// note on the next try and remembered then, instead of being added a second time. No network.
/// </summary>
public sealed partial class DiscordThreadTests
{
    [Fact]
    public async Task A_file_added_from_a_message_just_before_a_crash_is_not_added_again_on_the_next_try()
    {
        var id = _database.CreateNote("Made up trip", "Pack made up bags", _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);
        var typed = _discord.UserMessage(post, "Made up map notes", files: ["made-up-map.pdf"]);

        // The file joins the note, then Waypoint stops before it's remembered as added.
        FailPartway("BEFORE INSERT ON discord_attached_files");
        await Record.ExceptionAsync(() => Event(worker, "MESSAGE_CREATE", MessageEvent(post, typed)));
        StopFailingPartway();
        worker.StopDiscord();
        Assert.Single(_database.GetNoteSources(id));

        var again = MakeWorker(lists: null);
        again.StartDiscord();
        await Event(again, "MESSAGE_CREATE", MessageEvent(post, typed));

        var source = Assert.Single(_database.GetNoteSources(id));
        Assert.Equal("made-up-map.pdf", source.Name);
        Assert.True(_database.WasDiscordFileAttached("a" + typed.Id));
        Assert.Equal("Pack made up bags\n\nMade up map notes", _database.GetNote(id)!.Body);
        Assert.DoesNotContain(post.Messages, message => message.Id == typed.Id);
    }

    [Fact]
    public async Task A_file_the_note_already_had_is_still_added_from_a_message_read_for_the_first_time()
    {
        var id = _database.CreateNote("Made up trip", "Pack made up bags", _now);
        _database.AddNoteSource(id, "pdf", "made-up-map.pdf", "Cells have a nucleus and a membrane.", _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);

        // Sent again on purpose, so it's added again, the same as dropping it on the note twice on the PC.
        var typed = _discord.UserMessage(post, "", files: ["made-up-map.pdf"]);
        await Event(worker, "MESSAGE_CREATE", MessageEvent(post, typed));

        Assert.Equal(2, _database.GetNoteSources(id).Count);
    }
}
