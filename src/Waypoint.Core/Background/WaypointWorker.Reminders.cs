using System.Globalization;
using Waypoint.Core.Capture;
using Waypoint.Core.Discord;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Background;

/// <summary>Class alerts and reminders, and the notes that keep a reminder's card from going out twice.</summary>
public sealed partial class WaypointWorker
{
    private void CheckClassAlert()
    {
        if (_classAlerts?.Check(_clock()) is not { } alert)
        {
            return;
        }

        if (alert.Stage == ClassAlertStage.Warning)
        {
            var meeting = alert.Class.Meeting;
            var leaveAt = alert.LeaveAt.ToString("h:mm tt", CultureInfo.CurrentCulture);
            _ = NotifyAsync($"{meeting.Course} coming up", $"Leave by: {leaveAt}\nRoom: {meeting.Room}", CardTone.Attention);
            return;
        }

        // One message per class, so acknowledging here stops the tracker's repeats.
        _classAlerts.Acknowledge(alert.Class);
        bool first;
        lock (_leaveAlertsSent)
        {
            first = _leaveAlertsSent.Add(alert.Class);
        }

        if (first)
        {
            _ = NotifyAsync("Time to leave", $"Class: {alert.Class.Meeting.Course}\nRoom: {alert.Class.Meeting.Room}", CardTone.Urgent);
        }
    }

    private void CheckReminders()
    {
        // Assignment nudges wait out the quiet part of the night. Anything asked for, and exams, still come through.
        var now = _clock();
        var quiet = BedtimeSchedule.IsQuiet(now);
        DueReminder? reminder;
        lock (_activeReminders)
        {
            var dueNow = _database.GetDueReminders(now);
            DropStaleReminderSends(dueNow);
            reminder = dueNow.FirstOrDefault(due => (!quiet || due.Item.Source != ItemSource.Calendar) && !_activeReminders.Contains(ActiveKey(due)));
            if (reminder is null)
            {
                return;
            }

            // Tracked one by one, so a slow send or an unanswered card never holds up the next reminder.
            _activeReminders.Add(ActiveKey(reminder));
        }

        if (Discord is { } discord)
        {
            _ = SendReminderAsync(discord, reminder);
        }
        else
        {
            ShowReminder(reminder);
        }
    }

    private static (ItemSource, long) ActiveKey(DueReminder reminder) => (reminder.Item.Source, reminder.Id);

    private void Release(DueReminder reminder)
    {
        lock (_activeReminders)
        {
            _activeReminders.Remove(ActiveKey(reminder));
        }
    }

    private void ShowReminder(DueReminder reminder) => _host.ShowReminder(reminder, () => Release(reminder));

    /// <summary>Reminders go to Discord alone. The host's card is only for when Discord still can't be reached after retries.</summary>
    private async Task SendReminderAsync(DiscordBridge discord, DueReminder reminder)
    {
        // Only things that can be finished get the prompt. A nudge about an assignment or an exam alert doesn't finish it.
        // A dose is answered with Took it, so its card has buttons even though it repeats.
        var dose = reminder.Item is { Dose: true, Recurs: not null };
        var canFinish = dose || (reminder.Item.Recurs is null && (reminder.RepeatMinutes is not null || reminder.Item.Kind is ItemKind.Reminder or ItemKind.Task));
        var canStart = Activation.CanStart(reminder.Item);
        var waiting = _database.WaitingFor(reminder);
        var card = waiting is not null
            ? WaitingNudgeCard(reminder, waiting, DateOnly.FromDateTime(_clock()))
            : DiscordText.Reminder(reminder, DateOnly.FromDateTime(_clock()), canFinish, canStart);

        // The same offer the PC card makes: only on a reminder that can be started, and not right after a spot was saved.
        card = card with { SaveSpot = card.Transition && TransitionAssist.OffersSaveSpot(_database.GetLastSpotSaved(), _clock()), FeedbackFeature = Feedback.Reminder, FeedbackRef = ItemRef.Format(reminder.Item) };

        // The card is noted before it goes, so a crash before it's saved finds the card it sent instead of sending
        // another. The note is kept with Discord's nonce, so a try that went through unheard isn't made twice.
        var key = ReminderSendKey(reminder);
        var channel = discord.AlertsChannelFor();
        var send = _database.BeginDiscordSend(key, channel, _clock());
        var messageId = send.MessageId;
        if (messageId is null && !send.Fresh && channel is not null)
        {
            messageId = (await discord.FindSentAsync(channel, send.StartedAt, send.Nonce, card, known: KnownMessages(key))).MessageId;
        }

        var sent = messageId is not null;
        if (!sent)
        {
            sent = await AlertRetry.RunAsync(async () =>
            {
                var delivery = await discord.DeliverOnceAsync(channel, card, send.Nonce);
                messageId = delivery.MessageId;
                return delivery.Sent;
            }, RetryWait);
            DiscordHealth.Sent(sent, _clock());
        }

        if (!sent)
        {
            // Shown here instead, and answered here, so a later card for the same reminder starts afresh.
            _database.FinishDiscordSend(key);
            ShowReminder(reminder);
            return;
        }

        try
        {
            if (messageId is not null)
            {
                _database.SetDiscordSendMessage(key, messageId);
            }

            // The card's record, the reminder moving on, and the note going are one write, so a crash leaves the note
            // with the card in it and the next try saves that card instead of sending another.
            _database.InOneWrite(() =>
            {
                // A waiting nudge's buttons carry its item, so there's nothing to look up later.
                if (waiting is null && (canFinish || canStart) && messageId is not null)
                {
                    _database.TrackSentReminder(messageId, reminder.Item.Id, reminder.Item.Source, startOnly: !canFinish, sentAt: _clock(), dueOn: dose ? reminder.Item.DueDate : null);
                }

                _database.SettleSentReminder(reminder, _clock());
                _database.FinishDiscordSend(key);
                return 0;
            });
        }
        finally
        {
            Release(reminder);
        }

        _host.Changed();
    }

    /// <summary>
    /// One reminder's card, the same each time it's tried, until the reminder moves on. What it's for and when it's
    /// due are in it too, so a later reminder given the row id of one that's gone never takes that one's card.
    /// </summary>
    private static string ReminderSendKey(DueReminder reminder) =>
        $"{ReminderSendPrefix}{(reminder.Item.Source == ItemSource.Calendar ? "event" : "item")}:{reminder.Id}:{reminder.Item.Id}:{reminder.RemindAt}";

    private const string ReminderSendPrefix = "reminder:";

    /// <summary>Messages already kept as another card, note, or view, so a post looked for by its look never takes one.</summary>
    private HashSet<string> KnownMessages(string key)
    {
        var known = _database.GetTrackedMessages(key);
        known.UnionWith(Views.PlacedMessages());
        return known;
    }

    /// <summary>
    /// A card's note whose reminder is no longer due (finished, put off, or gone elsewhere) is let go, so it can't
    /// stay forever. A reminder still on its way is still due, so its note is kept.
    /// </summary>
    private void DropStaleReminderSends(IReadOnlyList<DueReminder> due)
    {
        var keys = due.Select(ReminderSendKey).ToHashSet(StringComparer.Ordinal);
        foreach (var send in _database.GetDiscordSends(ReminderSendPrefix))
        {
            if (!keys.Contains(send.Key))
            {
                _database.FinishDiscordSend(send.Key);
            }
        }
    }

    /// <summary>The pause between Discord retries. Tests set it to skip the waiting.</summary>
    public Func<TimeSpan, Task>? RetryWait { get; set; }

}
