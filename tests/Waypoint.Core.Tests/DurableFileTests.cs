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
}
