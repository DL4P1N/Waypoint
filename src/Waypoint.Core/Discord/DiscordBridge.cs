using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Waypoint.Core.Data;

namespace Waypoint.Core.Discord;

/// <summary>
/// Sends alerts to the phone and takes captures typed there. New messages arrive live through the gateway, and
/// polling catches up on anything sent while the PC was off or the connection was down. Each message is handled
/// once either way. Only the owner is listened to, everywhere.
/// </summary>
public sealed partial class DiscordBridge(HttpClient http, WaypointDatabase database, DiscordSettings settings)
{
    private const string ApiBase = "https://discord.com/api/v10";
    public const string LastMessageKey = "discord_last_message";

    /// <summary>
    /// The last message read in the notes channel, kept apart from the capture channel's, as the channel and the
    /// message, so a notes channel moved elsewhere starts over instead of reading from a place in another channel.
    /// </summary>
    public const string LastNotesMessageKey = "discord_last_notes_message";
    private const int MaxFetched = 50;
    private const int MaxAlertsRead = 25;
    private const string DoneButton = "waypoint:done";
    private const string SnoozeButton = "waypoint:snooze";
    private const string StartButton = "waypoint:start";
    private const string DidItButton = "waypoint:did-it";
    private const string SmallerButton = "waypoint:smaller";
    private const string NotNowButton = "waypoint:not-now";
    private const string TrialButton = "waypoint:trial";
    private const string WindDownStepDoneButton = "waypoint:wind-down-step-done";
    private const string EveningStepDoneButton = "waypoint:evening-step-done";
    private const string SnoozeBedtimeButton = "waypoint:snooze-bedtime";
    private const string StartNextButton = "waypoint:start-next";
    private const string MoreTimeButton = "waypoint:more-time";
    private const string NotNowReminderButton = "waypoint:not-now-reminder";
    private const string SaveSpotButton = "waypoint:save-spot";
    private const string SpotForm = "waypoint:spot-form";
    private const int ShowForm = 9;
    private const int FormSent = 5;
    private const int TextInput = 4;
    private const int ShortText = 1;
    private const int LongText = 2;
    private const int MaxFieldText = 300;
    private const int MaxFormText = 4000;
    private const int MaxPlaceholder = 100;
    private const int MaxRows = 5;

    /// <summary>Starts the ID of every pop-up form but Save my spot's. What follows comes back to <see cref="Submitted"/>.</summary>
    public const string FormPrefix = "waypoint:form:";
    private const int ReplyToPresser = 4;
    private const int AcknowledgeOnly = 6;
    private const int UpdateMessage = 7;
    private const int DeferredReply = 5;
    private const int DeferredUpdate = 6;
    private const int SuggestionsReply = 8;
    private const int OptionTyped = 4;
    private const int Menu = 3;
    private const int MaxSuggestions = 25;
    private const int MaxChoiceText = 100;
    private const int MaxMenuChoices = 25;
    private const int MaxButtons = 5;
    private const int MaxButtonLabel = 80;

    /// <summary>Buttons that carry what they act on, such as a wait-list purchase or an item to put back.</summary>
    public const string ActionPrefix = "waypoint:act:";

    /// <summary>Menus on pinned views. They're told apart from alert buttons, since using one keeps the view.</summary>
    public const string ViewPrefix = "waypoint:view-";
    private const int OnlySeenByPresser = 64;
    private const int CommandUsed = 2;
    private const int ButtonPressed = 3;
    private const int ButtonRow = 1;
    private const int Button = 2;
    private const int GreenButton = 3;
    private const int GrayButton = 2;
    private const int MaxRateLimitTries = 3;
    public const string LastAlertKey = "discord_last_alert";

    /// <summary>A wait longer than this isn't worth sitting through, so the call counts as failed and is tried later.</summary>
    private static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromSeconds(30);

    private static readonly Dictionary<string, AlertActionKind> Buttons = new()
    {
        [DoneButton] = AlertActionKind.Done,
        [SnoozeButton] = AlertActionKind.Snooze,
        [StartButton] = AlertActionKind.Start,
        [DidItButton] = AlertActionKind.DidIt,
        [SmallerButton] = AlertActionKind.Smaller,
        [NotNowButton] = AlertActionKind.NotNow,
        [TrialButton] = AlertActionKind.Trial,
        [WindDownStepDoneButton] = AlertActionKind.WindDownStepDone,
        [EveningStepDoneButton] = AlertActionKind.EveningStepDone,
        [SnoozeBedtimeButton] = AlertActionKind.SnoozeBedtime,
        [StartNextButton] = AlertActionKind.StartNext,
        [MoreTimeButton] = AlertActionKind.MoreTime,
        [NotNowReminderButton] = AlertActionKind.NotNowReminder,
    };

    private static readonly string[] DoneEmoji = ["✅", "☑️", "✔️"];
    private static readonly string[] SnoozeEmoji = ["⏰", "\U0001F4A4"];

    // Discord expects bots to name themselves, and can turn away requests that don't.
    private const string UserAgent = "DiscordBot (waypoint, 1.0)";

    private readonly SemaphoreSlim _polling = new(1, 1);
    private bool _commandsRegistered;

    public string? LastError { get; private set; }

    /// <summary>Why the last pin didn't work, kept apart from LastError since a working poll clears that.</summary>
    public string? PinProblem { get; private set; }

    /// <summary>Why the last change to a pinned view was turned down, until one goes through.</summary>
    public string? ViewProblem { get; private set; }

    /// <summary>When a poll last reached Discord, so polling can slow down while the live connection is up.</summary>
    public DateTimeOffset? LastPolled { get; private set; }

    /// <summary>How long to wait out a rate limit. Swapped in tests so they don't sleep.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>
    /// A message handed over is finished by whatever saves its work. One not finished in this long is taken to have
    /// been cut off, such as by a crash or a close, and is handed over again on the next poll.
    /// </summary>
    public static readonly TimeSpan UnfinishedAfter = TimeSpan.FromMinutes(3);

    /// <summary>The time claims on messages are kept by. Tests swap it to move past <see cref="UnfinishedAfter"/>.</summary>
    public Func<DateTime> Clock { get; init; } = () => DateTime.Now;

    /// <summary>
    /// Whether a message's work is still running here, such as a slow file. One that is isn't handed over again
    /// however long it takes, and its claim is kept fresh instead.
    /// </summary>
    public Func<string, bool>? StillWorking { get; set; }

    /// <summary>The owner's new messages in the capture channel, live or polled.</summary>
    public Action<DiscordMessage>? Captured { get; set; }

    /// <summary>A reaction or typed reply on an alert. Returns what was done, or null if there was nothing to do.</summary>
    public Func<AlertAction, string?>? AlertActed { get; set; }

    /// <summary>A button press on a card Waypoint sent. Returns what was done, which replaces the card.</summary>
    public Func<string, AlertActionKind, string?>? Pressed { get; set; }

    public Func<SlashCommand, CommandReply>? Command { get; set; }

    /// <summary>Choices to show while an option is being typed. Given the command so far and the option's name.</summary>
    public Func<SlashCommand, string, IReadOnlyList<MenuChoice>>? Suggest { get; set; }

    /// <summary>A pick from a pinned view's menu, given the menu, the message, and what was picked. Returns the view drawn again, or null to leave it.</summary>
    public Func<string, string, IReadOnlyList<string>, DiscordCard?>? Picked { get; set; }

    /// <summary>Raised when the live connection comes up or resumes, so anything missed meanwhile is polled for.</summary>
    public event Action? Connected;

    public string BotToken => settings.BotToken;

    /// <summary>Returns the sent message's ID, or null if it wasn't sent.</summary>
    public Task<string?> SendAlertAsync(DiscordCard card) => SendAsync(settings.AlertsChannelId, card);

    /// <summary>One try at an alert. This reports its own failure, since polling can clear LastError at any moment.</summary>
    public Task<DiscordDelivery> DeliverAsync(DiscordCard card, bool toBudgetChannel = false) =>
        TrySendAsync(toBudgetChannel ? settings.BudgetChannelId : settings.AlertsChannelId, card);

    /// <summary>
    /// One try at a card that asks for a typed answer, so it goes where typing is read. Without a capture channel
    /// it goes to alerts instead.
    /// </summary>
    public Task<DiscordDelivery> DeliverToCaptureAsync(DiscordCard card) => TrySendAsync(settings.CaptureChannelId ?? settings.AlertsChannelId, card);

    public Task<string?> SendCaptureReplyAsync(DiscordCard card) => SendAsync(settings.CaptureChannelId, card);

    /// <summary>Answers a capture under the message it came from, so it's clear which one this is about.</summary>
    public async Task<string?> ReplyAsync(string? toMessageId, DiscordCard card, string? nonce = null) =>
        (await TrySendAsync(settings.CaptureChannelId, card, toMessageId, nonce)).MessageId;

    /// <summary>One try at answering a capture under its message, carrying a nonce so trying again soon after never makes a second.</summary>
    public Task<DiscordDelivery> DeliverReplyAsync(string? toMessageId, DiscordCard card, string nonce) =>
        TrySendAsync(settings.CaptureChannelId, card, toMessageId, nonce);

    /// <summary>Answers a message sent in the notes channel, under it.</summary>
    public async Task<string?> ReplyInNotesAsync(string? toMessageId, DiscordCard card, string? nonce = null) =>
        (await TrySendAsync(PinnedViews.ChannelFor(database, settings, DiscordViews.Notes) ?? settings.CaptureChannelId, card, toMessageId, nonce)).MessageId;

    public Task<string?> SendBudgetAsync(DiscordCard card) => SendAsync(settings.BudgetChannelId, card);

    /// <summary>Sends a card to any channel, for pinned views. Returns its ID, or null if it wasn't sent.</summary>
    public Task<string?> PostAsync(string channel, DiscordCard card) => SendAsync(channel, card);

    /// <summary>
    /// One try at a post noted beforehand, carrying that note's nonce, so trying again soon after never makes a second.
    /// Without a channel there's nowhere to send it, which counts as sent, the same as for any alert.
    /// </summary>
    public Task<DiscordDelivery> DeliverOnceAsync(string? channel, DiscordCard card, string nonce) =>
        TrySendAsync(channel, card, nonce: nonce);

    /// <summary>The bot's own user ID, once Discord has said it on connecting. Null before that.</summary>
    public string? BotUserId { get; private set; }

    /// <summary>Where alerts go, or with <paramref name="toBudgetChannel"/>, where budget alerts go.</summary>
    public string? AlertsChannelFor(bool toBudgetChannel = false) => toBudgetChannel ? settings.BudgetChannelId : settings.AlertsChannelId;

    /// <summary>Changes a sent card in place. Editing makes no sound on the phone, so a view can change as often as it needs to.</summary>
    public async Task<EditOutcome> EditAsync(string channel, string messageId, DiscordCard card)
    {
        try
        {
            using var response = await SendWithLimitsAsync(() =>
            {
                var request = NewRequest(HttpMethod.Patch, $"{ApiBase}/channels/{channel}/messages/{messageId}");
                request.Content = JsonContent.Create(MessageBody(card));
                return request;
            });
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return EditOutcome.Missing;
            }

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                ViewProblem = "Discord turned down a change to a pinned view, so it stays as it was until what it shows changes.";
                return EditOutcome.Refused;
            }

            response.EnsureSuccessStatusCode();
            ViewProblem = null;
            return EditOutcome.Edited;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            return EditOutcome.Failed;
        }
    }

    /// <summary>
    /// Pins a card so it's easy to find at the top of the channel. This needs the Pin Messages permission. Without
    /// it the card still works, so a refusal is only noted. The older address is tried if the newer one isn't there.
    /// </summary>
    public async Task<bool> PinAsync(string channel, string messageId)
    {
        try
        {
            foreach (var url in new[] { $"{ApiBase}/channels/{channel}/messages/pins/{messageId}", $"{ApiBase}/channels/{channel}/pins/{messageId}" })
            {
                using var response = await SendWithLimitsAsync(() => NewRequest(HttpMethod.Put, url));
                if (response.IsSuccessStatusCode)
                {
                    PinProblem = null;
                    return true;
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    PinProblem = "Views can't be pinned, so give the bot the Pin Messages permission in the server or channel settings.";
                    return false;
                }

                if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    PinProblem = $"Pinning a view didn't work ({(int)response.StatusCode}).";
                    return false;
                }
            }

            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            return false;
        }
    }

    /// <summary>Removes a card Waypoint sent. One that's already gone counts as removed.</summary>
    public async Task<bool> DeleteAsync(string channel, string messageId)
    {
        try
        {
            using var response = await SendWithLimitsAsync(() => NewRequest(HttpMethod.Delete, $"{ApiBase}/channels/{channel}/messages/{messageId}"));
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            return false;
        }
    }

    /// <summary>For the Settings page. It goes to the alerts channel, or capture if there isn't one.</summary>
    public async Task<string?> SendTestAsync()
    {
        var channel = settings.AlertsChannelId ?? settings.CaptureChannelId ?? settings.BudgetChannelId;
        if (channel is null)
        {
            return "No channels are set.";
        }

        var sent = await TrySendAsync(channel, DiscordText.Notice("Test from Waypoint", "If you can see this, alerts will reach you here.", CardTone.Finished));
        return sent.Sent ? null : LastError ?? "It didn't send.";
    }

    /// <summary>Everything the live connection passes on. Unknown events are ignored.</summary>
    public async Task HandleEventAsync(string type, JsonElement data)
    {
        try
        {
            switch (type)
            {
                case "READY":
                    if (data.TryGetProperty("user", out var self) && Text(self, "id") is { } botId)
                    {
                        BotUserId = botId;
                    }

                    if (data.TryGetProperty("application", out var application) && Text(application, "id") is { } applicationId)
                    {
                        await RegisterCommandsAsync(applicationId);
                    }

                    Connected?.Invoke();
                    break;
                case "RESUMED":
                    Connected?.Invoke();
                    break;
                case "MESSAGE_CREATE":
                    await HandleMessageAsync(data);
                    break;
                case "MESSAGE_REACTION_ADD":
                    await HandleReactionAsync(data);
                    break;
                case "INTERACTION_CREATE":
                    // Its promise started counting when it was read. Only the change it makes waits its turn here.
                    var pending = Text(data, "id") is { } interactionId && _arrived.TryRemove(interactionId, out var early) ? early : Arrive(data);
                    await AnswerInteraction(data, Pressed ?? ((_, _) => null), Command, pending).InOrder;
                    break;
                case "THREAD_CREATE":
                    await HandleThreadCreateAsync(data);
                    break;
                case "THREAD_DELETE":
                    HandleThreadDelete(data);
                    break;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
        }
    }

    /// <summary>
    /// A message sent while connected, or one handed over again because its work was cut off. Polling skips it
    /// while it's claimed here, and for good once whatever saves its work marks it finished.
    /// </summary>
    /// <returns>True when it was handed over, false when it's one to act on that's done or being worked on, and null when it isn't one Waypoint acts on.</returns>
    private async Task<bool?> HandleMessageAsync(JsonElement message, Action<DiscordMessage>? onCapture = null)
    {
        if (!IsFromOwner(message) || Text(message, "id") is not { } id)
        {
            return null;
        }

        var channel = Text(message, "channel_id");
        if (channel == settings.CaptureChannelId && ReadMessage(message) is { } read)
        {
            if (!FirstTime(id, channel))
            {
                return false;
            }

            (onCapture ?? Captured)?.Invoke(read);
            return true;
        }

        if (channel == settings.AlertsChannelId && ReplyKind(Text(message, "content")) is { } kind)
        {
            if (!FirstTime(id, channel))
            {
                return false;
            }

            var target = message.TryGetProperty("message_reference", out var reference) ? Text(reference, "message_id") : null;
            await ActOnAlertAsync(new AlertAction(target, kind, id));
            return true;
        }

        if (channel is not null && channel == PinnedViews.ChannelFor(database, settings, DiscordViews.Notes) && ReadMessage(message) is { } sent)
        {
            if (!FirstTime(id, channel))
            {
                return false;
            }

            NotesSent?.Invoke(sent);
            return true;
        }

        if (channel is not null)
        {
            await HandleThreadMessageAsync(channel, id, message);
        }

        return null;
    }

    private async Task HandleReactionAsync(JsonElement reaction)
    {
        if (Text(reaction, "user_id") != settings.OwnerUserId || Text(reaction, "channel_id") != settings.AlertsChannelId || Text(reaction, "message_id") is not { } id)
        {
            return;
        }

        var name = reaction.TryGetProperty("emoji", out var emoji) ? Text(emoji, "name") : null;
        AlertActionKind? kind = name is null ? null : DoneEmoji.Contains(name) ? AlertActionKind.Done : SnoozeEmoji.Contains(name) ? AlertActionKind.Snooze : null;
        if (kind is { } action)
        {
            await ActOnAlertAsync(new AlertAction(id, action));
        }
    }

    /// <summary>Applies one action and shows it was handled: the card itself turns gray when it's known, otherwise a short note.</summary>
    public async Task ActOnAlertAsync(AlertAction action)
    {
        // An answer with no card in mind is about the newest reminder, so that's the card that turns gray.
        if (action.MessageId is null && database.LatestSentReminderMessage(action.SentAs) is { } latest)
        {
            action = action with { MessageId = latest };
        }

        var outcome = AlertActed?.Invoke(action);

        // Answered, or there was nothing for it to do, so a typed reply isn't handed over again.
        if (action.SentAs is { } typed && AlertActed is not null)
        {
            database.FinishDiscordMessage(typed, Clock());
        }

        if (outcome is null)
        {
            return;
        }

        if (action.MessageId is { } id && await MarkHandledAsync(id, outcome))
        {
            return;
        }

        await SendAlertAsync(DiscordText.Notice(outcome, "", CardTone.Finished));
    }

    /// <summary>Turns a sent alert gray with its buttons gone, as pressing one of them does.</summary>
    public Task<bool> MarkHandledAsync(string messageId, string outcome) => MarkHandledAsync(settings.AlertsChannelId, messageId, outcome);

    /// <summary>The same for a card that asked for a typed answer, which went where typing is read.</summary>
    public Task<bool> MarkCaptureCardHandledAsync(string messageId, string outcome) =>
        MarkHandledAsync(settings.CaptureChannelId ?? settings.AlertsChannelId, messageId, outcome);

    private async Task<bool> MarkHandledAsync(string? channel, string messageId, string outcome)
    {
        if (channel is null)
        {
            return false;
        }

        try
        {
            JsonElement? embed;
            using (var current = await SendWithLimitsAsync(() => NewRequest(HttpMethod.Get, $"{ApiBase}/channels/{channel}/messages/{messageId}")))
            {
                current.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await current.Content.ReadAsStringAsync());
                embed = FirstEmbed(document.RootElement)?.Clone();
            }

            using var response = await SendWithLimitsAsync(() =>
            {
                var request = NewRequest(HttpMethod.Patch, $"{ApiBase}/channels/{channel}/messages/{messageId}");
                request.Content = JsonContent.Create(Finished(embed, outcome));
                return request;
            });
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            LastError = exception.Message;
            return false;
        }
    }
}
