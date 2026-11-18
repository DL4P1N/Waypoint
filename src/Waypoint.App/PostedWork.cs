using System;
using System.Collections.Concurrent;
using System.Windows.Threading;

namespace Waypoint.App;

/// <summary>
/// Work the worker hands to the window thread from a background one, run in the order it came. Some of it saves
/// data: a Discord capture is marked as read before it's handed over, and only saved once its work runs, so work the
/// window thread never got to would lose it for good. Closing waits on Discord with the window thread busy, and the
/// dispatcher stops once closing is done, so anything still waiting is run by <see cref="RunLeft"/> before that.
/// </summary>
internal sealed class PostedWork(Dispatcher dispatcher, Action<Exception> failed)
{
    private readonly ConcurrentQueue<Action> _waiting = new();

    public void Post(Action action)
    {
        _waiting.Enqueue(action);
        dispatcher.BeginInvoke(RunNext);
    }

    /// <summary>On the window thread while closing: runs everything still waiting, once each, in order.</summary>
    public void RunLeft()
    {
        dispatcher.VerifyAccess();
        while (_waiting.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Closing goes on, and the rest still runs.
                failed(exception);
            }
        }
    }

    /// <summary>Each post adds one run, so a run takes one item, and whatever closing already ran is never run twice.</summary>
    private void RunNext()
    {
        if (_waiting.TryDequeue(out var action))
        {
            action();
        }
    }
}
