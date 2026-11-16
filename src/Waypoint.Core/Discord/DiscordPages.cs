using System.Globalization;

namespace Waypoint.Core.Discord;

/// <summary>
/// Long views split into pages with Back and Next, since Discord refuses a card over its limits. The page number
/// rides in the button's ID along with what's being paged, so a press still works after a restart and nothing is
/// kept in between.
/// </summary>
public static class DiscordPages
{
    /// <summary>Starts a page button's action. The target and the page number follow, as in "page:todo:2".</summary>
    public const string Prefix = "page:";

    /// <summary>A menu holds 25 choices, so a page holds no more than that, and each one can be picked from it.</summary>
    public const int MaxItems = 25;

    /// <summary>
    /// What a page's text may add up to: well under Discord's 6000 for a whole card, leaving room for the title,
    /// field names, and the page line.
    /// </summary>
    public const int Budget = 4500;

    // Discord's limit for a button's ID, less "waypoint:act:" in front.
    private const int MaxId = 100 - 13;

    public static string Id(string target, int page) => $"{Prefix}{target}:{page.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Whether a target fits in a button's ID with any page number.</summary>
    public static bool Fits(string target) => Id(target, 999).Length <= MaxId;

    /// <summary>Reads a page button's action back into what it pages and which page.</summary>
    public static bool TryRead(string action, out string target, out int page)
    {
        target = "";
        page = 0;
        if (!action.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = action[Prefix.Length..];
        var colon = rest.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(rest[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out page))
        {
            return false;
        }

        target = rest[..colon];
        return true;
    }

    /// <summary>Back on every page but the first, Next on every page but the last, and nothing when there's one page.</summary>
    public static IReadOnlyList<CardAction> Pager(string target, int page, int count)
    {
        if (count <= 1)
        {
            return [];
        }

        var buttons = new List<CardAction>();
        if (page > 0)
        {
            buttons.Add(new CardAction("Back", Id(target, page - 1), ButtonRole.Other));
        }

        if (page < count - 1)
        {
            buttons.Add(new CardAction("Next", Id(target, page + 1), ButtonRole.Other));
        }

        return buttons;
    }

    /// <summary>The title with where this page sits, such as "To do, page 2 of 3".</summary>
    public static string Titled(string title, int page, int count) =>
        count <= 1 ? title : $"{title}, page {(page + 1).ToString(CultureInfo.InvariantCulture)} of {count.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>A page that's gone, such as after things were finished, shows the last one instead.</summary>
    public static int Clamp(int page, int count) => Math.Clamp(page, 0, Math.Max(0, count - 1));

    /// <summary>
    /// Splits things into pages in order, each holding at most <paramref name="maxItems"/> and at most
    /// <paramref name="budget"/> characters by <paramref name="size"/>. There's always at least one page, even if empty.
    /// </summary>
    public static List<List<T>> Split<T>(IReadOnlyList<T> items, Func<T, int> size, int budget = Budget, int maxItems = MaxItems)
    {
        var pages = new List<List<T>> { new() };
        var used = 0;
        foreach (var item in items)
        {
            var length = size(item);
            if (pages[^1].Count > 0 && (pages[^1].Count >= maxItems || used + length > budget))
            {
                pages.Add([]);
                used = 0;
            }

            pages[^1].Add(item);
            used += length;
        }

        return pages;
    }

    /// <summary>
    /// Splits long text into pages at line breaks, and inside a line only when one line alone is too long. Each
    /// page fits in a card's description.
    /// </summary>
    public static List<string> SplitText(string text, int limit)
    {
        var pages = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw;
            while (line.Length > limit)
            {
                Flush();
                var cut = line.LastIndexOf(' ', limit - 1);
                cut = cut <= limit / 2 ? limit : cut;
                pages.Add(line[..cut]);
                line = line[cut..].TrimStart();
            }

            if (current.Length + line.Length + 1 > limit)
            {
                Flush();
            }

            current.Append(line).Append('\n');
        }

        Flush();
        return pages.Count == 0 ? [""] : pages;

        void Flush()
        {
            var page = current.ToString().Trim('\n');
            if (page.Trim().Length > 0)
            {
                pages.Add(page);
            }

            current.Clear();
        }
    }
}
