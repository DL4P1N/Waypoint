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

        if (uri.Scheme == Uri.UriSchemeHttps || IsThisMachineOrTailscale(uri))
        {
            return null;
        }

        return "The server address uses plain http to a place that isn't this PC or Tailscale (100.64.0.0/10 or fd7a:115c:a1e0::/48), "
            + "so anyone on the way could read the pairing token. Waypoint won't send it there. Use the Tailscale address or an https:// address.";
    }

    private static bool IsThisMachineOrTailscale(Uri uri)
    {
        if (uri.IsLoopback)
        {
            return true;
        }

        if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address))
        {
            return false;
        }

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
