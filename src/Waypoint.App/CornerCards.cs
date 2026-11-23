using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Waypoint.App;

/// <summary>
/// The cards up in the bottom-right corner, reminder cards and the record offer alike. Each new one takes the lowest
/// space free, counted from the real tops of the cards already up, so none ever covers another.
/// </summary>
public static class CornerCards
{
    public const double ScreenMargin = 16;
    public const double CardGap = 8;

    /// <summary>Whether a reminder card plays a sound as it comes up. On unless turned off in Settings.</summary>
    public const string SoundPreference = "reminder_card_sound";

    private static readonly List<Window> Placed = [];

    public static bool SoundOn(string? preference) => preference != "no";

    /// <summary>Raised when a card goes up in the corner or closes.</summary>
    public static event Action? Changed;

    /// <summary>The card put up last that's still open, or null.</summary>
    public static Window? Newest => Placed.LastOrDefault();

    public static int Count => Placed.Count;

    /// <summary>Places the card once it's laid out, and lets its space go when it closes.</summary>
    public static void Track(Window card)
    {
        card.Loaded += (_, _) => Place(card);
        card.Closed += (_, _) =>
        {
            if (Placed.Remove(card))
            {
                Changed?.Invoke();
            }
        };
    }

    private static void Place(Window card)
    {
        var area = SystemParameters.WorkArea;
        card.Left = area.Right - card.ActualWidth - ScreenMargin;
        card.Top = TopFor(card.ActualHeight, area.Top + ScreenMargin, area.Bottom - ScreenMargin, Placed.Select(open => (open.Top, open.Top + open.ActualHeight)));
        Placed.Add(card);
        Changed?.Invoke();
    }

    /// <summary>
    /// Where a card this tall goes: the lowest space between <paramref name="lowest"/> and <paramref name="highest"/>
    /// that no card in <paramref name="taken"/> covers, with a gap between cards. A space left by a closed card is
    /// used again when the new one fits in it. With no room left at all, it goes back to the corner.
    /// </summary>
    public static double TopFor(double height, double highest, double lowest, IEnumerable<(double Top, double Bottom)> taken, double gap = CardGap)
    {
        var bottom = lowest;
        foreach (var card in taken.OrderByDescending(card => card.Bottom))
        {
            // Every card from here on ends higher than this one, so if this one is clear above, so are they.
            if (card.Bottom + gap <= bottom - height)
            {
                break;
            }

            bottom = Math.Min(bottom, card.Top - gap);
        }

        return bottom - height >= highest ? bottom - height : lowest - height;
    }
}
