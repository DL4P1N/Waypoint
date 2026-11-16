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

    /// <summary>The largest request body for every route but the one that keeps a file with a note, which has its own limit.</summary>
    public long MaxRequestBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>A link pinged every few minutes while reminders are running, such as a healthchecks.io check. Off when empty.</summary>
    public string? HeartbeatUrl { get; set; }

    public int HeartbeatMinutes { get; set; } = 5;

    /// <summary>The zone reminders are meant for. The server warns if the system clock is set to another one.</summary>
    public string TimeZone { get; set; } = Core.Platform.TimeZones.Default;

    /// <summary>Off only in tests, which check the API without reminders going out underneath them.</summary>
    public bool RunBackground { get; set; } = true;

    /// <summary>Whether a restore ends the process so systemd starts it again on the restored data. Off only in tests.</summary>
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
        foreach (var address in Listen.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var host = address.Split("://", 2).Last().Split('/')[0];
            host = host.StartsWith('[') ? host[..(host.IndexOf(']') + 1)] : host.Split(':')[0];
            // The address is compared by value, so spellings such as 0, [::0], and [0:0::0] are caught too.
            var literal = IPAddress.TryParse(host.Trim('[', ']'), out var ip) ? ip : null;
            var wildcard = host is "*" or "+" || (literal is not null && IsEveryNetwork(literal));
            var named = !host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && literal is null;
            if ((wildcard || named) && !AllowAllInterfaces)
            {
                throw new InvalidOperationException(
                    $"Waypoint won't listen on every network ({address}). Set Waypoint__Listen to the Tailscale IP address (from tailscale ip -4), not a name, or set Waypoint__AllowAllInterfaces=true if that's really meant.");
            }
        }

        return Listen;
    }

    private static bool IsEveryNetwork(IPAddress address) =>
        address.Equals(IPAddress.Any)
        || address.Equals(IPAddress.IPv6Any)
        || (address.IsIPv4MappedToIPv6 && address.MapToIPv4().Equals(IPAddress.Any));
}
