using System.Collections.Concurrent;
using Waypoint.Core.Background;

namespace Waypoint.Core.Tests;

/// <summary>The one-at-a-time queue behind Discord's events and the server's background work.</summary>
public sealed class OrderedWorkTests
{
    /// <summary>
    /// Only so a hung test fails rather than hangs. Each test waits for its own work by a signal, so a slow machine
    /// only makes it later, never wrong.
    /// </summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private readonly ConcurrentQueue<Exception> _failures = new();
    private readonly ConcurrentQueue<string> _warnings = new();

    private OrderedWork Make(int longQueue = OrderedWork.DefaultLongQueue) =>
        new(_failures.Enqueue, _warnings.Enqueue, longQueue);

    [Fact]
    public async Task Work_runs_in_the_order_it_was_handed_in()
    {
        await using var work = Make();
        var order = new ConcurrentQueue<int>();
        var last = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        for (var index = 0; index < 50; index++)
        {
            var number = index;
            Assert.True(work.Post(async () =>
            {
                await Task.Delay(number % 3);
                order.Enqueue(number);
            }));
        }

        Assert.True(work.Post(last.SetResult));

        // Everything has run before stopping, so stopping's own limit is never what decides this.
        await last.Task.WaitAsync(Wait);
        Assert.Equal(0, await work.StopAsync(Wait));
        Assert.Equal(Enumerable.Range(0, 50), order);
    }

    [Fact]
    public async Task A_failing_piece_is_reported_and_the_next_still_runs()
    {
        await using var work = Make();
        var ran = new ConcurrentQueue<string>();
        work.Post(() => ran.Enqueue("first"));
        work.Post(() => throw new InvalidOperationException("broken"));
        work.Post(() => Task.FromException(new InvalidOperationException("broken later")));
        work.Post(() => ran.Enqueue("last"));
        var last = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        work.Post(last.SetResult);

        await last.Task.WaitAsync(Wait);
        Assert.Equal(0, await work.StopAsync(Wait));

        Assert.Equal(["first", "last"], ran);
        Assert.Equal(["broken", "broken later"], _failures.Select(failure => failure.Message));
    }

    [Fact]
    public async Task Stopping_finishes_what_is_queued_and_then_refuses_new_work_out_loud()
    {
        var work = Make();
        var gate = new TaskCompletionSource();
        var done = 0;
        work.Post(() => gate.Task);
        for (var index = 0; index < 5; index++)
        {
            work.Post(() => Interlocked.Increment(ref done));
        }

        var stopping = work.StopAsync(Wait);
        Assert.False(work.Post(() => Interlocked.Increment(ref done)));
        gate.SetResult();

        Assert.Equal(0, await stopping);
        Assert.Equal(5, done);
        Assert.False(work.Post(() => Interlocked.Increment(ref done)));
        Assert.Equal(5, done);
        Assert.Equal(2, _warnings.Count(warning => warning.Contains("after it stopped")));

        // Stopping again is harmless.
        Assert.Equal(0, await work.StopAsync(Wait));
        await work.DisposeAsync();
    }

    [Fact]
    public async Task Stopping_gives_up_on_a_hung_piece_in_time_cancels_it_and_says_what_was_left()
    {
        var work = Make();
        var sawCancel = new TaskCompletionSource();
        var later = 0;
        work.Post(async cancellationToken =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                sawCancel.SetResult();
            }
        });
        work.Post(() => Interlocked.Increment(ref later));
        work.Post(() => Interlocked.Increment(ref later));

        // The hung piece only ends once it's cancelled, so stopping coming back at all means it gave up on it.
        var left = await work.StopAsync(TimeSpan.FromMilliseconds(200)).WaitAsync(Wait);

        Assert.Equal(3, left);
        Assert.True(work.Stopping.IsCancellationRequested);
        await sawCancel.Task.WaitAsync(Wait);
        Assert.Contains(_warnings, warning => warning.Contains("3 pieces of work"));

        // What was skipped is never run late.
        await Task.Delay(100);
        Assert.Equal(0, later);
    }

    [Fact]
    public async Task A_long_queue_is_warned_about_once_with_how_long_it_is()
    {
        await using var work = Make(longQueue: 10);
        var gate = new TaskCompletionSource();
        work.Post(() => gate.Task);
        for (var index = 0; index < 30; index++)
        {
            work.Post(() => { });
        }

        var warning = Assert.Single(_warnings);
        Assert.Contains("11 pieces of work", warning);

        gate.SetResult();
        await work.StopAsync(Wait);
        Assert.Single(_warnings);
    }
}
