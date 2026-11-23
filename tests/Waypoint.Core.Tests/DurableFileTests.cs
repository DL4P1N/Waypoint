using Waypoint.Core.Platform;

namespace Waypoint.Core.Tests;

public sealed class DurableFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-durable-{Guid.NewGuid():N}");

    public DurableFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(_folder))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Writing_over_a_file_replaces_it_and_leaves_nothing_beside_it()
    {
        var path = Path.Combine(_folder, "kept.json");
        File.WriteAllText(path, "old");

        DurableFile.WriteAllText(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_folder));
    }

    [Fact]
    public void A_write_that_cant_be_moved_into_place_keeps_the_old_file_and_takes_its_unfinished_one_away()
    {
        var path = Path.Combine(_folder, "kept.json");
        File.WriteAllText(path, "old");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        Assert.ThrowsAny<Exception>(() => DurableFile.WriteAllBytes(path, [1, 2, 3]));

        Assert.Equal("old", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_folder));
    }

    [Fact]
    public void A_sweep_takes_only_working_files_left_alone_for_a_while()
    {
        var stale = Make("lecture.m4a.part", DateTime.UtcNow.AddHours(-2));
        var copying = Make("talk.m4a.part", DateTime.UtcNow);
        var kept = Make("lecture.part.m4a", DateTime.UtcNow.AddHours(-2));

        Assert.Equal(1, PartFiles.SweepStale(_folder, PartFiles.EndingIn(".part"), PartFiles.StaleAfter));

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(copying));
        Assert.True(File.Exists(kept));
        Assert.Equal(0, PartFiles.SweepStale(Path.Combine(_folder, "missing"), PartFiles.EndingIn(".part"), TimeSpan.Zero));
    }

    private string Make(string name, DateTime writtenUtc)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "made up");
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }
}
