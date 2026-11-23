using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Waypoint.App;

/// <summary>Registers Ctrl+Alt with a key system-wide, such as M for marking a recording.</summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 1;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;

    /// <summary>Ctrl+Alt+M, for marking a moment while recording. Registered only while a recording is going.</summary>
    public const uint VkM = 0x4D;

    /// <summary>Ctrl+Alt+R, which brings the newest corner card forward. Registered only while a card is up.</summary>
    public const uint VkR = 0x52;

    private static readonly IntPtr MessageOnlyParent = new(-3);

    private readonly HwndSource _source;
    private readonly Action _pressed;
    private readonly uint _key;
    private bool _registered;

    public GlobalHotkey(Action pressed, uint key)
    {
        _pressed = pressed;
        _key = key;
        _source = new HwndSource(new HwndSourceParameters("WaypointHotkey") { ParentWindow = MessageOnlyParent, WindowStyle = 0 });
        _source.AddHook(OnMessage);
    }

    public bool Register()
    {
        _registered = RegisterHotKey(_source.Handle, HotkeyId, ModControl | ModAlt | ModNoRepeat, _key);
        return _registered;
    }

    public void Dispose()
    {
        if (_registered)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
        }

        _source.RemoveHook(OnMessage);
        _source.Dispose();
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            _pressed();
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
