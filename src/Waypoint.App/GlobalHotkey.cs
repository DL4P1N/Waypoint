using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Interop;

namespace Waypoint.App;

/// <summary>Registers Ctrl+Alt with a key system-wide, such as M for marking a recording.</summary>
public sealed class GlobalHotkey : IDisposable
{
    private static int _lastId;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;

    /// <summary>Ctrl+Alt+M, for marking a moment while recording. Registered only while a recording is going.</summary>
    public const uint VkM = 0x4D;

    /// <summary>Ctrl+Alt+Space, for asking Waypoint from anywhere.</summary>
    public const uint VkSpace = 0x20;

    /// <summary>Ctrl+Alt+R, which brings the newest corner card forward. Registered only while a card is up.</summary>
    public const uint VkR = 0x52;

    private static readonly IntPtr MessageOnlyParent = new(-3);

    private readonly HwndSource _source;
    private readonly Action _pressed;
    private readonly uint _key;

    /// <summary>Each key has its own id, so two registered at once never answer for each other.</summary>
    private readonly int _id = Interlocked.Increment(ref _lastId);
    private bool _registered;

    /// <summary>This key's id with Windows, different for every one made.</summary>
    public int Id => _id;

    public GlobalHotkey(Action pressed, uint key)
    {
        _pressed = pressed;
        _key = key;
        _source = new HwndSource(new HwndSourceParameters("WaypointHotkey") { ParentWindow = MessageOnlyParent, WindowStyle = 0 });
        _source.AddHook(OnMessage);
    }

    /// <summary>False when another app already has the keys, which isn't a problem worth stopping for.</summary>
    public bool Register()
    {
        if (_registered)
        {
            return true;
        }

        _registered = RegisterHotKey(_source.Handle, _id, ModControl | ModAlt | ModNoRepeat, _key);
        return _registered;
    }

    public void Dispose()
    {
        if (_registered)
        {
            UnregisterHotKey(_source.Handle, _id);
            _registered = false;
        }

        _source.RemoveHook(OnMessage);
        _source.Dispose();
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == _id)
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
