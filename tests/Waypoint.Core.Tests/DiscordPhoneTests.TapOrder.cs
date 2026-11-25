using Waypoint.Core.Capture;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Tests;

/// <summary>Taps on different cards take effect in the order they were made, even while an earlier one waits on a slow sort.</summary>
public sealed partial class DiscordPhoneTests
{
    /// <summary>Holds one made up capture's sort until told, the way a slow sort does.</summary>
    private sealed class HeldSorter(Task release) : ICaptureSorter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default)
        {
            if (text == "made up slow thing")
            {
                Entered.TrySetResult();
                await release;
            }

            return null;
        }
    }

    [Fact]
    public async Task Did_it_on_one_card_then_Start_on_another_takes_effect_in_that_order_while_the_sort_is_slow()
    {
        var paper = AddTask("Made up paper", Today);
        var essay = AddTask("Made up essay", Today);
        _database.StartActivation(ItemRef.Format(paper), "Open the doc", null, Activation.TypedMethod, _now);
        _database.TrackSentReminder("card-essay", essay.Id);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sorter = new HeldSorter(release.Task);
        var worker = Worker(toDo: null, lists: null, sorter: sorter);
        var bridge = worker.Discord!;
        bridge.DeferAfter = TimeSpan.FromMilliseconds(100);
        var gateway = new DiscordGateway("token", bridge.HandleEventAsync, arrived: bridge.EventArrived);

        // Something typed earlier is being sorted slowly, so Did it waits behind it.
        worker.Captures.Capture("made up slow thing", null, CaptureOrigin.Discord);
        _ = worker.Captures.ProcessPendingAsync();
        await sorter.Entered.Task.WaitAsync(TimeSpan.FromMinutes(2));

        gateway.Deliver("INTERACTION_CREATE", Json("""
            {"id":"1","token":"tok1","application_id":"app","type":3,"channel_id":"alerts","data":{"custom_id":"waypoint:did-it","component_type":2},
             "member":{"user":{"id":"owner"}},"message":{"id":"m-session","embeds":[{"title":"Made up paper"}]}}
            """));
        gateway.Deliver("INTERACTION_CREATE", Json("""
            {"id":"2","token":"tok2","application_id":"app","type":3,"channel_id":"alerts","data":{"custom_id":"waypoint:start","component_type":2},
             "member":{"user":{"id":"owner"}},"message":{"id":"card-essay","embeds":[{"title":"Made up essay"}]}}
            """));

        // Both taps are promised while the sort is still held, and Start waits for Did it. Each promise has its own
        // timer, so on a busy machine either can be made first.
        await _discord.Made(request => request.Url.Contains("/interactions/2/tok2/callback"));
        await _discord.Made(request => request.Url.Contains("/interactions/1/tok1/callback"));
        Assert.Equal("Made up paper", _database.GetActiveSession(_now)?.TaskTitle);

        release.TrySetResult();
        await WaitFor(() => _database.GetActiveSession(_now)?.TaskTitle == "Made up essay");
        Assert.Contains(_database.GetOpenItems(), item => item.Id == essay.Id);
    }
}
