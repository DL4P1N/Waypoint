using System.Threading.Channels;

namespace Waypoint.Core.Background;

/// <summary>
/// Runs work one piece at a time, in the order it was handed in, off the caller's thread. Discord's events and the
/// server's background work go through this, so a message closed and then reopened is saved in that order.
/// A failure is passed to <paramref name="failed"/> and the next piece still runs.
/// </summary>
public sealed class OrderedWork
{
    private readonly Channel<Func<Task>> _work = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Action<Exception> _failed;

    public OrderedWork(Action<Exception> failed)
    {
        _failed = failed;
        _ = Task.Run(RunAsync);
    }

    public void Post(Func<Task> work) => _work.Writer.TryWrite(work);

    public void Post(Action work) => Post(() =>
    {
        work();
        return Task.CompletedTask;
    });

    private async Task RunAsync()
    {
        await foreach (var work in _work.Reader.ReadAllAsync())
        {
            try
            {
                await work();
            }
            catch (Exception exception)
            {
                _failed(exception);
            }
        }
    }
}
