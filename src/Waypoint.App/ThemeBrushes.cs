using System.Windows;
using System.Windows.Media;
using Waypoint.Core.Theme;

namespace Waypoint.App;

/// <summary>Swaps the app's color resources. Anything using them as a DynamicResource follows along.</summary>
internal static class ThemeBrushes
{
    public static void Apply(ResourceDictionary resources, ThemePalette palette)
    {
        resources["WindowBackground"] = Brush(palette.Tint);
        resources["Accent"] = Brush(palette.Accent);
        resources["Urgent"] = Brush(palette.Urgent);
        resources["Soon"] = Brush(palette.Soon);
        resources["MoneyMark"] = Brush(palette.Money);
        resources["QuietMark"] = Brush(palette.QuietMark);
        resources["SecondaryText"] = Brush(palette.SecondaryText);
        resources["FinishedText"] = Brush(ThemePalette.FinishedText);
        resources["FinishedDetail"] = Brush(ThemePalette.FinishedDetail);
    }

    /// <summary>For rows, which are rebuilt on every refresh and so pick up a new theme then.</summary>
    public static Brush For(ItemMark mark) => (Brush)Application.Current.Resources[mark switch
    {
        ItemMark.Urgent => "Urgent",
        ItemMark.Soon => "Soon",
        ItemMark.School => "Accent",
        ItemMark.Money => "MoneyMark",
        _ => "QuietMark",
    }];

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
