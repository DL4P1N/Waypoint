using System;
using System.Linq;
using System.Windows.Threading;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.App;

/// <summary>
/// A PC reminder card closed without one of its buttons, such as with Alt+F4. It comes back once after the reminder's
/// usual snooze time, as if snoozed, unless something answered it meanwhile: a press in Discord, the To do page, or
/// the phone. The reminder stays taken by this card until then, so it isn't shown or sent again in the meantime.
/// </summary>
internal static class UnansweredCard
{
    /// <summary>
    /// True while nothing has answered <paramref name="reminder"/> since its card showed: it's still due at the same
    /// time, for an item that isn't done. Anything that answers it, here or in Discord, finishes the item, puts the
    /// reminder off, or clears it.
    /// </summary>
    public static bool StillUnanswered(WaypointDatabase database, DueReminder reminder, DateTime now) =>
        database.GetDueReminders(now).Any(due => due.Id == reminder.Id
            && due.Item.Source == reminder.Item.Source
            && due.Item.Id == reminder.Item.Id
            && due.RemindAt == reminder.RemindAt);

    /// <summary>
    /// After <paramref name="after"/>, shows the card again through <paramref name="showAgain"/> if
    /// <paramref name="stillUnanswered"/> says so, and otherwise lets the reminder go through
    /// <paramref name="release"/>, so it's taken up again the usual way.
    /// </summary>
    public static void ComeBackAfter(TimeSpan after, Func<bool> stillUnanswered, Action showAgain, Action release)
    {
        var timer = new DispatcherTimer { Interval = after };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            bool again;
            try
            {
                again = stillUnanswered();
            }
            catch
            {
                // Couldn't tell, so it's let go and the next round decides, rather than held for good.
                release();
                throw;
            }

            if (again)
            {
                showAgain();
            }
            else
            {
                release();
            }
        };
        timer.Start();
    }
}
