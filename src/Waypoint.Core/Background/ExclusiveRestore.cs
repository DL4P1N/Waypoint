using System.Diagnostics;

namespace Waypoint.Core.Background;

/// <summary>
/// A restore replaces the database under everything else, so whatever writes to it is stopped first. Otherwise work
/// started against the old data could finish against the restored copy. Waypoint always starts again afterwards,
/// since what was stopped isn't started again here.
/// </summary>
public static class ExclusiveRestore
{
    /// <summary>How long the stops may take between them before the restore is given up.</summary>
    public static readonly TimeSpan StopLimit = TimeSpan.FromSeconds(30);

    /// <summary>Said when something was still writing when the time ran out.</summary>
    public const string StillBusy = "Waypoint was still finishing other work, so nothing was changed. Try the restore again once Waypoint has started again.";

    /// <summary>
    /// Runs each stop in order, each given what's left of <paramref name="limit"/>. True only once all of them say
    /// they stopped. A stop that says it didn't, throws, or runs past the time left ends it there with false, so the
    /// database is never replaced while something may still be writing.
    /// </summary>
    /// <remarks>Each stop starts on the caller's thread, so a stop that has to run on the window thread can.</remarks>
    public static async Task<bool> StopWritersAsync(IEnumerable<Func<TimeSpan, Task<bool>>> stops, TimeSpan limit, Action<Exception>? failed = null)
    {
        var waited = Stopwatch.StartNew();
        foreach (var stop in stops)
        {
            var left = limit - waited.Elapsed;
            if (left <= TimeSpan.Zero)
            {
                return false;
            }

            try
            {
                var stopping = stop(left);
                if (await Task.WhenAny(stopping, Task.Delay(left)) != stopping || !await stopping)
                {
                    return false;
                }
            }
            catch (Exception exception)
            {
                failed?.Invoke(exception);
                return false;
            }
        }

        return true;
    }
}
