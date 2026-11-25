using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Waypoint.App;
using Waypoint.Core.Data;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Waypoint.App.Tests;

/// <summary>
/// One STA thread with a plain <see cref="Application"/> carrying App.xaml's resources. Never constructs
/// <see cref="App"/>, since its startup signals a running Waypoint. Every test body runs on this thread.
/// Nothing here sends real OS input or moves the mouse: events are raised on the controls in process.
/// The thread and its windows live on a desktop of their own, so they never take part in the user's input.
/// </summary>
internal static class Ui
{
    private static readonly Lazy<Dispatcher> Thread = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly List<Exception> Unhandled = [];

    /// <summary>The problems raised on the UI thread so far, which a test expected, so they don't fail it.</summary>
    public static List<Exception> TakeUnhandled()
    {
        var taken = Unhandled.ToList();
        Unhandled.Clear();
        return taken;
    }

    public static void Run(Action<UiTest> body)
    {
        ExceptionDispatchInfo? failure = null;
        var taken = Foreground.Taken;
        Thread.Value.Invoke(() =>
        {
            // Work an earlier test left queued runs first, so a problem it raises late isn't put down to this test.
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Unhandled.Clear();
            var test = new UiTest();
            try
            {
                body(test);
                test.Pump();
                if (Unhandled.Count > 0)
                {
                    throw new AggregateException("Unhandled exception on the UI thread", Unhandled.ToList());
                }
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                test.Dispose();
            }
        });

        // Asked here, on the user's desktop, since the UI thread sees only its own. Checked first, as having the
        // user's foreground would also explain a test failing on what the user was doing.
        Assert.True(taken == Foreground.Taken && !Foreground.IsOurs(), "A test window took the foreground from the user and had to give it back");
        failure?.Throw();
    }

    /// <summary>The thread's entry point, kept here so it isn't collected while the thread runs.</summary>
    private static ThreadProc? _entry;

    /// <summary>
    /// Starts the UI thread on a desktop made for this run. A window on the desktop the user works on can be handed
    /// the foreground by Windows, for instance when the user's window closes or nothing is in front, and once there
    /// the thread shares in the user's real keyboard: a Ctrl or Shift the user holds in another app can show through
    /// in <see cref="Keyboard.Modifiers"/> mid-press. Windows on another desktop can never be the user's foreground
    /// or receive the user's keys, and no thread on the user's desktop can attach its input to them. The thread is
    /// made outside .NET because a desktop can only be set before a thread has a window, and an STA thread started by
    /// .NET already has one; it joins its STA once the desktop is set.
    /// </summary>
    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        Exception? problem = null;
        using var ready = new ManualResetEventSlim();
        _entry = _ =>
        {
            try
            {
                var desktop = CreateDesktop($"Waypoint UI tests {Environment.ProcessId}", null, 0, 0, DesktopAllAccess, 0);
                if (desktop == 0 || !SetThreadDesktop(desktop))
                {
                    throw new InvalidOperationException($"Couldn't make a desktop for the test windows (error {Marshal.GetLastWin32Error()})");
                }

                var thread = System.Threading.Thread.CurrentThread;
                thread.IsBackground = true;
                thread.Name = "Waypoint UI tests";
                if (!thread.TrySetApartmentState(ApartmentState.STA))
                {
                    throw new InvalidOperationException("The UI thread couldn't join an STA");
                }

                Foreground.Watch();
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources = AppResources();
                app.DispatcherUnhandledException += (_, e) =>
                {
                    Unhandled.Add(e.Exception);
                    e.Handled = true;
                };
                EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((sender, _) => UiTest.MenuOpened((ContextMenu)sender)));
                EventManager.RegisterClassHandler(typeof(RenameWindow), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => UiTest.RenameShown((RenameWindow)sender)));
                dispatcher = Dispatcher.CurrentDispatcher;
            }
            catch (Exception exception)
            {
                problem = exception;
            }
            finally
            {
                ready.Set();
            }

            if (problem is null)
            {
                Dispatcher.Run();
            }

            return 0;
        };

        // 4 MB of stack, more than .NET gives a thread of its own, so deep layouts have room.
        if (CreateThread(0, 4 << 20, Marshal.GetFunctionPointerForDelegate(_entry), 0, StackSizeIsReservation, out _) == 0)
        {
            throw new InvalidOperationException($"The UI thread didn't start (error {Marshal.GetLastWin32Error()})");
        }

        ready.Wait();
        if (problem is not null)
        {
            throw new InvalidOperationException("The UI thread didn't start", problem);
        }

        return dispatcher!;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ThreadProc(nint parameter);

    private const uint DesktopAllAccess = 0x000F01FF;
    private const uint StackSizeIsReservation = 0x00010000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateThread(nint attributes, nuint stackSize, nint start, nint parameter, uint flags, out uint id);

    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateDesktop(string name, string? device, nint mode, uint flags, uint access, nint attributes);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(nint desktop);

    /// <summary>App.xaml's resources, read from the file itself, plus two test-only overrides so nothing pops up on screen.</summary>
    private static ResourceDictionary AppResources()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "AppResources", "App.xaml");
        var xaml = File.ReadAllText(path);
        var inner = Regex.Match(xaml, @"<Application\.Resources>(?<body>.*)</Application\.Resources>", RegexOptions.Singleline).Groups["body"].Value;
        var wrapped = "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" + inner + "</ResourceDictionary>";
        var resources = (ResourceDictionary)XamlReader.Parse(wrapped);

        // Menus still open and fill their items, but draw nothing, so no popup appears over the user's screen.
        var emptyTemplate = new ControlTemplate(typeof(ContextMenu)) { VisualTree = new FrameworkElementFactory(typeof(Border)) };
        var menuStyle = new Style(typeof(ContextMenu));
        menuStyle.Setters.Add(new Setter(Control.TemplateProperty, emptyTemplate));
        menuStyle.Setters.Add(new Setter(ContextMenu.HasDropShadowProperty, true));
        menuStyle.Setters.Add(new Setter(UIElement.OpacityProperty, 0d));
        menuStyle.Setters.Add(new Setter(FrameworkElement.WidthProperty, 1d));
        menuStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, 1d));
        resources[typeof(ContextMenu)] = menuStyle;

        // The rename dialog is answered by the test as soon as it loads. Fully transparent, it can't be seen or clicked.
        var renameStyle = new Style(typeof(RenameWindow));
        renameStyle.Setters.Add(new Setter(Window.AllowsTransparencyProperty, true));
        renameStyle.Setters.Add(new Setter(UIElement.OpacityProperty, 0d));
        renameStyle.Setters.Add(new Setter(Window.ShowActivatedProperty, false));
        resources[typeof(RenameWindow)] = renameStyle;
        return resources;
    }
}

/// <summary>What one test gets: a temp database, an offscreen host window, and ways to raise input on controls.</summary>
internal sealed class UiTest : IDisposable
{
    private static UiTest? _current;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-uitest-{Guid.NewGuid()}.db");
    private readonly List<ContextMenu> _menus = [];
    private Window? _window;

    public UiTest()
    {
        _current = this;
        Database = new WaypointDatabase(_path);
    }

    public WaypointDatabase Database { get; }

    /// <summary>What the next rename dialog answers. Null cancels it.</summary>
    public string? RenameAnswer { get; set; }

    public int RenameDialogs { get; private set; }

    /// <summary>Runs while a name box is open, before it's answered, so a test can look at it as it's shown.</summary>
    public Action<RenameWindow>? RenameOpened { get; set; }

    public IReadOnlyList<ContextMenu> Menus => _menus;

    /// <summary>Hosts the control in a window far off screen that never activates, so it gets a real PresentationSource and layout.</summary>
    public T Host<T>(T content) where T : FrameworkElement
    {
        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            Width = 1400,
            Height = 900,
            Content = content,
        };
        _window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(_window).Handle;
            SetWindowLong(handle, ExStyle, GetWindowLong(handle, ExStyle) | ToolWindow);
        };
        _window.Show();
        Pump();
        return content;
    }

    /// <summary>Runs everything queued on the dispatcher, down to background work.</summary>
    public void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
    }

    public void Focus(IInputElement element)
    {
        for (var attempt = 0; attempt < 3 && !element.IsKeyboardFocused; attempt++)
        {
            Keyboard.Focus(element);
            Pump();
        }

        Assert.True(element.IsKeyboardFocused, $"Couldn't focus {element}, focus is on {Keyboard.FocusedElement}");
    }

    /// <summary>
    /// A key press with no modifiers as WPF raises it: PreviewKeyDown, then KeyDown unless handled. A handler that
    /// checks <see cref="Keyboard.Modifiers"/> reads this thread's own key state from Windows. The user's keys never
    /// reach it, since the thread is on a desktop of its own, but a test can leave a modifier showing held, so the
    /// modifiers are cleared here first.
    /// </summary>
    public bool Press(UIElement target, Key key)
    {
        SetModifiersShownHeld(false);
        var source = PresentationSource.FromVisual(target) ?? throw new InvalidOperationException("Not in a window");
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        if (!preview.Handled)
        {
            var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent };
            target.RaiseEvent(down);
            preview.Handled = down.Handled;
        }

        Pump();
        return preview.Handled;
    }

    /// <summary>Raises ContextMenuOpening as the menu key would, and returns the menu the page opened, if any.</summary>
    public ContextMenu? RightClick(UIElement target)
    {
        var before = _menus.Count;
        var ctor = typeof(ContextMenuEventArgs).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single(found => found.GetParameters().Length == 4);
        var args = (ContextMenuEventArgs)ctor.Invoke([target, true, -1d, -1d]);
        args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
        target.RaiseEvent(args);
        Pump();
        return _menus.Count > before ? _menus[^1] : null;
    }

    public static IReadOnlyList<string> Labels(ItemsControl menu) => menu.Items.OfType<MenuItem>().Select(item => (string)item.Header).ToList();

    /// <summary>Clicks a menu line and closes the menu, the way choosing it would.</summary>
    public void Choose(ContextMenu menu, params string[] path)
    {
        ItemsControl level = menu;
        MenuItem? item = null;
        foreach (var label in path)
        {
            item = level.Items.OfType<MenuItem>().Single(found => (string)found.Header == label);
            level = item;
        }

        item!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
        CloseAndWait(menu);
    }

    public void Close(ContextMenu menu)
    {
        Assert.True(menu.IsOpen, "menu was already closed");
        CloseAndWait(menu);
    }

    /// <summary>A menu raises Closed only once its fade-out has run, on a timer, so this waits for it.</summary>
    private void CloseAndWait(ContextMenu menu)
    {
        var closed = false;
        menu.Closed += (_, _) => closed = true;
        menu.IsOpen = false;
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!closed && DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        Assert.True(closed, "the menu never raised Closed");
        Pump();
    }

    /// <summary>A drag arriving over a target, then dropped on it, as DragDrop raises them. Returns the effect DragOver allowed.</summary>
    public DragDropEffects DragOnto(UIElement target, IDataObject data, bool drop = true, Point? at = null)
    {
        var point = at ?? new Point(1, 1);
        var over = Drag(target, data, DragDrop.PreviewDragOverEvent, DragDrop.DragOverEvent, point);
        if (drop)
        {
            Drag(target, data, DragDrop.PreviewDropEvent, DragDrop.DropEvent, point);
        }

        Pump();
        return over.Effects;
    }

    private static DragEventArgs Drag(UIElement target, IDataObject data, RoutedEvent preview, RoutedEvent bubble, Point point)
    {
        var ctor = typeof(DragEventArgs).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single(found => found.GetParameters().Length == 5);
        var args = (DragEventArgs)ctor.Invoke([data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, target, point]);
        args.Effects = DragDropEffects.Move;
        args.RoutedEvent = preview;
        target.RaiseEvent(args);
        if (!args.Handled)
        {
            args.RoutedEvent = bubble;
            target.RaiseEvent(args);
        }

        return args;
    }

    public static T Named<T>(FrameworkElement scope, string name) where T : class =>
        scope.FindName(name) as T ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}");

    public static object? Call(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)!.Invoke(target, args);

    public static T? Field<T>(object target, string field) =>
        (T?)target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);

    public static T? Prop<T>(object target, string property) =>
        (T?)target.GetType().GetProperty(property, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.GetValue(target);

    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }

    /// <summary>The generated container for an item, laid out so its template's parts exist.</summary>
    public FrameworkElement Container(ItemsControl list, object item)
    {
        list.UpdateLayout();
        var container = list.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement ?? throw new InvalidOperationException($"No row for {item}");
        container.ApplyTemplate();
        container.UpdateLayout();
        return container;
    }

    internal static void MenuOpened(ContextMenu menu) => _current?._menus.Add(menu);

    internal static void RenameShown(RenameWindow window)
    {
        if (_current is not { } test)
        {
            window.Close();
            return;
        }

        test.RenameDialogs++;
        test.RenameOpened?.Invoke(window);
        var box = (TextBox)window.FindName("NameBox");
        if (test.RenameAnswer is { } answer)
        {
            box.Text = answer;
            window.DialogResult = true;
        }
        else
        {
            window.DialogResult = false;
        }
    }

    public void Dispose()
    {
        foreach (var menu in _menus)
        {
            menu.IsOpen = false;
        }

        _window?.Close();
        Pump();
        _current = null;
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // Left in temp; it's harmless and the name is unique.
        }
    }

    private const int ExStyle = -20;

    /// <summary>Shift, Ctrl, Alt and the Windows keys, left, right and either.</summary>
    private static readonly byte[] Modifiers = [0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5];

    /// <summary>
    /// Sets whether this thread's own key state shows every modifier as held. It never sends input, so nothing
    /// changes for the window the user is in.
    /// </summary>
    internal static void SetModifiersShownHeld(bool held)
    {
        var state = new byte[256];
        if (!GetKeyboardState(state))
        {
            return;
        }

        foreach (var modifier in Modifiers)
        {
            state[modifier] = held ? (byte)0x80 : (byte)0;
        }

        SetKeyboardState(state);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState(byte[] state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKeyboardState(byte[] state);

    private const int ToolWindow = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLong(nint handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLong(nint handle, int index, nint value);
}

/// <summary>
/// Watches, from a thread on the user's desktop, for a window of this process becoming the user's foreground. The
/// test windows can't, being on a desktop of their own, so any time it happens is a window made somewhere else, and
/// the foreground goes straight back to the window the user was in.
/// </summary>
internal static class Foreground
{
    private static int _taken;
    private static nint _userWindow;
    private static WinEventProc? _callback;

    /// <summary>The number of times a window of this process became the user's foreground.</summary>
    public static int Taken => Volatile.Read(ref _taken);

    /// <summary>Whether the user's foreground window belongs to this process right now.</summary>
    public static bool IsOurs() => OwnedHere(GetForegroundWindow());

    public static void Watch()
    {
        var thread = new System.Threading.Thread(() =>
        {
            _callback = (_, _, _, _, _, _, _) => Check();
            SetWinEventHook(ForegroundEvent, ForegroundEvent, 0, _callback, 0, 0, OutOfContext);
            Check();
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                DispatchMessage(ref message);
            }
        })
        {
            IsBackground = true,
            Name = "Waypoint UI tests foreground watch",
        };
        thread.Start();
    }

    private static void Check()
    {
        var window = GetForegroundWindow();
        if (!OwnedHere(window))
        {
            if (window != 0)
            {
                _userWindow = window;
            }

            return;
        }

        Interlocked.Increment(ref _taken);
        if (_userWindow != 0)
        {
            SetForegroundWindow(_userWindow);
        }
    }

    private static bool OwnedHere(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        GetWindowThreadProcessId(window, out var process);
        return process == (uint)Environment.ProcessId;
    }

    private const uint ForegroundEvent = 0x0003;
    private const uint OutOfContext = 0x0000;

    private delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, nint window, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
