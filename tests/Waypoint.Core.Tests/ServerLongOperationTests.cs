using System.Net;
using System.Text.Json;
using Waypoint.Core.Remote;

namespace Waypoint.Core.Tests;

/// <summary>
/// Restores, imports, and Discord changes can take longer than a page read is allowed to wait. They use the
/// long-wait client, and one that still runs out of time says it may have gone through rather than that nothing
/// changed, without calling the server away.
/// </summary>
public sealed class ServerLongOperationTests
{
    /// <summary>Answers every request after a wait, the way a server busy restoring or reaching Discord does.</summary>
    private sealed class SlowServer(TimeSpan wait) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(wait, cancellationToken);
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/api/backups/restore" => JsonSerializer.Serialize(new BackupRestored(true, "before-restore.db", null, true), RemoteJson.Options),
                "/api/backups" => JsonSerializer.Serialize(new BackupRequest("waypoint-backup.db"), RemoteJson.Options),
                _ => JsonSerializer.Serialize(new TextValue("Done"), RemoteJson.Options),
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    /// <summary>A page client that gives up quickly, and a long-wait client that gives up after <paramref name="longWait"/>.</summary>
    private static ServerClient Client(TimeSpan serverTakes, TimeSpan longWait)
    {
        var handler = new SlowServer(serverTakes);
        return new ServerClient(new HttpClient(handler) { BaseAddress = new Uri("http://server/"), Timeout = TimeSpan.FromMilliseconds(100) }, "token")
        {
            FileHttp = new HttpClient(handler) { BaseAddress = new Uri("http://server/"), Timeout = longWait },
        };
    }

    public static TheoryData<string> Operations => ["restore", "backup", "import", "save discord", "test discord"];

    private static object Run(ServerClient client, string operation) => operation switch
    {
        "restore" => new ServerBackups(client).Restore(new BackupChoice("waypoint-backup.db", DateTime.Now, false)),
        "backup" => new ServerBackups(client).BackUpNow(),
        "import" => new ServerBudgetData(client).ImportStatements(["Date,Amount"]),
        "save discord" => new ServerSettingsData(client).SaveDiscord(new DiscordForm("owner", "capture", null, null, false), null),
        _ => new ServerSettingsData(client).TestDiscordAsync().GetAwaiter().GetResult(),
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public void A_long_operation_waits_longer_than_a_page_read(string operation)
    {
        var client = Client(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(30));

        Assert.NotNull(Run(client, operation));
        Assert.Equal(ServerState.Online, client.State);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public void A_long_operation_that_runs_out_of_time_may_have_gone_through_and_the_server_isnt_called_away(string operation)
    {
        var client = Client(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(200));

        var timedOut = Assert.Throws<ServerTimedOutException>(() => Run(client, operation));

        Assert.Contains("It may have gone through. Check", timedOut.Message);
        Assert.Contains("before trying again", timedOut.Message);
        Assert.NotEqual(ServerState.Offline, client.State);
    }

    [Fact]
    public void A_long_operation_while_the_server_is_down_still_says_it_cant_reach_it()
    {
        var handler = new DownServer();
        var client = new ServerClient(new HttpClient(handler) { BaseAddress = new Uri("http://server/") }, "token")
        {
            FileHttp = new HttpClient(handler) { BaseAddress = new Uri("http://server/") },
        };

        Assert.Throws<ServerUnavailableException>(() => new ServerBackups(client).Restore(new BackupChoice("waypoint-backup.db", DateTime.Now, false)));
        Assert.Equal(ServerState.Offline, client.State);
    }

    private sealed class DownServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No route to host");
    }
}
