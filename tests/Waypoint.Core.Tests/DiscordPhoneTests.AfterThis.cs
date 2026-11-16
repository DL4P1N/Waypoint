using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Tests;

/// <summary>
/// "After this" on Discord: an unanswered reminder is held, and comes back once on a card that's already going out.
/// "This didn't help" on Discord: a small button keeps the click, and a form offers one optional line.
/// </summary>
public sealed partial class DiscordPhoneTests
{
    private async Task<string> SentContaining(string text)
    {
        for (var tries = 0; tries < 100; tries++)
        {
            if (_discord.Of(HttpMethod.Post).Select(request => request.Body).LastOrDefault(body => body.Contains(text)) is { } found)
            {
                return found;
            }

            await Task.Delay(20);
        }

        throw new Xunit.Sdk.XunitException($"Nothing sent with {text}");
    }

    [Fact]
    public async Task An_ignored_reminder_comes_back_once_in_the_next_briefing_with_start()
    {
        var essay = AddTask("Write the essay", Today);
        var worker = Worker(toDo: null, lists: null);
        _database.SetPreference("last_briefing", Today.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        _database.TrackSentReminder("m-old", essay.Id, startOnly: true, sentAt: _now.AddMinutes(-40));

        worker.CheckAlerts();
        Assert.Equal(0, _database.HoldUnansweredReminders(_now));

        // The next morning's briefing carries it, once.
        _now = _now.AddHours(22);
        worker.CheckAlerts();
        var briefing = await SentContaining("Earlier you meant to");
        Assert.Contains("Write the essay", briefing);
        Assert.Contains(DiscordBridge.ActionPrefix + WaypointWorker.StartPick + ItemRef.Format(essay), briefing);
        Assert.Contains(DiscordBridge.ActionPrefix + WaypointWorker.AfterThisNotNow, briefing);
        Assert.Null(_database.TakeAfterThis(_now));
    }

    [Fact]
    public async Task Start_and_not_now_on_a_brought_back_item_work_from_the_card()
    {
        var essay = AddTask("Write the essay", Today);
        var worker = Worker(toDo: null, lists: null);

        var started = await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + WaypointWorker.StartPick + ItemRef.Format(essay), null, "m5", "c"));
        Assert.Equal("Write the essay", _database.GetActiveSession(_now)?.TaskTitle);
        Assert.True(started!.Card!.HasSessionButtons);

        var notNow = await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + WaypointWorker.AfterThisNotNow, null, "m6", "c"));
        Assert.Equal("Okay. It's still on your list.", notNow!.Outcome);
    }

    [Fact]
    public async Task Did_it_that_ends_a_session_brings_back_what_was_held_on_the_same_card()
    {
        var essay = AddTask("Write the essay", Today);
        var dishes = AddTask("Do the dishes", Today);
        var worker = Worker(toDo: null, lists: null);
        _database.StartActivation(ItemRef.Format(dishes), Activation.TrialOffer, null, Activation.TypedMethod, _now);
        _database.HoldAfterThis(essay, _now);

        var result = await worker.OnButtonActedAsync(new ButtonPress("waypoint:did-it", AlertActionKind.DidIt, "m7", "c"));

        Assert.Null(_database.GetActiveSession(_now));
        // The same labeled line the briefing and heads-up show.
        Assert.Contains(result!.Card!.Fields, field => field.Name == "Earlier you meant to" && field.Value == "Write the essay");
        Assert.Contains(result.Card.Actions, action => action.Label == "Start");
        Assert.Null(_database.TakeAfterThis(_now));
    }

    [Fact]
    public async Task Discord_reminders_suggestions_and_sessions_carry_a_small_didnt_help_button()
    {
        var bridge = Worker(toDo: null, lists: null).Discord!;

        await bridge.SendAlertAsync(new DiscordCard("Write the essay", null, [], CardTone.Reminder, HasStart: true, Transition: true, MoreTime: true)
        {
            SaveSpot = true,
            FeedbackFeature = Feedback.Reminder,
            FeedbackRef = "item:4",
        });

        var sent = _discord.Of(HttpMethod.Post).Last().Body;
        Assert.Contains("\"label\":\"This didn't help\"", sent.Replace("\\u0027", "'"));
        Assert.Contains("waypoint:didnt-help:reminder|item:4", sent);

        // It sits in a row of its own, after the card's own buttons, so none of them are pushed out.
        Assert.True(sent.IndexOf("waypoint:didnt-help", StringComparison.Ordinal) > sent.IndexOf("waypoint:save-spot", StringComparison.Ordinal));
        Assert.Contains("waypoint:not-now-reminder", sent);
    }

    [Fact]
    public async Task A_card_with_paging_rows_still_keeps_this_didnt_help_within_five_rows()
    {
        var bridge = Worker(toDo: null, lists: null).Discord!;
        CardAction[] Row(string name) => [new CardAction(name, name)];

        await bridge.SendAlertAsync(new DiscordCard("Write the essay", null, [], CardTone.Reminder, HasSessionButtons: true)
        {
            MoreRows = [Row("a"), Row("b"), Row("c"), Row("d"), Row("e")],
            FeedbackFeature = Feedback.Session,
        });

        using var sent = System.Text.Json.JsonDocument.Parse(_discord.Of(HttpMethod.Post).Last().Body);
        var rows = sent.RootElement.GetProperty("components").EnumerateArray().ToList();
        Assert.Equal(5, rows.Count);
        Assert.Contains("waypoint:didnt-help:session|", rows[^1].ToString());
    }

    [Fact]
    public async Task A_reminder_started_from_next_instead_of_its_card_is_not_brought_back_after_did_it()
    {
        var essay = AddTask("Write the essay", Today);
        var dishes = AddTask("Do the dishes", Today);
        var worker = Worker(toDo: null, lists: null);
        _database.TrackSentReminder("m1", essay.Id, startOnly: true, sentAt: _now);

        // Started from /next at 10:05 instead of the card, put down, then a session on something else.
        await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + WaypointWorker.StartPick + ItemRef.Format(essay), null, "m2", "c"));
        _database.CancelSession(_now.AddMinutes(10));
        _now = _now.AddMinutes(30);
        worker.CheckAlerts();
        _database.StartActivation(ItemRef.Format(dishes), Activation.TrialOffer, null, Activation.TypedMethod, _now);
        _now = _now.AddMinutes(15);

        var result = await worker.OnButtonActedAsync(new ButtonPress("waypoint:did-it", AlertActionKind.DidIt, "m7", "c"));

        Assert.DoesNotContain(result!.Card!.Fields, field => field.Name == "Earlier you meant to");
        Assert.Empty(result.Card.Actions);
    }

    [Fact]
    public async Task This_didnt_help_on_a_reminder_card_means_it_is_not_held_later()
    {
        var essay = AddTask("Write the essay", Today);
        var worker = Worker(toDo: null, lists: null);
        _database.TrackSentReminder("m3", essay.Id, startOnly: true, sentAt: _now);

        await worker.Discord!.HandleInteractionAsync(Json($$$"""
            {"id":"9","token":"tok","type":3,"channel_id":"alerts","data":{"custom_id":"waypoint:didnt-help:reminder|{{{ItemRef.Format(essay)}}}","component_type":2},"member":{"user":{"id":"owner"}},"message":{"id":"m3"}}
            """), (_, _) => null);

        Assert.Equal(0, _database.HoldUnansweredReminders(_now.AddMinutes(40)));
        Assert.Null(_database.TakeAfterThis(_now.AddMinutes(40)));
    }

    [Fact]
    public async Task Tonights_heads_up_carries_what_was_held_once()
    {
        // A Tuesday 9:30 class makes a 12:30 AM bedtime, so the heads-up is at 11 PM Monday.
        var classes = new Schedule.ClassSchedule(
            new DateOnly(2026, 9, 14),
            new DateOnly(2026, 12, 18),
            [new Schedule.ClassMeeting("CS 101", "ABC 100", [DayOfWeek.Tuesday], new TimeOnly(9, 30), new TimeOnly(10, 45), 15)],
            []);
        _now = new DateTime(2026, 11, 9, 23, 0, 0);
        var essay = AddTask("Write the essay", Today);
        _database.HoldAfterThis(essay, _now.AddMinutes(-20));
        var worker = Worker(toDo: null, lists: null, classes: classes);

        worker.CheckAlerts();

        var headsUp = await SentContaining("Earlier you meant to");
        Assert.Contains("Choose one last thing to finish", headsUp);
        Assert.Contains(DiscordBridge.ActionPrefix + WaypointWorker.StartPick + ItemRef.Format(essay), headsUp);
        Assert.Null(_database.TakeAfterThis(_now));
    }

    [Fact]
    public async Task Pressing_didnt_help_keeps_the_click_and_the_form_adds_an_optional_line()
    {
        var essay = AddTask("Write the essay", Today);
        var worker = Worker(toDo: null, lists: null);

        await worker.Discord!.HandleInteractionAsync(Json($$$"""
            {"id":"9","token":"tok","type":3,"channel_id":"alerts","data":{"custom_id":"waypoint:didnt-help:reminder|{{{ItemRef.Format(essay)}}}","component_type":2},"member":{"user":{"id":"owner"}},"message":{"id":"m3"}}
            """), (_, _) => null);

        var kept = Assert.Single(_database.GetFeedback());
        Assert.Equal((Feedback.Reminder, ItemRef.Format(essay), "Write the essay", (string?)null), (kept.Feature, kept.ItemRef, kept.ItemTitle, kept.Note));
        var form = _discord.All.Single(request => request.Url.Contains("/callback")).Body;
        Assert.Contains("\"type\":9", form);
        Assert.Contains($"waypoint:didnt-help-note:{kept.Id}", form);
        Assert.Contains("\"required\":false", form);

        await worker.Discord.HandleInteractionAsync(Json($$$"""
            {"id":"9","token":"tok","type":5,"channel_id":"alerts","data":{"custom_id":"waypoint:didnt-help-note:{{{kept.Id}}}","components":[
              {"type":1,"components":[{"type":4,"custom_id":"note","value":"I was in a game"}]}]},
             "member":{"user":{"id":"owner"}},"message":{"id":"m3"}}
            """), (_, _) => null);

        Assert.Equal("I was in a game", Assert.Single(_database.GetFeedback()).Note);
        Assert.Contains("\"type\":6", _discord.All.Where(request => request.Url.Contains("/callback")).Last().Body);
    }

    [Fact]
    public async Task A_slow_didnt_help_press_still_opens_its_form_at_once()
    {
        var worker = Worker(toDo: null, lists: null);
        var bridge = worker.Discord!;
        bridge.DeferAfter = TimeSpan.FromMilliseconds(1);
        var keep = bridge.FeedbackGiven!;
        bridge.FeedbackGiven = (feature, itemRef) =>
        {
            Thread.Sleep(200);
            return keep(feature, itemRef);
        };

        await bridge.HandleInteractionAsync(Json("""
            {"id":"9","token":"tok","type":3,"channel_id":"alerts","data":{"custom_id":"waypoint:didnt-help:session|","component_type":2},"member":{"user":{"id":"owner"}},"message":{"id":"m3"}}
            """), (_, _) => null);

        var callback = Assert.Single(_discord.All, request => request.Url.Contains("/callback"));
        Assert.Contains("\"type\":9", callback.Body);
        Assert.Single(_database.GetFeedback());
    }

    [Fact]
    public async Task Only_the_owner_can_leave_feedback()
    {
        var worker = Worker(toDo: null, lists: null);

        await worker.Discord!.HandleInteractionAsync(Json("""
            {"id":"9","token":"tok","type":3,"channel_id":"alerts","data":{"custom_id":"waypoint:didnt-help:session|","component_type":2},"member":{"user":{"id":"someone"}},"message":{"id":"m3"}}
            """), (_, _) => null);

        Assert.Empty(_database.GetFeedback());
    }
}
