using System.Windows;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.App.Tests;

/// <summary>
/// A PC reminder card closed without one of its buttons comes back once after the snooze time, unless it was answered
/// meanwhile somewhere else or it's quiet hours, and then goes quiet. Made up reminders only, in a temp database.
/// </summary>
public sealed class UnansweredCardTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromMilliseconds(50);

    /// <summary>When the made up reminders are checked, after they've come due.</summary>
    private static readonly DateTime Later = DateTime.Now.AddHours(3);

    [Fact]
    public void A_card_closed_unanswered_comes_back_once_then_goes_quiet_until_its_answered_elsewhere() => Ui.Run(ui =>
    {
        var reminder = DueNow(ui.Database, "Water the made up fern");
        var shown = new List<DueReminder>();
        var released = 0;
        var problems = new List<Exception>();

        // As the app does: the card that came back, closed again without an answer, waits without coming back.
        void ClosedUnanswered(DueReminder card, bool cameBack) => UnansweredCard.Wait(
            Soon,
            comeBack: !cameBack,
            () => UnansweredCard.StillDue(ui.Database, card, Later),
            _ => false,
            due =>
            {
                shown.Add(due);
                ClosedUnanswered(due, cameBack: true);
            },
            () => released++,
            problems.Add);

        ClosedUnanswered(reminder, cameBack: false);

        // Not straight away, so closing it isn't met with the same card again.
        Assert.Empty(shown);
        ServerToDoClickTests.PumpUntil(ui, () => shown.Count > 0, "the card to come back");

        // Shown again as it's due now, and only once: several more waits go by with nothing shown or let go.
        Assert.Equal(reminder.Id, Assert.Single(shown).Id);
        PumpFor(ui, TimeSpan.FromMilliseconds(400));
        Assert.Single(shown);
        Assert.Equal(0, released);

        // Done from the To do page: only then is it let go, so the usual checks take it up.
        ui.Database.SetDone(reminder.Item, true);
        ServerToDoClickTests.PumpUntil(ui, () => released == 1, "the reminder to be let go");
        PumpFor(ui, TimeSpan.FromMilliseconds(200));
        Assert.Single(shown);
        Assert.Equal(1, released);
        Assert.Empty(problems);
    });

    [Fact]
    public void A_card_that_would_come_back_in_quiet_hours_stays_quiet() => Ui.Run(ui =>
    {
        var reminder = DueNow(ui.Database, "Read the made up chapter");
        var shown = 0;
        var released = 0;
        var asked = 0;
        UnansweredCard.Wait(Soon, comeBack: true, () => UnansweredCard.StillDue(ui.Database, reminder, Later), _ =>
        {
            asked++;
            return true;
        }, _ => shown++, () => released++, exception => throw exception);

        ServerToDoClickTests.PumpUntil(ui, () => asked > 0, "the quiet check");
        PumpFor(ui, TimeSpan.FromMilliseconds(400));
        Assert.Equal(0, shown);
        Assert.Equal(0, released);
        Assert.Equal(1, asked);

        ui.Database.SnoozeReminder(reminder, Later.AddMinutes(30));
        ServerToDoClickTests.PumpUntil(ui, () => released == 1, "the reminder to be let go");
        Assert.Equal(0, shown);
    });

    [Fact]
    public void A_problem_showing_it_again_is_logged_and_the_reminder_let_go() => Ui.Run(ui =>
    {
        var reminder = DueNow(ui.Database, "Book the made up haircut");
        var released = 0;
        var problems = new List<Exception>();
        UnansweredCard.Wait(Soon, comeBack: true, () => UnansweredCard.StillDue(ui.Database, reminder, Later), _ => false,
            _ => throw new InvalidOperationException("Made up problem"), () => released++, problems.Add);

        ServerToDoClickTests.PumpUntil(ui, () => released > 0, "the reminder to be let go");
        PumpFor(ui, TimeSpan.FromMilliseconds(200));
        Assert.Equal(1, released);
        Assert.Equal("Made up problem", Assert.Single(problems).Message);
    });

    [Fact]
    public void A_card_answered_somewhere_else_meanwhile_doesnt_come_back_and_its_reminder_is_let_go() => Ui.Run(ui =>
    {
        var finished = DueNow(ui.Database, "Return the made up library book");
        var putOff = DueNow(ui.Database, "Call the made up dentist");
        var shown = new List<string>();
        var released = new List<string>();
        void Wait(DueReminder reminder) => UnansweredCard.Wait(
            Soon,
            comeBack: true,
            () => UnansweredCard.StillDue(ui.Database, reminder, Later),
            _ => false,
            due => shown.Add(due.Item.Title),
            () => released.Add(reminder.Item.Title),
            exception => throw exception);

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

    [Fact]
    public void Saying_it_didnt_help_then_closing_the_card_isnt_an_answer() => Ui.Run(ui =>
    {
        var reminder = DueNow(ui.Database, "Stretch the made up hamstrings");
        var kept = 0;
        var card = new ReminderWindow(reminder, "Done", 15, () => { }, () => { })
        {
            ShowActivated = false,
            Left = -20000,
            Top = -20000,
            FeedbackKept = () => ++kept,
        };
        ui.Pump();
        try
        {
            var link = UiTest.Descendants<Button>(UiTest.Named<FeedbackPrompt>(card, "FeedbackLine")).First(button => button.Content as string == CardLabels.DidntHelp);
            link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, link));
            card.Close();

            Assert.Equal(1, kept);
            Assert.False(card.Answered);
        }
        finally
        {
            card.Close();
        }
    });

    /// <summary>Keeps the window thread running for <paramref name="span"/>, so timers due meanwhile get to run.</summary>
    private static void PumpFor(UiTest ui, TimeSpan span)
    {
        var until = DateTime.UtcNow + span;
        ServerToDoClickTests.PumpUntil(ui, () => DateTime.UtcNow >= until, "the time to pass");
    }

    /// <summary>A made up reminder for two hours from now, which has come due by <see cref="Later"/>.</summary>
    private static DueReminder DueNow(WaypointDatabase database, string title)
    {
        var at = DateTime.Now.AddHours(2);
        var item = database.AddByHand(new HandAddedItem(ItemKind.Reminder, title, DateOnly.FromDateTime(at), TimeOnly.FromDateTime(at), RemindBefore: TimeSpan.Zero), DateTime.Now).Item!;
        return Assert.Single(database.GetDueReminders(Later), due => due.Item.Id == item.Id);
    }
}
