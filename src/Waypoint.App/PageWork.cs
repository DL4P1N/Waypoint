using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Waypoint.Core.Remote;

namespace Waypoint.App;

/// <summary>
/// How a page reads and changes its data. On this PC's database every call runs straight away, the same as calling
/// it directly, since it's quick. In server mode a slow server would hold the window still, so:
/// <list type="bullet">
/// <item>A change from a click (<see cref="Run{T}"/>) is sent off the window's thread. A page's changes go one at a
/// time, in the order they were asked for, so two quick clicks reach the server in order. What the page showed lately
/// is read again after each, so the page can show the change straight after.</item>
/// <item>Showing the page (<see cref="Show"/>) draws it at once from the answers kept from earlier reads, or its empty
/// state, and draws it again once what it asked for has been read. That waits behind changes still on their way, so
/// it always shows them. The page's own rules about what's being typed decide what a later drawing may replace.</item>
/// </list>
/// Closing waits a while for changes still on their way, with <see cref="Finish"/>.
/// </summary>
internal sealed class PageWork
{
    /// <summary>For this PC's database: everything runs straight away.</summary>
    public static PageWork Local { get; } = new(null);

    /// <summary>The most times a page is read and drawn again for one showing, when each drawing asks for more.</summary>
    private const int MaxRounds = 3;

    /// <summary>How many reads a page's changes read again after them. Past this only the latest drawing's are kept.</summary>
    private const int MaxShownFrom = 64;

    private static int s_saving;

    private readonly ServerClient? _server;
    private readonly object _gate = new();
    private readonly List<string> _waiting = [];
    private readonly HashSet<string> _shownFrom = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _closing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, (Action Show, Action? Again)> _afterChanges = [];
    private Task _tail = Task.CompletedTask;
    private ReadRound? _queuedRound;
    private int _pending;
    private int _changes;
    private int _drawing;
    private volatile bool _closed;

    public PageWork(ServerClient? server) => _server = server;

    /// <summary>Raised on the window's thread when changes start or stop being on their way, on any page.</summary>
    public static event Action? SavingChanged;

    /// <summary>Whether any page has a change on its way to the server.</summary>
    public static bool Saving => Volatile.Read(ref s_saving) > 0;

    /// <summary>Whether everything runs straight away, as on this PC's database or once closing.</summary>
    public bool RunsNow => _server is null || _closed;

    /// <summary>Whether a change or a read is still queued or on its way, so a refresh can leave the page as it is until it's done.</summary>
    public bool IsBusy => Volatile.Read(ref _pending) > 0;

    /// <summary>Whether a drawing from kept answers is under way, on the window's thread, so what it draws reads from them too.</summary>
    public bool Drawing => _drawing > 0;

    /// <summary>Whether a change is still queued or on its way.</summary>
    public bool ChangesWaiting => Volatile.Read(ref _changes) > 0;

    /// <summary>The changes not finished yet, by what asked for them, for the log when closing can't wait for them.</summary>
    public IReadOnlyList<string> Waiting
    {
        get
        {
            lock (_gate)
            {
                return _waiting.ToList();
            }
        }
    }

    public Task Run(Action work, [CallerMemberName] string what = "") => Run<object?>(() =>
    {
        work();
        return null;
    }, what);

    /// <summary>
    /// A change. On this PC's database it runs now and the task is already done, and a problem is thrown straight
    /// away. In server mode it runs off the window's thread after the page's earlier changes, and what awaits it
    /// carries on back on the window's thread.
    /// </summary>
    public Task<T> Run<T>(Func<T> work, [CallerMemberName] string what = "") => Queue(work, what, change: true);

    /// <summary>A read that has to ask the server, such as one sent as a request. Queued like a change, with no Saving... shown.</summary>
    public Task<T> Read<T>(Func<T> read, [CallerMemberName] string what = "") => Queue(read, what, change: false);

    /// <summary>A change nothing waits on. A problem with it is shown the way one from a click is.</summary>
    public async void Fire(Action work, [CallerMemberName] string what = "") => await Run(work, what);

    /// <summary>
    /// Shows the page. On this PC's database it's just drawn. In server mode it's drawn now from kept answers, and
    /// drawn again with <paramref name="again"/>, or the same again, once what it asked for that wasn't read just now
    /// has been read, after any changes on their way.
    /// </summary>
    public void Show(Action show, Action? again = null)
    {
        if (RunsNow)
        {
            show();
            return;
        }

        Draw(show, again ?? show, round: 1, Dispatcher.CurrentDispatcher);
    }

    /// <summary>
    /// Shows the page after a change. While another change is still on its way the drawing waits, once for each
    /// <paramref name="key"/>, until the last one's done, so a page never shows a moment between two clicks.
    /// </summary>
    public void ShowAfterChanges(string key, Action show, Action? again = null)
    {
        if (RunsNow || !HoldBack(key, show, again))
        {
            Show(show, again);
        }
    }

    /// <summary>Keeps a drawing for once the last change is done, if one is still on its way. False when none is.</summary>
    private bool HoldBack(string key, Action show, Action? again)
    {
        lock (_gate)
        {
            if (_changes == 0)
            {
                return false;
            }

            _afterChanges[key] = (show, again);
            return true;
        }
    }

    /// <summary>
    /// A read for setting the page up, such as which view was last open. In server mode it's answered now from what
    /// was kept, or as nothing yet, and what wasn't read just now is read for next time.
    /// </summary>
    public T Peek<T>(Func<T> read)
    {
        if (RunsNow)
        {
            return read();
        }

        var value = default(T)!;
        Draw(() => value = read(), () => { }, round: MaxRounds - 1, Dispatcher.CurrentDispatcher);
        return value;
    }

    public void Peek(Action read) => Peek<object?>(() =>
    {
        read();
        return null;
    });

    /// <summary>
    /// For closing: from now on everything runs straight away, and this waits up to <paramref name="limit"/> for the
    /// changes already on their way. Returns those still not done.
    /// </summary>
    public IReadOnlyList<string> Finish(TimeSpan limit) => FinishAll([this], limit);

    /// <summary>
    /// For closing several pages: each is marked closing first, so a page whose queue waits on a drawing on the
    /// window's thread carries straight on, and then all of them are waited for together, up to <paramref name="limit"/>
    /// in all. Returns the changes still not done.
    /// </summary>
    public static IReadOnlyList<string> FinishAll(IReadOnlyList<PageWork> works, TimeSpan limit)
    {
        var tails = new List<Task>();
        foreach (var work in works)
        {
            work._closed = true;
            work._closing.TrySetResult();
            lock (work._gate)
            {
                tails.Add(work._tail);
            }
        }

        try
        {
            Task.WhenAll(tails).Wait(limit);
        }
        catch (AggregateException)
        {
            // Each change hands its own problem to what asked for it.
        }

        return works.SelectMany(work => work.Waiting).ToList();
    }

    private Task<T> Queue<T>(Func<T> work, string what, bool change)
    {
        if (RunsNow)
        {
            return Task.FromResult(work());
        }

        var server = _server!;
        var dispatcher = Dispatcher.CurrentDispatcher;
        var paths = change ? Shown() : [];
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Started(what, dispatcher, change);
        Enqueue(() =>
        {
            T result;
            try
            {
                result = work();
            }
            catch (Exception exception)
            {
                Ended(what, dispatcher, change);
                done.SetException(exception);
                return Task.CompletedTask;
            }

            try
            {
                // Read before what awaits this carries on, so the page shows the change straight away.
                server.ReadAgain(paths);
            }
            finally
            {
                // The change went through even if reading the page again didn't, so the page is never left waiting.
                Ended(what, dispatcher, change);
                done.SetResult(result);
            }

            return Task.CompletedTask;
        });
        return done.Task;
    }

    private void Draw(Action show, Action again, int round, Dispatcher dispatcher)
    {
        var reads = new KeptReads();
        using (_server!.UseKeptReads(reads))
        {
            _drawing++;
            try
            {
                show();
            }
            finally
            {
                _drawing--;
            }
        }

        lock (_gate)
        {
            if (_shownFrom.Count + reads.Asked.Count > MaxShownFrom)
            {
                _shownFrom.Clear();
            }

            _shownFrom.UnionWith(reads.Asked);
        }

        if (reads.Missed.Count > 0 && round < MaxRounds && _server.IsOnline && !_closed)
        {
            ReadThenDraw(reads.Missed, again, round, dispatcher);
        }
    }

    /// <summary>
    /// Queues a read of these, then the page drawn again. A read already queued and not started takes these on too,
    /// so opening pages quickly doesn't pile up reads.
    /// </summary>
    private void ReadThenDraw(IEnumerable<string> paths, Action again, int round, Dispatcher dispatcher)
    {
        ReadRound next;
        lock (_gate)
        {
            if (_queuedRound is { } queued)
            {
                queued.Paths.UnionWith(paths);
                if (!queued.Shows.Contains(again))
                {
                    queued.Shows.Add(again);
                }

                return;
            }

            next = new ReadRound(new HashSet<string>(paths, StringComparer.Ordinal), [again], round + 1);
            _queuedRound = next;
        }

        var server = _server!;
        Started(null, dispatcher, change: false);
        Enqueue(async () =>
        {
            List<string> reading;
            List<Action> shows;
            lock (_gate)
            {
                if (ReferenceEquals(_queuedRound, next))
                {
                    _queuedRound = null;
                }

                reading = next.Paths.ToList();
                shows = next.Shows.ToList();
            }

            try
            {
                server.ReadAgain(reading);
                if (!_closed)
                {
                    var drawn = dispatcher.InvokeAsync(() =>
                    {
                        // What was read before a change still on its way would show a moment from before it.
                        foreach (var show in shows)
                        {
                            if (!HoldBack($"again {RuntimeHelpers.GetHashCode(show)}", show, null))
                            {
                                Draw(show, show, next.Round, dispatcher);
                            }
                        }
                    }).Task;
                    await Task.WhenAny(drawn, _closing.Task).ConfigureAwait(false);
                    if (drawn.IsFaulted)
                    {
                        // Thrown on the window's thread the way any problem drawing the page is.
                        var problem = drawn.Exception!.InnerException!;
                        _ = dispatcher.BeginInvoke(() => ExceptionDispatchInfo.Throw(problem));
                    }
                }
            }
            finally
            {
                Ended(null, dispatcher, change: false);
            }
        });
    }

    private sealed record ReadRound(HashSet<string> Paths, List<Action> Shows, int Round);

    /// <summary>One after another, off the window's thread. Nothing queued ever stops the queue.</summary>
    private void Enqueue(Func<Task> step)
    {
        lock (_gate)
        {
            var previous = _tail;
            _tail = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                try
                {
                    await step().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Each step hands its own problem to what asked for it.
                }
            });
        }
    }

    private List<string> Shown()
    {
        lock (_gate)
        {
            return _shownFrom.ToList();
        }
    }

    private void Started(string? what, Dispatcher dispatcher, bool change)
    {
        lock (_gate)
        {
            _pending++;
            if (change)
            {
                _changes++;
                _waiting.Add(what ?? "");
            }
        }

        if (change && Interlocked.Increment(ref s_saving) == 1)
        {
            dispatcher.BeginInvoke(() => SavingChanged?.Invoke());
        }
    }

    private void Ended(string? what, Dispatcher dispatcher, bool change)
    {
        var last = false;
        lock (_gate)
        {
            _pending--;
            if (change)
            {
                _changes--;
                _waiting.Remove(what ?? "");
                last = _changes == 0 && _afterChanges.Count > 0;
            }
        }

        if (last)
        {
            dispatcher.BeginInvoke(ShowWaitingDrawings);
        }

        if (change && Interlocked.Decrement(ref s_saving) == 0)
        {
            dispatcher.BeginInvoke(() => SavingChanged?.Invoke());
        }
    }

    /// <summary>Once the last change is done, the drawings held back for it.</summary>
    private void ShowWaitingDrawings()
    {
        List<(Action Show, Action? Again)> drawings;
        lock (_gate)
        {
            if (_changes > 0)
            {
                return;
            }

            drawings = _afterChanges.Values.ToList();
            _afterChanges.Clear();
        }

        foreach (var (show, again) in drawings)
        {
            Show(show, again);
        }
    }
}
