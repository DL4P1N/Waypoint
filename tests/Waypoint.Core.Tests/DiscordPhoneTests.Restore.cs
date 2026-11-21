using Waypoint.Core.Background;

namespace Waypoint.Core.Tests;

/// <summary>
/// A restore waits for a notice or reminder card still going out, or is turned down while one is, so neither can
/// finish against the restored data. Discord is down, and a held pause between its tries keeps the send going.
/// </summary>
public sealed partial class DiscordPhoneTests
{
    [Fact]
    public async Task A_restore_waits_for_a_notice_still_going_out_or_is_turned_down()
    {
        var worker = Worker(toDo: null, lists: null);
        var (paused, resume) = HoldRetries(worker);
        _discord.FailWith = "made up outage";
        _ = worker.NotifyAsync("Made up delivery window", "Between noon and two");
        await paused;

        await AssertRestoreWaitsFor(worker, resume);

        Assert.Single(CardsAbout("Made up delivery window"));
        await AssertNothingNewStarts(worker);
    }

    [Fact]
    public async Task A_restore_waits_for_a_reminder_card_still_going_out_or_is_turned_down()
    {
        DueReminderFor("Made up bike tune up");
        var worker = Worker(toDo: null, lists: null);
        var (paused, resume) = HoldRetries(worker);
        _discord.FailWith = "made up outage";
        worker.CheckAlerts();
        await paused;

        await AssertRestoreWaitsFor(worker, resume);

        Assert.Single(CardsAbout("Made up bike tune up"));
        Assert.Equal(1, Rows("sent_reminders"));
        await AssertNothingNewStarts(worker);
    }

    /// <summary>The first pause between tries is held until resumed, and the ones after it aren't.</summary>
    private static (Task Paused, Action Resume) HoldRetries(WaypointWorker worker)
    {
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.RetryWait = _ =>
        {
            paused.TrySetResult();
            return resume.Task;
        };
        return (paused.Task.WaitAsync(TimeSpan.FromMinutes(1)), () => resume.TrySetResult());
    }

    /// <summary>
    /// With too little time, the restore is turned down. With enough, it waits while the send is held, and goes
    /// ahead once the send has ended, with Discord answering again.
    /// </summary>
    private async Task AssertRestoreWaitsFor(WaypointWorker worker, Action resume)
    {
        Assert.False(await ExclusiveRestore.StopWritersAsync([limit => worker.StopWorkAsync(limit)], TimeSpan.FromMilliseconds(200)));

        var stopping = ExclusiveRestore.StopWritersAsync([limit => worker.StopWorkAsync(limit)], TimeSpan.FromMinutes(1));
        await Task.Delay(200);
        Assert.False(stopping.IsCompleted);

        _discord.FailWith = null;
        resume();
        Assert.True(await stopping);
    }

    /// <summary>Once stopped, a new notice isn't sent anywhere.</summary>
    private async Task AssertNothingNewStarts(WaypointWorker worker)
    {
        var posts = _discord.Of(HttpMethod.Post).Count();
        await worker.NotifyAsync("Made up later notice", "Not sent");

        Assert.Equal(posts, _discord.Of(HttpMethod.Post).Count());
        Assert.DoesNotContain("Made up later notice", _pc.ShownCopy());
    }
}
