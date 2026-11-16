using System.Drawing;
using System.Windows.Forms;
using Waypoint.Core.Theme;

namespace Waypoint.App;

/// <summary>
/// Flat black colors for the tray menu, with the theme's accent marking the item under the mouse,
/// the same way the app's own menus do.
/// </summary>
public sealed class TrayMenuColors(ThemePalette palette) : ProfessionalColorTable
{
    private static readonly Color Background = ColorTranslator.FromHtml(ThemePalette.Background);
    private static readonly Color Hover = Color.FromArgb(51, 51, 51);

    private readonly Color _accent = ColorTranslator.FromHtml(palette.Accent);
    private readonly Color _quiet = ColorTranslator.FromHtml(palette.QuietMark);

    public override Color ToolStripDropDownBackground => Background;
    public override Color MenuBorder => _quiet;
    public override Color MenuItemBorder => _accent;
    public override Color MenuItemSelected => Hover;
    public override Color ImageMarginGradientBegin => Background;
    public override Color ImageMarginGradientMiddle => Background;
    public override Color ImageMarginGradientEnd => Background;
    public override Color SeparatorDark => Hover;
    public override Color SeparatorLight => Hover;
    public override Color CheckBackground => _accent;
    public override Color CheckSelectedBackground => _accent;
    public override Color CheckPressedBackground => _accent;
}
