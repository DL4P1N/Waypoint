using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>
/// A notice left in a post that's retired, replaced or gone is never cleared away, so it's forgotten and never keeps
/// every round reading every note. No network.
/// </summary>
public sealed partial class DiscordThreadTests
{
    [Fact]
    public async Task A_notice_in_the_post_of_a_deleted_note_is_forgotten_and_the_rounds_settle_again()
    {
        var kept = _database.CreateNote("Made up kept note", "The made up kept words", _now);
        var deleted = _database.CreateNote("Made up deleted note", "The made up deleted words", _now);
        var worker = await Started(lists: null);
        var thread = _database.GetDiscordThread(WaypointDatabase.ThreadNote, deleted.ToString())!.Thread;
        _database.AddDiscordNotice(thread, "900000000000000999", _now);

        _database.DeleteNote(deleted, _now);
        await worker.SyncDiscordThreadsAsync();
        await worker.SyncDiscordThreadsAsync();

        Assert.False(_database.FindDiscordThread(thread)!.Live);
        Assert.Empty(_database.GetDiscordNotices(thread));
        Assert.False(_database.HasDiscordNotices("notice:"));
        Assert.True(worker.NotePostsSettled);
        Assert.NotNull(_database.GetDiscordThread(WaypointDatabase.ThreadNote, kept.ToString()));
    }

    [Fact]
    public async Task A_notice_in_a_post_that_is_gone_no_longer_counts_as_one_to_clear_away()
    {
        var id = _database.CreateNote("Made up cells", "The made up nucleus", _now);
        await Started(lists: null);
        var thread = _database.GetDiscordThread(WaypointDatabase.ThreadNote, id.ToString())!.Thread;
        _database.AddDiscordNotice(thread, "900000000000000998", _now);
        Assert.True(_database.HasDiscordNotices("notice:"));

        _database.MarkDiscordThreadGone(thread);

        Assert.False(_database.HasDiscordNotices("notice:"));
        Assert.Equal(1, _database.ForgetDiscordNoticesOfEndedPosts());
        Assert.Empty(_database.GetDiscordNotices(thread));
    }
}
