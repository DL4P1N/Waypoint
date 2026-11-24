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
    /// A client for the server. Over plain http, every connection is checked before anything is sent on it: it has to
    /// be to this machine or a Tailscale address, and go out from one, so with Tailscale down a Tailscale address
    /// reached some other way never gets the token. Where the address is a host name, not .ts.net, every send first
    /// checks, off the calling thread, that the name resolves only to such addresses, and a name that can't be looked
    /// up just now sends nothing and is tried again on the next send. No proxy is used for plain http.
    /// </summary>
    public static HttpClient Client(Uri baseAddress, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        if (baseAddress.Scheme != Uri.UriSchemeHttp)
        {
            return new HttpClient { BaseAddress = baseAddress };
        }

        var names = new Names(resolve ?? ((host, token) => Dns.GetHostAddressesAsync(host, token)));
        var vetted = new SocketsHttpHandler { UseProxy = false, ConnectCallback = names.ConnectAsync };
        return NeedsLookup(baseAddress)
            ? new HttpClient(new LookupGuard(names, vetted)) { BaseAddress = baseAddress }
            : new HttpClient(vetted) { BaseAddress = baseAddress };
    }

    /// <summary>
    /// Whether a connection made for plain http may carry the token: both ends are this machine or Tailscale. A
    /// Tailscale address reached from an address of another network went out some other way, such as the default
    /// route with Tailscale down.
    /// </summary>
    internal static bool IsVetted(EndPoint? local, EndPoint? remote) =>
        local is IPEndPoint from && remote is IPEndPoint to && IsThisMachineOrTailscale(from.Address) && IsThisMachineOrTailscale(to.Address);

    /// <summary>What names resolved to, kept a few minutes once they were found wholly inside Tailscale.</summary>
    private sealed class Names(Func<string, CancellationToken, Task<IPAddress[]>> resolve)
    {
        private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(5);
        private readonly Dictionary<string, (IPAddress[] Addresses, DateTime Until)> _good = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Where <paramref name="host"/> points: itself when it's an address, empty when it can't be looked up just now.</summary>
        public async Task<IPAddress[]> FindAsync(string host, CancellationToken cancellationToken)
        {
            if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
            {
                return [literal];
            }

            lock (_good)
            {
                if (_good.TryGetValue(host, out var kept) && kept.Until > DateTime.UtcNow)
                {
                    return kept.Addresses;
                }
            }

            IPAddress[] found;
            try
            {
                // Off the caller's thread, since a name lookup can be slow and some resolvers block.
                found = await Task.Run(() => resolve(host, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                found = [];
            }

            if (found.Length > 0 && found.All(IsThisMachineOrTailscale))
            {
                lock (_good)
                {
                    _good[host] = (found, DateTime.UtcNow + KeepFor);
                }
            }

            return found;
        }

        /// <summary>Connects only to a vetted address, and lets the connection be used only if it's vetted at both ends.</summary>
        public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            var host = context.DnsEndPoint.Host;
            var addresses = await FindAsync(host, cancellationToken).ConfigureAwait(false);
            if (addresses.Length == 0)
            {
                throw new HttpRequestException($"Waypoint can't look up {host} right now, so it sent nothing. It will try again.");
            }

            if (!addresses.All(IsThisMachineOrTailscale))
            {
                throw new HttpRequestException(Outside(host));
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                if (!IsVetted(socket.LocalEndPoint, socket.RemoteEndPoint))
                {
                    throw new HttpRequestException("Tailscale doesn't look connected on this PC, so Waypoint won't send the pairing token to the server another way over plain http. It will try again.");
                }

                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    private static string Outside(string host) =>
        $"{host} points somewhere that isn't this PC or Tailscale, so Waypoint won't send the pairing token there over plain http. Use the Tailscale address or an https:// address.";

    /// <summary>Checks a plain http host name before each send, so a name that points elsewhere says so plainly.</summary>
    private sealed class LookupGuard(Names names, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            var found = await names.FindAsync(host, cancellationToken).ConfigureAwait(false);
            if (found.Length == 0)
            {
                throw new HttpRequestException($"Waypoint can't look up {host} right now, so it sent nothing. It will try again.");
            }

            if (!found.All(IsThisMachineOrTailscale))
            {
                throw new HttpRequestException(Outside(host));
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
