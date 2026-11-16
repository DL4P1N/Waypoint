using System;
using System.Runtime.InteropServices;
using Waypoint.Core.Schedule;

namespace Waypoint.App;

/// <summary>
/// Time since the last keyboard or mouse input anywhere on this PC, from Windows. It's one number: which app is in
/// use is never seen, and nothing is kept. Only read during a night's late window.
/// </summary>
public sealed class WindowsIdle : IUserIdle
{
    public static WindowsIdle Instance { get; } = new();

    public TimeSpan? IdleFor(DateTime now)
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return null;
        }

        // Both are milliseconds since startup, and wrap together after 49 days.
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
