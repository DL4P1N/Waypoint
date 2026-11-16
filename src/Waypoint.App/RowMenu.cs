using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Waypoint.App;

/// <summary>One line of a row's right-click menu. Children make it open a submenu instead of acting.</summary>
internal sealed record RowMenuEntry(string Label, Action? Run = null, IReadOnlyList<RowMenuEntry>? Children = null)
{
    /// <summary>A thin line between parts of a menu.</summary>
    public static RowMenuEntry Separator { get; } = new("");

    /// <summary>The parts with a line between each, leaving out empty ones.</summary>
    public static IEnumerable<RowMenuEntry> Parts(params IEnumerable<RowMenuEntry>[] parts)
    {
        var first = true;
        foreach (var part in parts.Select(part => part.ToList()).Where(part => part.Count > 0))
        {
            if (!first)
            {
                yield return Separator;
            }

            first = false;
            foreach (var entry in part)
            {
                yield return entry;
            }
        }
    }

    /// <summary>What a screen reader says for the line, rather than the record's fields.</summary>
    public override string ToString() => Label;
}

/// <summary>Builds the right-click menu fresh each time, so it always matches the row's current state.</summary>
internal static class RowMenu
{
    /// <summary>A negative cursor position means the menu key, so it opens under the row rather than at the mouse.</summary>
    public static void Open(FrameworkElement row, ContextMenuEventArgs args, IEnumerable<RowMenuEntry> entries, Action closed) =>
        Open(row, args.CursorLeft < 0 ? PlacementMode.Bottom : PlacementMode.MousePoint, entries, closed);

    /// <summary>For a menu that drops down from a button, such as a page's + button.</summary>
    public static void Open(FrameworkElement target, PlacementMode placement, IEnumerable<RowMenuEntry> entries, Action closed)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = placement,
        };

        foreach (var entry in entries)
        {
            menu.Items.Add(ReferenceEquals(entry, RowMenuEntry.Separator) ? new Separator() : Item(menu, entry));
        }

        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is HwndSource source)
            {
                WindowStyling.Apply(source.Handle);
                WindowStyling.Blur(source.Handle);
            }
        };
        menu.Closed += (_, _) => closed();
        menu.IsOpen = true;
    }

    public static void Copy(string text)
    {
        try
        {
            Clipboard.SetDataObject(text);
        }
        catch (ExternalException)
        {
            // Another app has the clipboard open. Nothing is lost, and it can be tried again.
        }
    }

    private static MenuItem Item(ContextMenu menu, RowMenuEntry entry)
    {
        var item = new MenuItem { Header = entry.Label };
        foreach (var child in entry.Children ?? [])
        {
            item.Items.Add(Item(menu, child));
        }

        if (entry.Run is { } run)
        {
            // Runs once the menu has gone, so a dialog doesn't open on top of it.
            item.Click += (_, _) => menu.Dispatcher.BeginInvoke(run, DispatcherPriority.Input);
        }

        return item;
    }
}
