namespace Waypoint.Core.Capture;

/// <summary>What the energy buttons on To do ask for.</summary>
public enum EnergyLevel
{
    /// <summary>"I have 15 minutes": the most pressing thing that's small enough.</summary>
    FifteenMinutes,

    /// <summary>"Low energy": the lightest thing, with the most pressing of those first.</summary>
    LowEnergy,
}

/// <summary>The one thing picked, and a short plain reason. Item is a step when the step is what to do.</summary>
public sealed record EnergyPick(StoredItem Item, string Reason);

/// <summary>
/// Picks one thing for a short or tired stretch from dates and sizes alone, so the buttons work offline
/// and give the same answer every time. Small means a step, or a task with no steps. A deadline or exam
/// with no steps is big, and is only picked when there's nothing small, to be opened and nothing more.
/// </summary>
public static class EnergyPicks
{
    /// <summary>How far ahead a big item counts as worth opening when nothing small is left.</summary>
    public const int BigLookAheadDays = 7;

    /// <summary>A typed "I have N minutes" counts as a short stretch up to this many minutes.</summary>
    public const int ShortStretchMinutes = 30;

    private static readonly string[] LowEnergyWords =
    [
        "low energy", "im low energy", "i have low energy", "im exhausted", "i am exhausted", "exhausted", "im tired", "i am tired",
        "so tired", "im so tired", "im wiped", "i cant focus", "i can not focus", "cant focus", "i cannot focus", "no energy",
    ];

    private static readonly string[] ShortLeads = ["", "i have ", "ive got ", "i got ", "i only have ", "only ", "i have only ", "got "];

    /// <summary>
    /// A short typed request the buttons answer, such as "I have 15 minutes" or "I'm exhausted", so typing gives the
    /// same pick as the button with no sorter. Anything longer or less clear is null and goes to the sorter.
    /// </summary>
    public static EnergyLevel? ParseRequest(string text)
    {
        var clean = string.Join(' ', new string(text.ToLowerInvariant().Replace("'", "").Replace("\u2019", "").Select(letter => char.IsLetterOrDigit(letter) ? letter : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (LowEnergyWords.Contains(clean))
        {
            return EnergyLevel.LowEnergy;
        }

        foreach (var lead in ShortLeads)
        {
            if (!clean.StartsWith(lead, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = clean[lead.Length..].Split(' ');
            if (rest.Length == 2 && int.TryParse(rest[0], out var minutes) && minutes is > 0 and <= ShortStretchMinutes && rest[1] is "minutes" or "minute" or "min" or "mins")
            {
                return EnergyLevel.FifteenMinutes;
            }
        }

        return null;
    }

    /// <summary>The start of the reply for a pick, the same from a button or typed.</summary>
    public static string Heading(EnergyLevel level) => level == EnergyLevel.FifteenMinutes ? "For 15 minutes" : "For low energy";

    private enum Size
    {
        Step,
        Task,
        Big,
    }

    /// <summary>Declined holds refs just turned down with Not now (<see cref="NotNowDeclines"/>), which are passed over.</summary>
    public static EnergyPick? Pick(IReadOnlyList<StoredItem> open, EnergyLevel level, DateOnly today, IReadOnlySet<string>? declined = null)
    {
        var passed = NotNowDeclines.WithParents(declined ?? new HashSet<string>(), open);
        var candidates = Candidates(open, today)
            .Where(candidate => !passed.Contains(ItemRef.Format(candidate.Item)) && (candidate.Parent is not { } parent || !passed.Contains(ItemRef.Format(parent))))
            .ToList();
        var small = candidates.Where(candidate => candidate.Size != Size.Big);

        var ordered = level == EnergyLevel.FifteenMinutes
            ? small.OrderBy(candidate => Urgency(candidate.Due, today)).ThenBy(candidate => candidate.Due ?? DateOnly.MaxValue).ThenBy(candidate => candidate.Size)
            : small.OrderBy(candidate => candidate.Size == Size.Task ? 0 : 1).ThenBy(candidate => Urgency(candidate.Due, today)).ThenBy(candidate => candidate.Due ?? DateOnly.MaxValue);

        if (ordered.ThenBy(candidate => candidate.Item.Id).FirstOrDefault() is { } best)
        {
            return new EnergyPick(best.Item, Reason(best, today));
        }

        var big = candidates
            .Where(candidate => candidate.Size == Size.Big && candidate.Due is { } due && due.DayNumber - today.DayNumber <= BigLookAheadDays)
            .OrderBy(candidate => candidate.Due)
            .ThenBy(candidate => candidate.Item.Id)
            .FirstOrDefault();
        return big is null ? null : new EnergyPick(big.Item, $"Nothing small is waiting, so just open it. {DuePhrase(big.Due, today)}");
    }

    /// <summary>Today and earlier come first, then the next few days, then the rest, then no date.</summary>
    private static int Urgency(DateOnly? due, DateOnly today) => due switch
    {
        null => 3,
        { } day when day <= today => 0,
        { } day when day.DayNumber - today.DayNumber <= 3 => 1,
        _ => 2,
    };

    private static IEnumerable<Candidate> Candidates(IReadOnlyList<StoredItem> open, DateOnly today)
    {
        foreach (var item in open)
        {
            if (item.Done || item.ParentRef is not null || item.Recurs is not null || item.Kind is not (ItemKind.Task or ItemKind.Deadline or ItemKind.Exam))
            {
                continue;
            }

            // A past feed item left unchecked isn't something to start on, the same as the row's Start.
            if (item.Source == ItemSource.Calendar && item.DueDate < today)
            {
                continue;
            }

            if (ItemGroups.StepsOf(item, open).FirstOrDefault(step => !step.Done) is { } step)
            {
                yield return new Candidate(step, item, Size.Step, step.DueDate ?? item.DueDate);
            }
            else
            {
                yield return new Candidate(item, null, item.Kind == ItemKind.Task ? Size.Task : Size.Big, item.DueDate);
            }
        }
    }

    private static string Reason(Candidate candidate, DateOnly today)
    {
        var what = candidate.Parent is { } parent ? $"The next small step on {parent.Title}." : "It's a small one.";
        return $"{what} {DuePhrase(candidate.Due, today)}".Trim();
    }

    private static string DuePhrase(DateOnly? due, DateOnly today) => due switch
    {
        null => "",
        { } day when day < today => "It's overdue.",
        { } day when day == today => "It's due today.",
        { } day => $"It's due {ItemGroups.DayLabel(day, today).Replace("Tomorrow", "tomorrow")}.",
    };

    private sealed record Candidate(StoredItem Item, StoredItem? Parent, Size Size, DateOnly? Due);
}
