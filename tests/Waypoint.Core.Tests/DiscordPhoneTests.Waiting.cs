using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Tests;

/// <summary>A waiting-on nudge on Discord: Follow up, Still waiting, and Done, all answered locally on the card.</summary>
public sealed partial class DiscordPhoneTests
{
    private (WaitingItem Waiting, DueReminder Nudge) DueWait()
    {
        _database.AddWaitingByHand("Prof. Quill", "the extension", Today.AddDays(1), null, _now);
        var waiting = _database.GetWaiting().Single();
        var nudge = _database.GetDueReminders(Today.AddDays(1).ToDateTime(WaitingOn.NudgeTime)).Single();
        return (waiting, nudge);
    }

    private static string Id(CardAction action) => DiscordBridge.ActionPrefix + action.Id;

    [Fact]
    public void A_waiting_nudge_offers_follow_up_still_waiting_and_done_instead_of_start()
    {
        var (waiting, nudge) = DueWait();
        var plain = AddTask("Read chapter 4", Today);

        Assert.Equal(waiting.ItemId, _database.WaitingFor(nudge)?.ItemId);
        Assert.Null(_database.WaitingFor(new DueReminder(99, plain, null, null)));

        var card = WaypointWorker.WaitingNudgeCard(nudge, waiting, Today);
        Assert.False(card.Transition);
        Assert.False(card.HasStart);
        Assert.False(card.HasButtons);
        Assert.Equal([CardLabels.FollowUp, CardLabels.StillWaiting, "Done"], card.Actions.Select(action => action.Label));
    }

    [Fact]
    public async Task Waiting_nudge_buttons_draft_a_follow_up_move_the_check_in_and_finish_it()
    {
        var (waiting, nudge) = DueWait();
        var worker = Worker(toDo: null, lists: null);
        var card = WaypointWorker.WaitingNudgeCard(nudge, waiting, Today);
        CardAction Button(string label) => card.Actions.Single(action => action.Label == label);

        var drafted = await worker.OnButtonActedAsync(new ButtonPress(Id(Button(CardLabels.FollowUp)), null, "m1", "c"));
        Assert.Contains("to copy into an email", drafted!.Card!.Title + drafted.Card.Description);
        Assert.Contains("Hi Prof. Quill,", drafted.Card.Description);
        Assert.Contains(drafted.Card.Actions, action => action.Label == CardLabels.StillWaiting);

        var still = await worker.OnButtonActedAsync(new ButtonPress(Id(Button(CardLabels.StillWaiting)), null, "m1", "c"));
        Assert.StartsWith("Still waiting on Prof. Quill.", still!.Outcome);
        Assert.True(_database.GetWaiting().Single().FollowUpOn > waiting.FollowUpOn);

        var done = await worker.OnButtonActedAsync(new ButtonPress(Id(Button("Done")), null, "m1", "c"));
        Assert.Equal($"Finished {waiting.Title}", done!.Card!.Title);
        Assert.Empty(_database.GetWaiting());
    }

    [Fact]
    public async Task A_waiting_nudge_goes_to_Discord_with_its_own_buttons()
    {
        DueWait();
        var worker = Worker(toDo: null, lists: null);
        _now = Today.AddDays(1).ToDateTime(WaitingOn.NudgeTime);

        worker.CheckAlerts();
        await Waits.Until(() => _discord.All.Any(request => request.Body.Contains("\"label\":\"Still waiting\"")), every: 20);

        var sent = _discord.All.Single(request => request.Body.Contains("\"label\":\"Still waiting\"")).Body;
        Assert.Contains("\"label\":\"Follow up\"", sent);
        Assert.DoesNotContain("waypoint:start", sent);
        Assert.DoesNotContain(CardLabels.MoreTime, sent);
    }
}
