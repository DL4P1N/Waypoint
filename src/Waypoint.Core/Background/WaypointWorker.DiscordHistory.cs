using System.Globalization;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Background;

/// <summary>
/// History on the phone: what was finished, canceled, put back, or went past, with the History page's filters and
/// its Reopen. Reopening calls the same database method the page does.
/// </summary>
public sealed partial class WaypointWorker
{
    /// <summary>Reopens an entry from the History view. The filter, page, and any search words follow, so the view is drawn again in place.</summary>
    public const string ReopenMenu = DiscordBridge.ViewPrefix + "reopen";

    /// <summary>Reopen on one History entry opened from a search. The entry's ID follows.</summary>
    public const string ReopenEntry = "reopen:";

    /// <summary>Search on the History view opens a form. The filter follows.</summary>
    public const string HistorySearch = "history-search:";

    private const string HistoryTarget = "history";
    private const int HistoryRead = 200;
    private const int HistoryPerPage = 15;

    private static readonly HistoryFilter[] HistoryFilters = [HistoryFilter.All, HistoryFilter.Completed, HistoryFilter.Canceled, HistoryFilter.Reopened, HistoryFilter.Past];

    /// <summary>A page of History, newest first, with the page's filters as buttons and a menu to reopen one.</summary>
    private DiscordCard HistoryCard(HistoryFilter filter, int page, string? words = null, string? said = null)
    {
        var entries = _database.SearchHistory(words, HistoryRead, filter);
        var pages = DiscordPages.Split(entries, entry => HistoryLine(entry).Length + 1, NoteText, HistoryPerPage);
        page = DiscordPages.Clamp(page, pages.Count);
        var shown = pages[page];
        var carried = words is null ? "" : ":" + Carry(words);
        var target = $"{HistoryTarget}:{filter}{carried}";

        var typed = words is null ? null : DiscordText.Escape(words);
        var heading = typed is null ? "History" : $"History matching {typed}";
        var title = DiscordPages.Titled(filter == HistoryFilter.All ? heading : $"{heading}, {FilterLabel(filter).ToLowerInvariant()}", page, pages.Count);
        var empty = typed is null ? CardLabels.NothingYet : CardLabels.NothingMatches("in History", typed);
        var lines = string.Join("\n", shown.Select(HistoryLine));
        var description = said is null ? lines : $"{DiscordText.Escape(said)}\n\n{lines}";

        var filters = HistoryFilters
            .Select(each => new CardAction(FilterLabel(each), DiscordPages.Id($"{HistoryTarget}:{each}{carried}", 0), each == filter ? ButtonRole.Main : ButtonRole.Other))
            .ToList();
        var tools = DiscordPages.Pager(target, page, pages.Count).Append(new CardAction("Search", HistorySearch + filter, ButtonRole.Other)).ToList();

        var canReopen = _database.ReopenableEntries(shown);
        var reopenable = shown.Where(entry => canReopen.Contains(entry.Id)).ToList();
        return new DiscordCard(Shorten(title, 256), shown.Count == 0 ? said ?? empty : description, [], CardTone.Reminder)
        {
            MoreRows = DiscordViews.Rows(filters, tools),
            Menu = new DiscordMenu(
                $"{ReopenMenu}:{filter}:{page.ToString(CultureInfo.InvariantCulture)}{carried}",
                "Reopen one",
                reopenable.Select(entry => new MenuChoice(entry.Title, entry.Id.ToString(CultureInfo.InvariantCulture), $"{entry.Label} {Day(entry.At)}")).ToList()),
        };
    }

    private static string HistoryLine(HistoryEntry entry)
    {
        var course = entry.Course is { } name ? $" ({DiscordText.Escape(ItemGroups.ShortCourse(name))})" : "";
        var line = $"• {DiscordText.Escape(entry.Title)}{course} · {entry.Label} {Day(entry.At)}";
        return Shorten(line, 300);
    }

    private static string FilterLabel(HistoryFilter filter) => filter switch
    {
        HistoryFilter.Completed => "Completed",
        HistoryFilter.Canceled => "Canceled",
        HistoryFilter.Reopened => "Reopened",
        HistoryFilter.Past => "Past",
        _ => "All",
    };

    private static string Day(DateTime at) => at.ToString("MMM d", CultureInfo.InvariantCulture);

    /// <summary>The History page's search words, read back from a page button, or the filter alone.</summary>
    private DiscordCard? HistoryPage(string rest, int page)
    {
        var parts = rest.Split(':', 2);
        if (!Enum.TryParse<HistoryFilter>(parts[0], out var filter))
        {
            return null;
        }

        string? words = null;
        if (parts.Length > 1 && (words = Uncarry(parts[1])) is null)
        {
            return null;
        }

        return HistoryCard(filter, page, words);
    }

    /// <summary>Reopen picked from the History view's menu: put back the same way the History page does it, and drawn again in place.</summary>
    private DiscordCard? OnReopenPicked(string menu, IReadOnlyList<string> values)
    {
        var parts = menu[(ReopenMenu.Length + 1)..].Split(':', 3);
        if (parts.Length < 2 || !Enum.TryParse<HistoryFilter>(parts[0], out var filter) || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var page))
        {
            return null;
        }

        var words = parts.Length > 2 ? Uncarry(parts[2]) : null;
        var said = values.Select(value => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? Reopen(id) : null).FirstOrDefault(text => text is not null);
        return HistoryCard(filter, page, words, said ?? "That one can't be reopened anymore.");
    }

    /// <summary>Puts one History entry back, and says what happened. Null when there's no such entry.</summary>
    private string? Reopen(long entryId)
    {
        if (_database.GetHistory().FirstOrDefault(entry => entry.Id == entryId) is not { } entry)
        {
            return null;
        }

        if (!_database.ReopenEntry(entry))
        {
            return $"{entry.Title} can't be reopened anymore.";
        }

        _host.Changed();
        ViewsChanged();
        return $"Put {entry.Title} back on the list.";
    }

    /// <summary>One History entry, as a search opens it, with Reopen when it can be put back.</summary>
    private DiscordCard HistoryEntryCard(HistoryEntry entry)
    {
        var lines = new List<string> { $"{entry.Label} {Day(entry.At)}" };
        if (entry.Course is { } course)
        {
            lines.Add($"Class: {course}");
        }

        var card = DiscordText.Notice(entry.Title, string.Join("\n", lines), CardTone.Finished);
        return _database.CanReopen(entry)
            ? card with { Actions = [new CardAction("Reopen", ReopenEntry + entry.Id.ToString(CultureInfo.InvariantCulture))] }
            : card;
    }

    /// <summary>/history: the filter and search words are both optional.</summary>
    private CommandReply HistoryCommand(SlashCommand command)
    {
        var filter = Enum.TryParse<HistoryFilter>(command.Options.GetValueOrDefault("filter"), ignoreCase: true, out var picked) ? picked : HistoryFilter.All;
        var words = command.Options.GetValueOrDefault("search")?.Trim();
        return new CommandReply(HistoryCard(filter, 0, string.IsNullOrEmpty(words) ? null : words), true);
    }

    private static string Shorten(string text, int limit) => DiscordText.Shorten(text, limit);
}
