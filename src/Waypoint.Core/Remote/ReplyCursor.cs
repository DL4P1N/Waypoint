namespace Waypoint.Core.Remote;

/// <summary>
/// How far the PC has read the server's replies. Replies from before the app started aren't shown again. The
/// server counts its replies from 1 each time it starts, so when it has restarted, everything it has said since
/// is new to the PC and is read from the start.
/// </summary>
public sealed class ReplyCursor
{
    private string? _boot;
    private long? _last;

    /// <summary>The reply number to read after, given what the server says now.</summary>
    public long After(ServerStatus status)
    {
        if (_last is null)
        {
            _last = status.LastReply;
        }
        else if (status.Boot != _boot || status.LastReply < _last)
        {
            _last = 0;
        }

        _boot = status.Boot;
        return _last.Value;
    }

    public void Saw(IEnumerable<ServerReply> replies)
    {
        foreach (var reply in replies)
        {
            _last = Math.Max(_last ?? 0, reply.Seq);
        }
    }
}
