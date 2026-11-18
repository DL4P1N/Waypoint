namespace Waypoint.Core.Remote;

/// <summary>
/// Reading ahead, so the window never waits on the server for a refresh. The pages read through calls that wait for
/// the answer, which is fine for a click but not for the refresh every few seconds: a slow server would hold the
/// window still each time. So the app reads again, off the window's thread, what the pages asked for lately, and
/// then refreshes them with <see cref="UseRecentReads"/>, which answers from those reads instead of asking again.
/// A read is only used that way while it's recent and nothing was changed from here since it was made, so a
/// change made here always shows.
/// </summary>
public sealed partial class ServerClient
{
    /// <summary>How old a read can be and still answer inside <see cref="UseRecentReads"/>.</summary>
    public static readonly TimeSpan RecentReadAge = TimeSpan.FromSeconds(15);

    /// <summary>What the pages asked for within this long is read again by <see cref="RefreshReadsAsync"/>.</summary>
    public static readonly TimeSpan ReadAheadSpan = TimeSpan.FromMinutes(2);

    /// <summary>The most reads one round of reading ahead makes, the most recently asked for first.</summary>
    public const int MaxReadAhead = 64;

    // When each read was last asked for, and when each kept answer was read from the server. Both under the gate.
    private readonly Dictionary<string, DateTime> _asked = [];
    private readonly Dictionary<string, ReadStamp> _readAt = [];
    private readonly ThreadLocal<int> _recentDepth = new();
    private Task? _readingAhead;
    private long _readingAheadFrom;

    // Counts up as each change starts and again as it ends, so a read made while one was on its way isn't reused.
    private long _changes;

    /// <summary>When a kept answer was read, the changes counted then, and the order its read started in.</summary>
    private readonly record struct ReadStamp(DateTime At, long Changes, long Started);

    /// <summary>
    /// Called under the gate when a kept answer is changed here rather than read. It never counts as recent, and a read
    /// already on its way when it was changed doesn't replace it.
    /// </summary>
    private void SetByHand(string path) => _readAt[path] = new ReadStamp(DateTime.MinValue, -1, Interlocked.Increment(ref _readStarts));

    /// <summary>The clock for how old reads are. Tests move it on.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// Reads again, off the calling thread, what was asked for lately, so a refresh inside
    /// <see cref="UseRecentReads"/> has it ready. One round at a time: asking during one gives that one. It stops at
    /// the first sign the server is away, and never throws, since a page reading for itself reports any problem.
    /// A round asked for after a change from here, while one from before it is on its way, follows that one.
    /// </summary>
    public Task RefreshReadsAsync()
    {
        lock (_gate)
        {
            var changes = Interlocked.Read(ref _changes);
            if (_readingAhead is { IsCompleted: false } running)
            {
                if (_readingAheadFrom == changes)
                {
                    return running;
                }

                _readingAheadFrom = changes;
                return _readingAhead = running.ContinueWith(_ => ReadAhead(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }

            _readingAheadFrom = changes;
            return _readingAhead = Task.Run(ReadAhead);
        }
    }

    /// <summary>
    /// Until it's disposed, reads on this thread are answered from ones made within <see cref="RecentReadAge"/>
    /// with nothing changed from here since, without asking the server. Anything else is read as usual. It belongs
    /// to the thread that made it, so nothing inside it may wait with await.
    /// </summary>
    public IDisposable UseRecentReads()
    {
        _recentDepth.Value++;
        return new RecentScope(this);
    }

    private sealed class RecentScope(ServerClient client) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (!_done)
            {
                _done = true;
                client._recentDepth.Value--;
            }
        }
    }

    /// <summary>Marks a change as on its way until it's disposed.</summary>
    private IDisposable Changing()
    {
        Interlocked.Increment(ref _changes);
        return new ChangeMark(this);
    }

    private sealed class ChangeMark(ServerClient client) : IDisposable
    {
        public void Dispose() => Interlocked.Increment(ref client._changes);
    }

    private void Asked(string path)
    {
        lock (_gate)
        {
            _asked[path] = UtcNow();
            if (_asked.Count > MaxSavedReads)
            {
                var cutoff = UtcNow() - ReadAheadSpan;
                foreach (var old in _asked.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToList())
                {
                    _asked.Remove(old);
                }
            }
        }
    }

    /// <summary>The kept answer for a read inside <see cref="UseRecentReads"/>, if it's recent and nothing changed since.</summary>
    private string? TakeRecent(string path)
    {
        if (_recentDepth.Value == 0)
        {
            return null;
        }

        lock (_gate)
        {
            return _readAt.TryGetValue(path, out var stamp)
                && stamp.Changes == Interlocked.Read(ref _changes)
                && UtcNow() - stamp.At <= RecentReadAge
                && _lastReads.TryGetValue(path, out var json)
                    ? json
                    : null;
        }
    }

    /// <summary>
    /// Asks the server now and keeps the answer. Null if it didn't answer with one, in which case it's marked away or
    /// not paired as needed. A request it turned down throws, as it does for the page.
    /// </summary>
    private string? ReadNow(string path)
    {
        var changes = Interlocked.Read(ref _changes);
        var at = UtcNow();
        var started = Interlocked.Increment(ref _readStarts);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            using var response = SendNow(request);
            if (!Settle(response))
            {
                return null;
            }

            var json = ReadText(response);
            lock (_gate)
            {
                // A slow answer to an earlier read, landing after a later one, is still this caller's answer, but
                // the later one stays kept.
                if (!_readAt.TryGetValue(path, out var kept) || kept.Started < started)
                {
                    Remember(path, json);
                    _readAt[path] = new ReadStamp(at, changes, started);
                }
            }

            return json;
        }
        catch (Exception exception) when (IsConnectionProblem(exception, CancellationToken.None))
        {
            MarkOffline();
            return null;
        }
    }

    private void ReadAhead()
    {
        List<string> paths;
        lock (_gate)
        {
            var cutoff = UtcNow() - ReadAheadSpan;
            paths = _asked.Where(pair => pair.Value >= cutoff)
                .OrderByDescending(pair => pair.Value)
                .Take(MaxReadAhead)
                .Select(pair => pair.Key)
                .ToList();
        }

        foreach (var path in paths)
        {
            if (!IsOnline)
            {
                return;
            }

            try
            {
                ReadNow(path);
            }
            catch (InvalidOperationException)
            {
                // That page reads it for itself and says what went wrong. The rest are still read.
            }
        }
    }
}
