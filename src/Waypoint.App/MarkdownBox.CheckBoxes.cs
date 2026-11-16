using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Waypoint.App;

/// <summary>
/// Checklist boxes, drawn as ☐ and ☑ the way a note reads elsewhere. A list's own markers are only bullets and
/// numbers, and its squares read as bullets, so a checklist item's list has no marker and its box is drawn here, in
/// the space the marker would take, where a click ticks it (<see cref="InBox"/>). It's drawn under the text, which
/// is why the box's background stays see-through on Notes.
/// </summary>
public sealed partial class MarkdownBox
{
    private static readonly Typeface CheckTypeface = new(new FontFamily("Segoe UI Symbol"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private const double CheckSize = BaseSize + 3;

    internal readonly record struct CheckMark(Rect Place, string Glyph, bool Ticked);

    private void WatchCheckBoxes()
    {
        // The boxes follow the text, so they're drawn again when it changes or scrolls.
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => InvalidateVisual()));
        TextChanged += (_, _) => InvalidateVisual();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ForegroundProperty)
        {
            InvalidateVisual();
        }
    }

    /// <summary>Where each checklist box on screen goes, in this box's coordinates, from the top down.</summary>
    internal IReadOnlyList<CheckMark> CheckMarks()
    {
        var marks = new List<CheckMark>();
        foreach (var block in Document.Blocks)
        {
            if (block is not List || Info(block) is not { Kind: LineKind.Item, Type: ItemType.Task } info || TextPara(block) is not { } paragraph)
            {
                continue;
            }

            var start = paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            if (start.IsEmpty || start.Bottom < 0 || start.Top > ActualHeight)
            {
                continue;
            }

            // The same place a click ticks: just left of the text, within the indent.
            var width = IndentStep - 4;
            marks.Add(new CheckMark(new Rect(start.Left - 4 - width, start.Top, width, Math.Max(start.Height, 1)), info.Checked ? "☑" : "☐", info.Checked));
        }

        return marks;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (Document is null)
        {
            return;
        }

        var marks = CheckMarks();
        if (marks.Count == 0)
        {
            return;
        }

        var border = BorderThickness;
        drawingContext.PushClip(new RectangleGeometry(new Rect(
            border.Left,
            border.Top,
            Math.Max(0, ActualWidth - border.Left - border.Right),
            Math.Max(0, ActualHeight - border.Top - border.Bottom))));
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var muted = TryFindResource("SecondaryText") as Brush ?? Foreground;
        foreach (var mark in marks)
        {
            var glyph = new FormattedText(mark.Glyph, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, CheckTypeface, CheckSize, mark.Ticked ? muted : Foreground, dip);
            var at = new Point(mark.Place.Right - glyph.Width, mark.Place.Top + (mark.Place.Height - glyph.Height) / 2);
            drawingContext.DrawText(glyph, at);
        }

        drawingContext.Pop();
    }
}
