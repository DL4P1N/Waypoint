namespace Waypoint.Core.Schedule;

/// <summary>The shortest idle time of several sources, such as the keyboard and mouse and a game controller. Null only when none has a reading.</summary>
public sealed class MinimumIdle(params IUserIdle[] sources) : IUserIdle
{
    public TimeSpan? IdleFor(DateTime now)
    {
        TimeSpan? shortest = null;
        foreach (var source in sources)
        {
            if (source.IdleFor(now) is { } idle && (shortest is null || idle < shortest))
            {
                shortest = idle;
            }
        }

        return shortest;
    }
}

/// <summary>
/// Game controller input, from each controller's packet number, which changes whenever its state does. Only that a
/// change happened is noted, in memory. The first look at each controller only sets where it stands, and with no
/// change seen yet there's no reading. It only looks when asked, which is only during the late window.
/// </summary>
public sealed class ControllerIdle(Func<int, uint?> packet) : IUserIdle
{
    public const int Controllers = 4;

    private readonly uint?[] _seen = new uint?[Controllers];
    private DateTime? _lastInput;

    public TimeSpan? IdleFor(DateTime now)
    {
        var utcNow = now.ToUniversalTime();
        for (var index = 0; index < Controllers; index++)
        {
            var current = packet(index);
            if (current is { } number && _seen[index] is { } before && before != number)
            {
                _lastInput = utcNow;
            }

            _seen[index] = current;
        }

        return _lastInput is { } at ? (utcNow > at ? utcNow - at : TimeSpan.Zero) : null;
    }
}
