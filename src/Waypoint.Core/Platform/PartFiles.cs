namespace Waypoint.Core.Platform;

/// <summary>
/// Files written under a working name and renamed once whole, such as a copy or an upload ending in <c>.part</c>.
/// One left by a crash or a cut-off copy is never finished, so it's swept away instead of taking up space for good.
/// </summary>
public static class PartFiles
{
    /// <summary>
    /// A working file not written to for this long isn't being written anymore. Anything still copying writes far more
    /// often, so a sweep never takes one that's in use.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// Deletes the files directly in <paramref name="folder"/> whose names <paramref name="isPart"/> says are working
    /// files, and that haven't been written to for <paramref name="olderThan"/>. Returns how many went. One that can't
    /// be deleted now is left for the next sweep.
    /// </summary>
    public static int SweepStale(string folder, Func<string, bool> isPart, TimeSpan olderThan, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var removed = 0;
        try
        {
            if (!Directory.Exists(folder))
            {
                return 0;
            }

            foreach (var path in Directory.EnumerateFiles(folder))
            {
                if (isPart(Path.GetFileName(path)) && now - File.GetLastWriteTimeUtc(path) >= olderThan && TryDelete(path))
                {
                    removed++;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The folder can't be read now. The next sweep tries again.
        }

        return removed;
    }

    /// <summary>True for a name ending in <paramref name="suffix"/>, such as ".part".</summary>
    public static Func<string, bool> EndingIn(string suffix) => name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Deletes a working file that won't be finished. False when it's still held, so it's left for a sweep.</summary>
    public static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
