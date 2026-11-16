using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using Waypoint.App;

namespace Waypoint.App.Tests;

/// <summary>Checklist boxes read as boxes: ☐ and ☑, never a bullet's square.</summary>
public sealed partial class MarkdownBoxTests
{
    [Fact]
    public void Checklist_items_show_an_empty_box_or_a_ticked_one_and_bullets_keep_their_marks() => Ui.Run(ui =>
    {
        var box = Open(ui, "- [ ] milk\n- [x] eggs\n- one\n  - two\n    - three\n");

        var lists = box.Document.Blocks.OfType<List>().ToList();
        Assert.Equal(TextMarkerStyle.None, lists[0].MarkerStyle);
        Assert.Equal(TextMarkerStyle.None, lists[1].MarkerStyle);
        Assert.Equal(TextMarkerStyle.Square, lists[4].MarkerStyle);

        var marks = box.CheckMarks();
        Assert.Equal(["☐", "☑"], marks.Select(mark => mark.Glyph));
        var text = MarkdownBox.TextPara(lists[0])!.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        Assert.True(marks[0].Place.Right <= text.Left && marks[0].Place.Left >= 0, $"The box at {marks[0].Place} should sit left of the text at {text.Left}");
        Assert.True(marks[0].Place.Bottom <= marks[1].Place.Top + 0.5);
    });

    [Fact]
    public void Links_are_underlined_so_they_stand_apart_from_text_in_every_theme() => Ui.Run(ui =>
    {
        // The gray theme's links are a light gray close to the text, so the underline is what marks them.
        var box = Open(ui, "See [the course page](https://example.com) now.");
        var link = box.Document.Blocks.OfType<Paragraph>().First().Inlines.OfType<Hyperlink>().Single();

        Assert.Contains(link.TextDecorations, decoration => decoration.Location == TextDecorationLocation.Underline);
    });

    [Fact]
    public void Clicking_where_the_box_is_drawn_ticks_it_and_the_box_changes() => Ui.Run(ui =>
    {
        var box = Open(ui, "- [ ] milk");
        var place = box.CheckMarks().Single().Place;
        var middle = new Point(place.Left + place.Width / 2, place.Top + place.Height / 2);

        Assert.True(MarkdownBox.InBox(MarkdownBox.TextPara(box.Document.Blocks.FirstBlock)!, middle));
        Press(box, Key.Enter, ModifierKeys.Control);
        ui.Pump();

        Assert.Equal("- [x] milk", Md(box));
        Assert.Equal("☑", box.CheckMarks().Single().Glyph);
    });
}
