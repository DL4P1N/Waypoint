using Microsoft.Data.Sqlite;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>A database from before what Discord keeps grew is brought up to date when it's opened, with what it held kept.</summary>
public sealed class DiscordSchemaUpgradeTests : IDisposable
{
    private static readonly DateTime Morning = new(2026, 11, 15, 9, 0, 0);
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
        File.Delete(_path + "-wal");
        File.Delete(_path + "-shm");
    }

    [Fact]
    public void A_send_noted_before_notices_kept_what_they_say_is_kept_and_can_keep_it_now()
    {
        var before = new WaypointDatabase(_path);
        before.BeginDiscordSend("notice:made-up-post:abc", "made-up-post", Morning);
        Run(before, "ALTER TABLE discord_sends DROP COLUMN card");

        var after = Reopened();
        Assert.Null(after.GetDiscordSendCard("notice:made-up-post:abc"));
        after.SetDiscordSendCard("notice:made-up-post:abc", "made up words");
        Assert.Equal("made up words", after.GetDiscordSendCard("notice:made-up-post:abc"));
        Assert.Equal("made-up-post", after.GetDiscordSend("notice:made-up-post:abc")!.Channel);
    }

    private WaypointDatabase Reopened()
    {
        SqliteConnection.ClearAllPools();
        return new WaypointDatabase(_path);
    }

    private static void Run(WaypointDatabase database, string sql)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
