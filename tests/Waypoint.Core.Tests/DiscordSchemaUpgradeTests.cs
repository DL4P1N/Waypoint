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

    [Fact]
    public void A_database_from_before_tries_were_kept_starts_counting_them_when_opened()
    {
        var before = new WaypointDatabase(_path);
        Run(before, "DROP TABLE discord_message_tries");

        var after = Reopened();
        Assert.Equal(0, after.DiscordMessageTries("made-up-post", "made-up-message"));
        Assert.Equal(1, after.AddDiscordMessageTry("made-up-post", "made-up-message", Morning));
        Assert.Equal(2, after.AddDiscordMessageTry("made-up-post", "made-up-message", Morning.AddMinutes(5)));
        Assert.Equal(2, Reopened().DiscordMessageTries("made-up-post", "made-up-message"));
    }

    [Fact]
    public void A_count_of_tries_is_forgotten_once_the_message_is_dealt_with_or_after_a_month()
    {
        var database = new WaypointDatabase(_path);
        database.AddDiscordMessageTry("made-up-post", "made-up-old", Morning);
        database.AddDiscordMessageTry("made-up-post", "made-up-done", Morning);
        database.ForgetDiscordMessageTries("made-up-post", "made-up-done");

        database.AddDiscordMessageTry("made-up-post", "made-up-new", Morning.AddDays(31));

        Assert.Equal(0, database.DiscordMessageTries("made-up-post", "made-up-old"));
        Assert.Equal(0, database.DiscordMessageTries("made-up-post", "made-up-done"));
        Assert.Equal(1, database.DiscordMessageTries("made-up-post", "made-up-new"));
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
