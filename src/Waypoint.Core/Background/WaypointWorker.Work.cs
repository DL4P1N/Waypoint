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

        // Everything stopping waits for, on the PC and the server alike. Posts in the notes and lists forums are kept
        // track of on their own, so they're added here. Other work kept apart from this count goes in this list too.
        _workStop.Cancel();
        Task[] waits = [idle, ThreadsSettledAsync(), AlertRoundInFlight()];
        return WaitForWorkAsync(Task.WhenAll(waits), limit, cancellationToken);
    }

    private static async Task<bool> WaitForWorkAsync(Task idle, TimeSpan limit, CancellationToken cancellationToken)
    {
        try
        {
            await idle.WaitAsync(limit, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>The last problem logged for each kind of work, so the same one failing every round is logged once.</summary>
    private readonly Dictionary<string, string> _loggedProblems = [];

    /// <summary>Logs <paramref name="exception"/> for <paramref name="what"/>, unless it's the same as the last one logged for it.</summary>
    private void ReportOnce(string what, Exception exception)
    {
        var said = $"{exception.GetType().FullName}: {exception.Message}";
        lock (_loggedProblems)
        {
            if (_loggedProblems.TryGetValue(what, out var before) && before == said)
            {
                return;
            }

            _loggedProblems[what] = said;
        }

        PlainProblem.Report(exception);
    }

    /// <summary>The work went through, so its next problem is logged even if it's the same as before.</summary>
    private void ProblemCleared(string what)
    {
        lock (_loggedProblems)
        {
            _loggedProblems.Remove(what);
        }
    }

    /// <summary>Work cut short by <see cref="StopWorkAsync"/>, which isn't a problem worth logging.</summary>
    private bool Stopping(Exception exception) => exception is OperationCanceledException && _workStop.IsCancellationRequested;

    /// <summary>The pause between Discord tries: the tests' when they set one, or else one that stopping cuts short.</summary>
    private Task Pause(TimeSpan span) => RetryWait?.Invoke(span) ?? Task.Delay(span, _workStop.Token);
}
