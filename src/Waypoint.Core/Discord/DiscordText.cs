using System.Globalization;
using System.Text.RegularExpressions;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Discord;

/// <summary>The color down a card's edge, which says what kind of message it is before it's read.</summary>
public enum CardTone
{
    // Blurple rather than white, which can't be seen on Discord's light theme.
    Reminder = 0x5865F2,
    Attention = 0xFEE75C,
    Urgent = 0xED4245,
    Money = 0x57F287,
    Finished = 0x4E5058,
}

public sealed record CardField(string Name, string Value, bool Inline);

/// <summary>One choice in a card's menu. Value is what comes back when it's picked, and is never shown.</summary>
public sealed record MenuChoice(string Label, string Value, string? Description = null);

/// <summary>A pick-one menu under a card, such as checking off a list entry. Discord shows at most 25 choices.</summary>
public sealed record DiscordMenu(string CustomId, string Placeholder, IReadOnlyList<MenuChoice> Choices);

/// <summary>
/// One Discord embed, with Done and Snooze buttons if it can be finished, and a session's buttons while one is going.
/// HasStartNext starts on whatever is most pressing, for the /now card. Transition and MoreTime give a reminder Start, 10 more minutes (back once) and Skip.
/// Menu is for pinned views, which stay as they are when it's used and are drawn again with the change.
/// </summary>
/// <summary>Where a button sits: the main action first, then the rest, then Not now, then Skip.</summary>
public enum ButtonRole
{
    Main,
    Other,
    NotNow,
    Skip,
}

/// <summary>A button that carries what it acts on in its Id, so it still works after a restart.</summary>
public sealed record CardAction(string Label, string Id, ButtonRole Role = ButtonRole.Main);

public sealed record DiscordCard(string Title, string? Description, IReadOnlyList<CardField> Fields, CardTone Tone, bool HasButtons = false, bool HasStart = false, bool HasSessionButtons = false, bool CanTrial = false, AlertActionKind? BedtimeButton = null, bool HasStartNext = false, bool Transition = false, bool MoreTime = false)
{
    public DiscordMenu? Menu { get; init; }

    /// <summary>The words on <see cref="BedtimeButton"/> when they depend on the step, such as "Done: Stretch".</summary>
    public string? BedtimeLabel { get; init; }

    public IReadOnlyList<CardAction> Actions { get; init; } = [];

    /// <summary>Adds Save my spot, which opens a short form on the phone and saves locally, the same as /save-spot.</summary>
    public bool SaveSpot { get; init; }

    /// <summary>Adds a small This didn't help under the buttons, kept with this feature (see <see cref="Capture.Feedback"/>) and item reference.</summary>
    public string? FeedbackFeature { get; init; }

    public string? FeedbackRef { get; init; }

    /// <summary>A dose's card: Took it, then Not now (when <see cref="MoreTime"/> allows it), then Skip.</summary>
    public bool Dose { get; init; }

    /// <summary>More rows of buttons under the first, such as Back and Next on a long view. Discord allows five rows in all, a menu counting as one.</summary>
    public IReadOnlyList<IReadOnlyList<CardAction>> MoreRows { get; init; } = [];

    /// <summary>Answered as a new reply only the owner sees, so the pressed message, such as a pinned view, stays as it was.</summary>
    public bool Private { get; init; }
}

/// <summary>Lays messages out to be read at a glance: a headline, then short labeled fields.</summary>
public static partial class DiscordText
{
    private const int MaxFields = 25;
    private const int MaxFieldValue = 1024;
    private const int MaxDescription = 4096;
    private const int MaxTitle = 256;
    private const int MaxInlineValue = 34;
    private const int MaxReplyTitle = 120;

    /// <summary>Keeps names like "Streamco*Online" from turning into formatting.</summary>
    public static string Escape(string text) => Markup().Replace(text, @"\$0");

    /// <summary>Longest a voice note's words are shown under its reply. Enough to spot a mis-hearing.</summary>
    public const int MaxHeard = 300;

    /// <summary>"Heard: ..." under the reply to a voice note, so a mis-hearing is easy to spot.</summary>
    public static CardField HeardField(string text) => new("Heard", Escape(Shorten(text, MaxHeard)), false);

    /// <summary>"Earlier you meant to: X" as the labeled field a briefing or heads-up shows it as, for a card built another way.</summary>
    public static CardField HeldField(string line)
    {
        var lead = AfterThis.Lead.TrimEnd(':');
        var value = Escape(line.StartsWith(AfterThis.Lead, StringComparison.Ordinal) ? line[AfterThis.Lead.Length..].Trim() : line);
        return new CardField(lead, Fit(value, MaxFieldValue), value.Length <= MaxInlineValue);
    }

    /// <summary>Cuts text to fit with a mark that it was cut, never in the middle of a pair of characters such as an emoji.</summary>
    public static string Shorten(string text, int limit)
    {
        if (text.Length <= limit)
        {
            return text;
        }

        var keep = limit - 1;
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return text[..keep] + "…";
    }

    public static DiscordCard Notice(string headline, string body, CardTone tone = CardTone.Reminder)
    {
        var fields = new List<CardField>();
        var loose = new List<string>();

        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Labeled().Match(line) is { Success: true } label && fields.Count < MaxFields)
            {
                var value = Escape(label.Groups[2].Value);
                fields.Add(new CardField(Escape(label.Groups[1].Value), Fit(value, MaxFieldValue), value.Length <= MaxInlineValue));
            }
            else
            {
                loose.Add(Escape(line));
            }
        }

        return new DiscordCard(Fit(Escape(headline), MaxTitle), loose.Count > 0 ? FitLines(loose) : null, fields, tone);
    }

    /// <summary>
    /// A reply in words, such as an answer to something typed. Its first line is the headline when it's short, and
    /// nothing is split into fields, since a sentence like "Note: ..." isn't a label.
    /// </summary>
    public static DiscordCard Reply(string text, CardTone tone = CardTone.Reminder)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Escape).ToList();
        if (lines.Count == 0)
        {
            return new DiscordCard("Saved", null, [], tone);
        }

        if (lines[0].Length <= MaxReplyTitle)
        {
            return new DiscordCard(lines[0], lines.Count > 1 ? FitLines(lines.Skip(1).ToList()) : null, [], tone);
        }

        return new DiscordCard("Answer", FitLines(lines), [], tone);
    }

    public static DiscordCard Reminder(DueReminder reminder, DateOnly today, bool canFinish, bool canStart = false)
    {
        var item = reminder.Item;
        var day = item.DueDate is { } date ? DiscordViews.DayLabel(date, today) : null;
        var time = item.DueAt?.LocalDateTime.ToString("h:mm tt", CultureInfo.InvariantCulture);
        var when = $"{day} {time}".Trim();

        var fields = new List<CardField>();
        if (reminder.Message is not null)
        {
            fields.Add(new CardField("For", Fit(Escape(item.Title), MaxFieldValue), true));
        }

        if (when.Length > 0)
        {
            fields.Add(new CardField("When", Escape(when), true));
        }

        // A dose's Not now comes back once, so the card that comes back has only Took it and Skip.
        var dose = item is { Dose: true, Recurs: not null };
        return new DiscordCard(Fit(Escape(reminder.Message ?? item.Title), MaxTitle), null, fields, item.Kind == ItemKind.Exam ? CardTone.Attention : CardTone.Reminder, canFinish || dose, canStart && !dose)
        {
            Transition = !dose && canStart && TransitionAssist.Applies(reminder),
            MoreTime = dose ? !reminder.IsFollowUp : canStart && TransitionAssist.OffersMoreTime(reminder),
            Dose = dose,
        };
    }

    /// <summary>Discord refuses a card that's too long, so a long list stops at a whole line and says what's left.</summary>
    private static string FitLines(IReadOnlyList<string> lines)
    {
        var text = string.Join("\n", lines);
        if (text.Length <= MaxDescription)
        {
            return text;
        }

        var kept = new List<string>();
        var length = 0;
        foreach (var line in lines)
        {
            // Leaves room for the short last line.
            if (length + line.Length + 1 > MaxDescription - 20)
            {
                break;
            }

            kept.Add(line);
            length += line.Length + 1;
        }

        kept.Add($"and {lines.Count - kept.Count} more");
        return string.Join("\n", kept);
    }

    private static string Fit(string text, int limit) => text.Length > limit ? text[..limit] : text;

    [GeneratedRegex(@"[*_~`|\\]")]
    private static partial Regex Markup();

    [GeneratedRegex(@"^([A-Z][^:]{1,60}): (.+)$")]
    private static partial Regex Labeled();
}
