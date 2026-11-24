using System.Globalization;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Discord;

/// <summary>The views that can run long, drawn one page at a time with Back and Next under them.</summary>
public static partial class DiscordViews
{
    /// <summary>A card's description holds 4096 characters. A little is kept back.</summary>
    private const int PageText = 3800;

    private const int MaxLine = 300;

    /// <summary>A page of the week, one field per day, with anything overdue first. A menu under it marks one on this page done.</summary>
    public static DiscordCard ToDoCard(IEnumerable<StoredItem> open, DateOnly today, int page = 0)
    {
        var items = WeekItems(open, today);
        var pages = DiscordPages.Split(items, item => Clip(Line(item)).Length + 1);
        page = DiscordPages.Clamp(page, pages.Count);
        var shown = pages[page];

        var fields = new List<CardField>();
        foreach (var item in shown)
        {
            var name = DayLabel(item.DueDate, today);
            var line = Clip(Line(item));
            if (fields.Count > 0 && fields[^1].Name == name && fields[^1].Value.Length + line.Length + 1 <= MaxFieldValue)
            {
                fields[^1] = fields[^1] with { Value = fields[^1].Value + "\n" + line };
            }
            else
            {
                fields.Add(new CardField(name, line, false));
            }
        }

        var menu = new DiscordMenu(DoneMenu, "Mark one done", shown
            .Select(item => new MenuChoice(item.Title, ItemRef.Format(item), DayLabel(item.DueDate, today)))
            .ToList());

        return new DiscordCard(DiscordPages.Titled("To do", page, pages.Count), fields.Count == 0 ? "Nothing due this week." : null, fields, CardTone.Reminder)
        {
            Menu = menu,
            MoreRows = Rows(DiscordPages.Pager(ToDo, page, pages.Count)),
        };
    }

    /// <summary>Which page of the week an item is on, so the page stays after it's finished. The first page when it isn't there.</summary>
    public static int ToDoPageOf(IEnumerable<StoredItem> open, DateOnly today, long itemId)
    {
        var found = DiscordPages.Split(WeekItems(open, today), item => Clip(Line(item)).Length + 1).FindIndex(page => page.Any(item => item.Id == itemId));
        return Math.Max(found, 0);
    }

    public const string ListHashPrefix = "listh:";

    // Discord's limit for a menu's ID.
    private const int MaxMenuId = 100;

    /// <summary>
    /// The target a list's buttons and menu carry: its name when that fits, else a short hash of the name. Either
    /// way it finds the same list after lists are added or reordered, even after a restart.
    /// </summary>
    public static string ListTarget(string name)
    {
        var byName = PinnedViews.ListPrefix + name;
        return DiscordPages.Fits(byName) && CheckMenu.Length + 1 + byName.Length <= MaxMenuId ? byName : ListHashPrefix + NameHash(name);
    }

    /// <summary>Short and the same after a restart.</summary>
    public static string NameHash(string name) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..12];

    private static List<List<(ListEntry Entry, string Line)>> ListPages(ListOverview list)
    {
        var lines = list.Entries.Where(entry => !entry.Done).Select(entry => (Entry: entry, Line: Clip("☐ " + DiscordText.Escape(entry.Text))))
            .Concat(list.Entries.Where(entry => entry.Done).Select(entry => (Entry: entry, Line: Clip($"☑ ~~{DiscordText.Escape(entry.Text)}~~"))))
            .ToList();
        return DiscordPages.Split(lines, line => line.Line.Length + 1, PageText);
    }

    /// <summary>Which page of a list an entry is on, so the page stays after it's checked off. The first page when it isn't there.</summary>
    public static int ListPageOf(ListOverview list, long entryId) =>
        Math.Max(ListPages(list).FindIndex(page => page.Any(line => line.Entry.Id == entryId)), 0);

    /// <summary>What's left first, then what's checked off, struck through, a page at a time. A menu under it checks one off.</summary>
    public static DiscordCard ListCard(ListOverview list, int page = 0, string? target = null)
    {
        var pages = ListPages(list);
        page = DiscordPages.Clamp(page, pages.Count);
        var shown = pages[page];
        target ??= ListTarget(list.Name);

        // The menu says which list it's on, so it works on a list opened from anywhere, not only a pinned view.
        var menu = new DiscordMenu($"{CheckMenu}:{target}", "Check one off", shown.Where(line => !line.Entry.Done)
            .Select(line => new MenuChoice(line.Entry.Text, line.Entry.Id.ToString(CultureInfo.InvariantCulture)))
            .ToList());
        var name = DiscordText.Escape(list.Name);
        var title = DiscordPages.Titled(name.Length > MaxTitle - 20 ? name[..(MaxTitle - 20)] : name, page, pages.Count);
        return new DiscordCard(title, list.Entries.Count == 0 ? CardLabels.EmptyList : string.Join("\n", shown.Select(line => line.Line)), [], CardTone.Reminder)
        {
            Menu = menu,
            MoreRows = Rows(DiscordPages.Pager(target, page, pages.Count)),
        };
    }

    /// <summary>Every list with what's left on each, a page of lists at a time, for /lists.</summary>
    public static DiscordCard ListsCard(IReadOnlyList<ListOverview> lists, int page = 0)
    {
        if (lists.Count == 0)
        {
            return new DiscordCard("Lists", "No lists yet. Use /listadd to start one.", [], CardTone.Reminder);
        }

        var groups = lists.Select(list =>
        {
            var open = list.Entries.Where(entry => !entry.Done).Select(entry => "☐ " + DiscordText.Escape(entry.Text)).ToList();
            var name = DiscordText.Escape(list.Name);
            var value = FitLines(open.Count > 0 ? open : list.Entries.Count > 0 ? ["All done"] : ["Empty"], MaxFieldValue, FindEntries);
            return new CardField(name.Length > MaxFieldName ? name[..MaxFieldName] : name, value, true);
        }).ToList();
        var pages = DiscordPages.Split(groups, field => field.Name.Length + field.Value.Length);
        page = DiscordPages.Clamp(page, pages.Count);
        return new DiscordCard(DiscordPages.Titled("Lists", page, pages.Count), null, pages[page], CardTone.Reminder)
        {
            MoreRows = Rows(DiscordPages.Pager(Lists, page, pages.Count)),
        };
    }

    /// <summary>Who you're waiting on, a page at a time.</summary>
    public static DiscordCard WaitingCard(IReadOnlyList<WaitingItem> waiting, DateOnly today, int page = 0)
    {
        if (waiting.Count == 0)
        {
            return new DiscordCard("Waiting on", "You're not waiting on anyone right now.", [], CardTone.Reminder);
        }

        var lines = WaitingOn.Describe(waiting, today).Split('\n').Skip(1).Select(line => Clip("• " + DiscordText.Escape(line))).ToList();
        var pages = DiscordPages.Split(lines, line => line.Length + 1, PageText);
        page = DiscordPages.Clamp(page, pages.Count);
        return new DiscordCard(DiscordPages.Titled("Waiting on", page, pages.Count), string.Join("\n", pages[page]), [], CardTone.Reminder)
        {
            MoreRows = Rows(DiscordPages.Pager(Waiting, page, pages.Count)),
        };
    }

    /// <summary>One row of buttons, or none.</summary>
    public static IReadOnlyList<IReadOnlyList<CardAction>> Rows(params IReadOnlyList<CardAction>[] rows) =>
        rows.Where(row => row.Count > 0).ToList();

    private static string Clip(string line) => line.Length > MaxLine ? line[..(MaxLine - 1)] + "…" : line;
}
