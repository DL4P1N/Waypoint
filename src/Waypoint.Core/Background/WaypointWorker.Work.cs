namespace Waypoint.Core.Background;

/// <summary>
/// Work started without being waited for where it starts, such as a notice going out or the feed being read. Each
/// is kept track of, so closing and a restore can wait for it rather than have it finish against the restored data.
/// </summary>
public sealed partial class WaypointWorker
{
    private readonly object _workGate = new();
    private readonly CancellationTokenSource _workStop = new();
    private int _workRunning;
    private TaskCompletionSource? _workIdle;
    private bool _workStopping;

    /// <summary>
    /// Starts <paramref name="start"/> and keeps track of it until it ends. Once <see cref="StopWorkAsync"/> has been
    /// called, nothing new is started and this gives back <paramref name="refused"/> instead.
    /// </summary>
    private Task<T> Tracked<T>(Func<Task<T>> start, T refused)
    {
        lock (_workGate)
        {
            if (_workStopping)
            {
                return Task.FromResult(refused);
            }

            if (_workRunning++ == 0)
            {
                _workIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        Task<T> work;
        try
        {
            work = start();
        }
        catch (Exception exception)
        {
            work = Task.FromException<T>(exception);
        }

        work.ContinueWith(_ => WorkEnded(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return work;
    }

    private Task Tracked(Func<Task> start) => Tracked(async () =>
    {
        await start();
        return true;
    }, true);

    private void WorkEnded()
    {
        TaskCompletionSource? idle = null;
        lock (_workGate)
        {
            if (--_workRunning == 0)
            {
                idle = _workIdle;
                _workIdle = null;
            }
        }

        idle?.TrySetResult();
    }

    /// <summary>
    /// Starts nothing new from now on, cuts short the waits between Discord tries and the feed and nudges being read,
    /// and waits up to <paramref name="limit"/> for the work already started to end. True once it has; false if it
    /// ran out of time or was cancelled, so whatever is waiting on it knows something may still be writing.
    /// </summary>
    public Task<bool> StopWorkAsync(TimeSpan limit, CancellationToken cancellationToken = default)
    {
        Task idle;
        lock (_workGate)
        {
            _workStopping = true;
            idle = _workIdle?.Task ?? Task.CompletedTask;
        }

        // Posts in the notes and lists forums are kept track of on their own, and are waited for too.
        _workStop.Cancel();
        return WaitForWorkAsync(Task.WhenAll(idle, ThreadsSettledAsync()), limit, cancellationToken);
    }

    private static async Task<bool> WaitForWorkAsync(Task idle, TimeSpan limit, CancellationToken cancellationToken)
    {
        try
        {
            await idle.WaitAsync(limit, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>The pause between Discord tries: the tests' when they set one, or else one that stopping cuts short.</summary>
    private Task Pause(TimeSpan span) => RetryWait?.Invoke(span) ?? Task.Delay(span, _workStop.Token);
}
