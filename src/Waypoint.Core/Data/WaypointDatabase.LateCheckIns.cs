using System.Globalization;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Data;

/// <summary>Only when tonight's last check-in went out and any time picked to stop. Nothing about how the PC was used is kept.</summary>
public sealed partial class WaypointDatabase : ILateCheckInLog
{
    private const string LastCheckInKey = "late_checkin_last";
    private const string CheckInStopAtKey = "late_checkin_stop_at";

    public DateTime? LastCheckIn
    {
        get => ReadUtc(LastCheckInKey);
        set => WriteUtc(LastCheckInKey, value);
    }

    public DateTime? CheckInStopAt
    {
        get => ReadUtc(CheckInStopAtKey);
        set => WriteUtc(CheckInStopAtKey, value);
    }

    private DateTime? ReadUtc(string key) =>
        DateTime.TryParse(GetPreference(key), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    private void WriteUtc(string key, DateTime? value) =>
        SetPreference(key, value is { } at ? at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) : "");
}
