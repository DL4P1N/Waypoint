using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>A hold for "after this" goes as soon as the item is dealt with some other way, so it never comes back for nothing.</summary>
public sealed class AfterThisDealtWithTests : IDisposable
{
    private static readonly DateTime TenAm = new(2026, 11, 9, 10, 0, 0);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public AfterThisDealtWithTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private StoredItem Add(string title) =>
        _database.AddByHand(new HandAddedItem(ItemKind.Task, title, DateOnly.FromDateTime(TenAm)), TenAm).Item!;

    private StoredItem Held(string title)
    {
        var item = Add(title);
        _database.TrackSentReminder("m-" + title, item.Id, startOnly: true, sentAt: TenAm);
        Assert.Equal(1, _database.HoldUnansweredReminders(TenAm.AddMinutes(30)));
        return item;
    }

    private void FinishSession(DateTime at)
    {
        _database.CancelSession(at);
        Assert.Null(_database.GetActiveSession(at));
    }

    [Fact]
    public void Started_elsewhere_before_the_half_hour_then_did_it_does_not_bring_it_back()
    {
        var essay = Add("Write the essay");
        _database.TrackSentReminder("m1", essay.Id, startOnly: true, sentAt: TenAm);

        // Started from To do or /next instead of the card, then finished with Did it.
        _database.StartActivation(ItemRef.Format(essay), Activation.TrialOffer, null, Activation.TypedMethod, TenAm.AddMinutes(5));
        Assert.Equal(0, _database.HoldUnansweredReminders(TenAm.AddMinutes(30)));
        FinishSession(TenAm.AddMinutes(45));

        Assert.Null(_database.TakeAfterThis(TenAm.AddMinutes(45)));
    }

    [Fact]
    public void Started_elsewhere_after_it_was_held_drops_the_hold()
    {
        var essay = Held("Write the essay");

        _database.StartActivation(ItemRef.Format(essay), "Open it", null, Activation.TypedMethod, TenAm.AddMinutes(35));
        FinishSession(TenAm.AddMinutes(45));

        Assert.Null(_database.TakeAfterThis(TenAm.AddMinutes(45)));
    }

    [Fact]
    public void A_session_touched_since_the_card_went_out_counts_even_if_the_hold_was_made_another_way()
    {
        var essay = Add("Write the essay");
        _database.StartActivation(ItemRef.Format(essay), "Open it", null, Activation.TypedMethod, TenAm.AddMinutes(5));
        FinishSession(TenAm.AddMinutes(10));

        // A PC card shown at ten, held when its half hour ran out.
        _database.HoldAfterThis(essay, TenAm);

        Assert.Null(_database.TakeAfterThis(TenAm.AddMinutes(40)));
    }

    [Fact]
    public void Finishing_it_drops_the_hold()
    {
        var essay = Held("Write the essay");
        _database.SetDone(essay, true);
        _database.SetDone(essay, false);

        Assert.Null(_database.TakeAfterThis(TenAm.AddMinutes(40)));
    }

    [Fact]
    public void Rescheduling_it_drops_the_hold()
    {
        var essay = Held("Write the essay");
        _database.RescheduleItem(essay, DateOnly.FromDateTime(TenAm).AddDays(2), null, TenAm.AddMinutes(35));

        Assert.Null(_database.TakeAfterThis(TenAm.AddMinutes(40)));
    }

    [Fact]
    public void Deleting_it_drops_the_hold()
    {
        var essay = Held("Write the essay");
        var deleted = _database.DeleteItem(essay, TenAm.AddMinutes(35))!;
        _database.UndoDelete(deleted, TenAm.AddMinutes(36));

        Assert.Null(_database.TakeAfterThis(TenAm.AddMinutes(40)));
    }

    [Fact]
    public void Snoozing_its_reminder_drops_the_hold()
    {
        var essay = Held("Write the essay");
        _database.SnoozeReminder(new DueReminder(1, essay, null, null), TenAm.AddHours(2));

        Assert.Null(_database.TakeAfterThis(TenAm.AddMinutes(40)));
    }

    [Fact]
    public void This_didnt_help_on_its_reminder_drops_the_hold_and_stops_its_cards_holding_it()
    {
        var essay = Add("Write the essay");
        _database.TrackSentReminder("m1", essay.Id, startOnly: true, sentAt: TenAm);

        _database.DropAfterThis(ItemRef.Format(essay));

        Assert.Equal(0, _database.HoldUnansweredReminders(TenAm.AddMinutes(40)));
        Assert.Null(_database.TakeAfterThis(TenAm.AddMinutes(40)));
    }

    [Fact]
    public void An_untouched_item_still_comes_back()
    {
        Held("Write the essay");

        Assert.Equal("Write the essay", _database.TakeAfterThis(TenAm.AddMinutes(40))?.Title);
    }
}
