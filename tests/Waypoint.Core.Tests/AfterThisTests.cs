using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>"I'll do it after this": a reminder that went unanswered is held once, and comes back once at a natural point.</summary>
public sealed class AfterThisTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 9, 14, 0, 0);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public AfterThisTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private StoredItem Add(string title, ItemKind kind = ItemKind.Task) =>
        _database.AddByHand(new HandAddedItem(kind, title, DateOnly.FromDateTime(Now)), Now).Item!;

    [Fact]
    public void A_held_item_comes_back_once_and_then_is_gone()
    {
        var essay = Add("Write the essay");
        Assert.True(_database.HoldAfterThis(essay, Now));

        Assert.Equal("Write the essay", _database.TakeAfterThis(Now.AddMinutes(5))?.Title);
        Assert.Null(_database.TakeAfterThis(Now.AddMinutes(6)));
    }

    [Fact]
    public void Only_the_newest_hold_is_kept_so_nothing_older_trickles_back()
    {
        _database.HoldAfterThis(Add("Older"), Now);
        _database.HoldAfterThis(Add("Newer"), Now.AddMinutes(10));

        Assert.Equal("Newer", _database.TakeAfterThis(Now.AddMinutes(20))?.Title);
        Assert.Null(_database.TakeAfterThis(Now.AddMinutes(21)));
    }

    [Fact]
    public void An_older_unanswered_card_never_pushes_out_a_newer_hold()
    {
        var older = Add("Older");
        _database.HoldAfterThis(Add("Newer"), Now.AddMinutes(10));
        _database.TrackSentReminder("m1", older.Id, startOnly: true, sentAt: Now);

        _database.HoldUnansweredReminders(Now.AddMinutes(40));

        Assert.Equal("Newer", _database.TakeAfterThis(Now.AddMinutes(41))?.Title);
        Assert.Null(_database.TakeAfterThis(Now.AddMinutes(42)));
    }

    [Fact]
    public void A_newest_hold_that_was_finished_is_dropped_without_falling_back_to_an_older_one()
    {
        var done = Add("Already done");
        _database.HoldAfterThis(Add("Older"), Now);
        _database.HoldAfterThis(done, Now.AddMinutes(1));
        _database.SetDone(done, true);

        Assert.Null(_database.TakeAfterThis(Now.AddMinutes(2)));
        Assert.Null(_database.TakeAfterThis(Now.AddMinutes(3)));
    }

    [Fact]
    public void Holds_older_than_a_day_are_dropped_without_being_shown()
    {
        _database.HoldAfterThis(Add("Long ago"), Now);

        Assert.Null(_database.TakeAfterThis(Now.Add(AfterThis.Kept).AddMinutes(1)));
        Assert.Null(_database.TakeAfterThis(Now));
    }

    [Fact]
    public void A_finished_item_or_one_being_worked_on_is_not_brought_back()
    {
        var done = Add("Already done");
        var working = Add("Working on it");
        _database.HoldAfterThis(done, Now);
        _database.HoldAfterThis(working, Now.AddMinutes(1));
        _database.SetDone(done, true);
        _database.StartActivation(ItemRef.Format(working), "Open it", null, Activation.TypedMethod, Now.AddMinutes(2));

        Assert.Null(_database.TakeAfterThis(Now.AddMinutes(3)));
    }

    [Fact]
    public void Only_things_that_can_be_started_are_held()
    {
        var note = _database.AddByHand(new HandAddedItem(ItemKind.Event, "Club meeting", DateOnly.FromDateTime(Now)), Now).Item!;

        Assert.False(_database.HoldAfterThis(note, Now));
        Assert.Null(_database.TakeAfterThis(Now));
    }

    [Fact]
    public void Holding_the_same_item_again_keeps_one_hold()
    {
        var essay = Add("Write the essay");
        _database.HoldAfterThis(essay, Now);
        _database.HoldAfterThis(essay, Now.AddMinutes(5));

        Assert.NotNull(_database.TakeAfterThis(Now.AddMinutes(6)));
        Assert.Null(_database.TakeAfterThis(Now.AddMinutes(7)));
    }

    [Fact]
    public void A_Discord_reminder_with_no_answer_for_half_an_hour_is_held()
    {
        var essay = Add("Write the essay");
        _database.TrackSentReminder("m1", essay.Id, startOnly: true, sentAt: Now);

        Assert.Equal(0, _database.HoldUnansweredReminders(Now.AddMinutes(29)));
        Assert.Equal(1, _database.HoldUnansweredReminders(Now.Add(AfterThis.Unanswered)));
        Assert.Equal(0, _database.HoldUnansweredReminders(Now.AddMinutes(40)));

        Assert.Equal("Write the essay", _database.TakeAfterThis(Now.AddMinutes(45))?.Title);
    }

    [Fact]
    public void Answering_the_card_after_all_drops_its_hold()
    {
        var essay = Add("Write the essay");
        _database.TrackSentReminder("m1", essay.Id, startOnly: true, sentAt: Now);
        _database.HoldUnansweredReminders(Now.AddMinutes(31));

        Assert.NotNull(_database.TakeSentReminderItem("m1"));
        Assert.Null(_database.TakeAfterThis(Now.AddMinutes(40)));
    }

    [Fact]
    public void A_card_answered_in_time_is_never_held()
    {
        var essay = Add("Write the essay");
        _database.TrackSentReminder("m1", essay.Id, startOnly: true, sentAt: Now);
        _database.ApplyTransitionAction("m1", Discord.AlertActionKind.NotNowReminder, Now.AddMinutes(5));

        Assert.Equal(0, _database.HoldUnansweredReminders(Now.AddHours(1)));
        Assert.Null(_database.TakeAfterThis(Now.AddHours(1)));
    }

    [Fact]
    public void Cards_sent_before_holds_existed_or_too_long_ago_are_passed_over()
    {
        var essay = Add("Write the essay");
        _database.TrackSentReminder("old", essay.Id, startOnly: true);
        _database.TrackSentReminder("stale", essay.Id, startOnly: true, sentAt: Now.AddDays(-3));

        Assert.Equal(0, _database.HoldUnansweredReminders(Now));
        Assert.Null(_database.TakeAfterThis(Now));
    }

    [Fact]
    public void The_line_names_the_item_plainly()
    {
        Assert.Equal("Earlier you meant to: Write the essay", AfterThis.Line(Add("Write the essay")));
    }
}
