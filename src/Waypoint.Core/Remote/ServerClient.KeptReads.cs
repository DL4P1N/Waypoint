namespace Waypoint.Core.Remote;

/// <summary>
/// Showing a page without waiting on the server. A page opened, or drawn again after a click, is shown at once from
/// the answers kept from earlier reads, however old, inside <see cref="UseKeptReads"/>. What it asked for is noted, and
/// what wasn't read just now is read again off the window's thread with <see cref="ReadAgain"/>, after which the page
/// is shown again. A read with nothing kept answers as if the server were away, so a page read with a fallback shows
/// its empty state until the answer comes.
/// </summary>
public sealed partial class ServerClient
{
    private readonly ThreadLocal<KeptReads?> _kept = new();

    // Counts up as each read starts, so a read that started earlier never replaces the answer of one that started later.
    private long _readStarts;

    /// <summary>
    /// Until it's disposed, reads on this thread never ask the server: each is answered from what was kept, and noted
    /// in <paramref name="reads"/>. It belongs to the thread that made it, so nothing inside it may wait with await.
    /// </summary>
    public IDisposable UseKeptReads(KeptReads reads)
    {
        reads.Enter(this, _kept.Value);
        _kept.Value = reads;
        return reads;
    }

    /// <summary>
    /// Reads each of these from the server now, a few at a time, and keeps the answers. Made off the window's thread.
    /// It stops at the first sign the server is away, and never throws, since a page reading for itself reports any
    /// problem.
    /// </summary>
    public void ReadAgain(IEnumerable<string> paths)
    {
        var list = paths.Distinct(StringComparer.Ordinal).ToList();
        Parallel.ForEach(list, new ParallelOptions { MaxDegreeOfParallelism = 4 }, (path, loop) =>
        {
            if (!IsOnline)
            {
                loop.Stop();
                return;
            }

            try
            {
                ReadNow(path);
            }
            catch (InvalidOperationException)
            {
                // That page reads it for itself and says what went wrong.
            }
        });
    }

    internal void LeaveKept(KeptReads reads, KeptReads? outer)
    {
        if (ReferenceEquals(_kept.Value, reads))
        {
            _kept.Value = outer;
        }
    }

    /// <summary>
    /// The kept answer for a read inside <see cref="UseKeptReads"/>. Fresh when it was read within
    /// <see cref="RecentReadAge"/> with nothing changed from here since; anything else is noted as missed, to read again.
    /// </summary>
    private bool TryKept(string path, out string? json, out bool fresh)
    {
        json = null;
        fresh = false;
        if (_kept.Value is not { } kept)
        {
            return false;
        }

        lock (_gate)
        {
            fresh = _readAt.TryGetValue(path, out var stamp)
                && stamp.Changes == Interlocked.Read(ref _changes)
                && UtcNow() - stamp.At <= RecentReadAge;
            _lastReads.TryGetValue(path, out json);
        }

        kept.Noted(path, fresh);
        return true;
    }
}

/// <summary>
/// What a page asked for while it was shown from kept answers, and which of those weren't read just now, so they can
/// be read again and the page shown again from them. Used on one thread.
/// </summary>
public sealed class KeptReads : IDisposable
{
    private readonly HashSet<string> _asked = new(StringComparer.Ordinal);
    private readonly HashSet<string> _missed = new(StringComparer.Ordinal);
    private ServerClient? _client;
    private KeptReads? _outer;

    /// <summary>Everything asked for.</summary>
    public IReadOnlyCollection<string> Asked => _asked;

    /// <summary>What was answered from an old answer, or from nothing, and needs reading again.</summary>
    public IReadOnlyCollection<string> Missed => _missed;

    internal void Enter(ServerClient client, KeptReads? outer)
    {
        _client = client;
        _outer = outer;
    }

    internal void Noted(string path, bool fresh)
    {
        _asked.Add(path);
        if (!fresh)
        {
            _missed.Add(path);
        }

        _outer?.Noted(path, fresh);
    }

    public void Dispose()
    {
        if (_client is { } client)
        {
            client.LeaveKept(this, _outer);
            _client = null;
        }
    }
}
