using System.Collections.Concurrent;
using System.Globalization;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Notes;
using Waypoint.Core.Remote;

namespace Waypoint.Core.Background;

/// <summary>
/// /search looks everywhere the PC's Find anywhere does, through the same calls, and picking a result opens it.
/// /new opens the To do page's add form as a pop-up and adds through the same call.
/// </summary>
public sealed partial class WaypointWorker
{
    private const string SearchTarget = "search:";
    private const int SearchPerPage = 10;
    private const int SearchPerSection = 25;

    /// <summary>The add form from /new. The kind follows.</summary>
    public const string NewItemForm = "new:";

    // Words too long to ride in a button's ID, kept while the app runs. A press after a restart says it ran out.
    private const int MaxCarried = 40;
    private readonly ConcurrentDictionary<string, string> _carried = new();
    private readonly ConcurrentQueue<string> _carriedOrder = new();

    /// <summary>How many are kept at once. The oldest go first, so a long day of searching can't fill memory.</summary>
    private const int MaxKept = 500;

    /// <summary>Text for a button's ID: as it is when short, else a key to where it's kept.</summary>
    private string Carry(string text)
    {
        if (text.Length <= MaxCarried && !text.StartsWith('~'))
        {
            return text;
        }

        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..12];
        if (_carried.TryAdd(key, text))
        {
            _carriedOrder.Enqueue(key);
            while (_carriedOrder.Count > MaxKept && _carriedOrder.TryDequeue(out var oldest))
            {
                _carried.TryRemove(oldest, out _);
            }
        }

        return "~" + key;
    }

    private string? Uncarry(string text) => text.StartsWith('~') ? _carried.GetValueOrDefault(text[1..]) : text;

    private FindSources PhoneSources() =>
        new(new LocalToDoData(_database), new LocalListsData(_database), new LocalNotesData(_database), new LocalHistoryData(_database), new LocalBudgetData(_database));

    /// <summary>A page of what Find anywhere turns up, with a menu to open one.</summary>
    private DiscordCard SearchCard(string words, int page)
    {
        var found = FindAnywhere.Search(words, PhoneSources(), SearchPerSection).Select(thing => (Thing: thing, Value: OpenValue(thing))).ToList();
        var pages = DiscordPages.Split(found, row => SearchLine(row.Thing).Length + 1, DiscordPages.Budget, SearchPerPage);
        page = DiscordPages.Clamp(page, pages.Count);
        var shown = pages[page];
        var escaped = DiscordText.Escape(words);
        var description = words.Trim().Length < FindAnywhere.MinLength
            ? "Type at least two letters to search."
            : found.Count == 0 ? CardLabels.NothingMatches("on any page", escaped) : string.Join("\n", shown.Select(row => SearchLine(row.Thing)));
        return new DiscordCard(Shorten(DiscordPages.Titled($"Found for {escaped}", page, pages.Count), 256), description, [], CardTone.Reminder)
        {
            Menu = new DiscordMenu(OpenMenu, "Open one", shown.Where(row => row.Value is not null).Select(row => new MenuChoice(row.Thing.Title, row.Value!, row.Thing.Where)).ToList()),
            MoreRows = DiscordViews.Rows(DiscordPages.Pager(SearchTarget + Carry(words), page, pages.Count)),
        };
    }

    private static string SearchLine(FoundThing thing) => Shorten($"• {DiscordText.Escape(thing.Title)} · {DiscordText.Escape(thing.Where)}", 300);

    /// <summary>What the open menu carries for a found thing, such as "note:12".</summary>
    private static string? OpenValue(FoundThing thing) => thing switch
    {
        { Item: { } item } => "item:" + ItemRef.Format(item),
        { NoteId: { } note } => NoteTarget + note.ToString(CultureInfo.InvariantCulture),
        { Entry: { } entry } => "entry:" + entry.Id.ToString(CultureInfo.InvariantCulture),
        { History: { } entry } => "history:" + entry.Id.ToString(CultureInfo.InvariantCulture),
        { Purchase: { Id: > 0 } purchase } => "purchase:" + purchase.Id.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>Opens what the open menu picked. Null when it's gone.</summary>
    private DiscordCard? Open(string value)
    {
        var colon = value.IndexOf(':');
        if (colon <= 0)
        {
            return null;
        }

        var kind = value[..(colon + 1)];
        var rest = value[(colon + 1)..];
        long.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var id);
        var today = DateOnly.FromDateTime(_clock());
        switch (kind)
        {
            case FolderTarget:
                return rest == Unfiled ? FolderCard(null, 0) : id > 0 ? FolderCard(id, 0) : null;
            case NoteTarget:
                return NoteCard(id, 0);
            case "item:":
                return _database.FindItems([rest]).FirstOrDefault() is { } item ? ItemCard(item, today) : null;
            case "entry:":
                return _database.GetListOverview(includeEmptyShopping: true).FirstOrDefault(list => list.Entries.Any(entry => entry.Id == id)) is { } found
                    ? DiscordViews.ListCard(found)
                    : null;
            case "history:":
                return _database.GetHistory().FirstOrDefault(entry => entry.Id == id) is { } history ? HistoryEntryCard(history) : null;
            case "purchase:":
                return _database.SearchPurchases(null, 500).FirstOrDefault(row => row.Id == id) is { } purchase
                    ? DiscordText.Notice(MerchantName.Clean(purchase.Title), $"Amount: {Briefing.Money(purchase.AmountCents)}\nDay: {Day(purchase.Date)}\nCategory: {purchase.Category}", CardTone.Money)
                    : null;
            default:
                return null;
        }
    }

    /// <summary>One item as a search opens it, with Done and Start when it's still open.</summary>
    private static DiscordCard ItemCard(StoredItem item, DateOnly today)
    {
        var lines = new List<string>();
        if (item.DueDate is { } due)
        {
            var time = item.DueAt is { } at && ItemGroups.IsScheduled(item) ? " at " + at.LocalDateTime.ToString("h:mm tt", CultureInfo.InvariantCulture) : "";
            lines.Add($"When: {DiscordViews.DayLabel(due, today)}{time}");
        }

        lines.Add($"Kind: {item.Kind}");
        if (item.Course is { } course)
        {
            lines.Add($"Class: {course}");
        }

        if (item.Done)
        {
            lines.Add("Finished");
        }

        var card = DiscordText.Notice(item.Title, string.Join("\n", lines), item.Done ? CardTone.Finished : CardTone.Reminder);
        return item.Done
            ? card
            : card with
            {
                Actions =
                [
                    new CardAction("Done", FinishPick + ItemRef.Format(item)),
                    new CardAction("Start", StartPick + ItemRef.Format(item), ButtonRole.Other),
                ],
            };
    }

    /// <summary>The kinds /new offers, the same ones the To do page's add form has.</summary>
    private static readonly ItemKind[] NewKinds = [ItemKind.Task, ItemKind.Deadline, ItemKind.Reminder, ItemKind.Event, ItemKind.Exam];

    /// <summary>/new opens the add form for the kind picked, or a task.</summary>
    private static CommandReply NewItemCommand(SlashCommand command)
    {
        var kind = Enum.TryParse<ItemKind>(command.Options.GetValueOrDefault("kind"), ignoreCase: true, out var picked) && NewKinds.Contains(picked) ? picked : ItemKind.Task;
        var boxes = new List<FormBox>
        {
            new("title", "What it is", Required: true, MaxLength: 200),
            new("date", "Day", Required: kind != ItemKind.Task, MaxLength: 40, Placeholder: "today, tomorrow, friday, 11/14, or 2026-11-14"),
            new("time", kind == ItemKind.Event ? "Starts at" : "Time", MaxLength: 20, Placeholder: "Like 3pm or 15:30"),
        };
        if (kind == ItemKind.Event)
        {
            boxes.Add(new FormBox("end", "Ends at", MaxLength: 20, Placeholder: "Leave empty for an hour"));
        }

        boxes.Add(new FormBox("class", "Class or filter", MaxLength: 100, Placeholder: "Leave empty for Personal"));
        var label = kind.ToString().ToLowerInvariant();
        return new CommandReply(DiscordText.Notice("New", "")) { Form = new CardForm(NewItemForm + label, $"New {label}", boxes) };
    }

    /// <summary>The add form was sent. It adds the same way the To do page's form does, with nothing sent to the sorter.</summary>
    private CommandReply SaveNewItem(string kindText, IReadOnlyDictionary<string, string> values)
    {
        string? Box(string id) => values.GetValueOrDefault(id)?.Trim() is { Length: > 0 } value ? value : null;
        var place = Box("class");
        string? course = null;
        long? filterId = null;
        if (place is not null)
        {
            var filters = _database.GetToDoFilters();
            if (filters.FirstOrDefault(filter => string.Equals(filter.Name, place, StringComparison.OrdinalIgnoreCase) || string.Equals(filter.Course, place, StringComparison.OrdinalIgnoreCase)) is not { } filter)
            {
                var names = string.Join(", ", filters.Select(each => each.Name));
                return new CommandReply(DiscordText.Notice("That didn't work", $"There's no class or filter called {place}. {(names.Length > 0 ? $"There's {names}." : "")}".Trim(), CardTone.Attention), true);
            }

            course = filter.Source == FilterSource.Class ? filter.Course : null;
            filterId = filter.Source == FilterSource.Custom ? filter.Id : null;
        }

        var end = TypedDates.ParseTime(Box("end"));
        if (Box("end") is not null && end is null)
        {
            return new CommandReply(DiscordText.Notice("That didn't work", "Try an end time like 3pm or 15:30.", CardTone.Attention), true);
        }

        return AddFromPhone(Box("title") ?? "", Box("date"), Box("time"), kindText, _clock(), new HandAddedItem(ItemKind.Task, "", null, EndTime: end, Course: course, FilterId: filterId));
    }
}
