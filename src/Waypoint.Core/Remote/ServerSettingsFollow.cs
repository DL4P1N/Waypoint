using System.Text.Json;
using Waypoint.Core.Data;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Remote;

/// <summary>
/// In server mode the server's classes and bedtime are the ones in use, so a PC follows them instead of its own
/// settings.json. It reads them each time it syncs with the server, which covers starting up, reconnecting, and
/// a change saved from another PC. While the server is away it uses the last answer kept in the saved reads, and
/// with nothing kept yet it leaves what it has alone rather than clearing it.
/// </summary>
public static class ServerSettingsFollow
{
    /// <summary>The server's classes, or null when the server is away and they've never been read.</summary>
    public static ClassesForm? FindClasses(ServerClient server) => Find(() => server.Get<ClassesForm>(ServerSettingsData.ClassesPath));

    /// <summary>The server's bedtime, or null when the server is away and it's never been read.</summary>
    public static BedtimeSettings? FindBedtime(ServerClient server) => Find(() => server.Get<BedtimeSettings>(ServerSettingsData.BedtimePath));

    /// <summary>
    /// Puts what the server has into this PC's running schedules, changed in place so the week view, recording,
    /// the class start offer, and the wind-down follow. Returns true when anything changed.
    /// </summary>
    public static bool Apply(ClassesForm? classes, BedtimeSettings? bedtime, ClassSchedule? here, BedtimeSchedule? bedtimeHere, WaypointDatabase database)
    {
        var changed = false;
        if (classes is { Classes: not null } && here is not null)
        {
            var next = ClassSetup.FromForm(classes, here);
            if (Signature(next) != Signature(here))
            {
                here.Use(next);
                database.ScheduleCourses = here.Meetings.Select(meeting => meeting.Course).ToList();
                changed = true;
            }
        }

        if (bedtime is not null && bedtimeHere is not null
            && JsonSerializer.Serialize(bedtime, RemoteJson.Options) != JsonSerializer.Serialize(bedtimeHere.Settings, RemoteJson.Options))
        {
            bedtimeHere.Settings = bedtime;
            changed = true;
        }

        return changed;
    }

    private static string Signature(ClassSchedule schedule) =>
        JsonSerializer.Serialize(new { schedule.TermStart, schedule.TermEnd, schedule.Meetings }, RemoteJson.Options);

    private static T? Find<T>(Func<T> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception problem) when (problem is ServerUnavailableException or ServerBusyException or ServerRefusedException or JsonException)
        {
            return null;
        }
    }
}
