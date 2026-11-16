using System.Windows;
using System.Windows.Controls;

namespace Waypoint.App.Tests;

public sealed class FieldBoxTests
{
    [Fact]
    public void The_caret_sits_before_the_hint_not_inside_it() => Ui.Run(ui =>
    {
        var box = ui.Host(new TextBox { Style = (Style)Application.Current.Resources["FieldBox"], Tag = "Search", Width = 200 });
        box.ApplyTemplate();
        ui.Pump();

        var hint = UiTest.Descendants<TextBlock>(box).Single(text => text.Text == "Search");
        var hintLeft = hint.TranslatePoint(new Point(0, 0), box).X;
        var caretLeft = box.GetRectFromCharacterIndex(0).Left;

        Assert.True(Math.Abs(hintLeft - caretLeft) <= 0.5, $"hint starts at {hintLeft}, caret at {caretLeft}");
    });
}
