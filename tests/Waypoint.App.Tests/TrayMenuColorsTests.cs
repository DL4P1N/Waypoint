using System.Drawing;
using Waypoint.App;
using Waypoint.Core.Theme;

namespace Waypoint.App.Tests;

/// <summary>The tray menu takes its colors from the theme, since it can't use the app's brushes.</summary>
public sealed class TrayMenuColorsTests
{
    [Fact]
    public void The_item_under_the_mouse_is_marked_in_the_theme_accent()
    {
        var blue = Themes.All.Single(theme => theme.Name == "blue");
        var amber = Themes.All.Single(theme => theme.Name == "amber");

        Assert.Equal(ColorTranslator.FromHtml(blue.Accent).ToArgb(), new TrayMenuColors(blue).MenuItemBorder.ToArgb());
        Assert.Equal(ColorTranslator.FromHtml(amber.Accent).ToArgb(), new TrayMenuColors(amber).MenuItemBorder.ToArgb());
        Assert.Equal(ColorTranslator.FromHtml(amber.Accent).ToArgb(), new TrayMenuColors(amber).CheckBackground.ToArgb());
    }

    [Fact]
    public void The_menu_stays_on_the_app_background()
    {
        var palette = Themes.Default;
        var colors = new TrayMenuColors(palette);

        Assert.Equal(ColorTranslator.FromHtml(ThemePalette.Background).ToArgb(), colors.ToolStripDropDownBackground.ToArgb());
        Assert.Equal(ColorTranslator.FromHtml(palette.Accent).ToArgb(), colors.MenuItemBorder.ToArgb());
    }
}
