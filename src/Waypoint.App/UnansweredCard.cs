using System;
using System.Linq;
using System.Windows.Threading;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.App;

/// <summary>
/// A PC reminder card closed without one of its buttons, such as with Alt+F4. It comes back once after the reminder's
/// usual snooze time, as if snoozed, unless something answered it meanwhile: a press in Discord, the To do page, or
/// the phone. If the card that came back is closed the same way, or it's quiet hours when it would come back, it goes
/// quiet instead. The reminder stays taken by this card all the while, so it isn't shown or sent again, until
/// something answers it elsewhere or Waypoint starts again.
/// </summary>
internal static class UnansweredCard
{
    /// <summary>
    /// The reminder as it's due now, while nothing has answered it since its card showed: still due at the same time,
    /// for an item that isn't done. Null once anything here or in Discord has finished the item, put the reminder off,
    /// or cleared it.
    /// </summary>
    public static DueReminder? StillDue(WaypointDatabase database, DueReminder reminder, DateTime now) =>
        database.GetDueReminders(now).FirstOrDefault(due => due.Id == reminder.Id
            && due.Item.Source == reminder.Item.Source
            && due.Item.Id == reminder.Item.Id
            && due.RemindAt == reminder.RemindAt);

    /// <summary>
    /// Checks every <paramref name="every"/>. Once <paramref name="stillDue"/> gives null, the reminder is let go
    /// through <paramref name="release"/>, so it's taken up again the usual way. While it's still due: with
    /// <paramref name="comeBack"/>, the first check shows it again through <paramref name="showAgain"/>, unless
    /// <paramref name="quiet"/> says it's quiet then. Otherwise it stays held without being shown. A problem is logged
    /// and the reminder let go, rather than held for good.
    /// </summary>
    public static void Wait(
        TimeSpan every,
        bool comeBack,
        Func<DueReminder?> stillDue,
        Func<DueReminder, bool> quiet,
        Action<DueReminder> showAgain,
        Action release,
        Action<Exception> logError)
    {
        var timer = new DispatcherTimer { Interval = every };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (stillDue() is not { } due)
                {
                    timer.Stop();
                    release();
                    return;
                }

                if (!comeBack)
                {
                    return;
                }

                // Once only: any check after this one only watches for it being answered elsewhere.
                comeBack = false;
                if (quiet(due))
                {
                    return;
                }

                timer.Stop();
                showAgain(due);
            }
            catch (Exception exception)
            {
                timer.Stop();
                logError(exception);
                release();
            }
        };
        timer.Start();
    }
}
