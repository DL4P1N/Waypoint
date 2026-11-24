using Waypoint.Core.Capture;
using Waypoint.Core.Discord;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Background;

/// <summary>Sending Discord notices and handling the button presses on their cards.</summary>
public sealed partial class WaypointWorker
{
    /// <summary>Notifications go to Discord alone. The host shows it instead if Discord isn't set up, or still can't be reached after retries.</summary>
    /// <param name="feedback">Adds a small This didn't help kept under this feature (see <see cref="Feedback"/>), with no item.</param>
    /// <param name="bedtimeLabel">The words on <paramref name="bedtimeButton"/>, for a wind-down step's Done.</param>
    /// <remarks>Most callers don't wait for it, so a problem is logged here rather than lost, once until it goes out.</remarks>
    public Task NotifyAsync(string title, string text, CardTone tone = CardTone.Reminder, bool toBudgetChannel = false, bool canWait = false, AlertActionKind? bedtimeButton = null, IReadOnlyList<CardAction>? actions = null, bool toCaptureChannel = false, bool saveSpot = false, string? feedback = null, string? bedtimeLabel = null) =>
        Tracked(async () =>
        {
            try
            {
                await SendNoticeAsync(title, text, tone, toBudgetChannel, canWait, bedtimeButton, actions, toCaptureChannel, saveSpot, feedback, bedtimeLabel);
                ProblemCleared(NoticeProblem(title));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException && !Stopping(exception))
            {
                ReportOnce(NoticeProblem(title), exception);
            }
        });

    private static string NoticeProblem(string title) => $"notice:{title}";

    /// <summary>The same, handing back the Discord message's ID when it went to Discord, so the card can be found again.</summary>
    /// <param name="sendKey">
    /// For a notice that must go out once, such as a held one: it's noted under this key before it's sent and sent with
    /// Discord's nonce, so a try before that went through is found instead of sent again. The caller lets the note go.
    /// </param>
    private async Task<string?> SendNoticeAsync(string title, string text, CardTone tone = CardTone.Reminder, bool toBudgetChannel = false, bool canWait = false, AlertActionKind? bedtimeButton = null, IReadOnlyList<CardAction>? actions = null, bool toCaptureChannel = false, bool saveSpot = false, string? feedback = null, string? bedtimeLabel = null, string? sendKey = null, Action? tryLater = null)
    {
        if (canWait && BedtimeSchedule.IsQuiet(_clock()))
        {
            _database.HoldNotice(title, text, tone, toBudgetChannel);
            return null;
        }

        // Right after starting, Discord may still be connecting. The notice waits for it instead of showing here.
        var step = TimeSpan.FromSeconds(1);
        for (var waited = TimeSpan.Zero; WaitingForDiscord && waited < DiscordConnectGrace; waited += step)
        {
            await Pause(step);
        }

        if (Discord is not { } discord)
        {
            _host.ShowNotice(title, text);
            return null;
        }

        var card = DiscordText.Notice(title, text, tone) with { BedtimeButton = bedtimeButton, BedtimeLabel = bedtimeLabel, Actions = actions ?? [], SaveSpot = saveSpot, FeedbackFeature = feedback };
        string? id = null;
        var send = sendKey is null ? null : _database.BeginDiscordSend(sendKey, discord.AlertsChannelFor(toBudgetChannel), _clock());
        if (send is { MessageId: { } before })
        {
            id = before;
        }
        else if (send is { Fresh: false, Channel: { } channel })
        {
            var search = await discord.FindSentAsync(channel, send.StartedAt, send.Nonce, card, known: KnownMessages(send.Key));
            if (search.CantTell)
            {
                // Whether the earlier try went out can't be told, so it's never sent again from here. For a while
                // it's looked for again later. Once that's gone on as long as sending is tried, or Discord turns it
                // away for good, it shows on this PC instead and is let go.
                if (search.Result == SentLookup.Unknown && _clock() - send.StartedAt < AlertRetry.Total)
                {
                    tryLater?.Invoke();
                    return null;
                }

                DiscordHealth.Sent(false, _clock());
                _host.ShowNotice(title, text);
                return null;
            }

            id = search.MessageId;
        }

        var sent = id is not null || await AlertRetry.RunAsync(
            async () =>
            {
                var delivery = send is not null ? await discord.DeliverOnceAsync(send.Channel, card, send.Nonce)
                    : toCaptureChannel ? await discord.DeliverToCaptureAsync(card)
                    : await discord.DeliverAsync(card, toBudgetChannel);
                id = delivery.MessageId;
                return delivery.Sent;
            },
            Pause);
        DiscordHealth.Sent(sent, _clock());
        if (sent && send is not null && id is not null)
        {
            _database.SetDiscordSendMessage(send.Key, id);
        }

        if (!sent)
        {
            _host.ShowNotice(title, text);
        }

        return sent ? id : null;
    }

    /// <summary>A button pressed in Discord. Runs on the gateway's thread.</summary>
    public string? OnButtonPressed(string messageId, AlertActionKind kind)
    {
        _viewsDirty = true;

        // A session's buttons send the same words as typing, so both take the same path.
        var word = kind switch
        {
            AlertActionKind.DidIt => "did it",
            AlertActionKind.Smaller => "smaller",
            AlertActionKind.NotNow => "not now",
            AlertActionKind.Trial => "2 minutes",
            AlertActionKind.EveningStepDone => BedtimeRoutine.DoneText(BedtimeSchedule.Settings.EveningStepLabel()),
            AlertActionKind.WindDownStepDone => BedtimeRoutine.DoneText(BedtimeSchedule.Settings.WindDownStepLabel()),
            AlertActionKind.SnoozeBedtime => BedtimeRoutine.SnoozeWord,
            _ => null,
        };

        if (word is not null)
        {
            Captures.Capture(word, null, CaptureOrigin.Discord);
            _ = Captures.ProcessPendingAsync();
            return "Got it";
        }

        if (kind == AlertActionKind.StartNext)
        {
            if (Activation.PickTask(_database.WithoutQuietWaiting(_database.GetOpenItems(), DateOnly.FromDateTime(_clock())), null) is not { } next)
            {
                return CardLabels.NothingToStart;
            }

            Captures.StartOn(next, CaptureOrigin.Discord);
            return $"Started: {next.Title}";
        }

        if (kind == AlertActionKind.Start)
        {
            // The card, its answer, and the session are one write, so a failure partway leaves the card to press again.
            if (_database.StartFromReminder(messageId, _clock()) is not { } started)
            {
                return null;
            }

            Captures.Started(started.Reply, CaptureOrigin.Discord);
            return "Started";
        }

        var reply = kind is AlertActionKind.MoreTime or AlertActionKind.NotNowReminder
            ? _database.ApplyTransitionAction(messageId, kind, _clock())
            : _database.ApplyAlertAction(new AlertAction(messageId, kind), _clock(), DefaultSnoozeMinutes);
        _host.Changed();
        return reply;
    }
}
