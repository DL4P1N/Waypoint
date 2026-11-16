using Waypoint.Core.Capture;

namespace Waypoint.Core.Tests;

/// <summary>Make it smaller on Discord says it's working on the card at once, then the card turns into the step.</summary>
public sealed partial class DiscordPhoneTests
{
    [Fact]
    public async Task Make_it_smaller_on_Discord_shows_it_is_working_then_the_smaller_step()
    {
        var paper = AddTask("Paper", Today);
        _database.StartActivation(ItemRef.Format(paper), "Open the doc", null, Activation.TypedMethod, _now);
        var worker = Worker(toDo: null, lists: null);

        await worker.Discord!.HandleInteractionAsync(Json("""
            {"id":"9","token":"tok","application_id":"app","type":3,"channel_id":"alerts","data":{"custom_id":"waypoint:smaller","component_type":2},
             "member":{"user":{"id":"owner"}},"message":{"id":"m3","embeds":[{"title":"Paper"}]}}
            """), (_, _) => null);

        var first = _discord.All.First(request => request.Url.Contains("/callback")).Body;
        Assert.Contains("\"type\":7", first);
        Assert.Contains(Activation.SmallerWorking, first);

        var edited = _discord.All.Last(request => request.Url.Contains("/messages/@original"));
        Assert.Equal(HttpMethod.Patch, edited.Method);
        Assert.Contains(Activation.Fallback(0), edited.Body);
    }
}
