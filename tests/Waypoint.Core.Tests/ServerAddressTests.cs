using System.Net;
using Waypoint.Core.Remote;

namespace Waypoint.Core.Tests;

/// <summary>Where the PC may send the server token: plain http only to this machine or Tailscale, https anywhere.</summary>
public sealed class ServerAddressTests
{
    [Theory]
    [InlineData("http://localhost:5280")]
    [InlineData("http://127.0.0.1:5280")]
    [InlineData("http://[::1]:5280")]
    [InlineData("http://100.64.0.1:5280")]
    [InlineData("http://100.127.255.254:5280/")]
    [InlineData("http://[fd7a:115c:a1e0::1]:5280")]
    [InlineData("https://server.example.test")]
    [InlineData("http://optiplex:5280")]
    [InlineData("http://optiplex.tailnet-name.ts.net:5280")]
    [InlineData("http://server.example.test")]
    [InlineData("https://203.0.113.9:5280")]
    [InlineData("  http://100.101.102.103:5280  ")]
    public void AllowsLoopbackTailscaleAndHttps(string address) => Assert.Null(ServerAddress.Problem(address));

    [Theory]
    [InlineData("http://203.0.113.9:5280")]
    [InlineData("http://192.168.1.20:5280")]
    [InlineData("http://100.63.255.255:5280")]
    [InlineData("http://100.128.0.1:5280")]
    [InlineData("http://[2001:db8::1]:5280")]
    [InlineData("ftp://100.64.0.1")]
    [InlineData("100.64.0.1:5280")]
    [InlineData("")]
    [InlineData(null)]
    public void RefusesEverythingElse(string? address) => Assert.NotNull(ServerAddress.Problem(address));

    private static Task<HttpResponseMessage> Send(HttpClient client) => client.GetAsync("api/status");

    private static HttpClient Guarded(string url, Func<string, CancellationToken, Task<IPAddress[]>> resolve) => ServerAddress.Client(new Uri(url), resolve);

    [Fact]
    public async Task A_name_that_resolves_outside_Tailscale_never_gets_the_token()
    {
        var client = Guarded("http://optiplex:5280/", (_, _) => Task.FromResult(new[] { IPAddress.Parse("100.100.1.2"), IPAddress.Parse("203.0.113.9") }));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Send(client));

        Assert.Contains("won't send the pairing token", error.Message);
    }

    [Fact]
    public async Task A_name_that_cannot_be_looked_up_sends_nothing_and_says_so_plainly()
    {
        var client = Guarded("http://optiplex:5280/", (_, _) => throw new System.Net.Sockets.SocketException());

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Send(client));

        Assert.Contains("can't look up optiplex right now", error.Message);
    }

    [Fact]
    public async Task A_name_inside_Tailscale_passes_the_check_and_is_looked_up_once()
    {
        var lookups = 0;
        var client = Guarded("http://optiplex:5280/", (_, _) =>
        {
            Interlocked.Increment(ref lookups);
            return Task.FromResult(new[] { IPAddress.Parse("127.0.0.1"), IPAddress.Parse("100.100.1.2") });
        });
        client.Timeout = TimeSpan.FromSeconds(2);

        // Past the check it tries the real network, which has nothing here, so only that failure is expected.
        for (var i = 0; i < 2; i++)
        {
            var error = await Record.ExceptionAsync(() => Send(client));
            Assert.DoesNotContain("pairing token", error?.Message ?? "");
            Assert.DoesNotContain("can't look up", error?.Message ?? "");
        }

        Assert.Equal(1, lookups);
    }

    /// <summary>The message of an error and of what's inside it, since the connection's own error comes wrapped.</summary>
    private static string Said(Exception? error) =>
        error is null ? "" : $"{error.Message} {Said(error.InnerException)}";

    [Fact]
    public async Task A_tailnet_name_that_points_outside_Tailscale_never_gets_the_token()
    {
        var client = Guarded("http://optiplex.tailnet-name.ts.net:5280/", (_, _) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.9") }));
        // The check turns it away before any connection, so the limit is only for a hung test. A short one could run
        // out first on a busy machine and hide the check's own answer.
        client.Timeout = Waits.Hung;

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Send(client));

        Assert.Contains("won't send the pairing token", Said(error));
    }

    [Theory]
    [InlineData("100.101.2.3", "100.100.1.2", true)]
    [InlineData("127.0.0.1", "127.0.0.1", true)]
    [InlineData("::1", "::1", true)]
    [InlineData("::ffff:100.101.2.3", "::ffff:100.100.1.2", true)]
    [InlineData("fd7a:115c:a1e0::5", "fd7a:115c:a1e0::9", true)]
    // Tailscale down: the Tailscale address was reached out the default route, from the home network.
    [InlineData("192.168.1.20", "100.100.1.2", false)]
    [InlineData("::ffff:192.168.1.20", "::ffff:100.100.1.2", false)]
    [InlineData("100.101.2.3", "203.0.113.9", false)]
    public void A_connection_carries_the_token_only_when_both_ends_are_this_PC_or_Tailscale(string local, string remote, bool vetted) =>
        Assert.Equal(vetted, ServerAddress.IsVetted(new IPEndPoint(IPAddress.Parse(local), 50000), new IPEndPoint(IPAddress.Parse(remote), 5280)));

    [Fact]
    public async Task Plain_http_to_this_PC_still_goes_through_the_checked_connection()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var answered = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            var stream = accepted.GetStream();
            var buffer = new byte[4096];
            var heard = "";
            while (!heard.Contains("\r\n\r\n"))
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }

                heard += System.Text.Encoding.ASCII.GetString(buffer, 0, read);
            }

            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
            return heard;
        });
        using var client = ServerAddress.Client(new Uri($"http://127.0.0.1:{port}/"));
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "made-up-token");

        using var response = await Send(client);

        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        Assert.Contains("made-up-token", await answered);
    }
}
