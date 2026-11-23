using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Text.Json;
using System.Threading;
using System.Windows;
using Waypoint.Core;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Discord;
using Waypoint.Core.Remote;
using Waypoint.Core.Schedule;
using Waypoint.Core.Theme;
using Forms = System.Windows.Forms;

namespace Waypoint.App;

/// <summary>The reminder card: its buttons, and Save my spot opened over it.</summary>
public partial class App
{
    /// <param name="cameBack">Set on the card shown again after one was closed without an answer.</param>
    private void ShowReminderCard(DueReminder reminder, Action closed, bool cameBack = false)
    {
        // Repeating items and reminders are finished here. An exam's alert is only an alert.
        var finishesItem = reminder.RepeatMinutes is not null || reminder.Item.Kind is ItemKind.Reminder or ItemKind.Task;
        var snoozeMinutes = reminder.RepeatMinutes ?? WaypointWorker.DefaultSnoozeMinutes;

        SystemSounds.Asterisk.Play();
        ReminderWindow? window = null;
        ReminderCardHold? holdLater = null;

        // A dose gets Took it, Not now (once), and Skip, each kept for the day the card is for, whichever card answers first.
        if (reminder.Item is { Dose: true, Recurs: not null, DueDate: { } dueOn })
        {
            string Answer(DoseAnswer answer)
            {
                var text = _database.AnswerDose(reminder.Item.Id, dueOn, answer, DateTime.Now, reminder);
                RefreshStatus();
                return text;
            }

            window = new ReminderWindow(
                reminder,
                CardLabels.TookIt,
                TransitionAssist.FollowUpMinutes,
                () => ShowToast(reminder.Item.Title, Answer(DoseAnswer.Took)),
                () => Answer(DoseAnswer.NotNow),
                dose: new ReminderDose(!reminder.IsFollowUp, () => Answer(DoseAnswer.Skip)))
            {
                Slot = _openCards % MaxStackedCards,
                FeedbackKept = () => _database.AddFeedback(Feedback.Reminder, ItemRef.Format(reminder.Item), null, DateTime.Now),
                FeedbackNoted = (id, note) => _database.NoteFeedback(id, note),
            };
            _openCards++;
            var doseRef = ItemRef.Format(reminder.Item);
            _cardsUp.Add(doseRef);
            var doseCard = window;
            window.Closed += (_, _) =>
            {
                _openCards--;
                _cardsUp.Remove(doseRef);
                CardClosed(doseCard.Answered, reminder, closed, snoozeMinutes, cameBack);
            };
            window.Show();
            return;
        }

        // A waiting-on nudge gets Follow up, Still waiting, and Done, all answered here without the sorter.
        var waitingOn = _database.WaitingFor(reminder);
        var waiting = waitingOn is null
            ? null
            : new ReminderWaiting(
                () =>
                {
                    _database.DismissReminder(reminder);
                    _itemsWindow.ShowReply(WaitingOn.DraftReply(waitingOn));
                    _itemsWindow.ShowList();
                },
                () =>
                {
                    ShowToast("Still waiting", _database.StillWaiting(waitingOn.ItemId, null, DateTime.Now).Text);
                    RefreshStatus();
                });
        var transition = waiting is null && TransitionAssist.Applies(reminder)
            ? new ReminderTransition(
                finishesItem,
                () => _database.DismissReminder(reminder),
                () =>
                {
                    _database.DismissReminder(reminder);
                    _database.RecordReminderAnswer(reminder.Item, ReminderAnswer.NotNow, DateTime.Now);
                },
                TransitionAssist.OffersMoreTime(reminder) ? () => _database.FollowUpReminder(reminder, DateTime.Now) : null,
                TransitionAssist.OffersSaveSpot(_database.GetLastSpotSaved(), DateTime.Now) ? () => SaveSpotFromCard(window!) : null)
            : null;
        window = new ReminderWindow(
            reminder,
            finishesItem ? "Done" : "Got it",
            snoozeMinutes,
            () =>
            {
                if (finishesItem)
                {
                    _database.SetDone(reminder.Item, true);
                }
                else
                {
                    _database.DismissReminder(reminder);
                }

                RefreshStatus();
            },
            () => _database.SnoozeReminder(reminder, DateTime.Now.AddMinutes(snoozeMinutes)),
            reminder.Item.Source == ItemSource.Calendar ? () => _database.SkipNudges(reminder.Item.Id) : null,
            Activation.CanStart(reminder.Item)
                ? () =>
                {
                    _database.RecordReminderAnswer(reminder.Item, ReminderAnswer.Started, DateTime.Now);
                    _captures.StartOn(reminder.Item);
                }
                : null,
            transition,
            waiting)
        {
            Slot = _openCards % MaxStackedCards,
            FeedbackKept = () =>
            {
                // Saying it didn't help means it isn't held for after this. The card is still up, and closing it
                // without a button is treated like any other card closed that way.
                holdLater?.Answered();
                return _database.AddFeedback(Feedback.Reminder, ItemRef.Format(reminder.Item), null, DateTime.Now);
            },
            FeedbackNoted = (id, note) => _database.NoteFeedback(id, note),
        };
        _openCards++;

        // A card with Start left unanswered for a while means "after this", so the item is held once, to come back
        // when To do or Ask next opens. The card stays up, and answering it after all drops the hold.
        holdLater = ReminderCardHold.Applies(reminder.Item, waiting is not null) ? new ReminderCardHold(_database, reminder.Item, DateTime.Now) : null;
        holdLater?.Start();
        var cardRef = ItemRef.Format(reminder.Item);
        _cardsUp.Add(cardRef);

        var card = window;
        window.Closed += (_, _) =>
        {
            _openCards--;
            _cardsUp.Remove(cardRef);
            holdLater?.Answered();
            CardClosed(card.Answered, reminder, closed, snoozeMinutes, cameBack);
        };
        window.Show();
    }

    /// <summary>
    /// An answered card lets its reminder go now. One closed without an answer comes back once after the snooze time,
    /// unless it was answered meanwhile somewhere else, such as in Discord or on the To do page, or it's quiet hours
    /// for it then. The card that came back, closed the same way, doesn't come back again: its reminder stays held,
    /// shown nowhere, until it's answered elsewhere or Waypoint starts again.
    /// </summary>
    private void CardClosed(bool answered, DueReminder reminder, Action closed, int snoozeMinutes, bool cameBack)
    {
        if (answered || _restoring)
        {
            closed();
            return;
        }

        UnansweredCard.Wait(
            TimeSpan.FromMinutes(snoozeMinutes),
            comeBack: !cameBack,
            () => _restoring ? null : UnansweredCard.StillDue(_database, reminder, DateTime.Now),

            // The same quiet part of the night the reminder checks keep to: assignment nudges wait it out.
            due => due.Item.Source == ItemSource.Calendar && _bedtimeSchedule?.IsQuiet(DateTime.Now) == true,
            due => ShowReminderCard(due, closed, cameBack: true),
            closed,
            LogError);
    }

    /// <summary>The same form and save as the To do page's Save my spot, opened over the reminder card.</summary>
    private bool SaveSpotFromCard(Window card)
    {
        var form = new SaveSpotWindow(card, _database.GetActiveSession(DateTime.Now)?.TaskTitle, spot =>
            _database.SaveSpotByHand(spot.Project, spot.Stopped, spot.Next, spot.Links, spot.Thoughts, DateTime.Now));
        if (form.ShowDialog() != true || form.Reply is not { } reply)
        {
            return false;
        }

        _itemsWindow.ShowReply(reply);
        RefreshStatus();
        return true;
    }
}
