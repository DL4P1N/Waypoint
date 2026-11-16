using System.Globalization;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Discord;

/// <summary>
/// The pinned views drawn as cards: To do for the week, Waiting on, Budget, and one card per list. They only lay
/// out what the database already says, so the same cards answer the matching slash commands.
/// </summary>
public static partial class DiscordViews
{
    public const string ToDo = "todo";
    public const string Waiting = "waiting";
    public const string Budget = "budget";
    public const string Lists = "lists";
    public const string Notes = "notes";
    public const string History = "history";

    /// <summary>Marks an item done from the To do view. Its choices are item references.</summary>
    public const string DoneMenu = DiscordBridge.ViewPrefix + "done";

    /// <summary>Checks off an entry from a list's view. Its choices are entry IDs.</summary>
    public const string CheckMenu = DiscordBridge.ViewPrefix + "check";

    public const int WeekDays = 7;

    private const int MaxFieldValue = 1024;
    private const int MaxTitle = 256;
    private const int MaxFieldName = 256;

    private const string FindEntries = ", found by name in /check";

    public static readonly IReadOnlyList<string> Names = [ToDo, Waiting, Budget, Lists, Notes, History];

    public static string Label(string view) => view switch
    {
        ToDo => "To do",
        Waiting => "Waiting on",
        Budget => "Budget",
        Lists => "Lists",
        Notes => "Notes",
        History => "History",
        _ => view,
    };

    /// <summary>The day an item is shown under on Discord, the same as on the PC. Anything from before today is "Overdue", in one group, never red.</summary>
    public static string DayLabel(DateOnly? date, DateOnly today) => date < today ? "Overdue" : ItemGroups.DayLabel(date, today);

    /// <summary>A day as it reads mid-sentence: "today", "tomorrow", "Friday", or the date itself.</summary>
    public static string InSentence(DateOnly date, DateOnly today) => (date.DayNumber - today.DayNumber) switch
    {
        0 => "today",
        1 => "tomorrow",
        > 1 and < WeekDays => date.ToString("dddd", CultureInfo.InvariantCulture),
        _ => date.ToString("ddd, MMM d", CultureInfo.InvariantCulture),
    };

    /// <summary>What the week view lists: top-level items that can be finished, from anything overdue through six days ahead, then undated ones.</summary>
    public static IReadOnlyList<StoredItem> WeekItems(IEnumerable<StoredItem> open, DateOnly today) =>
        open.Where(item => item.ParentRef is null && !item.Done && item.Kind is not (ItemKind.Note or ItemKind.Expense or ItemKind.Shopping))
            .Where(item => item.DueDate is null || item.DueDate < today.AddDays(WeekDays))
            .OrderBy(item => item.DueDate is null)
            .ThenBy(item => item.DueDate)
            .ThenBy(item => item.DueAt ?? DateTimeOffset.MaxValue)
            .ToList();

    /// <summary>A short list of the commands, for /help and for a command Discord still shows but Waypoint no longer has.</summary>
    public static DiscordCard HelpCard(string title = "Commands") =>
        new(title, string.Join("\n",
        [
            "/now: what to do next",
            "/todo: this week, with a menu to mark one done",
            "/add, /done, /move, /start: change what's on the list",
            "/today: classes, what's due, and safe to spend",
            "/budget, /spent, /afford: money",
            "/lists, /check, /listadd: lists",
            "/waiting: who you're waiting to hear back from",
            "/new: add something with a form",
            "/notes, /history, /search: find notes, what's done, or anything",
            "/purchases: this month's purchases",
            "/settings, /order: settings, and the order of filters, lists, and folders",
            "/snooze, /save-spot: the latest reminder, and where you stopped",
            "/pinhere, /unpin: keep a view pinned in a channel",
            "",
            "A file sent in the notes channel becomes a note.",
            "In the notes and lists forums, type in a post to add to that note or list, or start a new post to make one.",
            "",
            "Anything typed in the capture channel is sorted like the box on the PC.",
        ]), [], CardTone.Reminder);

    public static DiscordCard BudgetCard(IReadOnlyList<string> lines) =>
        DiscordText.Notice("Budget", lines.Count == 0 ? "Nothing set up yet. Type your budget or balances in the capture channel, or add them on the Budget page on the PC." : string.Join("\n", lines), CardTone.Money);

    private static string Line(StoredItem item)
    {
        var time = item.DueAt is { } at && ItemGroups.IsScheduled(item) ? " · " + at.LocalDateTime.ToString("h:mm tt", CultureInfo.InvariantCulture) : "";
        var course = item.Course is { } name ? $" ({DiscordText.Escape(ItemGroups.ShortCourse(name))})" : "";
        var kind = item.Kind == ItemKind.Exam ? "Exam: " : "";
        return $"• {kind}{DiscordText.Escape(item.Title)}{course}{time}";
    }

    /// <summary>
    /// Keeps whole lines and says how many didn't fit, since Discord refuses anything over its limit. When there's
    /// room it also says where the rest can be found.
    /// </summary>
    private static string FitLines(IReadOnlyList<string> lines, int limit, string find = "")
    {
        find = limit >= 200 ? find : "";
        var reserve = 20 + find.Length;
        var kept = new List<string>();
        var length = 0;
        foreach (var line in lines)
        {
            var piece = line.Length > limit - reserve ? line[..(limit - reserve)] : line;
            if (length + piece.Length + 1 > limit - reserve)
            {
                break;
            }

            kept.Add(piece);
            length += piece.Length + 1;
        }

        if (kept.Count < lines.Count)
        {
            kept.Add($"and {lines.Count - kept.Count} more{find}");
        }

        return string.Join("\n", kept);
    }
}
