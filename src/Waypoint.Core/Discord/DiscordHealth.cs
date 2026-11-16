namespace Waypoint.Core.Discord;

/// <summary>
/// Whether Discord is really working, for the server's heartbeat and /health. Sends that fail even after their
/// retries, or a live connection that stays down, count as trouble once they've lasted longer than the grace time,
/// so a brief outage never wakes anyone. One send that goes through clears the send trouble.
/// </summary>
public sealed class DiscordHealth
{
    private readonly object _lock = new();
    private DateTime? _sendsFailingSince;
    private DateTime? _downSince;

    public void Sent(bool worked, DateTime now)
    {
        lock (_lock)
        {
            _sendsFailingSince = worked ? null : _sendsFailingSince ?? now;
        }
    }

    public void Connection(bool up, DateTime now)
    {
        lock (_lock)
        {
            _downSince = up ? null : _downSince ?? now;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _sendsFailingSince = null;
            _downSince = null;
        }
    }

    /// <summary>What's wrong, once it has lasted longer than <paramref name="grace"/>, or null.</summary>
    public string? Problem(DateTime now, TimeSpan grace)
    {
        lock (_lock)
        {
            if (_sendsFailingSince is { } failing && now - failing > grace)
            {
                return $"Sending to Discord has failed for {Minutes(now - failing)} minutes.";
            }

            return _downSince is { } down && now - down > grace
                ? $"The connection to Discord has been down for {Minutes(now - down)} minutes."
                : null;
        }
    }

    private static int Minutes(TimeSpan span) => (int)span.TotalMinutes;
}
