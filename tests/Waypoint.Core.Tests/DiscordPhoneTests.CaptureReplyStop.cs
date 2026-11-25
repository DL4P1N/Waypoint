using Waypoint.Core.Capture;

namespace Waypoint.Core.Tests;

/// <summary>Stopping Discord ends the pauses between tries at a capture's answer at once.</summary>
public sealed partial class DiscordPhoneTests
{
    [Fact]
    public async Task Stopping_Discord_ends_the_wait_between_tries_at_a_captures_answer_at_once()
    {
        var worker = Worker(toDo: null, lists: null);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A pause that would last far longer than the test, the way the real minutes between tries do.
        worker.RetryWait = _ =>
        {
            paused.TrySetResult();
            return Task.Delay(TimeSpan.FromMinutes(10));
        };
        _discord.FailWith = "made up outage";

        var answering = worker.ReplyInDiscordAsync(new CaptureOutcome([], [], "Made up answer", Origin: CaptureOrigin.Discord) { CaptureId = 42 }, "Made up answer");
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(30));
        worker.StopDiscord();

        await answering.WaitAsync(Waits.Hung);
        Assert.Single(_discord.Of(HttpMethod.Post), post => post.Body.Contains("Made up answer"));
    }
}
