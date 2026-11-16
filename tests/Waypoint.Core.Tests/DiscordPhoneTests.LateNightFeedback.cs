using System.Text.Json;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Discord;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Tests;

/// <summary>This didn't help on the late-night check-in, in a row of its own and only for the owner.</summary>
public sealed partial class DiscordPhoneTests
{
    private static string DidntHelpPress(string user) => $$$"""
        {"id":"9","token":"tok","type":3,"channel_id":"alerts","data":{"custom_id":"waypoint:didnt-help:late-night|","component_type":2},"member":{"user":{"id":"{{{user}}}"}},"message":{"id":"m3"}}
        """;

    [Fact]
    public async Task A_late_night_check_in_has_this_didnt_help_in_its_last_row_within_five()
    {
        var worker = LateWorker(new SetIdle());
        _now = LateBedtime.AddMinutes(30);
        worker.CheckAlerts();
        await WaitFor(() => CheckInsSent() == 1);

        var body = _discord.Of(HttpMethod.Post).Single(request => request.Body.Contains("waypoint:act:late-save")).Body;
        using var sent = JsonDocument.Parse(body);
        var rows = sent.RootElement.GetProperty("components").EnumerateArray().ToList();
        Assert.InRange(rows.Count, 2, 5);
        Assert.Contains("waypoint:didnt-help:late-night|", rows[^1].ToString());
        Assert.Contains(LateCheckIns.NotTonight, body);

        // Redrawn after Done, it keeps the row.
        var redrawn = await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + WaypointWorker.LateWindDownStepDone + LateNight, null, "m1", "alerts"));
        if (redrawn?.Card is { } card)
        {
            Assert.Equal(Feedback.LateNight, card.FeedbackFeature);
        }
    }

    [Fact]
    public async Task This_didnt_help_on_a_late_night_check_in_is_kept_only_for_the_owner()
    {
        var worker = LateWorker(new SetIdle());

        await worker.Discord!.HandleInteractionAsync(Json(DidntHelpPress("stranger")), (_, _) => null);
        Assert.Empty(_database.GetFeedback());

        await worker.Discord.HandleInteractionAsync(Json(DidntHelpPress("owner")), (_, _) => null);
        var kept = Assert.Single(_database.GetFeedback());
        Assert.Equal((Feedback.LateNight, (string?)null), (kept.Feature, kept.ItemRef));
        Assert.Equal("Late-night check-in", Feedback.Label(kept.Feature));
    }
}
