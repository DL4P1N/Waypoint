using System.Runtime.CompilerServices;

namespace Waypoint.Core.Tests;

/// <summary>
/// Clears the waypoint-* folders and files these tests leave in the temp folder when a run is stopped half way, once
/// at the start of each run. Only direct children of the temp folder named waypoint-*, and only ones untouched for a
/// day, so a run still going elsewhere is left alone. Nothing outside the temp folder is ever looked at.
/// </summary>
internal static class TestFolderSweep
{
    [ModuleInitializer]
    internal static void Run() => Sweep(Path.GetTempPath(), TimeSpan.FromDays(1), DateTime.UtcNow);

    internal static void Sweep(string temp, TimeSpan age, DateTime nowUtc)
    {
        try
        {
            var root = new DirectoryInfo(temp);
            foreach (var entry in root.EnumerateFileSystemInfos("waypoint-*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (entry.LinkTarget is not null || nowUtc - entry.LastWriteTimeUtc < age)
                    {
                        continue;
                    }

                    if (entry is DirectoryInfo directory)
                    {
                        directory.Delete(recursive: true);
                    }
                    else
                    {
                        entry.Delete();
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // In use or not ours to delete: left for the next run.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public sealed class TestFolderSweepTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"waypoint-sweeptest-{Guid.NewGuid()}");

    public TestFolderSweepTests() => Directory.CreateDirectory(_temp);

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public void Only_old_waypoint_entries_directly_in_the_folder_are_removed()
    {
        var old = Directory.CreateDirectory(Path.Combine(_temp, "waypoint-old"));
        File.WriteAllText(Path.Combine(old.FullName, "inside.txt"), "made up");
        old.LastWriteTime = DateTime.Now.AddDays(-3);
        var oldFile = Path.Combine(_temp, "waypoint-old.db");
        File.WriteAllText(oldFile, "made up");
        File.SetLastWriteTime(oldFile, DateTime.Now.AddDays(-3));
        var fresh = Directory.CreateDirectory(Path.Combine(_temp, "waypoint-fresh"));
        var other = Directory.CreateDirectory(Path.Combine(_temp, "other-old"));
        other.LastWriteTime = DateTime.Now.AddDays(-3);

        TestFolderSweep.Sweep(_temp, TimeSpan.FromDays(1), DateTime.UtcNow);

        Assert.False(old.Exists || File.Exists(oldFile) || Directory.Exists(old.FullName));
        Assert.True(Directory.Exists(fresh.FullName));
        Assert.True(Directory.Exists(other.FullName));
    }
}
