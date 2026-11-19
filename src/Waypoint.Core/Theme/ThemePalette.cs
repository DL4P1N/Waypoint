using System.Globalization;

namespace Waypoint.Core.Theme;

/// <summary>One look. The app turns these into its brushes, so the contrast tests check what's really shown.</summary>
public sealed record ThemePalette(string Name, string Accent, string Urgent, string Soon, string Money, string QuietMark, string SecondaryText)
{
    public const string Background = "#000000";
    public const string Surface = "#141414";

    /// <summary>
    /// The tint over the acrylic behind every window and menu, in the panels' color. The acrylic already pulls what's
    /// behind toward a middle gray, as Windows' own does, so the tint can stay light. See <see cref="Themes"/> for how
    /// light.
    /// </summary>
    public string WindowTint { get; init; } = SolidWindowTint;

    /// <summary>The tint with Less see-through on: the same black as the panels, nearly solid, so little shows through.</summary>
    public const string SolidWindowTint = "#F2141414";

    // Grayer than anything still to do, but light enough to read, at 4.5:1 or better even over the window background.
    public const string FinishedText = "#C8C8C8";
    public const string FinishedDetail = "#C8C8C8";

    /// <summary>A preference of its own, set beside the theme, so it's kept whichever theme is picked.</summary>
    public bool LessSeeThrough { get; init; }

    /// <summary>The window tint this look uses.</summary>
    public string Tint => LessSeeThrough ? SolidWindowTint : WindowTint;

    /// <summary>The WCAG contrast ratio between two colors, from 1 to 21.</summary>
    public static double Contrast(string first, string second)
    {
        var (light, dark) = (Luminance(first), Luminance(second));
        return (Math.Max(light, dark) + 0.05) / (Math.Min(light, dark) + 0.05);
    }

    private static int Channel(string color, int index) =>
        int.Parse(color.AsSpan(1 + (index * 2), 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static double Luminance(string color) =>
        (0.2126 * Linear(Channel(color, 0))) + (0.7152 * Linear(Channel(color, 1))) + (0.0722 * Linear(Channel(color, 2)));

    private static double Linear(int channel)
    {
        var value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}

public static class Themes
{
    private const string SharedUrgent = "#FF7A70";
    private const string SharedSoon = "#FFC857";
    private const string SharedMoney = "#6FD08C";
    private const string SharedQuiet = "#8A8A8A";
    private const string SharedSecondary = "#D4D4D4";

    // Half the panels' black over the acrylic, the lightest that keeps every theme readable over a white page behind:
    // 4.5:1 for plain, secondary, and finished text and 3:1 for colored text and marks. The shared red needs this much
    // in the colored themes.
    private const string AcrylicTint = "#80141414";

    public static readonly ThemePalette Default = new("gray", "#D0D0D0", SharedUrgent, SharedSoon, SharedMoney, SharedQuiet, SharedSecondary) { WindowTint = AcrylicTint };

    public static readonly IReadOnlyList<ThemePalette> All =
    [
        Default,
        Default with { Name = "blue", Accent = "#6CB2FF" },
        Default with { Name = "amber", Accent = "#FFB547" },
        Default with { Name = "teal", Accent = "#4FD1C5" },
        new("mono", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#C8C8C8", SharedQuiet, SharedSecondary) { WindowTint = AcrylicTint },
    ];

    public static string Names => string.Join(", ", All.Select(theme => theme.Name));

    /// <summary>Finds a theme in what was typed, so "the amber theme" works as well as "amber".</summary>
    public static ThemePalette? Find(string? text) =>
        text is null ? null : All.FirstOrDefault(theme => text.Contains(theme.Name, StringComparison.OrdinalIgnoreCase));
}
