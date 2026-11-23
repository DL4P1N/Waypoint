using System.Collections.Concurrent;
using System.Text.Json;
using Waypoint.Core;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;
using Waypoint.Core.Remote;

namespace Waypoint.Server;

/// <summary>
/// Notes and practice questions being written, each a job the PC checks on. A running job lives in memory, and every
/// job is also kept in the database, so how one ended is still known after a restart, for an hour. A job the restart
/// cut off is told to the PC as interrupted, and isn't run again on its own. Whatever was saved by then stays saved.
/// A job that breaks off is logged here and reported as failed, with a plain reason, so the PC can tell it apart.
/// </summary>
public sealed class NoteJobs
{
    private static readonly TimeSpan KeepFinished = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly WaypointDatabase _store;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _running;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<CancellationTokenSource> _newStop;
    private bool _shuttingDown;

    /// <param name="store">Where each job and how it ended is kept. Jobs it has as running were cut off by a restart.</param>
    /// <param name="maxRunning">How many are written at once. Each is a claude -p, which is slow and heavy.</param>
    /// <param name="utcNow">The clock for job times and forgetting finished jobs, so tests can move it.</param>
    /// <param name="newStop">Makes each job's stop switch, so tests can see it's let go of.</param>
    public NoteJobs(ILogger logger, WaypointDatabase store, int maxRunning = 2, Func<DateTime>? utcNow = null, Func<CancellationTokenSource>? newStop = null)
    {
        _logger = logger;
        _store = store;
        _running = new SemaphoreSlim(maxRunning, maxRunning);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _newStop = newStop ?? (() => new CancellationTokenSource());

        var cut = _store.InterruptRunningNoteJobs(_utcNow());
        if (cut > 0)
        {
            _logger.LogWarning("{Count} notes job(s) were running when the server stopped. They're marked as interrupted and aren't run again.", cut);
        }

        Sweep();
    }

    /// <summary>
    /// Starts a job, or returns null when as many as allowed are already running, so the PC can try later.
    /// <paramref name="noteId"/> is the note it writes, when that's known at the start. With <paramref name="once"/>,
    /// the key the PC sent the start with, a job already started under it is given back instead of starting another,
    /// so a start sent again after its answer was lost checks on the first.
    /// </summary>
    public NoteJobStarted? Start(NoteJobKind kind, long? noteId, Func<CancellationToken, Task<NoteJobState>> work, string? once = null)
    {
        Sweep();
        var seen = once is not null && _store.SentOnceAnswer(once) is { } kept ? kept : null;
        if (seen is not null && StillKnown(seen) is { } already)
        {
            return already;
        }

        if (!_running.Wait(0))
        {
            return null;
        }

        // Known here as running before it's kept, so from the moment anyone can learn of it, it's never taken for one
        // kept as running whose ending couldn't be kept.
        var id = Guid.NewGuid().ToString("N");
        var job = new Job(id, _newStop());
        _jobs[id] = job;
        try
        {
            // A key whose job has since been forgotten starts a new one, as sending it fresh would.
            var started = once is null || seen is not null
                ? Add(_store)
                : _store.Once(once, _utcNow(), Add);
            if (started.Id != id)
            {
                // Started under the same key a moment ago, by the same start sent twice at once.
                Forget(job);
                return started;
            }
        }
        catch
        {
            Forget(job);
            throw;
        }

        NoteJobStarted Add(WaypointDatabase store)
        {
            store.AddNoteJob(id, kind, noteId, _utcNow());
            return new NoteJobStarted(id);
        }

        var token = job.Token;
        var task = Task.Run(() => work(token), CancellationToken.None);

        // The one place a job's end is handled, however it ended and whoever looks at it. It runs on its own
        // thread, never inline on one that's stopping the job, so the switch isn't let go of while in use.
        _ = task.ContinueWith(ended => Ended(job, ended), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return new NoteJobStarted(id);
    }

    /// <summary>A job made but never run, let go of again.</summary>
    private void Forget(Job job)
    {
        _jobs.TryRemove(job.Id, out _);
        job.Release();
        _running.Release();
    }

    /// <summary>
    /// How a job stands. One that ended is answered from the database, as often as asked, for an hour after it ended.
    /// One the server doesn't know is lost, and so is one kept as running that isn't running here: how it ended
    /// couldn't be kept. Asking only reads, so the PC checking every few seconds never writes; finished jobs are
    /// swept as new ones start.
    /// </summary>
    public NoteJobState State(string id)
    {
        if (_jobs.ContainsKey(id))
        {
            // Still running, or ended a moment ago and not kept yet, so the PC asks again.
            return NoteJobState.Running();
        }

        return _store.GetNoteJob(id) switch
        {
            null => NoteJobState.Lost(),
            { Outcome: NoteJobOutcome.Running } => NoteJobState.Lost(),
            { FinishedAt: { } ended } when ended < _utcNow() - KeepFinished => NoteJobState.Lost(),
            { } kept => FromStore(kept),
        };
    }

    /// <summary>The job a kept start answered with, while it's still known here.</summary>
    private NoteJobStarted? StillKnown(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<NoteJobStarted>(json, RemoteJson.Options) is { Id.Length: > 0 } job
                && (_jobs.ContainsKey(job.Id) || _store.GetNoteJob(job.Id) is not null)
                    ? job
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Stops a job. A job that already ended, or one that isn't known, is left as it is.</summary>
    public void Stop(string id)
    {
        if (_jobs.TryGetValue(id, out var job))
        {
            job.Cancel();
        }
    }

    /// <summary>
    /// Stops every job, as the server shuts down. Each lets go of its switch once it has ended, and one stopped this
    /// way is kept as interrupted by the restart, not as stopped by the person.
    /// </summary>
    public void StopAll()
    {
        Volatile.Write(ref _shuttingDown, true);
        foreach (var job in _jobs.Values)
        {
            job.Cancel();
        }
    }

    /// <summary>
    /// Stops every job, as before a restore, and waits up to <paramref name="limit"/> for each to end and be kept, so
    /// none is kept in the restored data. True once none is left running.
    /// </summary>
    public async Task<bool> StopAllAsync(TimeSpan limit)
    {
        StopAll();
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!_jobs.IsEmpty)
        {
            if (waited.Elapsed >= limit)
            {
                return false;
            }

            await Task.Delay(20);
        }

        return true;
    }

    /// <summary>The plain sentence for why a job failed, never the exception's own text, which can carry paths and keys.</summary>
    private static string Reason(AggregateException? exception) =>
        exception is null ? "Something unexpected went wrong." : PlainProblem.Describe(exception.GetBaseException());

    private static bool IsStopped(Task task) =>
        task.IsCanceled || task.Exception?.GetBaseException() is OperationCanceledException;

    /// <summary>
    /// What the PC is told from a kept job that ended. A job that ran to the end is told with what came of it. A job
    /// cut off by a restart is lost and marked interrupted, which every PC tells as the server restarting while
    /// writing them.
    /// </summary>
    private static NoteJobState FromStore(NoteJobRecord kept)
    {
        switch (kept.Outcome)
        {
            case NoteJobOutcome.Succeeded:
                var result = Result(kept.Result);
                return new NoteJobState(Done: true, Notes: result?.Notes, Study: result?.Study, Outcome: NoteJobOutcome.Succeeded);
            case NoteJobOutcome.Failed:
                return NoteJobState.Failed(kept.Problem ?? NoteJobText.NoReason);
            case NoteJobOutcome.Cancelled:
                return NoteJobState.Cancelled();
            default:
                return kept.Interrupted ? NoteJobState.InterruptedByRestart() : NoteJobState.Lost();
        }
    }

    private static NoteJobState? Result(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<NoteJobState>(json, RemoteJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Ended(Job job, Task<NoteJobState> task)
    {
        try
        {
            if (task.IsFaulted && !IsStopped(task))
            {
                var exception = task.Exception!.GetBaseException();
                _logger.LogError(exception, "Writing notes failed: {Reason} The files stay saved in Sources, so it can be tried again.", Reason(task.Exception));
            }

            Keep(job, task);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The job is then told as lost, since it's kept as running but no longer runs here. What it saved to the
            // note stays saved.
            _logger.LogError(exception, "How a notes job ended couldn't be kept.");
        }
        finally
        {
            _jobs.TryRemove(job.Id, out _);
            job.Release();
            _running.Release();
        }
    }

    /// <summary>
    /// Keeps how a job ended. Only what the PC needs to be told is kept: the result of one that ran to the end, or the
    /// plain reason of one that failed. Never the prompt, paths, or the exception's own text.
    /// </summary>
    private void Keep(Job job, Task<NoteJobState> task)
    {
        var id = job.Id;
        var now = _utcNow();
        if (task.IsCompletedSuccessfully && Volatile.Read(ref _shuttingDown) && job.Token.IsCancellationRequested && EndedWithout(task.Result))
        {
            // Writing turns a stop into its own outcome and returns. Stopped by the shutdown, it's told as the
            // restart, as one the restart cut off before it could return is.
            _store.FinishNoteJob(id, NoteJobOutcome.Lost, now, problem: null, result: null, interrupted: true);
        }
        else if (task.IsCompletedSuccessfully)
        {
            var state = task.Result;
            var result = JsonSerializer.Serialize(new NoteJobState(Done: true, Notes: state.Notes, Study: state.Study), RemoteJson.Options);
            _store.FinishNoteJob(id, NoteJobOutcome.Succeeded, now, problem: null, result, state.Study?.NoteId);
        }
        else if (IsStopped(task) && Volatile.Read(ref _shuttingDown))
        {
            _store.FinishNoteJob(id, NoteJobOutcome.Lost, now, problem: null, result: null, interrupted: true);
        }
        else if (IsStopped(task))
        {
            _store.FinishNoteJob(id, NoteJobOutcome.Cancelled, now, problem: null, result: null);
        }
        else
        {
            _store.FinishNoteJob(id, NoteJobOutcome.Failed, now, Reason(task.Exception), result: null);
        }
    }

    /// <summary>Whether a job came back without the notes or questions it was writing, as one that was stopped does.</summary>
    private static bool EndedWithout(NoteJobState state) =>
        state.Notes?.Status == NoteWriteStatus.Failed || state.Study?.Status == StudyStatus.Failed;

    /// <summary>Forgets finished jobs nobody came back for within the hour. Their switches were let go of as they ended.</summary>
    private void Sweep() => _store.PruneNoteJobs(_utcNow() - KeepFinished);

    private sealed class Job(string id, CancellationTokenSource stop)
    {
        private readonly Lock _gate = new();
        private CancellationTokenSource? _stop = stop;

        public string Id { get; } = id;

        /// <summary>Taken before the job starts, while the switch is surely still held.</summary>
        public CancellationToken Token { get; } = stop.Token;

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
