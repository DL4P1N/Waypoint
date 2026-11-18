using System.Collections.Concurrent;
using Waypoint.Core;
using Waypoint.Core.Remote;

namespace Waypoint.Server;

/// <summary>
/// Notes and practice questions being written, each a job the PC checks on. Jobs live in memory: after a restart the
/// PC hears the job is lost, and whatever was saved by then stays saved. A job that breaks off is logged here and
/// reported as failed, with a plain reason, so the PC can tell it apart from a lost one.
/// </summary>
public sealed class NoteJobs
{
    private static readonly TimeSpan KeepFinished = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _running;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<CancellationTokenSource> _newStop;

    /// <param name="maxRunning">How many are written at once. Each is a claude -p, which is slow and heavy.</param>
    /// <param name="utcNow">The clock for forgetting finished jobs, so tests can move it.</param>
    /// <param name="newStop">Makes each job's stop switch, so tests can see it's let go of.</param>
    public NoteJobs(ILogger logger, int maxRunning = 2, Func<DateTime>? utcNow = null, Func<CancellationTokenSource>? newStop = null)
    {
        _logger = logger;
        _running = new SemaphoreSlim(maxRunning, maxRunning);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _newStop = newStop ?? (() => new CancellationTokenSource());
    }

    /// <summary>Starts a job, or returns null when as many as allowed are already running, so the PC can try later.</summary>
    public NoteJobStarted? Start(Func<CancellationToken, Task<NoteJobState>> work)
    {
        Sweep();
        if (!_running.Wait(0))
        {
            return null;
        }

        var stop = _newStop();
        var token = stop.Token;
        var task = Task.Run(() => work(token), CancellationToken.None);
        var job = new Job(stop, task);
        var id = Guid.NewGuid().ToString("N");
        _jobs[id] = job;

        // The one place a job's end is handled, however it ended and whoever looks at it. It runs on its own
        // thread, never inline on one that's stopping the job, so the switch isn't let go of while in use.
        _ = task.ContinueWith(ended => Ended(job, ended), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return new NoteJobStarted(id);
    }

    public NoteJobState State(string id)
    {
        Sweep();
        if (!_jobs.TryGetValue(id, out var job))
        {
            return NoteJobState.Lost();
        }

        var task = job.Task;
        if (!task.IsCompleted)
        {
            return NoteJobState.Running();
        }

        _jobs.TryRemove(id, out _);
        if (task.IsCompletedSuccessfully)
        {
            return task.Result with { Done = true, Gone = false, Outcome = NoteJobOutcome.Succeeded, Problem = null };
        }

        return IsStopped(task) ? NoteJobState.Cancelled() : NoteJobState.Failed(Reason(task.Exception));
    }

    /// <summary>Stops a job. A job that already ended, or one that isn't known, is left as it is.</summary>
    public void Stop(string id)
    {
        if (_jobs.TryGetValue(id, out var job))
        {
            job.Cancel();
        }
    }

    /// <summary>Stops every job, as the server shuts down. Each lets go of its switch once it has ended.</summary>
    public void StopAll()
    {
        foreach (var job in _jobs.Values)
        {
            job.Cancel();
        }
    }

    /// <summary>The plain sentence for why a job failed, never the exception's own text, which can carry paths and keys.</summary>
    private static string Reason(AggregateException? exception) =>
        exception is null ? "Something unexpected went wrong." : PlainProblem.Describe(exception.GetBaseException());

    private static bool IsStopped(Task task) =>
        task.IsCanceled || task.Exception?.GetBaseException() is OperationCanceledException;

    private void Ended(Job job, Task<NoteJobState> task)
    {
        try
        {
            if (task.IsFaulted && !IsStopped(task))
            {
                var exception = task.Exception!.GetBaseException();
                _logger.LogError(exception, "Writing notes failed: {Reason} The files stay saved in Sources, so it can be tried again.", Reason(task.Exception));
            }
        }
        finally
        {
            job.FinishedAt = _utcNow();
            job.Release();
            _running.Release();
        }
    }

    /// <summary>Forgets finished jobs nobody came back for within the hour. Their switches were let go of as they ended.</summary>
    private void Sweep()
    {
        var now = _utcNow();
        foreach (var old in _jobs.Where(entry => entry.Value.FinishedAt is { } at && now - at > KeepFinished).ToList())
        {
            _jobs.TryRemove(old.Key, out _);
        }
    }

    private sealed class Job(CancellationTokenSource stop, Task<NoteJobState> task)
    {
        private readonly Lock _gate = new();
        private CancellationTokenSource? _stop = stop;
        private DateTime? _finishedAt;

        public Task<NoteJobState> Task { get; } = task;

        public DateTime? FinishedAt
        {
            get
            {
                lock (_gate)
                {
                    return _finishedAt;
                }
            }

            set
            {
                lock (_gate)
                {
                    _finishedAt = value;
                }
            }
        }

        /// <summary>Stops the job, unless it ended and its switch was let go of already.</summary>
        public void Cancel()
        {
            lock (_gate)
            {
                _stop?.Cancel();
            }
        }

        /// <summary>Lets go of the switch, once. A stop at the same moment either goes first or finds it gone.</summary>
        public void Release()
        {
            CancellationTokenSource? stop;
            lock (_gate)
            {
                stop = _stop;
                _stop = null;
            }

            stop?.Dispose();
        }
    }
}
