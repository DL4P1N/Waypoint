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

/// <summary>
/// How one send went. A channel that's turned off counts as sent, since nothing was meant to go there. Status is
/// Discord's answer to a send it turned away, when it gave one.
/// </summary>
public sealed record DiscordDelivery(string? MessageId, bool Sent, System.Net.HttpStatusCode? Status = null)
{
    /// <summary>Turned away for good: the channel is gone or the bot may not post there, so trying again won't help.</summary>
    public bool Refused => Status is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound;
}

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
