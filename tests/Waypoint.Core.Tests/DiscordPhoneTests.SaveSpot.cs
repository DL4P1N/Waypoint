using System.Text.Json;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Tests;

/// <summary>Save my spot from a Discord card: a button opens a short form, and sending it saves the spot locally.</summary>
public sealed partial class DiscordPhoneTests
{
    private const string SaveSpotPress = """
        {"id":"9","token":"tok","type":3,"channel_id":"alerts","data":{"custom_id":"waypoint:save-spot","component_type":2},"member":{"user":{"id":"owner"}},"message":{"id":"m3"}}
        """;

    private const string SpotFormSent = """
        {"id":"9","token":"tok","type":5,"channel_id":"alerts","data":{"custom_id":"waypoint:spot-form","components":[
          {"type":1,"components":[{"type":4,"custom_id":"project","value":"Waypoint"}]},
          {"type":1,"components":[{"type":4,"custom_id":"stopped","value":"the card buttons"}]},
          {"type":1,"components":[{"type":4,"custom_id":"next","value":"wire up the form"}]}]},
         "member":{"user":{"id":"owner"}},"message":{"id":"m3"}}
        """;

    [Fact]
    public async Task A_card_can_carry_a_save_my_spot_button()
    {
        var bridge = Worker(toDo: null, lists: null).Discord!;

        await bridge.SendAlertAsync(new DiscordCard("Write the essay", null, [], CardTone.Reminder, HasStart: true, Transition: true, MoreTime: true) { SaveSpot = true });

        var sent = _discord.Of(HttpMethod.Post).Last().Body;
        Assert.Contains("\"label\":\"Save my spot\"", sent);
        Assert.Contains("waypoint:save-spot", sent);
    }

    [Fact]
    public async Task Save_my_spot_opens_a_short_form_and_sending_it_saves_the_spot()
    {
        var worker = Worker(toDo: null, lists: null);

        await worker.Discord!.HandleInteractionAsync(Json(SaveSpotPress), (_, _) => null);
        var form = _discord.All.Single(request => request.Url.Contains("/callback")).Body;
        Assert.Contains("\"type\":9", form);
        Assert.Contains("waypoint:spot-form", form);
        Assert.Contains("\"custom_id\":\"stopped\"", form);
        Assert.Contains("\"custom_id\":\"next\"", form);

        await worker.Discord.HandleInteractionAsync(Json(SpotFormSent), (_, _) => null);
        var saved = _discord.All.Where(request => request.Url.Contains("/callback")).Last().Body;
        Assert.Contains("Saved your spot on Waypoint", saved);
        Assert.Contains("\"flags\":64", saved);
        Assert.Contains("Next: wire up the form", _database.DescribeHandoff("Waypoint"));
    }

    [Fact]
    public async Task A_startable_reminder_on_Discord_offers_save_my_spot_until_one_is_saved()
    {
        // Tomorrow afternoon by the real clock, since reminders already past when an item is saved are dropped then.
        _now = DateTime.Today.AddDays(1).AddHours(15);
        _database.CompleteCapture(_database.AddCapture("essay"), [new SortedItem(ItemKind.Task, "Write the essay", DateOnly.FromDateTime(_now), null, Reminders: [new PlannedReminder(_now.AddMinutes(-1), null)])]);
        var worker = Worker(toDo: null, lists: null);

        worker.CheckAlerts();
        for (var tries = 0; tries < 100 && !_discord.All.Any(request => request.Body.Contains("waypoint:start")); tries++)
        {
            await Task.Delay(20);
        }

        Assert.Contains("waypoint:save-spot", _discord.All.Single(request => request.Body.Contains("waypoint:start")).Body);
    }

    [Fact]
    public void The_save_your_spot_bedtime_stages_offer_the_button_and_say_so()
    {
        var plan = new BedtimePlan(Today.ToDateTime(new TimeOnly(23, 0)), Today.AddDays(1).ToDateTime(new TimeOnly(7, 0)), null);

        Assert.True(BedtimeRoutine.OffersSaveSpot(new BedtimeReminder(BedtimeTask.SaveSpot, plan), spotSaved: false));
        Assert.True(BedtimeRoutine.OffersSaveSpot(new BedtimeReminder(BedtimeTask.WrapUp, plan), spotSaved: false));
        Assert.False(BedtimeRoutine.OffersSaveSpot(new BedtimeReminder(BedtimeTask.WrapUp, plan), spotSaved: true));
        Assert.False(BedtimeRoutine.OffersSaveSpot(new BedtimeReminder(BedtimeTask.EveningStep, plan), spotSaved: false));
        Assert.DoesNotContain("Type", BedtimeRoutine.Describe(new BedtimeReminder(BedtimeTask.SaveSpot, plan)).Text);
    }
}
