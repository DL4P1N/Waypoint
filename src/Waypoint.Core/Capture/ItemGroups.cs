using System.Globalization;
using System.Text.RegularExpressions;

namespace Waypoint.Core.Capture;

public sealed record ItemGroup(string Label, IReadOnlyList<StoredItem> Items);

public static partial class ItemGroups
{
    private static readonly string[] FeedEventWords = ["Help Session", "Review Session", "Office Hours", "Holiday", "Fall Break", "Spring Break", "Lecture", "Meeting", "Appointment"];

    /// <summary>
    /// Whether an item is something to do. Notes live on Notes, and purchases and shopping are Budget's and the lists',
    /// so none of them are rows on To do or the week, even though a typed purchase is stored open.
    /// </summary>
    public static bool IsToDo(StoredItem item) => item.Kind is not (ItemKind.Note or ItemKind.Shopping or ItemKind.Expense);

    /// <summary>Groups items by day, keeping the order they were given in. Steps stay with their parent, and only things to do are grouped.</summary>
    public static IReadOnlyList<ItemGroup> ByDay(IEnumerable<StoredItem> items, DateOnly today) =>
        items
            .Where(item => item.ParentRef is null && IsToDo(item))
            .GroupBy(item => DayLabel(item.DueDate, today))
            .Select(group => new ItemGroup(group.Key, group.ToList()))
            .ToList();

    /// <summary>
    /// Whether something happens at a time rather than being due. The feed labels everything the
    /// same, so its events are told apart from assignments by their titles.
    /// </summary>
    public static bool IsScheduled(StoredItem item) =>
        item.Kind is ItemKind.Reminder or ItemKind.Exam or ItemKind.Event
        || (item.Source == ItemSource.Calendar
            && !item.Title.Contains("Signup", StringComparison.OrdinalIgnoreCase)
            && !item.Title.Contains("Sign up", StringComparison.OrdinalIgnoreCase)
            && FeedEventWords.Any(word => item.Title.Contains(word, StringComparison.OrdinalIgnoreCase)));

    /// <summary>An item's open steps, in the order they should be done.</summary>
    public static IReadOnlyList<StoredItem> StepsOf(StoredItem parent, IEnumerable<StoredItem> items) =>
        items.Where(item => item.ParentRef == ItemRef.Format(parent)).OrderBy(item => item.Id).ToList();

    /// <summary>Open items with finished ones mixed back in by date, after the open ones of each day.</summary>
    public static IReadOnlyList<StoredItem> WithFinished(IEnumerable<StoredItem> open, IEnumerable<StoredItem> finished) =>
        open.Concat(finished)
            .OrderBy(item => item.DueDate is null)
            .ThenBy(item => item.DueDate)
            .ThenBy(item => item.Done)
            .ThenBy(item => item.DueAt ?? DateTimeOffset.MaxValue)
            .ToList();

    public static string DayLabel(DateOnly? date, DateOnly today)
    {
        if (date is not { } day)
        {
            return "No date";
        }

        return (day.DayNumber - today.DayNumber) switch
        {
            < 0 => "Overdue",
            0 => "Today",
            1 => "Tomorrow",
            < 7 => day.ToString("dddd", CultureInfo.InvariantCulture),
            _ => day.ToString("ddd, MMM d", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// A short reply saying what was made from a capture, where each thing went, and for when, in the words the
    /// pages use: "Added Call the bank to To do, with no date", "Added milk and eggs to Shopping", "Added $12.40 at
    /// Blue Plate to Budget".
    /// </summary>
    public static string AddedSummary(IEnumerable<SortedItem> items)
    {
        var list = items.ToList();
        var sentences = new List<string>();
        var shopping = list.Where(item => item.Kind == ItemKind.Shopping).Select(item => item.Title).ToList();
        var shoppingSaid = false;
        foreach (var item in list)
        {
            if (item.Kind == ItemKind.Shopping)
            {
                if (!shoppingSaid)
                {
                    sentences.Add($"Added {JoinNames(shopping)} to Shopping");
                    shoppingSaid = true;
                }

                continue;
            }

            sentences.Add(item.Kind switch
            {
                ItemKind.Expense => item.AmountCents is { } cents
                    ? $"Added {Briefing.Money(cents)} at {item.Title} to Budget"
                    : $"Added {item.Title} to Budget",
                ItemKind.Note => $"Added {item.Title} to Notes",
                _ => $"Added {item.Title} to To do, {When(item)}{StepCount(item.Steps)}{FirstStep(item.Steps)}",
            });
        }

        return JoinSentences(sentences);
    }

    /// <summary>"milk", "milk and eggs", or "milk, eggs, and bread".</summary>
    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{string.Join(", ", names.Take(names.Count - 1))}, and {names[^1]}",
    };

    /// <summary>When a To do item is for, as a reply says it.</summary>
    private static string When(SortedItem item)
    {
        var day = item.DueDate?.ToString("ddd, MMM d", CultureInfo.InvariantCulture);
        var time = item.DueTime?.ToString("h:mm tt", CultureInfo.InvariantCulture);
        var repeats = item.Recurs is { } recurs ? $"{recurs.Label.ToLowerInvariant()}, " : "";
        return (day, time) switch
        {
            (null, null) => item.Recurs is { } only ? only.Label.ToLowerInvariant() : "with no date",
            (null, { } at) => $"{repeats}at {at}",
            ({ } on, null) => item.Kind == ItemKind.Deadline ? $"{repeats}due {on}" : $"{repeats}for {on}",
            ({ } on, { } at) => item.Kind == ItemKind.Deadline ? $"{repeats}due {on} at {at}" : $"{repeats}for {on} at {at}",
        };
    }

    private static string StepCount(IReadOnlyList<PlannedStep>? steps) =>
        steps is { Count: > 0 } ? $", in {steps.Count} steps" : "";

    private static string FirstStep(IReadOnlyList<PlannedStep>? steps) =>
        steps is { Count: > 0 } ? $". Start with: {steps[0].Title}" : "";

    /// <summary>A short reply covering everything a capture did, without its answer.</summary>
    public static string OutcomeSummary(CaptureOutcome outcome)
    {
        var parts = outcome.Changes.Select(ChangeSummary).ToList();
        if (outcome.Items.Count > 0)
        {
            parts.Insert(0, AddedSummary(outcome.Items));
        }

        if (outcome.MonthlyBudgetCents is { } budget)
        {
            parts.Add($"Monthly budget set to {Briefing.Money(budget)}");
        }

        parts.AddRange(outcome.BudgetReplies ?? []);
        parts.AddRange(outcome.OpReplies ?? []);
        return JoinSentences(parts);
    }

    /// <summary>A reply that's already a sentence keeps its own ending, so nothing doubles up.</summary>
    private static string JoinSentences(IReadOnlyList<string> parts) =>
        string.Concat(parts.Select((part, index) =>
            index == parts.Count - 1 ? part : part.EndsWith('.') || part.EndsWith('?') || part.EndsWith('!') ? $"{part} " : $"{part}. "));

    public static string ChangeSummary(AppliedChange applied)
    {
        var change = applied.Change;
        if (applied.Note is { } note)
        {
            return note;
        }

        if (!applied.Applied)
        {
            return change.Source == ItemSource.Calendar
                ? $"{applied.Title} comes from the calendar, so it stays as it is"
                : $"Couldn't change {applied.Title}";
        }

        return change.Action switch
        {
            ChangeAction.Done => $"Finished {applied.Title}",
            ChangeAction.Cancel => $"Canceled {applied.Title}",
            ChangeAction.Reopen => $"{applied.Title} is back on the list",
            ChangeAction.StopReminders => $"No more reminders for {applied.Title}",
            ChangeAction.UseFeedDate => $"{applied.Title} is back on its calendar date",
            ChangeAction.BreakDown => $"Broke {applied.Title} into {change.Steps!.Count} steps. Start with: {change.Steps[0].Title}",
            _ when change.DueDate is { } date => $"Moved {applied.Title} to {date.ToString("ddd, MMM d", CultureInfo.InvariantCulture)}{TimeSuffix(change.DueTime)}",
            _ when change.DueTime is not null => $"Moved {applied.Title} to{TimeSuffix(change.DueTime)}",
            _ => $"Renamed to {applied.Title}",
        };
    }

    private static string TimeSuffix(TimeOnly? time) =>
        time is { } clock ? " " + clock.ToString("h:mm tt", CultureInfo.InvariantCulture) : "";

    /// <summary>Shortens "CS 220-01 Term 1" to "CS 220". Other names are left as they are.</summary>
    public static string ShortCourse(string course)
    {
        var match = CourseCode().Match(course);
        return match.Success ? match.Value : course;
    }

    [GeneratedRegex(@"^[A-Z]+ \d{3,4}")]
    private static partial Regex CourseCode();
}
