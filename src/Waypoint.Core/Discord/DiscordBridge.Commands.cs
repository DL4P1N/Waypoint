using System.Net.Http.Json;
using System.Text.Json;

namespace Waypoint.Core.Discord;

/// <summary>The slash commands Waypoint offers, and setting them up for the server.</summary>
public sealed partial class DiscordBridge
{
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
}
