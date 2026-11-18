using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Discord;

/// <summary>
/// A private server's bot. Only OwnerUserId can add captures. Leaving a channel out turns that channel off.
/// ToDoChannelId, ListsChannelId, NotesChannelId and HistoryChannelId keep pinned views there, which /pinhere can also set from Discord.
/// NotesForumChannelId and ListsForumChannelId are forum channels where each note and each list is its own post.
/// </summary>
public sealed record DiscordSettings(
    string BotToken,
    string OwnerUserId,
    string? CaptureChannelId = null,
    string? AlertsChannelId = null,
    string? BudgetChannelId = null,
    string? ToDoChannelId = null,
    string? ListsChannelId = null,
    string? NotesChannelId = null,
    string? HistoryChannelId = null,
    string? NotesForumChannelId = null,
    string? ListsForumChannelId = null);

public enum AlertActionKind
{
    Done,
    Snooze,
    Start,
    DidIt,
    Smaller,
    NotNow,
    Trial,
    WindDownStepDone,
    EveningStepDone,
    SnoozeBedtime,

    /// <summary>Starts on whatever is most pressing, from the /now card.</summary>
    StartNext,
    MoreTime,
    NotNowReminder,
}

/// <summary>How one send went. A channel that's turned off counts as sent, since nothing was meant to go there.</summary>
public sealed record DiscordDelivery(string? MessageId, bool Sent);

/// <summary>
/// What the owner did to an alert. No message ID means the latest reminder. SentAs is the owner's own typed reply,
/// when it came as one, so it's marked as dealt with in the same write as what it did.
/// </summary>
public sealed record AlertAction(string? MessageId, AlertActionKind Kind, string? SentAs = null);

/// <summary>A file on a message. Url is Discord's own download link. DurationSeconds is set on a voice message.</summary>
public sealed record DiscordAttachment(string Id, string FileName, long Size, string Url, string? ContentType, double? DurationSeconds = null);

/// <summary>
/// One of the owner's messages in the capture channel. Text can be empty when it only carries files. ReplyTo is the
/// message it answers with Discord's Reply, and ReplyToText is what that message's card said, when Discord sends it.
/// </summary>
public sealed record DiscordMessage(string Id, string Text, DateTimeOffset SentAt, IReadOnlyList<DiscordAttachment> Attachments, string? ReplyTo = null, string? ReplyToText = null);

/// <summary>A slash command as it was used. Options are the values typed into it, by name. ChannelId is where it was used.</summary>
public sealed record SlashCommand(string Name, IReadOnlyDictionary<string, string> Options, string? ChannelId = null);

/// <summary>
/// How editing a sent message went. Missing means it was deleted, so it has to be sent again. Refused means
/// Discord turned the content down, so the same content would only be turned down again.
/// </summary>
public enum EditOutcome
{
    Edited,
    Missing,
    Failed,
    Refused,
}

/// <summary>What a slash command answers with. Private replies are only seen by the owner and fade on their own.</summary>
public sealed record CommandReply(DiscordCard Card, bool Private = false)
{
    /// <summary>A pop-up form to open instead of answering with the card.</summary>
    public CardForm? Form { get; init; }
}

/// <summary>A button pressed on a card. Kind is set for the fixed buttons, and CustomId says what a carrying button acts on.</summary>
public sealed record ButtonPress(string CustomId, AlertActionKind? Kind, string MessageId, string? ChannelId);

/// <summary>What a press comes to: a card to replace the pressed one, or a line to show under it as it turns gray.</summary>
public sealed record ButtonResult(DiscordCard? Card = null, string? Outcome = null)
{
    /// <summary>A pop-up form to open, leaving the pressed card as it was.</summary>
    public CardForm? Form { get; init; }
}

/// <summary>How a press will be answered, known before the work is done: a slow one is promised differently for each.</summary>
public enum PressReply
{
    /// <summary>The pressed message is replaced or edited.</summary>
    Update,

    /// <summary>A reply only the owner sees, leaving the pressed message as it is.</summary>
    Private,

    /// <summary>A pop-up form, which only an immediate answer can open.</summary>
    Form,
}

/// <summary>
/// Sends alerts to the phone and takes captures typed there. New messages arrive live through the gateway, and
/// polling catches up on anything sent while the PC was off or the connection was down. Each message is handled
/// once either way. Only the owner is listened to, everywhere.
/// </summary>
public sealed partial class DiscordBridge(HttpClient http, WaypointDatabase database, DiscordSettings settings)
{
    private const string ApiBase = "https://discord.com/api/v10";
    public const string LastMessageKey = "discord_last_message";
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

    private static readonly string[] DownloadHosts = ["cdn.discordapp.com", "media.discordapp.net"];

    private const int MaxDownloadRedirects = 3;

    /// <summary>
    /// The client attachments are downloaded with. It shouldn't follow redirects itself, so each hop is checked against
    /// Discord's file hosts first. Without one, the main client is used and where it ended up is checked.
    /// </summary>
    public HttpClient? Downloads { get; init; }

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
    public async Task<string?> ReplyAsync(string? toMessageId, DiscordCard card) =>
        (await TrySendAsync(settings.CaptureChannelId, card, toMessageId)).MessageId;

    /// <summary>Answers a message sent in the notes channel, under it.</summary>
    public async Task<string?> ReplyInNotesAsync(string? toMessageId, DiscordCard card) =>
        (await TrySendAsync(PinnedViews.ChannelFor(database, settings, DiscordViews.Notes) ?? settings.CaptureChannelId, card, toMessageId)).MessageId;

    public Task<string?> SendBudgetAsync(DiscordCard card) => SendAsync(settings.BudgetChannelId, card);

    /// <summary>Sends a card to any channel, for pinned views. Returns its ID, or null if it wasn't sent.</summary>
    public Task<string?> PostAsync(string channel, DiscordCard card) => SendAsync(channel, card);

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
                    await HandleInteractionAsync(data, Pressed ?? ((_, _) => null), Command);
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
        if (action.MessageId is null && database.LatestSentReminderMessage() is { } latest)
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

    /// <summary>
    /// Answers a button press or a slash command. Discord allows three seconds, so a quick answer goes straight back.
    /// One that takes longer is promised first, then filled in when it's ready, so Discord never says the app
    /// didn't respond. A pressed card turns gray with its buttons gone, unless the press hands back a new card.
    /// Anyone but the owner is turned away.
    /// </summary>
    public async Task HandleInteractionAsync(JsonElement interaction, Func<string, AlertActionKind, string?> apply, Func<SlashCommand, CommandReply>? command = null)
    {
        try
        {
            var type = interaction.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.Number ? kind.GetInt32() : ButtonPressed;

            // Make it smaller waits on Claude, so the card says so at once and becomes the step when it's ready.
            if (type == ButtonPressed && IsOwnersPress(interaction, SmallerButton))
            {
                await CallbackAsync(interaction, Body(new Answer(UpdateMessage, MessageBody(DiscordText.Notice(Activation.SmallerWorking, "")))));
                if (await Respond(interaction, apply, command) is { Data: { } step })
                {
                    await EditOriginalAsync(interaction, step);
                }

                return;
            }

            // Suggestions can't be promised, so they're always answered at once.
            var work = type == OptionTyped ? Respond(interaction, apply, command) : Task.Run(() => Respond(interaction, apply, command));
            var press = type == ButtonPressed ? HowPressWillBeAnswered(interaction) : PressReply.Update;
            if (type == OptionTyped || press == PressReply.Form || await Task.WhenAny(work, Task.Delay(DeferAfter)) == work)
            {
                if (await work is { } answer)
                {
                    await CallbackAsync(interaction, Body(answer));
                }

                return;
            }

            // Slow, so Discord is told the answer is coming. A command's answer then shows only to the owner.
            var replies = type is CommandUsed or FormSent || press == PressReply.Private;
            await CallbackAsync(interaction, Body(new Answer(replies ? DeferredReply : DeferredUpdate, replies ? new { flags = OnlySeenByPresser } : null)));
            if (await work is { Data: { } late })
            {
                await EditOriginalAsync(interaction, late);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
        }
    }

    private PressReply HowPressWillBeAnswered(JsonElement interaction)
    {
        if (!interaction.TryGetProperty("data", out var data) || Text(data, "custom_id") is not { } id)
        {
            return PressReply.Update;
        }

        // This didn't help keeps the click and opens its form, which can't wait either.
        if (id == SaveSpotButton || id.StartsWith(DidntHelpPrefix, StringComparison.Ordinal))
        {
            return PressReply.Form;
        }

        if (PressWillReply is not { } how)
        {
            return PressReply.Update;
        }

        try
        {
            var message = interaction.TryGetProperty("message", out var pressed) ? Text(pressed, "id") ?? "" : "";
            return how(id, message, PickedValues(data));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            return PressReply.Update;
        }
    }

    private static List<string> PickedValues(JsonElement data) =>
        data.TryGetProperty("values", out var picked) && picked.ValueKind == JsonValueKind.Array
            ? picked.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToList()
            : [];

    /// <summary>Fills in an answer that was promised or shown as working, on the message the interaction answered.</summary>
    private async Task EditOriginalAsync(JsonElement interaction, object data)
    {
        if (Text(interaction, "application_id") is not { } application)
        {
            return;
        }

        using var edited = await SendWithLimitsAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Patch, $"{ApiBase}/webhooks/{application}/{Text(interaction, "token")}/messages/@original") { Content = JsonContent.Create(data) };
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            return request;
        });
        edited.EnsureSuccessStatusCode();
    }

    /// <summary>Whether the owner pressed this button. Anyone else's press goes the usual way and is turned away there.</summary>
    private bool IsOwnersPress(JsonElement interaction, string button)
    {
        if (!interaction.TryGetProperty("data", out var data) || Text(data, "custom_id") != button)
        {
            return false;
        }

        var user = interaction.TryGetProperty("member", out var member) ? member.GetProperty("user") : interaction.TryGetProperty("user", out var direct) ? direct : default;
        return user.ValueKind == JsonValueKind.Object && Text(user, "id") == settings.OwnerUserId;
    }

    /// <summary>
    /// The Save my spot form was sent: what it's for, where it stopped, and what's next, any of them empty. It hands
    /// back the line to show. The form only offers it, and nothing goes to the sorter.
    /// </summary>
    public Func<string?, string?, string?, string>? SpotSaved { get; set; }

    /// <summary>
    /// Any other pop-up form was sent: its ID after <see cref="FormPrefix"/>, and each box's value by its ID. The
    /// reply is what to show.
    /// </summary>
    public Func<string, IReadOnlyDictionary<string, string>, CommandReply>? Submitted { get; set; }

    /// <summary>One of the owner's messages in the notes channel, such as a file to make notes from.</summary>
    public Action<DiscordMessage>? NotesSent { get; set; }

    /// <summary>What the Save my spot form fills in for what it's for, such as the session going now.</summary>
    public Func<string?>? SpotProject { get; set; }

    /// <summary>
    /// A carried button that also opens the Save my spot form, given the action after <see cref="ActionPrefix"/>. It
    /// does its own part first, such as ending tonight's check-ins, and returns true to open the form.
    /// </summary>
    public Func<string, bool>? OpensSpotForm { get; set; }

    /// <summary>
    /// How a press (its ID, the pressed message, and any menu values) will be answered, so a slow one is promised
    /// the right way: a private reply must not be filled in on the pressed message, and a form can't wait.
    /// </summary>
    public Func<string, string, IReadOnlyList<string>, PressReply>? PressWillReply { get; set; }

    /// <summary>How long an answer may take before Discord is told it's coming. Tests shorten it.</summary>
    public TimeSpan DeferAfter { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Any button press, before the fixed handling. A card handed back replaces the pressed one, and null leaves
    /// the press to <see cref="Pressed"/>. Buttons that carry what they act on only come here.
    /// </summary>
    public Func<ButtonPress, Task<ButtonResult?>>? Acted { get; set; }

    /// <summary>Said instead of an error's own text, which is meant for the log and can hold paths or codes.</summary>
    public const string WentWrong = "Something went wrong on Waypoint's side, so nothing changed. Try again in a moment.";

    private sealed record Answer(int Type, object? Data = null);

    private static Dictionary<string, object> Body(Answer answer)
    {
        var body = new Dictionary<string, object> { ["type"] = answer.Type };
        if (answer.Data is { } data)
        {
            body["data"] = data;
        }

        return body;
    }

    private async Task CallbackAsync(JsonElement interaction, object body)
    {
        var url = $"{ApiBase}/interactions/{Text(interaction, "id")}/{Text(interaction, "token")}/callback";
        using var answer = await SendWithLimitsAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            return request;
        });
        answer.EnsureSuccessStatusCode();
    }

    private async Task<Answer?> Respond(JsonElement interaction, Func<string, AlertActionKind, string?> apply, Func<SlashCommand, CommandReply>? command)
    {
        var type = interaction.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.Number ? kind.GetInt32() : ButtonPressed;
        if (!interaction.TryGetProperty("data", out var data))
        {
            return null;
        }

        var user = interaction.TryGetProperty("member", out var member) ? member.GetProperty("user") : interaction.GetProperty("user");
        var isOwner = Text(user, "id") == settings.OwnerUserId;
        var channel = Text(interaction, "channel_id");

        if (type == OptionTyped)
        {
            return new Answer(SuggestionsReply, new { choices = isOwner ? Suggestions(data, channel) : [] });
        }

        if (type == FormSent && IsFeedbackForm(Text(data, "custom_id")))
        {
            return isOwner ? NoteFeedback(Text(data, "custom_id")!, data) : OnlyOwner();
        }

        if (type == FormSent && Text(data, "custom_id") is { } formId && formId.StartsWith(FormPrefix, StringComparison.Ordinal))
        {
            if (Submitted is not { } submitted)
            {
                return null;
            }

            if (!isOwner)
            {
                return OnlyOwner();
            }

            CommandReply answered;
            try
            {
                answered = submitted(formId[FormPrefix.Length..], FormValues(data));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LastError = exception.Message;
                answered = new CommandReply(DiscordText.Notice("That didn't work", WentWrong, CardTone.Attention), true);
            }

            return new Answer(ReplyToPresser, MessageBody(answered.Card, flags: answered.Private ? OnlySeenByPresser : null));
        }

        if (type == FormSent)
        {
            if (Text(data, "custom_id") != SpotForm || SpotSaved is not { } save)
            {
                return null;
            }

            if (!isOwner)
            {
                return OnlyOwner();
            }

            var values = FormValues(data);
            string saved;
            try
            {
                saved = save(values.GetValueOrDefault("project"), values.GetValueOrDefault("stopped"), values.GetValueOrDefault("next"));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LastError = exception.Message;
                saved = WentWrong;
            }

            return new Answer(ReplyToPresser, MessageBody(DiscordText.Notice(saved, "", CardTone.Finished), flags: OnlySeenByPresser));
        }

        if (type == CommandUsed)
        {
            if (command is null || Text(data, "name") is not { } name)
            {
                return null;
            }

            if (!isOwner)
            {
                return OnlyOwner();
            }

            // With nothing typed, Save my spot opens the same short form as the button on a card.
            if (name is "save-spot" or "savespot" && !Options(data).ContainsKey("stopped"))
            {
                return new Answer(ShowForm, SpotFormBody());
            }

            CommandReply reply;
            try
            {
                reply = command(new SlashCommand(name, Options(data), channel));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LastError = exception.Message;
                reply = new CommandReply(DiscordText.Notice("That didn't work", WentWrong, CardTone.Attention), true);
            }

            return reply.Form is { } form
                ? new Answer(ShowForm, FormBody(form))
                : new Answer(ReplyToPresser, MessageBody(reply.Card, flags: reply.Private ? OnlySeenByPresser : null));
        }

        var button = Text(data, "custom_id");
        if (button?.StartsWith(ViewPrefix, StringComparison.Ordinal) == true && interaction.TryGetProperty("message", out var view))
        {
            return isOwner ? Pick(button, Text(view, "id")!, data) : OnlyOwner();
        }

        // Save my spot opens its form straight away, and the card stays as it was, so Start is still there after.
        if (button == SaveSpotButton)
        {
            return isOwner ? new Answer(ShowForm, SpotFormBody()) : OnlyOwner();
        }

        if (button?.StartsWith(DidntHelpPrefix, StringComparison.Ordinal) == true)
        {
            return isOwner ? DidntHelp(button) : OnlyOwner();
        }

        AlertActionKind? known = button is not null && Buttons.TryGetValue(button, out var found) ? found : null;
        var carries = button?.StartsWith(ActionPrefix, StringComparison.Ordinal) == true;
        if (button is null || (known is null && !carries) || !interaction.TryGetProperty("message", out var message))
        {
            return null;
        }

        if (!isOwner)
        {
            return OnlyOwner();
        }

        if (carries && SpotSaved is not null && OpensSpot(button[ActionPrefix.Length..]))
        {
            return new Answer(ShowForm, SpotFormBody());
        }

        var messageId = Text(message, "id")!;
        if (Acted is { } acted)
        {
            ButtonResult? result;
            try
            {
                result = await acted(new ButtonPress(button, known, messageId, channel));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LastError = exception.Message;
                result = new ButtonResult(Outcome: WentWrong);
            }

            if (result?.Form is { } form)
            {
                return new Answer(ShowForm, FormBody(form));
            }

            if (result?.Card is { } card)
            {
                return card.Private
                    ? new Answer(ReplyToPresser, MessageBody(card, flags: OnlySeenByPresser))
                    : new Answer(UpdateMessage, MessageBody(card));
            }

            if (result?.Outcome is { } outcome)
            {
                return new Answer(UpdateMessage, Finished(FirstEmbed(message), outcome));
            }
        }

        return known is { } pressed
            ? new Answer(UpdateMessage, Finished(FirstEmbed(message), apply(messageId, pressed) ?? "Already taken care of"))
            : new Answer(UpdateMessage, Finished(FirstEmbed(message), "That button has run out. Try the command again."));
    }

    private bool OpensSpot(string action)
    {
        try
        {
            return OpensSpotForm?.Invoke(action) == true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            return false;
        }
    }

    /// <summary>The Save my spot form: three short boxes, none required, with what's going now filled in.</summary>
    private object SpotFormBody()
    {
        string? project = null;
        try
        {
            project = SpotProject?.Invoke();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
        }

        object Box(string id, string label, int style, string? value = null) => new
        {
            type = ButtonRow,
            components = new object[]
            {
                value is null
                    ? (object)new { type = TextInput, custom_id = id, label, style, required = false, max_length = MaxFieldText }
                    : new { type = TextInput, custom_id = id, label, style, required = false, max_length = MaxFieldText, value = Shorten(value, MaxFieldText) },
            },
        };

        return new
        {
            custom_id = SpotForm,
            title = CardLabels.SaveSpot,
            components = new[]
            {
                Box("project", "What you're working on", ShortText, string.IsNullOrWhiteSpace(project) ? null : project),
                Box("stopped", "Where you stopped", LongText),
                Box("next", "What's next when you come back", ShortText),
            },
        };
    }

    /// <summary>The boxes of a sent form, by their IDs.</summary>
    private static Dictionary<string, string> FormValues(JsonElement data)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!data.TryGetProperty("components", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return values;
        }

        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("components", out var boxes) || boxes.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var box in boxes.EnumerateArray())
            {
                if (Text(box, "custom_id") is { } id && Text(box, "value") is { } value)
                {
                    values[id] = value;
                }
            }
        }

        return values;
    }

    /// <summary>A view keeps its place. It's drawn again with the change, or left alone if nothing changed.</summary>
    private Answer Pick(string menu, string messageId, JsonElement data)
    {
        var values = PickedValues(data);

        DiscordCard? card;
        try
        {
            card = Picked?.Invoke(menu, messageId, values);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            card = null;
        }

        return card is null ? new Answer(AcknowledgeOnly)
            : card.Private ? new Answer(ReplyToPresser, MessageBody(card, flags: OnlySeenByPresser))
            : new Answer(UpdateMessage, MessageBody(card));
    }

    /// <summary>A pop-up form, kept inside Discord's limits: five boxes, and short titles and labels.</summary>
    private static object FormBody(CardForm form) => new
    {
        custom_id = FormPrefix + form.Id,
        title = Shorten(form.Title, CardForm.MaxTitle),
        components = form.Boxes.Take(CardForm.MaxBoxes).Select(box =>
        {
            var input = new Dictionary<string, object>
            {
                ["type"] = TextInput,
                ["custom_id"] = box.Id,
                ["label"] = Shorten(box.Label, CardForm.MaxLabel),
                ["style"] = box.Long ? LongText : ShortText,
                ["required"] = box.Required,
                ["max_length"] = Math.Clamp(box.MaxLength, 1, MaxFormText),
            };
            if (!string.IsNullOrEmpty(box.Value))
            {
                input["value"] = Shorten(box.Value, Math.Clamp(box.MaxLength, 1, MaxFormText));
            }

            if (box.Placeholder is { Length: > 0 } placeholder)
            {
                input["placeholder"] = Shorten(placeholder, MaxPlaceholder);
            }

            return (object)new { type = ButtonRow, components = new object[] { input } };
        }).ToArray(),
    };

    private object[] Suggestions(JsonElement data, string? channel)
    {
        if (Suggest is null || Text(data, "name") is not { } name || Focused(data) is not { } focused)
        {
            return [];
        }

        try
        {
            return Suggest(new SlashCommand(name, Options(data), channel), focused)
                .Take(MaxSuggestions)
                .Select(choice => (object)new { name = Shorten(choice.Label, MaxChoiceText), value = Shorten(choice.Value, MaxChoiceText) })
                .ToArray();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            return [];
        }
    }

    /// <summary>The option being typed while suggestions are asked for.</summary>
    private static string? Focused(JsonElement data) =>
        data.TryGetProperty("options", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(option => option.TryGetProperty("focused", out var focused) && focused.ValueKind == JsonValueKind.True).Select(option => Text(option, "name")).FirstOrDefault()
            : null;

    private static string Shorten(string text, int limit) => DiscordText.Shorten(text, limit);

    private static Answer OnlyOwner() => new(ReplyToPresser, new { content = "Only the owner can do that.", flags = OnlySeenByPresser });

    private static Dictionary<string, string> Options(JsonElement data)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (data.TryGetProperty("options", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in list.EnumerateArray())
            {
                if (Text(option, "name") is { } name && option.TryGetProperty("value", out var value))
                {
                    options[name] = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
                }
            }
        }

        return options;
    }

    private static JsonElement? FirstEmbed(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty("embeds", out var embeds) && embeds.ValueKind == JsonValueKind.Array && embeds.GetArrayLength() > 0 && embeds[0].ValueKind == JsonValueKind.Object ? embeds[0] : null;

    /// <summary>
    /// A handled card keeps what it said, turns gray with its buttons gone, and says what was done underneath.
    /// The outcome leaves out the card's own title, so "Done: Call mom" under "Call mom" reads "Done".
    /// </summary>
    private static object Finished(JsonElement? embed, string outcome)
    {
        var title = embed is { } shown ? Text(shown, "title") : null;
        if (title is null)
        {
            return new { embeds = new[] { new { title = outcome, color = (int)CardTone.Finished } }, components = Array.Empty<object>() };
        }

        var fields = embed!.Value.TryGetProperty("fields", out var list) && list.ValueKind == JsonValueKind.Array ? list.Clone() : (JsonElement?)null;
        var footer = outcome.EndsWith(": " + title, StringComparison.Ordinal) ? outcome[..^(title.Length + 2)] : outcome;
        var card = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["color"] = (int)CardTone.Finished,
            ["footer"] = new { text = footer },
        };
        if (Text(embed.Value, "description") is { } description)
        {
            card["description"] = description;
        }

        if (fields is { } kept)
        {
            card["fields"] = kept;
        }

        return new { embeds = new[] { card }, components = Array.Empty<object>() };
    }

    private const int TextOption = 3;
    private const int WholeNumberOption = 4;
    private const int NumberOption = 10;

    private static object Option(string name, string description, int type = TextOption, bool required = false, bool suggest = false, int? maxLength = null)
    {
        var option = new Dictionary<string, object> { ["name"] = name, ["description"] = description, ["type"] = type, ["required"] = required };
        if (suggest)
        {
            option["autocomplete"] = true;
        }

        if (maxLength is { } most)
        {
            option["max_length"] = most;
        }

        return option;
    }

    /// <summary>The most a search box takes, the same as the forms' search box, so a long paste can't end up in a button's ID or a title.</summary>
    private const int MaxSearchWords = 100;

    private static object Choices(string name, string description, bool required, params (string Name, string Value)[] choices) =>
        new { name, description, type = TextOption, required, choices = choices.Select(choice => new { name = choice.Name, value = choice.Value }).ToArray() };

    private static readonly (string, string)[] ViewChoices = [("To do", "todo"), ("Waiting on", "waiting"), ("Budget", "budget"), ("Lists", "lists"), ("Notes", "notes"), ("History", "history")];

    /// <summary>
    /// The slash commands, set for the server the channels are in. Setting the whole list replaces it, so this is
    /// safe to do on every start. They only read or do small things, so none of them waits on the sorter.
    /// Options that name an item or a list suggest matches as they're typed.
    /// </summary>
    public static object[] CommandList =>
    [
        new { name = "help", description = "What each command does", type = 1 },
        new { name = "todo", description = "Today, tomorrow, and the rest of the week", type = 1 },
        new
        {
            name = "add",
            description = "Add something to do, with a date if it has one",
            type = 1,
            options = new[]
            {
                Option("title", "What it is", required: true),
                Option("date", "today, tomorrow, friday, 11/14, or 2026-11-14"),
                Option("time", "Like 3pm or 15:30"),
                Choices("kind", "Leave empty for a task, or a reminder when there's a time", false, ("Task", "task"), ("Deadline", "deadline"), ("Reminder", "reminder"), ("Event", "event"), ("Exam", "exam")),
            },
        },
        new { name = "done", description = "Finish something on the list", type = 1, options = new[] { Option("item", "Start typing to pick it", required: true, suggest: true) } },
        new { name = "took", description = "Mark a dose taken", type = 1, options = new[] { Option("item", "Leave empty when there's only one", suggest: true) } },
        new
        {
            name = "move",
            description = "Move something to another day or time",
            type = 1,
            options = new[]
            {
                Option("item", "Start typing to pick it", required: true, suggest: true),
                Option("date", "today, tomorrow, friday, 11/14, or 2026-11-14. Leave empty to keep the day"),
                Option("time", "Like 3pm or 15:30"),
            },
        },
        new { name = "start", description = "Start on something now, or on whatever is most pressing", type = 1, options = new[] { Option("item", "Leave empty to start on what's most pressing", suggest: true) } },
        new
        {
            name = "spent",
            description = "Add a purchase",
            type = 1,
            options = new[]
            {
                Option("amount", "How much, like 12.50", NumberOption, required: true),
                Option("what", "What it was for", required: true),
                Option("category", "Leave empty to pick one automatically", suggest: true),
            },
        },
        new { name = "lists", description = "Every list and what's left on it", type = 1 },
        new
        {
            name = "check",
            description = "Check something off a list",
            type = 1,
            options = new[] { Option("entry", "Start typing to pick it", required: true, suggest: true), Option("list", "Which list, if it's on more than one", suggest: true) },
        },
        new
        {
            name = "listadd",
            description = "Add something to a list",
            type = 1,
            options = new[] { Option("list", "Which list", required: true, suggest: true), Option("entry", "What to add, with commas between several", required: true) },
        },
        new { name = "waiting", description = "Who you're waiting to hear back from", type = 1 },
        new { name = "pinhere", description = "Keep a view pinned in this channel, updated as things change", type = 1, options = new[] { Choices("view", "Which view", true, ViewChoices) } },
        new { name = "unpin", description = "Stop keeping a pinned view", type = 1, options = new[] { Choices("view", "Which view", true, ViewChoices) } },
        new { name = "now", description = "What to do next", type = 1 },
        new { name = "today", description = "Classes, what's due, and safe to spend today", type = 1 },
        new { name = "budget", description = "Safe to spend, balances, and anything that needs a look", type = 1 },
        new
        {
            name = "afford",
            description = "Can I afford this before payday?",
            type = 1,
            options = new object[] { new { name = "amount", description = "How much, like 65 or 12.50", type = 10, required = true } },
        },
        new
        {
            name = "snooze",
            description = "Snooze the latest reminder",
            type = 1,
            options = new object[] { new { name = "minutes", description = "Minutes. Leave empty for 15", type = WholeNumberOption, required = false, min_value = 1, max_value = 720 } },
        },
        new
        {
            name = "notes",
            description = "Your note folders, a folder, or a search of your notes",
            type = 1,
            options = new[] { Option("folder", "Start typing to pick a folder", suggest: true), Option("search", "Words to look for in your notes", maxLength: MaxSearchWords) },
        },
        new
        {
            name = "history",
            description = "What was finished, canceled, reopened, or went past",
            type = 1,
            options = new[] { Choices("filter", "Leave empty for all of it", false, ("All", "all"), ("Completed", "completed"), ("Canceled", "canceled"), ("Reopened", "reopened"), ("Past", "past")), Option("search", "Words to look for", maxLength: MaxSearchWords) },
        },
        new { name = "search", description = "Look through To do, lists, notes, History, and purchases", type = 1, options = new[] { Option("words", "What to look for", required: true, maxLength: MaxSearchWords) } },
        new
        {
            name = "new",
            description = "Add something with a form: a task, deadline, reminder, event, or exam",
            type = 1,
            options = new[] { Choices("kind", "Leave empty for a task", false, ("Task", "task"), ("Deadline", "deadline"), ("Reminder", "reminder"), ("Event", "event"), ("Exam", "exam")) },
        },
        new { name = "settings", description = "Work hours, bedtime, theme, and connections, with the simple ones to change", type = 1 },
        new
        {
            name = "order",
            description = "Move To do filters, lists, or note folders up or down",
            type = 1,
            options = new[] { Choices("what", "What to put in order", true, ("To do filters", "filters"), ("Lists", "lists"), ("Note folders", "folders")) },
        },
        new { name = "purchases", description = "This month's purchases, newest first", type = 1 },
        new
        {
            name = "save-spot",
            description = "Save where you stopped, so it's easy to pick back up",
            type = 1,
            options = new object[]
            {
                new { name = "stopped", description = "Where you stopped. Leave empty to fill in a form", type = 3, required = false },
                new { name = "next", description = "The next step", type = 3, required = false },
            },
        },
    ];

    /// <summary>Once per start. The server is found from a channel, so nothing extra needs to be set up.</summary>
    public async Task RegisterCommandsAsync(string applicationId)
    {
        if (_commandsRegistered || (settings.CaptureChannelId ?? settings.AlertsChannelId ?? settings.BudgetChannelId) is not { } channel)
        {
            return;
        }

        try
        {
            string? guild;
            using (var found = await SendWithLimitsAsync(() => NewRequest(HttpMethod.Get, $"{ApiBase}/channels/{channel}")))
            {
                found.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await found.Content.ReadAsStringAsync());
                guild = Text(document.RootElement, "guild_id");
            }

            if (guild is null)
            {
                return;
            }

            using var response = await SendWithLimitsAsync(() =>
            {
                var request = NewRequest(HttpMethod.Put, $"{ApiBase}/applications/{applicationId}/guilds/{guild}/commands");
                request.Content = JsonContent.Create(CommandList);
                return request;
            });
            response.EnsureSuccessStatusCode();
            _commandsRegistered = true;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            LastError = $"Slash commands weren't set up: {exception.Message}";
        }
    }

    /// <summary>
    /// Reads reactions and the owner's replies in the alerts channel. Reactions are read from the
    /// recent messages each time, and typed replies only once, even if the live connection already took them.
    /// </summary>
    public async Task<IReadOnlyList<AlertAction>> ReadAlertActionsAsync(CancellationToken cancellationToken = default)
    {
        var actions = new List<AlertAction>();
        if (settings.AlertsChannelId is not { } channel)
        {
            return actions;
        }

        try
        {
            using var response = await SendWithLimitsAsync(() => NewRequest(HttpMethod.Get, $"{ApiBase}/channels/{channel}/messages?limit={MaxAlertsRead}"), cancellationToken);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var messages = document.RootElement.EnumerateArray()
                .OrderBy(message => ulong.Parse(message.GetProperty("id").GetString()!))
                .ToList();

            var last = database.GetPreference(LastAlertKey);
            foreach (var message in messages)
            {
                var id = message.GetProperty("id").GetString()!;
                actions.AddRange(Reactions(message).Select(kind => new AlertAction(id, kind)));

                if (last is not null && ulong.Parse(id) > ulong.Parse(last) && IsFromOwner(message) && ReplyKind(Text(message, "content")) is { } kind && FirstTime(id, channel))
                {
                    var target = message.TryGetProperty("message_reference", out var reference) ? Text(reference, "message_id") : null;
                    actions.Add(new AlertAction(target, kind, id));
                }
            }

            if (messages.Count > 0)
            {
                database.SetPreference(LastAlertKey, messages[^1].GetProperty("id").GetString()!);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LastError = exception.Message;
        }

        return actions;
    }

    private static IEnumerable<AlertActionKind> Reactions(JsonElement message)
    {
        if (!message.TryGetProperty("reactions", out var reactions) || reactions.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var reaction in reactions.EnumerateArray())
        {
            var name = reaction.TryGetProperty("emoji", out var emoji) ? Text(emoji, "name") : null;
            if (name is not null && DoneEmoji.Contains(name))
            {
                yield return AlertActionKind.Done;
            }
            else if (name is not null && SnoozeEmoji.Contains(name))
            {
                yield return AlertActionKind.Snooze;
            }
        }
    }

    private static AlertActionKind? ReplyKind(string? text) => text?.Trim().TrimEnd('.', '!').ToLowerInvariant() switch
    {
        "done" or "finished" or "complete" or "completed" or "did it" => AlertActionKind.Done,
        "snooze" or "later" => AlertActionKind.Snooze,
        _ => null,
    };

    /// <summary>
    /// Hands over the owner's new messages, oldest first. The first run only marks where to start. Messages the
    /// live connection already handed over are skipped. A poll still running keeps the next one from reading the same messages.
    /// Messages handed over before whose work was cut off are read again first, from whichever channel they came in.
    /// </summary>
    public async Task PollAsync(Action<DiscordMessage>? onMessage = null, CancellationToken cancellationToken = default)
    {
        onMessage ??= Captured;
        if (settings.CaptureChannelId is not { } channel || !await _polling.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            await RetryUnfinishedAsync(onMessage, cancellationToken);
            var last = database.GetPreference(LastMessageKey);
            var query = last is null ? "limit=1" : $"limit={MaxFetched}&after={last}";

            using var response = await SendWithLimitsAsync(() => NewRequest(HttpMethod.Get, $"{ApiBase}/channels/{channel}/messages?{query}"), cancellationToken);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var messages = document.RootElement.EnumerateArray()
                .OrderBy(message => ulong.Parse(message.GetProperty("id").GetString()!))
                .ToList();

            // An empty channel still marks the place, or its first message would only be marked too.
            if (last is null && messages.Count == 0)
            {
                database.SetPreference(LastMessageKey, "0");
            }

            foreach (var message in messages)
            {
                var id = message.GetProperty("id").GetString()!;
                if (last is not null && IsFromOwner(message) && ReadMessage(message) is { } read && FirstTime(id, channel))
                {
                    onMessage?.Invoke(read);
                }

                database.SetPreference(LastMessageKey, id);
            }

            LastError = null;
            LastPolled = DateTimeOffset.Now;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LastError = exception.Message;
        }
        finally
        {
            _polling.Release();
        }
    }

    /// <summary>
    /// Saves an attachment, refusing anything bigger than <paramref name="maxBytes"/>. Only Discord's own file
    /// links are followed. Returns why it couldn't, or null once it's saved.
    /// </summary>
    public async Task<string?> DownloadAsync(DiscordAttachment attachment, string path, long maxBytes, CancellationToken cancellationToken = default)
    {
        if (attachment.Size > maxBytes)
        {
            return $"{attachment.FileName} is too big to read. The limit is {maxBytes / (1024 * 1024)} MB.";
        }

        var notDiscord = $"{attachment.FileName} didn't come from Discord, so it wasn't opened.";
        if (!Uri.TryCreate(attachment.Url, UriKind.Absolute, out var url) || !IsDiscordFile(url))
        {
            return notDiscord;
        }

        try
        {
            // Redirects are followed here, one at a time, so each place it's sent is checked before it's asked.
            // A client that follows them itself is checked by where it ended up.
            var client = Downloads ?? http;
            HttpResponseMessage response;
            for (var hop = 0; ; hop++)
            {
                response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var landed = response.RequestMessage?.RequestUri ?? url;
                if (!IsDiscordFile(landed))
                {
                    response.Dispose();
                    return notDiscord;
                }

                if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is not { } location)
                {
                    break;
                }

                response.Dispose();
                url = location.IsAbsoluteUri ? location : new Uri(landed, location);
                if (!IsDiscordFile(url))
                {
                    return notDiscord;
                }

                if (hop >= MaxDownloadRedirects)
                {
                    return $"Couldn't download {attachment.FileName}. It was sent on too many times.";
                }
            }

            using var owned = response;
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var target = File.Create(path))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        break;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                if (total <= maxBytes)
                {
                    return null;
                }
            }

            File.Delete(path);
            return $"{attachment.FileName} is too big to read. The limit is {maxBytes / (1024 * 1024)} MB.";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            LastError = exception.Message;
            return $"Couldn't download {attachment.FileName}. Try sending it again.";
        }
    }

    private static bool IsDiscordFile(Uri url) =>
        url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && DownloadHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True the first time a message is seen, from either the live connection or a poll, and again once its work
    /// has gone <see cref="UnfinishedAfter"/> without being finished. The claim is saved before it's handed over.
    /// </summary>
    private bool FirstTime(string id, string? channel) => database.ClaimDiscordMessage(id, channel ?? "", Clock(), UnfinishedAfter);

    /// <summary>
    /// Reads again each message whose work was cut off and hands it over the same way as when it came in. One
    /// deleted since, or that no longer asks for anything, such as after an edit, has nothing left to do. One that
    /// can't be read right now counts as a try and is tried on a later poll, so it's let go in the end.
    /// </summary>
    private async Task RetryUnfinishedAsync(Action<DiscordMessage>? onCapture, CancellationToken cancellationToken)
    {
        foreach (var (id, channel) in database.GetUnfinishedDiscordMessages(Clock(), UnfinishedAfter))
        {
            try
            {
                using var response = await SendWithLimitsAsync(() => NewRequest(HttpMethod.Get, $"{ApiBase}/channels/{channel}/messages/{id}"), cancellationToken);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    database.FinishDiscordMessage(id, Clock());
                    continue;
                }

                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (document.RootElement.ValueKind != JsonValueKind.Object || Text(document.RootElement, "id") != id)
                {
                    database.CountDiscordTry(id, Clock());
                    continue;
                }

                // The claim knows the channel, which a message read back on its own may leave out.
                var message = Text(document.RootElement, "channel_id") is null ? WithChannel(document.RootElement, channel) : document.RootElement.Clone();
                if (await HandleMessageAsync(message, onCapture) is null)
                {
                    database.FinishDiscordMessage(id, Clock());
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                LastError = exception.Message;
                database.CountDiscordTry(id, Clock());
            }
        }
    }

    private static JsonElement WithChannel(JsonElement message, string channel)
    {
        var fields = message.EnumerateObject().ToDictionary(field => field.Name, field => (object)field.Value.Clone());
        fields["channel_id"] = channel;
        return JsonSerializer.SerializeToElement(fields);
    }

    private static DiscordMessage? ReadMessage(JsonElement message)
    {
        var text = Text(message, "content") ?? "";
        var attachments = new List<DiscordAttachment>();
        if (message.TryGetProperty("attachments", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in files.EnumerateArray())
            {
                if (Text(file, "id") is { } id && Text(file, "filename") is { } name && Text(file, "url") is { } url)
                {
                    var size = file.TryGetProperty("size", out var bytes) && bytes.ValueKind == JsonValueKind.Number ? bytes.GetInt64() : 0;
                    var seconds = file.TryGetProperty("duration_secs", out var duration) && duration.ValueKind == JsonValueKind.Number ? duration.GetDouble() : (double?)null;
                    attachments.Add(new DiscordAttachment(id, name, size, url, Text(file, "content_type"), seconds));
                }
            }
        }

        if (text.Trim().Length == 0 && attachments.Count == 0)
        {
            return null;
        }

        var replyTo = message.TryGetProperty("message_reference", out var reference) ? Text(reference, "message_id") : null;
        var replyToText = replyTo is not null && message.TryGetProperty("referenced_message", out var referenced) ? CardText(referenced) : null;
        return new DiscordMessage(Text(message, "id")!, text, SentAt(message), attachments, replyTo, replyToText);
    }

    /// <summary>Everything a message's card says, title, text and fields, one per line.</summary>
    private static string? CardText(JsonElement message)
    {
        if (FirstEmbed(message) is not { } embed)
        {
            return Text(message, "content");
        }

        var lines = new List<string?> { Text(embed, "title"), Text(embed, "description") };
        if (embed.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
        {
            lines.AddRange(fields.EnumerateArray().Select(field => $"{Text(field, "name")}: {Text(field, "value")}"));
        }

        return string.Join('\n', lines.Where(line => !string.IsNullOrEmpty(line)));
    }

    /// <summary>
    /// Something the owner typed: an ordinary message or a reply (types 0 and 19). Discord's own notices, such as
    /// "changed the post title", carry the owner as their author too, but aren't anything they wrote.
    /// </summary>
    private bool IsFromOwner(JsonElement message) =>
        message.TryGetProperty("author", out var author)
        && Text(author, "id") == settings.OwnerUserId
        && !(author.TryGetProperty("bot", out var bot) && bot.ValueKind == JsonValueKind.True)
        && (!message.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.Number || type.GetInt32() is 0 or 19);

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset SentAt(JsonElement message) =>
        DateTimeOffset.TryParse(Text(message, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var sent) ? sent : DateTimeOffset.Now;

    private async Task<string?> SendAsync(string? channel, DiscordCard card) => (await TrySendAsync(channel, card)).MessageId;

    private async Task<DiscordDelivery> TrySendAsync(string? channel, DiscordCard card, string? replyTo = null)
    {
        if (channel is null)
        {
            return new DiscordDelivery(null, true);
        }

        try
        {
            using var response = await SendWithLimitsAsync(() =>
            {
                var request = NewRequest(HttpMethod.Post, $"{ApiBase}/channels/{channel}/messages");
                request.Content = JsonContent.Create(MessageBody(card, replyTo));
                return request;
            });
            response.EnsureSuccessStatusCode();
            LastError = null;

            using var sent = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return new DiscordDelivery(Text(sent.RootElement, "id"), true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            return new DiscordDelivery(null, false);
        }
    }

    /// <summary>Item titles can come from the calendar, so nothing sent is allowed to ping anyone.</summary>
    private static Dictionary<string, object> MessageBody(DiscordCard card, string? replyTo = null, int? flags = null)
    {
        var body = new Dictionary<string, object>
        {
            ["embeds"] = new[]
            {
                new
                {
                    title = card.Title,
                    description = card.Description,
                    color = (int)card.Tone,
                    fields = card.Fields.Select(field => new { name = field.Name, value = field.Value, inline = field.Inline }).ToArray(),
                },
            },
            ["components"] = Components(card),
            ["allowed_mentions"] = new { parse = Array.Empty<string>(), replied_user = false },
        };

        if (replyTo is not null)
        {
            // A deleted original shouldn't stop the answer from arriving.
            body["message_reference"] = new { message_id = replyTo, fail_if_not_exists = false };
        }

        if (flags is { } value)
        {
            body["flags"] = value;
        }

        return body;
    }

    private static object[] Components(DiscordCard card)
    {
        var rows = ButtonRowFor(card).ToList();
        // This didn't help keeps a row of its own at the bottom, so paging never pushes it past Discord's five rows.
        var feedback = FeedbackRow(card);
        var room = MaxRows - rows.Count - (card.Menu is { Choices.Count: > 0 } ? 1 : 0) - (feedback is null ? 0 : 1);
        rows.AddRange(card.MoreRows.Where(row => row.Count > 0).Take(Math.Max(0, room)).Select(row => (object)new
        {
            type = ButtonRow,
            components = row.Take(MaxButtons)
                .Select(button => (object)new { type = Button, style = button.Role == ButtonRole.Main ? GreenButton : GrayButton, label = Shorten(button.Label, MaxButtonLabel), custom_id = ActionPrefix + button.Id })
                .ToArray(),
        }));
        if (card.Menu is { Choices.Count: > 0 } menu)
        {
            var options = menu.Choices.Take(MaxMenuChoices).Select(choice => choice.Description is { } description
                ? (object)new { label = Shorten(choice.Label, MaxChoiceText), value = Shorten(choice.Value, MaxChoiceText), description = Shorten(description, MaxChoiceText) }
                : new { label = Shorten(choice.Label, MaxChoiceText), value = Shorten(choice.Value, MaxChoiceText) });
            rows.Add(new { type = ButtonRow, components = new object[] { new { type = Menu, custom_id = menu.CustomId, placeholder = menu.Placeholder, options = options.ToArray() } } });
        }

        if (feedback is not null && rows.Count < MaxRows)
        {
            rows.Add(feedback);
        }

        return rows.ToArray();
    }

    /// <summary>
    /// Every card's buttons in one order and one set of colors: the main action first and green, then anything else,
    /// then Not now to bring it back later, then Skip for nothing more to come, all gray.
    /// </summary>
    private static object[] ButtonRowFor(DiscordCard card)
    {
        var buttons = new List<(ButtonRole Role, string Label, string Id)>();
        if (card.Dose)
        {
            // Took it keeps the time for that day. Not now brings it back once, and Skip lets the day go.
            buttons.Add((ButtonRole.Main, CardLabels.TookIt, DoneButton));
            if (card.MoreTime)
            {
                buttons.Add((ButtonRole.NotNow, CardLabels.NotNow, SnoozeButton));
            }

            buttons.Add((ButtonRole.Skip, CardLabels.Skip, NotNowReminderButton));
        }
        else if (card.Transition)
        {
            // Start is the way in. 10 more minutes brings it back once, and Skip ends it.
            buttons.Add((ButtonRole.Main, "Start", StartButton));
            if (card.HasButtons)
            {
                buttons.Add((ButtonRole.Other, "Done", DoneButton));
            }

            if (card.MoreTime)
            {
                buttons.Add((ButtonRole.NotNow, CardLabels.MoreTime, MoreTimeButton));
            }

            buttons.Add((ButtonRole.Skip, CardLabels.Skip, NotNowReminderButton));
        }
        else if (card.HasButtons)
        {
            buttons.Add((ButtonRole.Main, "Done", DoneButton));
            buttons.Add((ButtonRole.NotNow, CardLabels.NotNow, SnoozeButton));
        }

        if (card.HasStart && !card.Transition)
        {
            buttons.Add((card.HasButtons ? ButtonRole.Other : ButtonRole.Main, "Start", StartButton));
        }

        if (card.HasStartNext)
        {
            buttons.Add((ButtonRole.Main, "Start", StartNextButton));
        }

        if (card.HasSessionButtons)
        {
            buttons.Add((ButtonRole.Main, "Did it", DidItButton));
            buttons.Add((ButtonRole.Other, "Make it smaller", SmallerButton));
            if (card.CanTrial)
            {
                buttons.Add((ButtonRole.Other, "Try 2 minutes", TrialButton));
            }

            buttons.Add((ButtonRole.NotNow, CardLabels.Pause, NotNowButton));
        }

        switch (card.BedtimeButton)
        {
            case AlertActionKind.WindDownStepDone:
                buttons.Add((ButtonRole.Main, card.BedtimeLabel ?? "Done", WindDownStepDoneButton));
                break;
            case AlertActionKind.EveningStepDone:
                buttons.Add((ButtonRole.Main, card.BedtimeLabel ?? "Done", EveningStepDoneButton));
                break;
            case AlertActionKind.SnoozeBedtime:
                buttons.Add((ButtonRole.NotNow, CardLabels.NotNow, SnoozeBedtimeButton));
                break;
        }

        if (card.SaveSpot)
        {
            buttons.Add((ButtonRole.Other, CardLabels.SaveSpot, SaveSpotButton));
        }

        buttons.AddRange(card.Actions.Select(action => (action.Role, action.Label, ActionPrefix + action.Id)));
        var row = buttons
            .OrderBy(button => button.Role)
            .Take(MaxButtons)
            .Select(button => (object)new { type = Button, style = button.Role == ButtonRole.Main ? GreenButton : GrayButton, label = Shorten(button.Label, MaxButtonLabel), custom_id = button.Id })
            .ToArray();
        return row.Length == 0 ? [] : [new { type = ButtonRow, components = row }];
    }

    /// <summary>
    /// Sends one call, waiting out Discord's rate limit and trying again when told to. The request is built fresh
    /// for each try, since a sent request can't be sent again.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithLimitsAsync(Func<HttpRequestMessage> build, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = build();
            var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= MaxRateLimitTries)
            {
                return response;
            }

            var wait = await RetryAfterAsync(response, cancellationToken);
            if (wait > MaxRateLimitWait)
            {
                return response;
            }

            response.Dispose();
            await Delay(wait, cancellationToken);
        }
    }

    /// <summary>Discord puts the wait in the body in seconds, with a header as backup.</summary>
    public static async Task<TimeSpan> RetryAfterAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("retry_after", out var seconds)
                && seconds.ValueKind == JsonValueKind.Number)
            {
                return TimeSpan.FromSeconds(Math.Max(0, seconds.GetDouble()));
            }
        }
        catch (JsonException)
        {
            // Falls through to the header.
        }

        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        return response.Headers.TryGetValues("Retry-After", out var values)
            && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var header)
            ? TimeSpan.FromSeconds(header)
            : TimeSpan.FromSeconds(1);
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", settings.BotToken);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        return request;
    }
}
