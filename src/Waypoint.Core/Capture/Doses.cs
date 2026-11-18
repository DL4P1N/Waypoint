using System.Globalization;
using System.Text.RegularExpressions;

namespace Waypoint.Core.Capture;

/// <summary>An answer on a dose's card. Took it keeps the time, Not now brings it back once, and Skip lets the day go.</summary>
public enum DoseAnswer
{
    Took,
    NotNow,
    Skip,
}

/// <summary>One time a dose was marked taken. DueOn is the day of the reminder it answered.</summary>
public sealed record DoseTaken(DateOnly DueOn, DateTime TakenAt);

/// <summary>A dose reminder and the times it was marked taken in the last month, newest first.</summary>
public sealed record DoseRecord(StoredItem Item, IReadOnlyList<DoseTaken> Taken);

/// <summary>What Took it came to. Recorded is false when that day was already marked, so nothing was kept twice.</summary>
public sealed record DoseReply(string Text, bool Recorded);

/// <summary>"Did I take my meds?" read locally. Subject is what was asked about, and AsksWhen is "when did I last take it?".</summary>
public sealed record DoseQuestion(string Subject, bool AsksWhen);

/// <summary>
/// Doses are repeating reminders for taking medicine. Only the times Took it was pressed are kept, for a month, so
/// "did I take it?" can be answered. Answers only say what was marked. They never suggest taking one, and nothing
/// counts missed days.
/// </summary>
public static partial class Doses
{
    /// <summary>How long taken times are kept.</summary>
    public const int KeepDays = 30;

    public const string NoneYet = "No reminders are marked as doses yet, so there are no times to go by.";

    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "it", "them", "everything", "med", "meds", "medicine", "medicines", "medication", "medications",
        "pill", "pills", "dose", "doses", "tablet", "tablets",
    };

    /// <summary>The question in <paramref name="text"/>, or null when it isn't one about taking something.</summary>
    public static DoseQuestion? ParseQuestion(string text)
    {
        var trimmed = text.Trim().TrimEnd('?', '.', '!', ' ');
        var match = Question().Match(trimmed);
        if (!match.Success)
        {
            return null;
        }

        var subject = match.Groups["what"].Value.Trim();
        for (var before = ""; before != subject;)
        {
            before = subject;
            subject = Trailing().Replace(subject, "").Trim();
            subject = Leading().Replace(subject, "").Trim();
        }

        return subject.Length == 0 ? null : new DoseQuestion(subject, match.Groups["when"].Success);
    }

    /// <summary>Whether the question names no dose in particular, such as "my meds", so it's about all of them.</summary>
    public static bool IsGeneric(string subject) => Generic.Contains(subject.Trim());

    /// <summary>The doses a question is about: all of them for "my meds", otherwise the ones whose names match it best.</summary>
    public static IReadOnlyList<DoseRecord> Matching(IReadOnlyList<DoseRecord> doses, string subject)
    {
        if (IsGeneric(subject))
        {
            return doses;
        }

        // Whole words, so "did I take out the trash?" can't match a "Workout supplement".
        var words = SearchIndex.Words(subject);
        var scored = doses
            .Select(dose => (Dose: dose, Score: words.Count(word => SearchIndex.Words(dose.Item.Title).Contains(word, StringComparer.OrdinalIgnoreCase))))
            .Where(hit => hit.Score > 0)
            .ToList();
        var best = scored.Count == 0 ? 0 : scored.Max(hit => hit.Score);
        return scored.Where(hit => hit.Score == best).Select(hit => hit.Dose).ToList();
    }

    /// <summary>
    /// The local answer to a did-I-take-it question, or null when <paramref name="text"/> isn't one, or names
    /// something that isn't a dose (such as "did I take out the trash?"), which is left for the sorter.
    /// </summary>
    public static string? Reply(string text, IReadOnlyList<DoseRecord> doses, DateTime now)
    {
        if (ParseQuestion(text) is not { } question)
        {
            return null;
        }

        if (doses.Count == 0)
        {
            return IsGeneric(question.Subject) ? NoneYet : null;
        }

        var matching = Matching(doses, question.Subject);
        return matching.Count switch
        {
            0 => null,
            1 => Answer(matching[0], question.AsksWhen, now),
            _ => string.Join("\n", matching.Select(dose => $"{dose.Item.Title}: {Answer(dose, question.AsksWhen, now)}")),
        };
    }

    /// <summary>"Yes, at 8:12 AM today." or "Not marked today. Last marked yesterday at 8:05 AM." Only what was marked.</summary>
    public static string Answer(DoseRecord dose, bool asksWhen, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var latest = dose.Taken.MaxBy(taken => taken.TakenAt);
        var none = $"Nothing marked in the last {KeepDays} days.";
        if (asksWhen)
        {
            return latest is null ? none : $"Last marked {When(latest.TakenAt, now)} at {Time(latest.TakenAt)}.";
        }

        if (dose.Taken.FirstOrDefault(taken => taken.DueOn == today) is { } todays)
        {
            return $"Yes, at {Time(todays.TakenAt)} {When(todays.TakenAt, now)}.";
        }

        return latest is null
            ? $"Not marked today. {none}"
            : $"Not marked today. Last marked {When(latest.TakenAt, now)} at {Time(latest.TakenAt)}.";
    }

    public static string Marked(string title, DateTime at) => $"{title}: marked taken at {Time(at)}.";

    /// <summary>"Already marked taken at 8:12 AM." Said instead of keeping the same day twice.</summary>
    public static string AlreadyTaken(DateTime at, DateTime now)
    {
        var day = When(at, now);
        return day == "today" ? $"Already marked taken at {Time(at)}." : $"Already marked taken {day} at {Time(at)}.";
    }

    /// <summary>"today", "yesterday", "on Monday" within a week, or "on Nov 9".</summary>
    public static string When(DateTime at, DateTime now) =>
        (DateOnly.FromDateTime(now).DayNumber - DateOnly.FromDateTime(at).DayNumber) switch
        {
            0 => "today",
            1 => "yesterday",
            > 1 and < 7 => "on " + at.ToString("dddd", CultureInfo.InvariantCulture),
            _ => "on " + at.ToString("MMM d", CultureInfo.InvariantCulture),
        };

    public static string Time(DateTime at) => at.ToString("h:mm tt", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(?:(?<when>when)\s+(?:did|have)\s+i\s+(?:last\s+)?|(?:did|have|had)\s+i\s+(?:already\s+|actually\s+)?)(?:take|taken|took)\s+(?<what>.+?)(?:\s+last)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Question();

    [GeneratedRegex(@"\s+(?:today|yet|already|this morning|this afternoon|this evening|tonight)$", RegexOptions.IgnoreCase)]
    private static partial Regex Trailing();

    [GeneratedRegex(@"^(?:all of my|all my|my|the|a|an)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex Leading();
}
