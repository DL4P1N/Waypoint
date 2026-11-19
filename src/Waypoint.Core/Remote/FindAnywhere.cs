using System.Globalization;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;

namespace Waypoint.Core.Remote;

/// <summary>The page a found thing lives on, in the order Find anywhere lists them.</summary>
public enum FoundPage
{
    ToDo,
    Lists,
    Notes,
    History,
    Budget,
}

/// <summary>
/// One thing Find anywhere turned up: what it's called, a short line saying where it is, and what's needed to show
/// it on its page. Only the field for its own page is set.
/// </summary>
public sealed record FoundThing(FoundPage Page, string Title, string Where)
{
    public StoredItem? Item { get; init; }

    public ListEntry? Entry { get; init; }

    public long? NoteId { get; init; }

    public HistoryEntry? History { get; init; }

    public SpendingRow? Purchase { get; init; }

    /// <summary>What a screen reader says for the row.</summary>
    public override string ToString() => $"{Title}, {Where}";
}

/// <summary>Each page's data, this PC's or the server's. A page that's missing is just not searched.</summary>
public sealed record FindSources(IToDoData? ToDo, IListsData? Lists, INotesData? Notes, IHistoryData? History, IBudgetData? Budget);

/// <summary>
/// What Find anywhere turned up, with the pages that answered and those that couldn't be reached just now. A page
/// with no data to search is in neither.
/// </summary>
public sealed record FindResult(IReadOnlyList<FoundThing> Found, IReadOnlyList<FoundPage> Answered, IReadOnlyList<FoundPage> Unreached)
{
    /// <summary>Some pages couldn't be searched, so what's found may not be everything.</summary>
    public bool Partial => Unreached.Count > 0;
}

/// <summary>
/// Searches every page at once, with no Claude call: open to-dos, list entries, notes, History, and purchases.
/// Each page is searched the way its own search box does it, through the same data calls the page uses, so it
/// works the same in server mode, and while the server is away it finds what the pages last had.
/// </summary>
public static class FindAnywhere
{
    /// <summary>How many things each page shows at most, so one busy page can't push the others out of sight.</summary>
    public const int PerPage = 6;

    /// <summary>Shorter than this and nearly everything would match.</summary>
    public const int MinLength = 2;

    public static IReadOnlyList<FoundThing> Search(string? text, FindSources sources, int perPage = PerPage) => Look(text, sources, perPage).Found;

    /// <summary>
    /// Like <see cref="Search"/>, and also says which pages answered and which couldn't be reached, so a search that
    /// couldn't reach a page isn't taken for one where nothing matched.
    /// </summary>
    public static FindResult Look(string? text, FindSources sources, int perPage = PerPage)
    {
        var words = text?.Trim() ?? "";
        var found = new List<FoundThing>();
        var answered = new List<FoundPage>();
        var unreached = new List<FoundPage>();
        if (words.Length < MinLength)
        {
            return new FindResult(found, answered, unreached);
        }

        void Page(FoundPage page, object? source, Func<IEnumerable<FoundThing>> search)
        {
            if (source is null)
            {
                return;
            }

            try
            {
                // A page read with nothing kept and no server would look empty, so it's counted as not reached instead.
                using var noFallbacks = ServerClient.NoFallbacks();
                found.AddRange(search().Take(perPage).ToList());
                answered.Add(page);
            }
            catch (ServerUnavailableException)
            {
                // A page that can't answer right now is left out, and the rest still show.
                unreached.Add(page);
            }
        }

        Page(FoundPage.ToDo, sources.ToDo, () => ToDo(sources.ToDo, words));
        Page(FoundPage.Lists, sources.Lists, () => Lists(sources.Lists, words));
        Page(FoundPage.Notes, sources.Notes, () => Notes(sources.Notes, words));
        Page(FoundPage.History, sources.History, () => History(sources.History, words, perPage));
        Page(FoundPage.Budget, sources.Budget, () => Purchases(sources.Budget, words, perPage));
        return new FindResult(found, answered, unreached);
    }

    /// <summary>Notes, shopping, and purchases have their own pages, so To do only offers what it lists itself.</summary>
    private static IEnumerable<FoundThing> ToDo(IToDoData? toDo, string words) =>
        (toDo?.GetOpenItems() ?? [])
            .Where(item => item.Kind is not (ItemKind.Note or ItemKind.Shopping or ItemKind.Expense))
            .Where(item => ToDoFilters.MatchesSearch(words, item, null))
            .Select(item => new FoundThing(FoundPage.ToDo, item.Title, item.DueDate is { } date ? $"To do, {Day(date)}" : "To do") { Item = item });

    private static IEnumerable<FoundThing> Lists(IListsData? lists, string words)
    {
        var parts = Parts(words);
        return (lists?.GetListOverview() ?? [])
            .SelectMany(list => list.Entries)
            .Where(entry => parts.All(part => entry.Text.Contains(part, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(entry => entry.Done)
            .Select(entry => new FoundThing(FoundPage.Lists, entry.Text, entry.Done ? $"Notes, {ListName(entry.List)}, checked" : $"Notes, {ListName(entry.List)}") { Entry = entry });
    }

    /// <summary>Lists live on the Notes page, so a list entry reads "Notes, Shopping list".</summary>
    private static string ListName(string list) =>
        list.EndsWith(" list", StringComparison.OrdinalIgnoreCase) || list.Equals("list", StringComparison.OrdinalIgnoreCase) ? list : list + " list";

    private static IEnumerable<FoundThing> Notes(INotesData? notes, string words) =>
        (notes?.SearchNotes(words) ?? [])
            .Select(note => new FoundThing(FoundPage.Notes, note.Title, note.About is { Length: > 0 } about ? $"Notes, {about}" : "Notes") { NoteId = note.Id });

    private static IEnumerable<FoundThing> History(IHistoryData? history, string words, int limit) =>
        (history?.SearchHistory(words, limit, HistoryFilter.All) ?? [])
            .Select(row => new FoundThing(FoundPage.History, row.Entry.Title, $"History, {row.Entry.Label.ToLowerInvariant()} {Day(DateOnly.FromDateTime(row.Entry.At))}") { History = row.Entry });

    private static IEnumerable<FoundThing> Purchases(IBudgetData? budget, string words, int limit) =>
        (budget?.SearchPurchases(words, limit) ?? [])
            .Select(row => new FoundThing(FoundPage.Budget, MerchantName.Clean(row.Title), $"Budget, {Day(row.Date)}, {Briefing.Money(row.AmountCents)}") { Purchase = row });

    private static string[] Parts(string words) => words.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Day(DateOnly date) => date.ToString("MMM d", CultureInfo.InvariantCulture);
}
