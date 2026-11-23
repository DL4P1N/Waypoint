using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace Waypoint.App.Tests;

/// <summary>The shared looks in App.xaml that keyboard and low-vision use depend on.</summary>
public sealed class SharedStylesTests
{
    [Fact]
    public void A_checked_box_shows_a_tick_and_an_empty_one_has_a_border_that_can_be_seen() => Ui.Run(ui =>
    {
        var box = ui.Host(new CheckBox { Content = "Less see-through", IsChecked = true });
        ui.Pump();

        var frame = UiTest.Descendants<Border>(box).First(border => border.Name == "Box");
        var tick = UiTest.Descendants<Path>(box).Single(path => path.Name == "Mark");
        Assert.True(tick.IsVisible);
        box.IsChecked = false;
        ui.Pump();
        Assert.False(tick.IsVisible);

        // At least 3:1, as a control's outline needs to be, on the window's black and on a panel.
        var border = ((System.Windows.Media.SolidColorBrush)frame.BorderBrush).Color.ToString()[3..];
        Assert.True(Waypoint.Core.Theme.ThemePalette.Contrast($"#{border}", Waypoint.Core.Theme.ThemePalette.Background) >= 3);
        Assert.True(Waypoint.Core.Theme.ThemePalette.Contrast($"#{border}", Waypoint.Core.Theme.ThemePalette.Surface) >= 3);
    });

    [Fact]
    public void The_focus_ring_is_two_pixels_so_it_shows_over_a_hover_fill() => Ui.Run(ui =>
    {
        var style = (Style)Application.Current.Resources["FocusRing"];
        var setter = style.Setters.OfType<Setter>().Single(found => found.Property == Control.TemplateProperty);
        var ring = (Rectangle)((ControlTemplate)setter.Value).LoadContent();

        Assert.Equal(2, ring.StrokeThickness);
    });

    [Fact]
    public void A_menu_line_reached_with_the_arrow_keys_gets_the_bar() => Ui.Run(ui =>
    {
        var first = new MenuItem { Header = "Rename" };
        var second = new MenuItem { Header = "Delete" };
        ui.Host(new StackPanel { Children = { first, second } });
        ui.Focus(second);

        var row = (Border)second.Template.FindName("Row", second);
        Assert.Equal(((System.Windows.Media.SolidColorBrush)Application.Current.Resources["Accent"]).Color, ((System.Windows.Media.SolidColorBrush)row.BorderBrush).Color);
        Assert.Equal(System.Windows.Media.Colors.Transparent, ((System.Windows.Media.SolidColorBrush)((Border)first.Template.FindName("Row", first)).BorderBrush).Color);
    });

    [Fact]
    public void The_button_Enter_presses_is_filled_with_the_accent() => Ui.Run(ui =>
    {
        var save = new Button { Content = "Save", IsDefault = true };
        var cancel = new Button { Content = "Cancel" };
        ui.Host(new StackPanel { Children = { save, cancel } });

        var accent = ((System.Windows.Media.SolidColorBrush)Application.Current.Resources["Accent"]).Color;
        Assert.Equal(accent, ((System.Windows.Media.SolidColorBrush)save.Background).Color);
        Assert.Equal(System.Windows.Media.Colors.Black, ((System.Windows.Media.SolidColorBrush)save.Foreground).Color);
        Assert.Equal(System.Windows.Media.Colors.Transparent, ((System.Windows.Media.SolidColorBrush)cancel.Background).Color);
    });

    [Fact]
    public void The_column_and_side_menu_show_their_shortcuts_on_hover() => Ui.Run(ui =>
    {
        var column = new NestedColumn { AddLabel = "New list", SearchHint = "Search lists" };

        Assert.Equal("New list (Ctrl+N)", UiTest.Named<Button>(column, "AddButton").ToolTip);
        Assert.Equal("Search lists (Ctrl+F)", UiTest.Named<TextBox>(column, "SearchBox").ToolTip);
        Assert.Equal("New list", System.Windows.Automation.AutomationProperties.GetName(UiTest.Named<Button>(column, "AddButton")));
    });

    [Fact]
    public void Headings_and_hints_keep_a_dark_edge_for_bright_content_behind_the_blur() => Ui.Run(ui =>
    {
        var heading = new TextBlock { Text = "Left this week", Style = (Style)Application.Current.Resources["SectionHeading"] };
        var box = new TextBox { Tag = "Search", Style = (Style)Application.Current.Resources["FieldBox"] };
        ui.Host(new StackPanel { Children = { heading, box } });

        Assert.IsType<System.Windows.Media.Effects.DropShadowEffect>(heading.Effect);
        var hint = UiTest.Descendants<TextBlock>(box).Single(found => found.Text == "Search");
        Assert.Same(heading.Effect, hint.Effect);
        Assert.Equal(System.Windows.Automation.AutomationHeadingLevel.Level2, System.Windows.Automation.AutomationProperties.GetHeadingLevel(heading));
    });

    [Fact]
    public void A_button_that_cant_be_pressed_reads_as_resting_even_when_Enter_would_press_it() => Ui.Run(ui =>
    {
        var save = new Button { Content = "Save", IsDefault = true, IsEnabled = false };
        ui.Host(new StackPanel { Children = { save } });

        var quiet = ((System.Windows.Media.SolidColorBrush)Application.Current.Resources["QuietMark"]).Color;
        Assert.Equal(quiet, ((System.Windows.Media.SolidColorBrush)save.Foreground).Color);
        Assert.Equal(System.Windows.Media.Colors.Transparent, ((System.Windows.Media.SolidColorBrush)save.Background).Color);
        save.IsEnabled = true;
        ui.Pump();
        Assert.NotEqual(quiet, ((System.Windows.Media.SolidColorBrush)save.Foreground).Color);
    });

    [Fact]
    public void Check_boxes_and_choices_keep_the_gap_to_their_words_themselves() => Ui.Run(ui =>
    {
        var words = new CheckBox { Content = "Keep the audio" };
        var bare = new CheckBox();
        var choice = new RadioButton { Content = "Microphone", GroupName = "Test" };
        var other = new RadioButton { Content = "Computer audio", GroupName = "Test" };
        ui.Host(new StackPanel { Children = { words, bare, choice, other } });

        Assert.Equal(10, ((ContentPresenter)words.Template.FindName("Words", words)).Margin.Left);
        Assert.Equal(0, ((ContentPresenter)bare.Template.FindName("Words", bare)).Margin.Left);
        Assert.Equal(10, ((ContentPresenter)choice.Template.FindName("Words", choice)).Margin.Left);

        // The picked choice shows a dot, not only a color.
        var dot = (Ellipse)choice.Template.FindName("Dot", choice);
        Assert.False(dot.IsVisible);
        choice.IsChecked = true;
        ui.Pump();
        Assert.True(dot.IsVisible);
        Assert.False(((Ellipse)other.Template.FindName("Dot", other)).IsVisible);
    });

    [Fact]
    public void Ask_Waypoint_and_the_corner_cards_use_the_app_font() => Ui.Run(ui =>
    {
        var capture = new CaptureWindow(_ => { }, _ => { });
        var offer = new RecordOfferWindow("Biology 101", () => { });
        try
        {
            Assert.Equal("Segoe UI Variable Text, Segoe UI", capture.FontFamily.Source);
            Assert.Equal("Segoe UI Variable Text, Segoe UI", offer.FontFamily.Source);
        }
        finally
        {
            capture.Close();
            offer.Close();
        }
    });

    [Fact]
    public void A_scroll_bar_thumb_is_thin_until_the_mouse_is_on_it() => Ui.Run(ui =>
    {
        var bar = new System.Windows.Controls.Primitives.ScrollBar { Height = 200, Maximum = 100, ViewportSize = 20 };
        ui.Host(new StackPanel { Children = { bar } });

        var thumb = UiTest.Descendants<System.Windows.Controls.Primitives.Thumb>(bar).Single();
        var fill = (Border)thumb.Template.FindName("Bar", thumb);
        Assert.Equal(4, fill.Width);
        Assert.Equal(8, bar.Width);
    });
}
