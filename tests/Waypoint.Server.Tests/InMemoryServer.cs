using Waypoint.Core.Remote;

namespace Waypoint.Server.Tests;

/// <summary>
/// The PC's side made over the server these tests run in memory. <see cref="ServerClient"/> gives up on a request
/// after <see cref="ServerClient.RequestTimeout"/>, which suits a server over the network, but here the server shares
/// the machine with every other test, and a busy machine can hold a request that long. Taken as the server being
/// away, that would fail a test that has nothing to do with waiting. Tests about the wait running out make it run out
/// on purpose, so they're unaffected.
/// </summary>
internal static class InMemoryServer
{
    /// <summary>How long a request to the in-memory server may take before the test gives up on it.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromMinutes(2);

    /// <summary>How long a test waits for something the server does in the background before failing.</summary>
    public static readonly TimeSpan Background = TimeSpan.FromSeconds(60);

    public static ServerClient Client(HttpClient http, string? token, string? clientId = null)
    {
        var client = new ServerClient(http, token, clientId);

        // Set after, since the client shortens any longer wait it's given.
        http.Timeout = Wait;
        return client;
    }
}
