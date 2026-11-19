namespace Waypoint.Core.Discord;

/// <summary>
/// Tries Discord for about five minutes before giving up on it. A try only follows a failed one,
/// so a sent alert never goes out twice.
/// </summary>
public static class AlertRetry
{
    public static readonly IReadOnlyList<TimeSpan> Waits =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(1.5),
        TimeSpan.FromMinutes(2),
    ];

    /// <summary>How long all the tries take, from the first to the last.</summary>
    public static TimeSpan Total => Waits.Aggregate(TimeSpan.Zero, (sum, wait) => sum + wait);

    /// <summary>True once <paramref name="attempt"/> succeeds, false if every try failed.</summary>
    public static async Task<bool> RunAsync(Func<Task<bool>> attempt, Func<TimeSpan, Task>? wait = null)
    {
        if (await attempt())
        {
            return true;
        }

        foreach (var pause in Waits)
        {
            await (wait ?? Task.Delay)(pause);
            if (await attempt())
            {
                return true;
            }
        }

        return false;
    }
}
