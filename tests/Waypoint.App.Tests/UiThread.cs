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
/// </summary>
internal static class Ui
{
    private static readonly Lazy<Dispatcher> Thread = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly List<Exception> Unhandled = [];

    public static void Run(Action<UiTest> body)
    {
        ExceptionDispatchInfo? failure = null;
        Thread.Value.Invoke(() =>
        {
            Unhandled.Clear();
            var returned = UiTest.ForegroundReturned;
            var test = new UiTest();
            try
            {
                body(test);
                test.Pump();
                Assert.True(returned == UiTest.ForegroundReturned, "The test window took the foreground from the user and had to give it back");
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
        failure?.Throw();
    }

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        Exception? problem = null;
        using var ready = new ManualResetEventSlim();
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                // This process may still hold the right to take the foreground (it was started recently). Locking it
                // means nothing here can pull focus away from the window the user is working in.
                UiTest.LockSetForegroundWindow(1);
                AppDomain.CurrentDomain.ProcessExit += (_, _) => UiTest.LockSetForegroundWindow(2);
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
        })
        {
            IsBackground = true,
            Name = "Waypoint UI tests",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (problem is not null)
        {
            throw new InvalidOperationException("The UI thread didn't start", problem);
        }

        return dispatcher!;
    }

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
        _userForeground = Foreground();
        _window.Show();
        Pump();
        return content;
    }

    /// <summary>Runs everything queued on the dispatcher, down to background work.</summary>
    public void Pump()
    {
        GiveBackForeground();
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
            GiveBackForeground();
            Pump();
        }

        Assert.True(element.IsKeyboardFocused, $"Couldn't focus {element}, focus is on {Keyboard.FocusedElement}");
    }

    /// <summary>A key press as WPF raises it: PreviewKeyDown, then KeyDown unless handled.</summary>
    public bool Press(UIElement target, Key key)
    {
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
        var until = DateTime.UtcNow.AddSeconds(3);
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
    private nint _userForeground;

    /// <summary>The number of times Windows handed this process the foreground and it was given straight back.</summary>
    public static int ForegroundReturned { get; private set; }

    /// <summary>
    /// Focusing a control needs Win32 focus in its window, which activates it within this thread only. If Windows
    /// ever hands this process the foreground anyway, it goes straight back to the window the user was in.
    /// </summary>
    private void GiveBackForeground()
    {
        if (_userForeground != 0 && Foreground() == 0)
        {
            ForegroundReturned++;
            SetForegroundWindow(_userForeground);
        }
    }

    /// <summary>The foreground window, or 0 when it belongs to this process.</summary>
    private static nint Foreground()
    {
        var window = GetForegroundWindow();
        GetWindowThreadProcessId(window, out var process);
        return process == (uint)Environment.ProcessId ? 0 : window;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LockSetForegroundWindow(uint code);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
    private const int ToolWindow = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLong(nint handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLong(nint handle, int index, nint value);
}
