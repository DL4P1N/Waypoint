using Waypoint.Core.Capture;
using Waypoint.Core.Theme;

namespace Waypoint.Core.Data;

public sealed partial class WaypointDatabase
{
    private const string ThemeKey = "theme";
    private const string LessSeeThroughKey = "less_see_through";

    public ThemePalette GetTheme() =>
        (Themes.Find(GetPreference(ThemeKey)) ?? Themes.Default) with { LessSeeThrough = GetPreference(LessSeeThroughKey) == "yes" };

    /// <summary>Makes the window background much more solid, or back to the usual blur. Kept on this PC, like the theme.</summary>
    public void SetLessSeeThrough(bool on) => SetPreference(LessSeeThroughKey, on ? "yes" : "no");

    public OpReply SetTheme(string? name)
    {
        if (Themes.Find(name) is not { } theme)
        {
            return new OpReply($"The themes are {Themes.Names}.", true);
        }

        SetPreference(ThemeKey, theme.Name);
        return new OpReply($"Using the {theme.Name} theme.");
    }
}
