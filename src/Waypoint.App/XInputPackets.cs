using System;
using System.Runtime.InteropServices;

namespace Waypoint.App;

/// <summary>
/// Each game controller's packet number, from Windows' own XInput (xinput1_4.dll). The number changes whenever the
/// controller's state does. Only the number is read: never which buttons, and never which app is in use.
/// </summary>
public static class XInputPackets
{
    private const int Connected = 0;
    private static bool _missing;

    /// <summary>The packet number, or null when that controller isn't connected or XInput isn't there.</summary>
    public static uint? Read(int index)
    {
        if (_missing)
        {
            return null;
        }

        try
        {
            return XInputGetState(index, out var state) == Connected ? state.PacketNumber : null;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            _missing = true;
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [DllImport("xinput1_4.dll")]
    private static extern int XInputGetState(int userIndex, out XInputState state);
}
