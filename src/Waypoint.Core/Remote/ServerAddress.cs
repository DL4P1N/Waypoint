using System.Net;
using System.Net.Sockets;

namespace Waypoint.Core.Remote;

/// <summary>
/// The rule for where the PC may send the server token, the same one the server applies to what it listens on: plain
/// http only to this machine or Tailscale (127.0.0.0/8, ::1, 100.64.0.0/10, fd7a:115c:a1e0::/48), https anywhere.
/// </summary>
public static class ServerAddress
{
    /// <summary>Why the token mustn't be sent to <paramref name="address"/>, or null when it may be.</summary>
    public static string? Problem(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)
            || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "The server address isn't a web address starting with http:// or https://, so Waypoint won't send anything to it.";
        }

        if (uri.Scheme == Uri.UriSchemeHttps || IsThisMachineOrTailscale(uri) || NeedsLookup(uri) || IsTailnetName(uri.Host))
        {
            // A host name over plain http is allowed here and checked on every send by the handler from Client.
            return null;
        }

        return "The server address uses plain http to a place that isn't this PC or Tailscale (100.64.0.0/10 or fd7a:115c:a1e0::/48), "
            + "so anyone on the way could read the pairing token. Waypoint won't send it there. Use the Tailscale address or an https:// address.";
    }

    /// <summary>A host name, not .ts.net, over plain http: allowed only while it resolves wholly into Tailscale's ranges.</summary>
    private static bool NeedsLookup(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp
        && uri.HostNameType == UriHostNameType.Dns
        && !IsTailnetName(uri.Host)
        && !uri.IsLoopback;

    private static bool IsTailnetName(string host) => host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A client for the server. Where the address is a plain http host name, every send first checks, off the calling
    /// thread, that the name resolves only to this machine or Tailscale addresses, so the token never goes anywhere
    /// else, and a name that can't be looked up just now sends nothing and is tried again on the next send.
    /// </summary>
    public static HttpClient Client(Uri baseAddress, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null) =>
        NeedsLookup(baseAddress)
            ? new HttpClient(new LookupGuard(resolve ?? ((host, token) => Dns.GetHostAddressesAsync(host, token)))) { BaseAddress = baseAddress }
            : new HttpClient { BaseAddress = baseAddress };

    private sealed class LookupGuard(Func<string, CancellationToken, Task<IPAddress[]>> resolve) : DelegatingHandler(new HttpClientHandler())
    {
        private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(5);
        private readonly Dictionary<string, DateTime> _good = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            bool known;
            lock (_good)
            {
                known = _good.TryGetValue(host, out var until) && until > DateTime.UtcNow;
            }

            if (!known)
            {
                IPAddress[] found;
                try
                {
                    // Off the caller's thread, since a name lookup can be slow and some resolvers block.
                    found = await Task.Run(() => resolve(host, cancellationToken), cancellationToken).ConfigureAwait(false);
                }
                catch (System.Net.Sockets.SocketException)
                {
                    found = [];
                }

                if (found.Length == 0)
                {
                    throw new HttpRequestException($"Waypoint can't look up {host} right now, so it sent nothing. It will try again.");
                }

                if (!found.All(IsThisMachineOrTailscale))
                {
                    throw new HttpRequestException($"{host} points somewhere that isn't this PC or Tailscale, so Waypoint won't send the pairing token there over plain http. Use the Tailscale address or an https:// address.");
                }

                lock (_good)
                {
                    _good[host] = DateTime.UtcNow + KeepFor;
                }
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsThisMachineOrTailscale(Uri uri)
    {
        if (uri.IsLoopback)
        {
            return true;
        }

        return IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IsThisMachineOrTailscale(address);
    }

    private static bool IsThisMachineOrTailscale(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] == 100 && (bytes[1] & 0xC0) == 64
            : bytes is [0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0, ..];
    }
}
