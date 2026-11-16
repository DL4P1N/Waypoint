using System.Globalization;
using System.Text.RegularExpressions;

namespace Waypoint.Core.Capture;

/// <summary>
/// An item that's waiting on someone else, such as a reply from a professor. It stays out of the picks
/// until its follow-up day, or until a reply from them shows up in email.
/// </summary>
public sealed record WaitingItem(
    long ItemId,
    string Title,
    string Who,
    string? Topic,
    DateTime Since,
    DateOnly FollowUpOn,
    string? Email = null,
    DateTime? RepliedAt = null,
    string? ReplyFrom = null,
    string? ReplySubject = null)
{
    /// <summary>Its follow-up day came, or they wrote back, so it's worth seeing again.</summary>
    public bool IsDue(DateOnly today) => RepliedAt is not null || FollowUpOn <= today;
}

/// <summary>The short replies to a follow-up nudge, which work offline the same way session words do.</summary>
public enum WaitingWord
{
    StillWaiting,
    Draft,
}

/// <summary>The fixed rules for waiting items: when to check back, what the nudge says, and the draft it offers.</summary>
public static partial class WaitingOn
{
    public const int DefaultFollowUpDays = 3;

    /// <summary>The one nudge comes mid-morning, when an email is easy to send.</summary>
    public static readonly TimeOnly NudgeTime = new(10, 0);

    private static readonly HashSet<string> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        "prof", "professor", "dr", "mr", "mrs", "ms", "mx", "the", "my", "a", "an", "from", "to", "on", "at", "of", "and",
    };

    private static readonly string[] StillWords = ["still waiting", "still waiting on it", "still waiting on them", "no reply yet", "nothing yet"];
    private static readonly string[] DraftWords = ["draft follow up", "draft a follow up", "follow up draft", "draft the follow up", "write a follow up", "draft it"];

    /// <summary>A few days on, moved to Monday when that lands on a weekend, since that's when people answer.</summary>
    public static DateOnly DefaultFollowUp(DateOnly from)
    {
        var day = from.AddDays(DefaultFollowUpDays);
        return day.DayOfWeek switch
        {
            DayOfWeek.Saturday => day.AddDays(2),
            DayOfWeek.Sunday => day.AddDays(1),
            _ => day,
        };
    }

    /// <summary>A date the user gave, unless it's already gone by, in which case the usual few days.</summary>
    public static DateOnly FollowUpOr(DateOnly? asked, DateOnly today) =>
        asked is { } date && date > today ? date : DefaultFollowUp(today);

    public static string TitleFor(string who, string? topic) =>
        string.IsNullOrWhiteSpace(topic) ? $"Hear back from {who.Trim()}" : $"Hear back from {who.Trim()} about {topic.Trim()}";

    /// <summary>What the wait is about, from a title like "Hear back from Prof. Quill about the extension".</summary>
    public static string? TopicOf(string title)
    {
        var at = title.LastIndexOf(" about ", StringComparison.OrdinalIgnoreCase);
        var topic = at >= 0 ? title[(at + " about ".Length)..].Trim().TrimEnd('.') : null;
        return string.IsNullOrWhiteSpace(topic) ? null : topic;
    }

    public static string NudgeMessage(WaitingItem waiting) =>
        $"Follow up with {waiting.Who}? Say \"still waiting\" to check again in a few days, or \"draft follow up\" for a short note.";

    /// <summary>What Follow up and "draft follow up" answer with: the note to copy, and nothing sent.</summary>
    public static string DraftReply(WaitingItem waiting) =>
        $"Here's a follow-up for {waiting.Who} to copy into an email:\n\n{Draft(waiting)}";

    public static string ReplyMessage(string who, string subject) =>
        string.IsNullOrWhiteSpace(subject)
            ? $"{who} wrote back. Mark this done if that settles it."
            : $"{who} wrote back: \"{subject.Trim()}\". Mark this done if that settles it.";

    /// <summary>
    /// A short, polite follow-up to copy into an email. It's only ever text: nothing here sends anything.
    /// </summary>
    public static string Draft(WaitingItem waiting)
    {
        var topic = waiting.Topic ?? TopicOf(waiting.Title);
        var about = topic is null ? "" : $" about {LowerFirst(topic)}";
        var when = waiting.Since.ToString("dddd, MMMM d", CultureInfo.InvariantCulture);
        return $"""
            {Greeting(waiting.Who)}

            I wanted to follow up on my message from {when}{about}. When you have a moment, could you let me know where things stand?

            Thank you,
            """;
    }

    /// <summary>"Hi Prof. Quill," for a name, and "Hello," for an office like HR or something vague like "my professor".</summary>
    public static string Greeting(string who)
    {
        var name = who.Trim();
        var letters = name.Where(char.IsLetter).ToList();
        var isName = letters.Count > 0 && char.IsUpper(name[0]) && !(letters.All(char.IsUpper) && letters.Count <= 4);
        return isName ? $"Hi {name}," : "Hello,";
    }

    /// <summary>
    /// Whether an email is their reply. The address they gave matches outright. Otherwise a word of their name,
    /// leaving out titles like "Prof.", has to be a whole word of the sender's name or the front of their address.
    /// </summary>
    public static bool IsReplyFrom(WaitingItem waiting, string address, string? name)
    {
        if (waiting.Email is { Length: > 0 } email)
        {
            return string.Equals(email.Trim(), address.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        var words = NameWords(waiting.Who).ToList();
        if (words.Count == 0)
        {
            return false;
        }

        var local = address.Split('@')[0];
        var sender = Words(name ?? "").Concat(Words(local)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return words.Any(sender.Contains);
    }

    public static WaitingWord? ParseWord(string text)
    {
        var clean = string.Join(' ', Words(text.Replace("-", " ")).Select(word => word.ToLowerInvariant()));
        return StillWords.Contains(clean) ? WaitingWord.StillWaiting
            : DraftWords.Contains(clean) ? WaitingWord.Draft
            : null;
    }

    /// <summary>One line per item, for "what am I waiting on".</summary>
    public static string Describe(IReadOnlyList<WaitingItem> waiting, DateOnly today)
    {
        if (waiting.Count == 0)
        {
            return "You're not waiting on anyone right now.";
        }

        var lines = waiting.OrderBy(item => item.FollowUpOn).Select(item => item.RepliedAt is not null
            ? $"{item.Title}: {item.Who} wrote back."
            : item.FollowUpOn <= today ? $"{item.Title}: time to follow up."
            : $"{item.Title}: follow up {When(item.FollowUpOn, today)}.");
        return string.Join("\n", lines.Prepend("Waiting on:"));
    }

    /// <summary>"today", "tomorrow", or the day's name, to sit in the middle of a sentence.</summary>
    public static string When(DateOnly day, DateOnly today) => (day.DayNumber - today.DayNumber) switch
    {
        <= 0 => "today",
        1 => "tomorrow",
        _ => ItemGroups.DayLabel(day, today),
    };

    private static IEnumerable<string> NameWords(string who) =>
        Words(who).Where(word => word.Length >= 2 && !Titles.Contains(word));

    private static IEnumerable<string> Words(string text) =>
        WordPattern().Matches(text).Select(match => match.Value);

    private static string LowerFirst(string text) =>
        text.Length > 1 && char.IsUpper(text[0]) && char.IsLower(text[1]) ? char.ToLowerInvariant(text[0]) + text[1..] : text;

    [GeneratedRegex(@"[\p{L}\p{N}']+")]
    private static partial Regex WordPattern();
}
