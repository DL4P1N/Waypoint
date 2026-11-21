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
    [InlineData("https://203.0.113.9:5280")]
    [InlineData("  http://100.101.102.103:5280  ")]
    public void AllowsLoopbackTailscaleAndHttps(string address) => Assert.Null(ServerAddress.Problem(address));

    [Theory]
    [InlineData("http://203.0.113.9:5280")]
    [InlineData("http://192.168.1.20:5280")]
    [InlineData("http://100.63.255.255:5280")]
    [InlineData("http://100.128.0.1:5280")]
    [InlineData("http://server.example.test")]
    [InlineData("http://[2001:db8::1]:5280")]
    [InlineData("ftp://100.64.0.1")]
    [InlineData("100.64.0.1:5280")]
    [InlineData("")]
    [InlineData(null)]
    public void RefusesEverythingElse(string? address) => Assert.NotNull(ServerAddress.Problem(address));
}
