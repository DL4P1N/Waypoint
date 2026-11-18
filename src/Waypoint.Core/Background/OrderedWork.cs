using System.Threading.Channels;

namespace Waypoint.Core.Background;

/// <summary>
/// Runs work one piece at a time, in the order it was handed in, off the caller's thread. Discord's events and the
/// server's background work go through this, so a message closed and then reopened is saved in that order.
/// A failure is passed to the failure callback and the next piece still runs.
/// </summary>
/// <remarks>
/// Nothing handed in is dropped quietly. The queue has no hard limit, because what goes through it is captures,
/// edits, and button presses, and a producer made to wait could be the read loop that keeps Discord connected or
/// a piece of work posting the next one. A warning is given instead, once, when the queue gets longer than it
/// should. Stopping finishes what's queued within a time limit and says what was left. Work handed in after that
/// is refused out loud: <see cref="Post(Func{CancellationToken, Task})"/> returns false and a warning is given.
/// </remarks>
public sealed class OrderedWork : IAsyncDisposable
{
    /// <summary>More waiting than this is warned about. Normal use keeps it at a handful.</summary>
    public const int DefaultLongQueue = 500;

    /// <summary>How long stopping waits for what's queued when no other limit is given.</summary>
    public static readonly TimeSpan DefaultStopLimit = TimeSpan.FromSeconds(5);

    private readonly Channel<Func<CancellationToken, Task>> _work =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _stopping = new();
    private readonly Action<Exception> _failed;
    private readonly Action<string> _warn;
    private readonly int _longQueue;
    private readonly Task _running;
    private readonly object _gate = new();
    private Task<int>? _stopped;

    // Waiting plus the one running, so a stuck piece still counts.
    private int _pending;
    private int _warned;

    /// <param name="failed">Told about each piece that throws.</param>
    /// <param name="warn">Told about a long queue, work left undone at stopping, and work refused after it. Without
    /// one, these go to <paramref name="failed"/> so they still reach the log.</param>
    /// <param name="longQueue">How many may wait before the warning.</param>
    public OrderedWork(Action<Exception> failed, Action<string>? warn = null, int longQueue = DefaultLongQueue)
    {
        _failed = failed;
        _warn = warn ?? (message => failed(new InvalidOperationException(message)));
        _longQueue = longQueue;
        _running = Task.Run(RunAsync);
    }

    /// <summary>Cancelled when stopping runs out of time, so a piece that's still going can give up.</summary>
    public CancellationToken Stopping => _stopping.Token;

    /// <summary>False, with a warning, once stopping has begun. The work is not run then.</summary>
    public bool Post(Func<CancellationToken, Task> work)
    {
        var pending = Interlocked.Increment(ref _pending);
        if (!_work.Writer.TryWrite(work))
        {
            Interlocked.Decrement(ref _pending);
            _warn("Work was handed in after it stopped, so it wasn't run.");
            return false;
        }

        if (pending > _longQueue && Interlocked.Exchange(ref _warned, 1) == 0)
        {
            _warn($"{pending} pieces of work are waiting to run in order, more than {_longQueue}. Something is slow or stuck.");
        }

        return true;
    }

    public bool Post(Func<Task> work) => Post(_ => work());

    public bool Post(Action work) => Post(_ =>
    {
        work();
        return Task.CompletedTask;
    });

    /// <summary>
    /// Takes no more work, finishes what's queued, and gives the number left undone. If that takes longer than
    /// <paramref name="limit"/>, <see cref="Stopping"/> is cancelled, the rest is skipped, and a warning says how many.
    /// Calling it again gives the same answer.
    /// </summary>
    public Task<int> StopAsync(TimeSpan limit)
    {
        lock (_gate)
        {
            return _stopped ??= StopCoreAsync(limit);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(DefaultStopLimit);

    private async Task<int> StopCoreAsync(TimeSpan limit)
    {
        _work.Writer.TryComplete();
        try
        {
            await _running.WaitAsync(limit);
            return 0;
        }
        catch (TimeoutException)
        {
        }

        // Read before cancelling, since skipped pieces count down as they go.
        var left = Volatile.Read(ref _pending);
        _stopping.Cancel();
        _warn($"Stopped after {limit.TotalSeconds:0.#} seconds with {left} pieces of work left undone.");
        return left;
    }

    private async Task RunAsync()
    {
        await foreach (var work in _work.Reader.ReadAllAsync())
        {
            try
            {
                if (!_stopping.IsCancellationRequested)
                {
                    await work(_stopping.Token);
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                // Given up on at stopping, which already said so.
            }
            catch (Exception exception)
            {
                _failed(exception);
            }
            finally
            {
                if (Interlocked.Decrement(ref _pending) == 0)
                {
                    // Caught up, so a later backlog is warned about again.
                    Interlocked.Exchange(ref _warned, 0);
                }
            }
        }
    }
}
