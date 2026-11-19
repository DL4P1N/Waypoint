using System.Diagnostics;

namespace Waypoint.Server;

/// <summary>
/// Keeps a restore to itself. Once one starts, every change through the API is turned away with a 503, the changes
/// already under way are waited for, and then whatever else writes is stopped before the copy goes in. It stays that
/// way until the server starts again on the restored data.
/// </summary>
public sealed class ServerMaintenance
{
    /// <summary>What a change sent during a restore is told.</summary>
    public const string Restoring = "Restoring, try again shortly.";

    private readonly Lock _gate = new();
    private readonly List<Func<TimeSpan, Task<bool>>> _writers = [];
    private int _changing;
    private bool _restoring;

    public bool IsRestoring
    {
        get
        {
            lock (_gate)
            {
                return _restoring;
            }
        }
    }

    /// <summary>Other work that writes, stopped before a restore, such as notes being written. Each says whether it stopped in time.</summary>
    public IReadOnlyList<Func<TimeSpan, Task<bool>>> Writers
    {
        get
        {
            lock (_gate)
            {
                return [.. _writers];
            }
        }
    }

    public void AddWriter(Func<TimeSpan, Task<bool>> stop)
    {
        lock (_gate)
        {
            _writers.Add(stop);
        }
    }

    /// <summary>A change through the API, counted until it's disposed. Null during a restore, when it mustn't run.</summary>
    public IDisposable? TryStartChange()
    {
        lock (_gate)
        {
            if (_restoring)
            {
                return null;
            }

            Interlocked.Increment(ref _changing);
            return new Change(this);
        }
    }

    /// <summary>Turns away new changes. False if a restore has already started.</summary>
    public bool TryStartRestore()
    {
        lock (_gate)
        {
            if (_restoring)
            {
                return false;
            }

            _restoring = true;
            return true;
        }
    }

    /// <summary>Waits for the changes already under way to finish, up to <paramref name="limit"/>.</summary>
    public async Task<bool> DrainChangesAsync(TimeSpan limit)
    {
        var waited = Stopwatch.StartNew();
        while (Volatile.Read(ref _changing) > 0)
        {
            if (waited.Elapsed >= limit)
            {
                return false;
            }

            await Task.Delay(20);
        }

        return true;
    }

    /// <summary>Takes changes again, for a restore given up before anything was stopped.</summary>
    public void EndRestore()
    {
        lock (_gate)
        {
            _restoring = false;
        }
    }

    private sealed class Change(ServerMaintenance owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                Interlocked.Decrement(ref owner._changing);
            }
        }
    }
}
