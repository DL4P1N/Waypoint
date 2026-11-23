using System.Windows;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.App.Tests;

/// <summary>
/// A PC reminder card closed without one of its buttons comes back once after the snooze time, unless it was answered
/// meanwhile somewhere else. Made up reminders only, in a temp database.
/// </summary>
public sealed class UnansweredCardTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromMilliseconds(50);

    /// <summary>When the made up reminders are checked, after they've come due.</summary>
    private static readonly DateTime Later = DateTime.Now.AddHours(3);

    [Fact]
    public void A_card_closed_with_its_reminder_still_unanswered_comes_back_once_after_the_wait() => Ui.Run(ui =>
    {
        var reminder = DueNow(ui.Database, "Water the made up fern");
        var shown = 0;
        var released = 0;

        UnansweredCard.ComeBackAfter(Soon, () => UnansweredCard.StillUnanswered(ui.Database, reminder, Later), () => shown++, () => released++);

        // Not straight away, so closing it isn't met with the same card again.
        Assert.Equal(0, shown);
        ServerToDoClickTests.PumpUntil(ui, () => shown + released > 0, "the wait to end");
        Assert.Equal(1, shown);
        Assert.Equal(0, released);

        // Once only: the card shown again is what brings it back after that, if it's closed again.
        Thread.Sleep(200);
        ui.Pump();
        Assert.Equal(1, shown);
    });

    [Fact]
    public void A_card_answered_somewhere_else_meanwhile_doesnt_come_back_and_its_reminder_is_let_go() => Ui.Run(ui =>
    {
        var finished = DueNow(ui.Database, "Return the made up library book");
        var putOff = DueNow(ui.Database, "Call the made up dentist");
        var shown = new List<string>();
        var released = new List<string>();
        void Wait(DueReminder reminder) => UnansweredCard.ComeBackAfter(
            Soon,
            () => UnansweredCard.StillUnanswered(ui.Database, reminder, Later),
            () => shown.Add(reminder.Item.Title),
            () => released.Add(reminder.Item.Title));

        Wait(finished);
        Wait(putOff);

        // Done from the To do page, and snoozed from a Discord card, while both PC cards were closed.
        ui.Database.SetDone(finished.Item, true);
        ui.Database.SnoozeReminder(putOff, Later.AddMinutes(30));

        ServerToDoClickTests.PumpUntil(ui, () => released.Count == 2, "both waits to end");
        Assert.Empty(shown);
    });

    [Fact]
    public void Only_a_button_answers_the_card() => Ui.Run(ui =>
    {
        var reminder = DueNow(ui.Database, "Feed the made up cat");
        var pressed = new ReminderWindow(reminder, "Done", 15, () => { }, () => { }) { ShowActivated = false, Left = -20000, Top = -20000 };
        var closed = new ReminderWindow(reminder, "Done", 15, () => { }, () => { }) { ShowActivated = false, Left = -20000, Top = -20000 };
        try
        {
            var primary = UiTest.Named<Button>(pressed, "Primary");
            primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, primary));
            closed.Close();

            Assert.True(pressed.Answered);
            Assert.False(closed.Answered);
        }
        finally
        {
            pressed.Close();
            closed.Close();
        }
    });

    /// <summary>A made up reminder for two hours from now, which has come due by <see cref="Later"/>.</summary>
    private static DueReminder DueNow(WaypointDatabase database, string title)
    {
        var at = DateTime.Now.AddHours(2);
        var item = database.AddByHand(new HandAddedItem(ItemKind.Reminder, title, DateOnly.FromDateTime(at), TimeOnly.FromDateTime(at), RemindBefore: TimeSpan.Zero), DateTime.Now).Item!;
        return Assert.Single(database.GetDueReminders(Later), due => due.Item.Id == item.Id);
    }
}
