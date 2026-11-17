using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Waypoint.App;

/// <summary>Turns off Windows 11's rounded corners and uses a dark title bar. Windows and menus also show acrylic behind them.</summary>
public static class WindowStyling
{
    private const int UseImmersiveDarkMode = 20;
    private const int WindowCornerPreference = 33;
    private const int BorderColor = 34;
    private const int DoNotRound = 1;
    private const uint NoBorder = 0xFFFFFFFE;
    private const int AccentPolicyAttribute = 19;
    private const int BlurBehind = 3;
    private const int AcrylicBlurBehind = 4;

    /// <summary>
    /// The gray Windows lays over its acrylic, given as AABBGGRR. Windows 11's own acrylic pulls what's behind toward
    /// a middle gray (its luminosity layer), so a white page shows as about #939393 and a black one as about #3F3F3F
    /// rather than white and black. This gray at 58% does the same over the blur, measured on Windows 11 26H2.
    /// </summary>
    internal const uint AcrylicGray = 0x955E5E5E;

    private const int StyleIndex = -16;
    private const long ResizeFrame = 0x00040000;
    private const int NonClientLeftButtonDown = 0x00A1;
    private const int CaptureChanged = 0x0215;
    private const int SetCursorMessage = 0x0020;
    private const int MouseMove = 0x0200;
    private const int LeftButtonUp = 0x0202;
    private const int HitLeft = 10;
    private const int HitBottomRight = 17;
    private const uint KeepPlaceAndRedrawFrame = 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020 | 0x0200;
    private const uint KeepOrderAndFocus = 0x0004 | 0x0010;

    public static void Apply(Window window) =>
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            Apply(handle);
            Blur(handle);
            if (window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
            {
                ResizeWithoutFrame(window, handle);
            }
        };

    public static void Apply(IntPtr handle)
    {
        var enabled = 1;
        var corners = DoNotRound;
        DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int));
        DwmSetWindowAttribute(handle, WindowCornerPreference, ref corners, sizeof(int));
    }

    /// <summary>
    /// Puts acrylic behind a WPF window or menu, like the Start menu and right-click menus in dark mode: a heavy blur
    /// with a little grain, pulled toward a middle gray so a white page behind turns soft gray. The tint on top is the
    /// WindowBackground brush, so it's set in App.xaml with the other colors. Windows before the April 2018 update
    /// get the plain blur.
    /// </summary>
    /// <remarks>
    /// Windows 11's own backdrop (DWMWA_SYSTEMBACKDROP_TYPE) isn't used: it turns solid gray whenever its window isn't
    /// the active one, and a menu never is, so menus would never be see-through and windows only while focused.
    /// </remarks>
    public static void Blur(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (HwndSource.FromHwnd(handle) is { CompositionTarget: { } target })
        {
            target.BackgroundColor = Colors.Transparent;
        }

        var whole = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(handle, ref whole);
        var border = unchecked((int)NoBorder);
        DwmSetWindowAttribute(handle, BorderColor, ref border, sizeof(int));

        var accent = Environment.OSVersion.Version.Build >= 17134
            ? new AccentPolicy { State = AcrylicBlurBehind, GradientColor = AcrylicGray }
            : new AccentPolicy { State = BlurBehind };
        var size = Marshal.SizeOf<AccentPolicy>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, pointer, false);
            var data = new CompositionAttribute { Attribute = AccentPolicyAttribute, Data = pointer, Size = size };
            SetWindowCompositionAttribute(handle, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    /// <summary>
    /// Windows 11 paints its own solid frame color over the blur on any window with a resize frame, so this window
    /// never has one. Moving works without it. Windows' own resizing doesn't, so dragging an edge or corner is done
    /// here, and so are the resize cursors Windows only shows on windows with the frame.
    /// </summary>
    private static void ResizeWithoutFrame(Window owner, IntPtr handle)
    {
        SetResizeFrame(handle, false);
        var edge = 0;
        var start = default(NativePoint);
        var bounds = default(NativeRect);
        HwndSource.FromHwnd(handle)?.AddHook((IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            switch (message)
            {
                case NonClientLeftButtonDown when (int)wParam is >= HitLeft and <= HitBottomRight:
                    edge = (int)wParam;
                    GetCursorPos(out start);
                    GetWindowRect(window, out bounds);
                    SetCapture(window);
                    handled = true;
                    break;
                case MouseMove when edge != 0:
                    GetCursorPos(out var now);
                    Resize(owner, window, edge, bounds, now.X - start.X, now.Y - start.Y);
                    handled = true;
                    break;
                case LeftButtonUp when edge != 0:
                    edge = 0;
                    ReleaseCapture();
                    handled = true;
                    break;
                case CaptureChanged when edge != 0:
                    edge = 0;
                    break;
                case SetCursorMessage when ResizeCursor((short)(lParam.ToInt64() & 0xFFFF)) is { } cursor:
                    SetCursor(LoadCursor(IntPtr.Zero, cursor));
                    handled = true;
                    return 1;
            }

            return IntPtr.Zero;
        });
    }

    private static void Resize(Window owner, IntPtr handle, int edge, NativeRect bounds, int dx, int dy)
    {
        var scale = VisualTreeHelper.GetDpi(owner);
        var minWidth = (int)Math.Ceiling((double.IsNaN(owner.MinWidth) ? 0 : owner.MinWidth) * scale.DpiScaleX);
        var minHeight = (int)Math.Ceiling((double.IsNaN(owner.MinHeight) ? 0 : owner.MinHeight) * scale.DpiScaleY);
        var (left, top, right, bottom) = Resized(edge, (bounds.Left, bounds.Top, bounds.Right, bounds.Bottom), dx, dy, minWidth, minHeight);
        SetWindowPos(handle, IntPtr.Zero, left, top, right - left, bottom - top, KeepOrderAndFocus);
    }

    /// <summary>
    /// The window's edges after dragging <paramref name="edge"/> (a hit-test code: 10 left, 11 right, 12 top,
    /// 13 to 17 the corners and bottom) by the pointer's movement, never smaller than the minimum size.
    /// </summary>
    internal static (int Left, int Top, int Right, int Bottom) Resized(int edge, (int Left, int Top, int Right, int Bottom) bounds, int dx, int dy, int minWidth, int minHeight)
    {
        var (left, top, right, bottom) = bounds;
        if (edge is 10 or 13 or 16)
        {
            left = Math.Min(bounds.Left + dx, right - minWidth);
        }

        if (edge is 11 or 14 or 17)
        {
            right = Math.Max(bounds.Right + dx, left + minWidth);
        }

        if (edge is 12 or 13 or 14)
        {
            top = Math.Min(bounds.Top + dy, bottom - minHeight);
        }

        if (edge is 15 or 16 or 17)
        {
            bottom = Math.Max(bounds.Bottom + dy, top + minHeight);
        }

        return (left, top, right, bottom);
    }

    /// <summary>The arrow for an edge or corner, from the standard cursor ids. Null anywhere else.</summary>
    private static int? ResizeCursor(int hit) => hit switch
    {
        10 or 11 => 32644,
        12 or 15 => 32645,
        13 or 17 => 32642,
        14 or 16 => 32643,
        _ => null,
    };

    private static void SetResizeFrame(IntPtr handle, bool on)
    {
        var style = (long)GetWindowLongPtr(handle, StyleIndex);
        var wanted = on ? style | ResizeFrame : style & ~ResizeFrame;
        if (wanted == style)
        {
            return;
        }

        SetWindowLongPtr(handle, StyleIndex, (IntPtr)wanted);
        if (!on)
        {
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, KeepPlaceAndRedrawFrame);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int State;
        public int Flags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionAttribute
    {
        public int Attribute;
        public IntPtr Data;
        public int Size;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    // Undocumented, but stable since Windows 10 and what third-party blur tools use.
    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionAttribute data);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, int cursor);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
