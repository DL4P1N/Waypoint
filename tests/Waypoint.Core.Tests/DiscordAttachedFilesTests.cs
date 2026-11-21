using Microsoft.Data.Sqlite;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>Files added from Discord are remembered for a month, then forgotten so the list stays small.</summary>
public sealed class DiscordAttachedFilesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public DiscordAttachedFilesTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void A_file_added_over_a_month_ago_is_forgotten_and_a_recent_one_is_kept()
    {
        var start = new DateTime(2026, 10, 1, 9, 0, 0);
        _database.MarkDiscordFileAttached("made-up-old", 1, start);
        _database.MarkDiscordFileAttached("made-up-recent", 1, start.AddDays(20));

        _database.MarkDiscordFileAttached("made-up-new", 1, start.AddDays(31));

        Assert.False(_database.WasDiscordFileAttached("made-up-old"));
        Assert.True(_database.WasDiscordFileAttached("made-up-recent"));
        Assert.True(_database.WasDiscordFileAttached("made-up-new"));
    }
}
