namespace Waypoint.Core.Platform;

/// <summary>
/// Reminders use local time, so a server has to be in the user's zone. This reads a zone by either kind of name,
/// such as America/Chicago or Central Standard Time, on Windows and Linux alike.
/// </summary>
public static class TimeZones
{
    /// <summary>The machine's own zone, which is used unless Waypoint__TimeZone names another.</summary>
    public static string Default => TimeZoneInfo.Local.Id;

    public static TimeZoneInfo? Find(string id)
    {
        if (TryFind(id) is { } zone)
        {
            return zone;
        }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId) && TryFind(windowsId) is { } fromIana)
        {
            return fromIana;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var ianaId) && TryFind(ianaId) is { } fromWindows ? fromWindows : null;
    }

    /// <summary>True when both name the same zone, whichever kind of name each one is. Compared by Windows name, which several IANA names share.</summary>
    public static bool SameZone(TimeZoneInfo zone, string id) =>
        Find(id) is { } wanted && WindowsName(wanted.Id) == WindowsName(zone.Id);

    /// <summary>A line for the log when this machine's clock isn't in the zone the user lives in, or null when it is.</summary>
    public static string? Warning(TimeZoneInfo local, string wanted) =>
        Find(wanted) is null ? $"The time zone {wanted} isn't known on this system."
        : SameZone(local, wanted) ? null
        : $"This system is set to {local.Id}, but reminders are meant for {wanted}. Set the system time zone, for example with timedatectl set-timezone {wanted}.";

    /// <summary>
    /// The same, also checking the system's own zone. The service can set TZ for itself, which makes its clock look
    /// right while the system, and with it the backup timer and the journal, is in another zone.
    /// </summary>
    public static string? Warning(TimeZoneInfo local, string wanted, string? systemZone) =>
        Warning(local, wanted)
        ?? (systemZone is null || Find(wanted) is null || Find(systemZone) is not { } system || SameZone(system, wanted) ? null
            : $"The system time zone is {systemZone}, but reminders are meant for {wanted}. Waypoint uses {wanted}, but the backup timer and the log follow the system. Set it with timedatectl set-timezone {wanted}.");

    /// <summary>The system's zone on Linux, from where /etc/localtime points, or null when it can't be told.</summary>
    public static string? SystemZone(string localtime = "/etc/localtime")
    {
        try
        {
            return new FileInfo(localtime).LinkTarget is { } target ? ZoneFromLink(target) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>America/Chicago from a link such as /usr/share/zoneinfo/America/Chicago.</summary>
    public static string? ZoneFromLink(string target)
    {
        const string Marker = "zoneinfo/";
        var at = target.IndexOf(Marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var zone = target[(at + Marker.Length)..];
        foreach (var prefix in new[] { "posix/", "right/" })
        {
            if (zone.StartsWith(prefix, StringComparison.Ordinal))
            {
                zone = zone[prefix.Length..];
            }
        }

        return zone.Length > 0 ? zone : null;
    }

    private static string WindowsName(string id) =>
        TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId) ? windowsId : id;

    private static TimeZoneInfo? TryFind(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }
}
