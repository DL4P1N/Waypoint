using System.Diagnostics;

namespace Waypoint.Core.Tests;

/// <summary>
/// Waiting for something done in the background. The limit is only so a hung test fails rather than hangs: it's
/// far longer than anything here takes, so a busy machine only makes a test later, never wrong.
/// </summary>
internal static class Waits
{
    public static readonly TimeSpan Hung = TimeSpan.FromSeconds(60);

    /// <summary>Checks again until it's true, and says whether it came true before <see cref="Hung"/>.</summary>
    public static async Task<bool> Until(Func<bool> condition, int every = 10)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            if (waited.Elapsed > Hung)
            {
                return condition();
            }

            await Task.Delay(every);
        }

        return true;
    }

    /// <summary>The same as <see cref="Until"/>, for a test that can't wait asynchronously.</summary>
    public static bool UntilNow(Func<bool> condition, int every = 10)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            if (waited.Elapsed > Hung)
            {
                return condition();
            }

            Thread.Sleep(every);
        }

        return true;
    }
}
