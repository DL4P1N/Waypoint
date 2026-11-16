using System;
using System.IO;
using System.Runtime.InteropServices;
using Waypoint.Core.Recording;
using Forms = System.Windows.Forms;

namespace Waypoint.App;

/// <summary>
/// Tells Windows the PC is busy while a lecture records, so it doesn't fall asleep on its own. This only stops
/// idle sleep. It can't stop sleep from closing the lid or pressing the power button, and it never closes, locks,
/// or blocks anything. Windows drops the request by itself if Waypoint stops, so a crash can't leave it on.
/// Call Hold and Release from the same thread, since the request belongs to the thread that made it.
/// </summary>
public sealed class KeepAwake : IKeepAwake
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    public void Hold() => SetThreadExecutionState(EsContinuous | EsSystemRequired);

    public void Release() => SetThreadExecutionState(EsContinuous);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}

/// <summary>The battery and disk readings the recording warnings use.</summary>
public static class PowerReadings
{
    public static PowerReading Battery()
    {
        var status = Forms.SystemInformation.PowerStatus;
        if (status.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery) || status.BatteryChargeStatus == Forms.BatteryChargeStatus.Unknown)
        {
            return PowerReading.PluggedIn;
        }

        var percent = status.BatteryLifePercent is >= 0 and <= 1 ? (int?)Math.Round(status.BatteryLifePercent * 100) : null;
        return new PowerReading(status.PowerLineStatus == Forms.PowerLineStatus.Offline, percent);
    }

    /// <summary>Free space on the drive that holds the folder, or null when Windows can't say.</summary>
    public static long? FreeBytes(string folder)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(folder)) is { Length: > 0 } root ? new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
