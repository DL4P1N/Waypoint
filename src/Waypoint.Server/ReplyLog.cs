using Waypoint.Core.Remote;

namespace Waypoint.Server;

/// <summary>
/// Replies to captures typed on the PC, kept until the PC picks them up. Only the latest few are held, since a
/// reply the PC missed while it was off is only useful for a short while.
/// </summary>
public sealed class ReplyLog
{
    private const int Kept = 50;

    private readonly object _gate = new();
    private readonly LinkedList<ServerReply> _replies = new();
    private long _seq;

    /// <summary>New each time the server starts, so the PC can tell the numbers have started over.</summary>
    public string Boot { get; } = Guid.NewGuid().ToString("N");

    public long Last
    {
        get
        {
            lock (_gate)
            {
                return _seq;
            }
        }
    }

    public void Add(string text, IReadOnlyList<Core.Capture.StoredItem> made, bool startedSession, bool opensWindow)
    {
        lock (_gate)
        {
            _replies.AddLast(new ServerReply(++_seq, text, made, startedSession, opensWindow));
            while (_replies.Count > Kept)
            {
                _replies.RemoveFirst();
            }
        }
    }

    public IReadOnlyList<ServerReply> After(long seq)
    {
        lock (_gate)
        {
            return _replies.Where(reply => reply.Seq > seq).ToList();
        }
    }
}
