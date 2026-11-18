using System.Globalization;
using System.Text.Json;
using Waypoint.Core.Budget;

namespace Waypoint.Core.Capture;

public static class SortedItemParser
{
    public static IReadOnlyList<SortedItem> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Parse(document.RootElement);
    }

    public static IReadOnlyList<SortedItem> Parse(JsonElement root)
    {
        var items = new List<SortedItem>();

        foreach (var element in root.GetProperty("items").EnumerateArray())
        {
            var title = element.GetProperty("title").GetString()?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                continue;
            }

            items.Add(new SortedItem(
                ParseKind(element.GetProperty("kind").GetString()),
                title,
                ParseDate(element.GetProperty("due_date")),
                ParseAmount(element.GetProperty("amount_cents")),
                ParseTime(element),
                ParseReminders(element),
                ParseRepeat(element),
                ParseSteps(element),
                ParseRecurrence(element),
                ParseText(element, "category"),
                element.TryGetProperty("one_off", out var oneOff) && oneOff.ValueKind == JsonValueKind.True,
                ParseText(element, "about"),
                EndTime: ParseTime(element, "end_time"),
                Dose: element.TryGetProperty("dose", out var dose) && dose.ValueKind == JsonValueKind.True));
        }

        return items;
    }

    /// <summary>Reads the changes and answer alongside the items.</summary>
    public static CaptureResult ParseResult(JsonElement root) =>
        new(Parse(root), ParseChanges(root), ParseText(root, "answer"), ParseBudget(root), ParseBudgetOps(root), ParseOps(root), ParseText(root, "focus_ref"));

    /// <summary>Reads a nudge plan, skipping events it can't place.</summary>
    public static IReadOnlyList<EventNudges> ParseNudges(JsonElement root)
    {
        var plans = new List<EventNudges>();
        foreach (var plan in root.GetProperty("plans").EnumerateArray())
        {
            if (ItemRef.TryParse(plan.GetProperty("ref").GetString(), out var source, out var id) && source == ItemSource.Calendar)
            {
                plans.Add(new EventNudges(id, ParseReminders(plan) ?? []));
            }
        }

        return plans;
    }

    private static IReadOnlyList<ItemChange> ParseChanges(JsonElement root)
    {
        var changes = new List<ItemChange>();
        if (!root.TryGetProperty("changes", out var elements) || elements.ValueKind != JsonValueKind.Array)
        {
            return changes;
        }

        foreach (var element in elements.EnumerateArray())
        {
            if (ItemRef.TryParse(element.GetProperty("ref").GetString(), out var source, out var id)
                && Enum.TryParse<ChangeAction>(element.GetProperty("action").GetString()?.Replace("_", ""), ignoreCase: true, out var action))
            {
                changes.Add(new ItemChange(
                    source,
                    id,
                    action,
                    ParseText(element, "title"),
                    element.TryGetProperty("due_date", out var date) ? ParseDate(date) : null,
                    ParseTime(element),
                    ParseSteps(element)));
            }
        }

        return changes;
    }

    private static IReadOnlyList<PlannedStep>? ParseSteps(JsonElement item)
    {
        if (!item.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parsed = new List<PlannedStep>();
        foreach (var step in steps.EnumerateArray())
        {
            if (ParseText(step, "title") is { } title)
            {
                var hasTime = DateTime.TryParseExact(ParseText(step, "at"), "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at);
                parsed.Add(new PlannedStep(title, hasTime ? at : null));
            }
        }

        return parsed.Count > 0 ? parsed : null;
    }

    private static Recurrence? ParseRecurrence(JsonElement item) =>
        item.TryGetProperty("recurrence", out var element)
        && element.ValueKind == JsonValueKind.Object
        && Enum.TryParse<RecurrenceUnit>(element.GetProperty("unit").GetString(), ignoreCase: true, out var unit)
            ? new Recurrence(unit, element.GetProperty("interval").GetInt32())
            : null;

    private static long? ParseBudget(JsonElement root) =>
        root.TryGetProperty("monthly_budget_cents", out var element) ? ParseAmount(element) : null;

    private static IReadOnlyList<BudgetOp> ParseBudgetOps(JsonElement root)
    {
        var ops = new List<BudgetOp>();
        if (!root.TryGetProperty("budget_ops", out var elements) || elements.ValueKind != JsonValueKind.Array)
        {
            return ops;
        }

        foreach (var element in elements.EnumerateArray())
        {
            if (Enum.TryParse<BudgetOpKind>(element.GetProperty("op").GetString()?.Replace("_", ""), ignoreCase: true, out var kind)
                && ParseText(element, "name") is { } name)
            {
                ops.Add(new BudgetOp(
                    kind,
                    name,
                    ParseAmount(element.GetProperty("amount_cents")),
                    ParseText(element, "category"),
                    element.TryGetProperty("hours", out var hours) && hours.ValueKind == JsonValueKind.Number ? hours.GetInt32() : null,
                    ParseText(element, "reason")));
            }
        }

        return ops;
    }

    /// <summary>Reads assistant ops, skipping any kind this version doesn't know.</summary>
    private static IReadOnlyList<AssistantOp> ParseOps(JsonElement root)
    {
        var ops = new List<AssistantOp>();
        if (!root.TryGetProperty("ops", out var elements) || elements.ValueKind != JsonValueKind.Array)
        {
            return ops;
        }

        foreach (var element in elements.EnumerateArray())
        {
            if (AssistantOps.TryParseKind(element.GetProperty("op").GetString(), out var kind))
            {
                ops.Add(new AssistantOp(
                    kind,
                    ParseText(element, "name"),
                    ParseText(element, "text"),
                    ParseEntries(element),
                    element.TryGetProperty("amount_cents", out var amount) ? ParseAmount(amount) : null,
                    ParseMoment(element, "at"),
                    ParseMoment(element, "until"),
                    ParseText(element, "next"),
                    ParseText(element, "account")));
            }
        }

        return ops;
    }

    private static IReadOnlyList<string>? ParseEntries(JsonElement element) =>
        element.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array
            ? entries.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.String).Select(entry => entry.GetString()!).ToList()
            : null;

    private static DateTime? ParseMoment(JsonElement element, string name) =>
        DateTime.TryParseExact(ParseText(element, name), "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment) ? moment : null;

    private static string? ParseText(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static IReadOnlyList<PlannedReminder>? ParseReminders(JsonElement item)
    {
        if (!item.TryGetProperty("reminders", out var reminders) || reminders.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parsed = new List<PlannedReminder>();
        foreach (var reminder in reminders.EnumerateArray())
        {
            if (DateTime.TryParseExact(
                reminder.GetProperty("at").GetString(),
                "yyyy-MM-ddTHH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var at))
            {
                parsed.Add(new PlannedReminder(at, reminder.GetProperty("message").GetString()));
            }
        }

        return parsed.Count > 0 ? parsed : null;
    }

    private static int? ParseRepeat(JsonElement item) =>
        item.TryGetProperty("repeat_minutes", out var element)
        && element.ValueKind == JsonValueKind.Number
        && element.TryGetInt32(out var minutes)
            ? minutes
            : null;

    private static TimeOnly? ParseTime(JsonElement item, string name = "due_time") =>
        item.TryGetProperty(name, out var element)
        && element.ValueKind == JsonValueKind.String
        && TimeOnly.TryParseExact(element.GetString(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : null;

    private static ItemKind ParseKind(string? kind) =>
        Enum.TryParse<ItemKind>(kind, ignoreCase: true, out var parsed) ? parsed : ItemKind.Note;

    private static DateOnly? ParseDate(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
        && DateOnly.TryParseExact(element.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static long? ParseAmount(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var cents) ? cents : null;
}
