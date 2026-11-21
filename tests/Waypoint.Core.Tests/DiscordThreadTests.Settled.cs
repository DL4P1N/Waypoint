using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>
/// Once a round finds every note post as it should be, the next rounds aren't made until something they show changes
/// or a notice is waiting to be cleared away.
/// </summary>
public sealed partial class DiscordThreadTests
{
    [Fact]
    public async Task A_round_after_one_that_found_every_post_as_it_should_be_is_skipped_until_a_note_changes()
    {
        var id = _database.CreateNote("Made up cells", "The made up nucleus", _now);
        var worker = await Started(lists: null);
        Assert.True(worker.NotePostsSettled);

        _now = _now.AddMinutes(1);
        await worker.SyncDiscordThreadsAsync();
        Assert.True(worker.NotePostsSettled);
        Assert.DoesNotContain(_discord.All, call => call.Method != HttpMethod.Get);

        // A source added on the PC changes the count under the note.
        _database.AddNoteSource(id, "pdf", "slides.pdf", "Made up slide text", _now);
        await worker.SyncDiscordThreadsAsync();
        Assert.Contains(_discord.All, call => call.Method == HttpMethod.Patch);
        Assert.Contains("Sources: 1 file", PostFor(id).Shown);

        // The note edited on the PC is still sent, and then it's settled again.
        _discord.Clear();
        _database.SaveNote(id, "Made up cells", "The made up nucleus, edited", _now);
        await worker.SyncDiscordThreadsAsync();
        Assert.Contains("edited", Descriptions(PostFor(id))[0]);
        await worker.SyncDiscordThreadsAsync();
        Assert.True(worker.NotePostsSettled);
    }

    [Fact]
    public async Task A_notice_in_a_note_post_is_still_cleared_away_after_rounds_were_skipped()
    {
        var id = _database.CreateNote("Made up cells", "The made up nucleus", _now);
        var worker = await Started(lists: null);
        var thread = _database.GetDiscordThread(WaypointDatabase.ThreadNote, id.ToString())!.Thread;
        _database.AddDiscordNotice(thread, "900000000000000999", _now);

        _now = _now.AddMinutes(61);
        await worker.SyncDiscordThreadsAsync();

        Assert.Empty(_database.GetDiscordNotices(thread));
    }
}
