using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Platform;

namespace Waypoint.Server.Tests;

public sealed class ServerSetupTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-token-{Guid.NewGuid()}");

    public ServerSetupTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    /// <summary>Keeps every line the server logs, to check what ends up in the journal.</summary>
    private sealed class KeptLog<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    [Fact]
    public void A_new_pairing_token_goes_to_an_owner_only_file_and_never_to_the_log()
    {
        var log = new KeptLog<WaypointServer>();
        var options = Options.Create(new ServerOptions { DataDirectory = _folder, RunBackground = false });
        var server = new WaypointServer(options, new ServerHost(NullLogger<ServerHost>.Instance), new ReplyLog(), new WorkerParts(path => new FileTokenStore(path), ConnectGateway: false), log);
        var file = Path.Combine(_folder, ServerToken.PairingFileName);

        var token = File.ReadAllText(file).Trim();

        Assert.True(server.Token.Matches(token));
        Assert.DoesNotContain(log.Lines, line => line.Contains(token));
        Assert.Contains(log.Lines, line => line.Contains(file));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
    }

    [Fact]
    public void The_pairing_file_is_removed_and_logged_once_a_PC_has_paired()
    {
        var log = new KeptLog<WaypointServer>();
        var options = Options.Create(new ServerOptions { DataDirectory = _folder, RunBackground = false });
        var server = new WaypointServer(options, new ServerHost(NullLogger<ServerHost>.Instance), new ReplyLog(), new WorkerParts(path => new FileTokenStore(path), ConnectGateway: false), log);
        var file = Path.Combine(_folder, ServerToken.PairingFileName);
        Assert.True(File.Exists(file));

        server.Paired();

        Assert.False(File.Exists(file));
        Assert.Contains(log.Lines, line => line.Contains("removed") && line.Contains(file));
        server.Paired();
    }

    /// <summary>The heartbeat's address can hold a secret, so HttpClient's per-request lines, which show it, aren't logged.</summary>
    [Fact]
    public void The_http_client_does_not_log_each_request_address()
    {
        using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Waypoint:DataDirectory", _folder);
            builder.UseSetting("Waypoint:RunBackground", "false");
        });
        var loggers = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ILoggerFactory>(factory.Services);

        foreach (var category in new[] { "System.Net.Http.HttpClient.Default.LogicalHandler", "System.Net.Http.HttpClient.Default.ClientHandler" })
        {
            var logger = loggers.CreateLogger(category);
            Assert.False(logger.IsEnabled(LogLevel.Information));
            Assert.True(logger.IsEnabled(LogLevel.Warning));
        }
    }

    private sealed class DiscordDown : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
    }

    /// <summary>
    /// A bad bot token or a Discord outage used to leave the heartbeat pinging, since only the alert checks counted.
    /// Sends failing for more than ten minutes now hold it back, and /health says so too.
    /// </summary>
    [Fact]
    public void Discord_failing_for_ten_minutes_makes_the_server_unhealthy()
    {
        File.WriteAllText(Path.Combine(_folder, "settings.json"), """{ "discord": { "botToken": "token", "ownerUserId": "123456789012345678", "alertsChannelId": "223456789012345678" } }""");
        var options = Options.Create(new ServerOptions { DataDirectory = _folder, RunBackground = false });
        var parts = new WorkerParts(path => new FileTokenStore(path), Http: () => new HttpClient(new DiscordDown()), CaptureSorter: new FlakySorter(), ConnectGateway: false);
        var server = new WaypointServer(options, new ServerHost(NullLogger<ServerHost>.Instance), new ReplyLog(), parts, new KeptLog<WaypointServer>());
        server.Worker.StartDiscord();
        var failed = new DateTime(2026, 11, 13, 9, 0, 0);
        server.Worker.DiscordHealth.Sent(false, failed);

        server.LastCheck = failed.AddMinutes(5);
        Assert.True(server.IsHealthy(failed.AddMinutes(5), runsBackground: true));

        var later = failed.AddMinutes(11);
        server.LastCheck = later;
        Assert.False(server.IsHealthy(later, runsBackground: true));
        Assert.Contains("Discord", server.Problem(later, runsBackground: true));

        server.LastCheck = later.AddMinutes(-3);
        Assert.Equal("Reminders on the server have stopped running.", server.Problem(later, runsBackground: true));
        Assert.Null(server.Problem(later, runsBackground: false));
    }

    [Fact]
    public void A_token_is_made_once_and_only_its_hash_is_kept()
    {
        var token = ServerToken.LoadOrCreate(_folder, out var created);
        var again = ServerToken.LoadOrCreate(_folder, out var createdAgain);

        Assert.NotNull(created);
        Assert.Null(createdAgain);
        Assert.True(token.Matches(created));
        Assert.True(again.Matches(created));
        Assert.DoesNotContain(created, File.ReadAllText(Path.Combine(_folder, ServerToken.FileName)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("guess")]
    public void Anything_but_the_token_is_refused(string? presented)
    {
        var token = ServerToken.LoadOrCreate(_folder, out _);

        Assert.False(token.Matches(presented));
    }

    [Theory]
    [InlineData("http://0.0.0.0:5280")]
    [InlineData("http://*:5280")]
    [InlineData("http://+:5280")]
    [InlineData("http://[::]:5280")]
    [InlineData("http://[::0]:5280")]
    [InlineData("http://[0:0::0]:5280")]
    [InlineData("http://[0000:0000:0000:0000:0000:0000:0000:0000]:5280")]
    [InlineData("http://[::ffff:0.0.0.0]:5280")]
    [InlineData("http://0:5280")]
    [InlineData("http://0.0:5280")]
    [InlineData("http://127.0.0.1:5280;http://0:5281")]
    public void Every_network_at_once_is_refused_unless_asked_for(string listen)
    {
        Assert.Throws<InvalidOperationException>(() => new ServerOptions { Listen = listen }.CheckedListen());
        // Every network includes the public ones, so plain HTTP there needs both settings.
        Assert.Throws<InvalidOperationException>(() => new ServerOptions { Listen = listen, AllowAllInterfaces = true }.CheckedListen());
        Assert.Equal(listen, new ServerOptions { Listen = listen, AllowAllInterfaces = true, AllowPublicHttp = true }.CheckedListen());
    }

    [Theory]
    [InlineData("http://waypoint-server:5280")]
    [InlineData("http://waypoint-server.tail1234.ts.net:5280")]
    public void A_host_name_is_refused_since_it_would_listen_on_every_network(string listen)
    {
        Assert.Throws<InvalidOperationException>(() => new ServerOptions { Listen = listen }.CheckedListen());
        Assert.Throws<InvalidOperationException>(() => new ServerOptions { Listen = listen, AllowAllInterfaces = true }.CheckedListen());
        Assert.Equal(listen, new ServerOptions { Listen = listen, AllowAllInterfaces = true, AllowPublicHttp = true }.CheckedListen());
    }

    [Theory]
    [InlineData(ServerOptions.DefaultListen)]
    [InlineData("http://localhost:5280")]
    [InlineData("http://[fd7a:115c:a1e0::1]:5280")]
    [InlineData("http://100.101.102.103:5280")]
    public void This_machine_or_the_tailscale_address_is_fine(string listen)
    {
        var options = new ServerOptions { Listen = listen };

        Assert.Equal(listen, options.CheckedListen());
        Assert.Empty(options.PublicHttpAddresses());
        Assert.Null(options.PublicHttpWarning());
    }

    [Theory]
    [InlineData("http://127.0.0.2:5280")]
    [InlineData("http://[::1]:5280")]
    [InlineData("http://[::ffff:127.0.0.1]:5280")]
    [InlineData("http://100.64.0.1:5280")]
    [InlineData("http://100.127.255.254:5280")]
    [InlineData("http://[fd7a:115c:a1e0:ab12::5]:5280")]
    public void Loopback_and_the_whole_tailscale_range_are_fine_over_http(string listen)
    {
        Assert.Equal(listen, new ServerOptions { Listen = listen }.CheckedListen());
    }

    [Theory]
    [InlineData("http://192.168.1.20:5280")]
    [InlineData("http://10.0.0.5:5280")]
    [InlineData("http://203.0.113.7:5280")]
    [InlineData("http://100.63.255.255:5280")]
    [InlineData("http://100.128.0.1:5280")]
    [InlineData("http://[2001:db8::7]:5280")]
    [InlineData("http://[fd7a:115c:a1e1::1]:5280")]
    [InlineData("http://127.0.0.1:5280;http://192.168.1.20:5281")]
    public void A_home_network_or_public_address_over_http_is_refused_unless_asked_for(string listen)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => new ServerOptions { Listen = listen }.CheckedListen());
        Assert.Contains("Waypoint__AllowPublicHttp=true", refused.Message);
        Assert.Null(new ServerOptions { Listen = listen }.PublicHttpWarning());

        var allowed = new ServerOptions { Listen = listen, AllowPublicHttp = true };
        Assert.Equal(listen, allowed.CheckedListen());
        Assert.Contains("plain HTTP", allowed.PublicHttpWarning());
    }

    [Theory]
    [InlineData("https://192.168.1.20:5280")]
    [InlineData("https://203.0.113.7:5280")]
    public void A_home_network_or_public_address_over_https_is_fine(string listen)
    {
        var options = new ServerOptions { Listen = listen };

        Assert.Equal(listen, options.CheckedListen());
        Assert.Null(options.PublicHttpWarning());
    }

    [Fact]
    public void Work_handed_to_the_server_runs_in_the_order_it_came()
    {
        var host = new ServerHost(NullLogger<ServerHost>.Instance);
        var order = new List<int>();
        using var done = new CountdownEvent(20);

        // The earlier ones take longer, so anything run side by side would finish out of order.
        for (var index = 0; index < 20; index++)
        {
            var number = index;
            host.Post(() =>
            {
                Thread.Sleep(20 - number);
                lock (order)
                {
                    order.Add(number);
                }

                done.Signal();
            });
        }

        // A failure is logged and doesn't stop what comes after it.
        host.Post(() => throw new InvalidOperationException("broken"));
        var after = new ManualResetEventSlim();
        host.Post(after.Set);

        Assert.True(done.Wait(InMemoryServer.Background));
        Assert.True(after.Wait(InMemoryServer.Background));
        Assert.Equal(Enumerable.Range(0, 20), order);
    }

    /// <summary>Fails the way the claude CLI does, until it's told to work.</summary>
    private sealed class FlakySorter : ICaptureSorter
    {
        public string? Error { get; set; } = "Claude Code exited with code 1. Invalid API key sk-ant-api03-abcdefghijklmnop\nsecond line with more detail";

        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default) =>
            Error is { } error
                ? throw new InvalidOperationException(error)
                : Task.FromResult<CaptureResult?>(new CaptureResult([new SortedItem(ItemKind.Note, text, null, null)]));
    }

    [Fact]
    public async Task A_sorting_failure_is_logged_once_without_what_was_typed_or_the_key()
    {
        var log = new KeptLog<WaypointServer>();
        var sorter = new FlakySorter();
        var options = Options.Create(new ServerOptions { DataDirectory = _folder, RunBackground = false });
        var server = new WaypointServer(options, new ServerHost(NullLogger<ServerHost>.Instance), new ReplyLog(), new WorkerParts(path => new FileTokenStore(path), CaptureSorter: sorter, ConnectGateway: false), log);
        log.Lines.Clear();

        server.Worker.Captures.Capture("pick up the dry cleaning at 5");
        await server.Worker.Captures.ProcessPendingAsync();
        await server.Worker.Captures.ProcessPendingAsync();

        var line = Assert.Single(log.Lines);
        Assert.Contains("Sorting failed: Claude Code exited with code 1.", line);
        Assert.DoesNotContain("dry cleaning", line);
        Assert.DoesNotContain("sk-ant", line);
        Assert.DoesNotContain("second line", line);

        // The PC's status line still gets the whole error.
        Assert.Contains("second line", server.Worker.Captures.LastError);

        // A different error is news, and so is the same one again once sorting has worked in between.
        sorter.Error = "Claude Code took too long to respond.";
        await server.Worker.Captures.ProcessPendingAsync();
        Assert.Equal(2, log.Lines.Count);

        sorter.Error = null;
        await server.Worker.Captures.ProcessPendingAsync();
        sorter.Error = "Claude Code took too long to respond.";
        server.Worker.Captures.Capture("call the landlord");
        await server.Worker.Captures.ProcessPendingAsync();
        Assert.Equal(3, log.Lines.Count);
    }
}
