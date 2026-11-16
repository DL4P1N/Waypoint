using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Waypoint.App;

/// <summary>
/// Says what just happened on a page, with a way back when there is one. A plain message lasts until the next thing
/// done on the page, which replaces it with its own or clears it. A message with an Undo stays through clicks that
/// only look around, such as picking a row or a filter, and goes once something else changes data
/// (<see cref="Changed"/>), another message is shown, or it's closed. Either kind goes when the page is left.
/// </summary>
public partial class UndoBar : UserControl
{
    private Action? _undo;
    private object? _about;
    private UIElement? _page;

    /// <summary>Set when something is pressed on the page. Showing a message unsets it, so only an old one is cleared.</summary>
    private bool _stale;

    /// <summary>
    /// Set while the action that showed the message is still running, so a change it reports after showing its own
    /// message doesn't clear it.
    /// </summary>
    private bool _fresh;

    public UndoBar()
    {
        InitializeComponent();
        Loaded += (_, _) => Watch();
    }

    /// <summary>
    /// Shows the message, replacing any before it. About is what the message is about, such as an item, so the
    /// message can go once that thing changes and it no longer holds. Action names the button when it's a next
    /// step rather than an Undo, such as "Pick a folder...".
    /// </summary>
    public void Show(string message, Action? undo = null, object? about = null, string action = "Undo")
    {
        UndoButton.Content = action;
        _undo = undo;
        _about = about;
        _stale = false;
        _fresh = true;
        Dispatcher.BeginInvoke(() => _fresh = false, DispatcherPriority.Background);
        Message.Text = message;
        UndoButton.Visibility = undo is null ? Visibility.Collapsed : Visibility.Visible;
        Visibility = Visibility.Visible;
        LiveRegion.Announce(Message);
    }

    /// <summary>
    /// The page changed data. A message from an earlier action goes, since an Undo for it may no longer put things
    /// back the way they were. A message the same action just showed stays.
    /// </summary>
    public void Changed()
    {
        if (!_fresh)
        {
            Clear();
        }
    }

    public void Clear()
    {
        _undo = null;
        _about = null;
        _stale = false;
        _fresh = false;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// For picking something else on the page, such as another list or note: a plain message goes, and a way back
    /// stays, since nothing changed.
    /// </summary>
    public void ClearPlain()
    {
        if (_undo is null)
        {
            Clear();
        }
    }

    /// <summary>Clears the message when it's about this, since what it said is out of date now.</summary>
    public void Forget(object about)
    {
        if (_about is not null && Equals(_about, about))
        {
            Clear();
        }
    }

    /// <summary>
    /// Follows the page the bar is on. A press there marks the message as old, and once that press is done, with
    /// whatever it did, an old message is cleared. Leaving the page clears it too.
    /// </summary>
    private void Watch()
    {
        var page = PageOf(this);
        if (page is null || ReferenceEquals(page, _page))
        {
            return;
        }

        _page = page;
        page.AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) => Pressed(e.OriginalSource)), true);
        page.AddHandler(PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space or Key.Delete)
            {
                Pressed(e.OriginalSource);
            }
        }), true);
        page.AddHandler(MouseUpEvent, new MouseButtonEventHandler((_, _) => Done()), true);
        page.AddHandler(KeyUpEvent, new KeyEventHandler((_, _) => Done()), true);
        page.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
            {
                Clear();
            }
        };
    }

    private void Pressed(object source)
    {
        // The bar's own buttons, and scrolling, aren't something new done on the page. A way back stays until
        // something changes data, which the page reports with Changed.
        if (Visibility == Visibility.Visible && _undo is null && source is DependencyObject element && !Within<UndoBar>(element) && !Within<ScrollBar>(element))
        {
            _stale = true;
        }
    }

    /// <summary>After the press has done its work, since that may have shown a message of its own.</summary>
    private void Done()
    {
        if (_stale)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_stale)
                {
                    Clear();
                }
            }, DispatcherPriority.Background);
        }
    }

    private static bool Within<T>(DependencyObject element) where T : DependencyObject
    {
        for (DependencyObject? at = element; at is not null; at = ParentOf(at))
        {
            if (at is T)
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

    /// <summary>The page the bar is part of: the nearest user control above it, or the window if there's none.</summary>
    private static UIElement? PageOf(DependencyObject bar)
    {
        UIElement? found = null;
        for (var at = VisualTreeHelper.GetParent(bar); at is not null; at = VisualTreeHelper.GetParent(at))
        {
            if (at is UserControl control)
            {
                return control;
            }

            found = at as UIElement ?? found;
        }

        return found;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Clear();

    private void OnUndoClicked(object sender, RoutedEventArgs e)
    {
        var undo = _undo;
        Clear();
        undo?.Invoke();
    }
}
