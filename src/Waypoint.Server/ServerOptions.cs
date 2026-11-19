using System.Net;

namespace Waypoint.Server;

/// <summary>
/// The server's own settings, from the Waypoint section of configuration. On Linux they come from environment
/// variables in /etc/waypoint/waypoint.env, written as Waypoint__Listen=http://100.101.102.103:5280 and so on.
/// Personal settings such as Discord stay in settings.json in the data folder, the same file the PC app uses.
/// </summary>
public sealed class ServerOptions
{
    public const string Section = "Waypoint";
    public const string DefaultListen = "http://127.0.0.1:5280";

    /// <summary>Where waypoint.db, settings.json, and the pairing token live.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>
    /// The address to listen on. Set it to the server's Tailscale address. It's this machine only until then,
    /// and every network at once is refused unless <see cref="AllowAllInterfaces"/> says otherwise.
    /// </summary>
    public string Listen { get; set; } = DefaultListen;

    public bool AllowAllInterfaces { get; set; }

    /// <summary>
    /// Allows plain HTTP on an address that isn't this machine or Tailscale, such as a home network or public address.
    /// Off, the server refuses to start there, since anyone who could reach it would see what's sent, the pairing
    /// token included. Every network at once over HTTP counts too, so it needs this as well as <see cref="AllowAllInterfaces"/>.
    /// </summary>
    public bool AllowPublicHttp { get; set; }

    /// <summary>The largest request body for every route but the one that keeps a file with a note, which has its own limit.</summary>
    public long MaxRequestBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>A link pinged every few minutes while reminders are running, such as a healthchecks.io check. Off when empty.</summary>
    public string? HeartbeatUrl { get; set; }

    public int HeartbeatMinutes { get; set; } = 5;

    /// <summary>The zone reminders are meant for. The server warns if the system clock is set to another one.</summary>
    public string TimeZone { get; set; } = Core.Platform.TimeZones.Default;

    /// <summary>Off only in tests, which check the API without reminders going out underneath them.</summary>
    public bool RunBackground { get; set; } = true;

    /// <summary>
    /// Whether a restore ends the process so systemd starts it again on the restored data. Off only in tests, where the
    /// server stays up after a restore but with everything that writes stopped and changes turned away.
    /// </summary>
    public bool RestartAfterRestore { get; set; } = true;

    /// <summary>The data folder, made if needed. Never the PC app's own folder, so a trial run on Windows can't touch it.</summary>
    public string ResolveDataDirectory()
    {
        var directory = DataDirectory
            ?? Environment.GetEnvironmentVariable("WAYPOINT_DATA_DIR")
            ?? (OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Waypoint Server")
                : "/var/lib/waypoint");
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// The listen address, checked. Throws for a wildcard address, and for a host name, since Kestrel listens on
    /// every network for any name but localhost. Either is allowed only when that was asked for outright.
    /// </summary>
    public string CheckedListen()
    {
        foreach (var address in Addresses())
        {
            var (host, literal) = Host(address);
            var wildcard = host is "*" or "+" || (literal is not null && IsEveryNetwork(literal));
            var named = !host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && literal is null;
            if ((wildcard || named) && !AllowAllInterfaces)
            {
                throw new InvalidOperationException(
                    $"Waypoint won't listen on every network ({address}). Set Waypoint__Listen to the Tailscale IP address (from tailscale ip -4), not a name, or set Waypoint__AllowAllInterfaces=true if that's really meant.");
            }
        }

        if (PublicHttpAddresses() is [var first, ..] && !AllowPublicHttp)
        {
            throw new InvalidOperationException(
                $"Waypoint won't listen over plain HTTP on {first}, which isn't this machine or a Tailscale address (100.64.0.0/10 or fd7a:115c:a1e0::/48), since anyone who can reach it would see what's sent, the pairing token included. Set Waypoint__Listen to the Tailscale IP address (from tailscale ip -4), or set Waypoint__AllowPublicHttp=true if that's really meant.");
        }

        return Listen;
    }

    /// <summary>
    /// The listen addresses that take plain HTTP somewhere other than this machine or Tailscale: a home network or
    /// public address, every network at once, or a host name, which Kestrel takes to mean every network.
    /// </summary>
    public IReadOnlyList<string> PublicHttpAddresses() =>
        Addresses()
            .Where(address => !address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .Where(address => Host(address) switch
            {
                (var host, null) => !host.Equals("localhost", StringComparison.OrdinalIgnoreCase),
                (_, var literal) => !IsThisMachineOrTailscale(literal),
            })
            .ToList();

    /// <summary>Said in the log at startup and by /health while plain HTTP is allowed outside this machine and Tailscale.</summary>
    public string? PublicHttpWarning() =>
        AllowPublicHttp && PublicHttpAddresses().Count > 0
            ? "Waypoint__AllowPublicHttp is on and the server takes plain HTTP outside this machine and Tailscale, so anyone who can reach it sees what's sent, the pairing token included. Listen on the Tailscale address instead."
            : null;

    private IEnumerable<string> Addresses() =>
        Listen.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The host part of a listen address, and the IP address it names, if it's one.</summary>
    private static (string Host, IPAddress? Literal) Host(string address)
    {
        var host = address.Split("://", 2).Last().Split('/')[0];
        host = host.StartsWith('[') ? host[..(host.IndexOf(']') + 1)] : host.Split(':')[0];
        // The address is compared by value, so spellings such as 0, [::0], and [0:0::0] are caught too.
        return (host, IPAddress.TryParse(host.Trim('[', ']'), out var ip) ? ip : null);
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
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            // 100.64.0.0/10, the shared range Tailscale gives its IPv4 addresses from.
            ? bytes[0] == 100 && (bytes[1] & 0xC0) == 64
            // fd7a:115c:a1e0::/48, Tailscale's IPv6 range.
            : bytes is [0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0, ..];
    }

    private static bool IsEveryNetwork(IPAddress address) =>
        address.Equals(IPAddress.Any)
        || address.Equals(IPAddress.IPv6Any)
        || (address.IsIPv4MappedToIPv6 && address.MapToIPv4().Equals(IPAddress.Any));
}
