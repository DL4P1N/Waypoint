using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Waypoint.App;

/// <summary>
/// File and folder dialogs run Windows' own loop on the window thread, so closing started while one is open would run
/// inside it and the app couldn't end until it was answered. Closing closes them first and goes ahead once they've
/// returned, the usual way, so drafts are saved as always. A dialog closed that way, or opened after closing starts,
/// is answered as cancelled, so nothing picked in it is written after closing.
/// </summary>
internal sealed class FileDialogs
{
    /// <summary>How long closing waits for an open dialog to close before it goes ahead anyway.</summary>
    private static readonly TimeSpan CloseLimit = TimeSpan.FromSeconds(5);

    /// <summary>How often an open dialog is asked to close again while closing waits.</summary>
    private static readonly TimeSpan AskAgain = TimeSpan.FromMilliseconds(250);

    private readonly Action _closeOpen;
    private readonly TimeSpan _limit;
    private int _open;
    private Action? _whenClosed;

    public FileDialogs(Action closeOpen, TimeSpan limit)
    {
        _closeOpen = closeOpen;
        _limit = limit;
    }

    /// <summary>The dialogs the app opens, all on the window thread.</summary>
    public static FileDialogs Shared { get; } = new(CloseOnThisThread, CloseLimit);

    /// <summary>Set once closing has started. No dialog opens from then on.</summary>
    public bool Closing { get; private set; }

    /// <summary>The number of dialogs open right now.</summary>
    public int Open => _open;

    /// <summary>
    /// Shows a dialog through <paramref name="show"/>, or gives <paramref name="cancelled"/> without showing it once
    /// closing has started. A dialog still open when closing starts also gives <paramref name="cancelled"/>.
    /// </summary>
    public T Show<T>(Func<T> show, T cancelled)
    {
        if (Closing)
        {
            return cancelled;
        }

        _open++;
        try
        {
            var answer = show();
            return Closing ? cancelled : answer;
        }
        finally
        {
            if (--_open == 0 && _whenClosed is not null)
            {
                // After the code that opened it has finished, back on the window thread's usual loop.
                Dispatcher.CurrentDispatcher.BeginInvoke(RunWhenClosed);
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="then"/> straight away when no dialog is open. Otherwise closes them and runs it once they
    /// have all returned, or once the limit has passed if one won't close, so closing is never held up for long.
    /// </summary>
    public void CloseThen(Action then)
    {
        Closing = true;
        if (_open == 0)
        {
            then();
            return;
        }

        if (_whenClosed is not null)
        {
            // Closing was already asked for and is waiting on the same dialogs.
            return;
        }

        _whenClosed = then;
        _closeOpen();
        var waited = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = AskAgain };
        timer.Tick += (_, _) =>
        {
            if (_whenClosed is null)
            {
                timer.Stop();
            }
            else if (waited.Elapsed >= _limit)
            {
                timer.Stop();
                RunWhenClosed();
            }
            else
            {
                _closeOpen();
            }
        };
        timer.Start();
    }

    private void RunWhenClosed()
    {
        var then = _whenClosed;
        _whenClosed = null;
        then?.Invoke();
    }

    /// <summary>
    /// Asks every dialog window on this thread to close, as pressing Esc or the close button would. File and folder
    /// dialogs take that as Cancel. Message boxes they show over themselves close the same way.
    /// </summary>
    private static void CloseOnThisThread()
    {
        EnumThreadWindows(GetCurrentThreadId(), (window, _) =>
        {
            var name = new char[16];
            var length = GetClassName(window, name, name.Length);
            if (new string(name, 0, Math.Max(length, 0)) == DialogClass)
            {
                PostMessage(window, CloseMessage, 0, 0);
            }

            return true;
        }, 0);
    }

    private const string DialogClass = "#32770";
    private const uint CloseMessage = 0x0010;

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, char[] name, int length);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
