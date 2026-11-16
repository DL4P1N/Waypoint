using System.Globalization;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Background;

/// <summary>
/// Buttons, menus, forms, and commands on the phone's longer views: Back and Next, Notes, History, search, and the
/// add form. Each one reads and changes things through the same database calls the PC's pages use.
/// </summary>
public sealed partial class WaypointWorker
{
    /// <summary>A carried action from one of the phone's views. Null when it isn't one of them.</summary>
    private ButtonResult? OnPhoneAction(string action, string messageId)
    {
        if (DiscordPages.TryRead(action, out var target, out var page))
        {
            // On a list's own forum post, the page keeps Start over and the line about typing there.
            if (_database.FindDiscordThreadByMessage(messageId) is { Kind: WaypointDatabase.ThreadList } && ListForTarget(target) is { } posted)
            {
                return new ButtonResult(ListPostCard(posted, page));
            }

            return PageOf(target, page) is { } card
                ? new ButtonResult(card)
                : new ButtonResult(Outcome: "That's gone now. Try the command again.");
        }

        if (action == SettingsBedtime)
        {
            return new ButtonResult { Form = BedtimeForm() };
        }

        if (action == SettingsHours)
        {
            return new ButtonResult { Form = WorkHoursForm() };
        }

        // From the pinned budget view, purchases come as a private reply so the view stays.
        if (action == PurchasesOpen)
        {
            return new ButtonResult(PurchasesCard(DateOnly.FromDateTime(_clock()), 0) with { Private = true });
        }

        if (action == NoteSearch)
        {
            return new ButtonResult { Form = NoteSearchForm() };
        }

        if (action.StartsWith(NewNote, StringComparison.Ordinal))
        {
            return new ButtonResult { Form = NewNoteForm(action[NewNote.Length..]) };
        }

        if (action.StartsWith(HistorySearch, StringComparison.Ordinal))
        {
            return new ButtonResult { Form = new CardForm(action, "Search History", [new FormBox("words", "Words to look for", Required: true, MaxLength: 100)]) };
        }

        if (TakeId(action, ReopenEntry) is { } entry)
        {
            return new ButtonResult(Outcome: Reopen(entry) ?? "That one can't be reopened anymore.");
        }

        if (OnOrderAction(action) is { } ordered)
        {
            return new ButtonResult(ordered);
        }

        return null;
    }

    /// <summary>
    /// How a press will be answered, known before any work is done so a slow one can be promised the right way:
    /// forms open at once, and what a pinned view opens comes as a private reply, never on the pinned message.
    /// </summary>
    private PressReply HowPressWillReply(string customId, string messageId, IReadOnlyList<string> values)
    {
        if (customId == OpenMenu)
        {
            return Views.ViewFor(messageId) is null ? PressReply.Update : PressReply.Private;
        }

        if (!customId.StartsWith(DiscordBridge.ActionPrefix, StringComparison.Ordinal))
        {
            return PressReply.Update;
        }

        var action = customId[DiscordBridge.ActionPrefix.Length..];
        if (action == PurchasesOpen)
        {
            return PressReply.Private;
        }

        return action is SettingsBedtime or SettingsHours or NoteSearch
            || action.StartsWith(NewNote, StringComparison.Ordinal)
            || action.StartsWith(NoteEdit, StringComparison.Ordinal)
            || action.StartsWith(HistorySearch, StringComparison.Ordinal)
            || action.StartsWith(LateSaveAndStop, StringComparison.Ordinal)
            ? PressReply.Form
            : PressReply.Update;
    }

    /// <summary>The pinned views added for the phone beyond To do, Waiting on, Budget, and Lists: the notes folder index and History.</summary>
    private IEnumerable<PinnedView> PhoneViews(DiscordSettings settings, DateTime now)
    {
        if (PinnedViews.ChannelFor(_database, settings, DiscordViews.Notes) is { } notes)
        {
            yield return new PinnedView(DiscordViews.Notes, notes, NotesIndexCard(0));
        }

        if (PinnedViews.ChannelFor(_database, settings, DiscordViews.History) is { } history)
        {
            yield return new PinnedView(DiscordViews.History, history, HistoryCard(HistoryFilter.All, 0));
        }
    }

    /// <summary>
    /// A pick from one of the phone's menus. Null leaves it to the older menus. What's opened from a pinned view
    /// comes as a new private reply, so the pinned view stays where it is. <paramref name="view"/> is that view, if any.
    /// </summary>
    private DiscordCard? OnPhonePick(string menu, string? view, IReadOnlyList<string> values)
    {
        if (menu == OpenMenu)
        {
            var card = values.Count > 0 ? Open(values[0]) : null;
            card ??= DiscordText.Notice("That's gone now", "Try the command again.", CardTone.Attention) with { Private = true };
            return view is null ? card : card with { Private = true };
        }

        if (menu.StartsWith(ReopenMenu + ":", StringComparison.Ordinal))
        {
            return OnReopenPicked(menu, values);
        }

        if (menu.StartsWith(OrderMenu + ":", StringComparison.Ordinal))
        {
            return OnOrderPicked(menu, values);
        }

        return null;
    }

    /// <summary>One page of a view, by what its buttons carry. Null when what it was about is gone.</summary>
    private DiscordCard? PageOf(string target, int page)
    {
        var today = DateOnly.FromDateTime(_clock());
        switch (target)
        {
            case DiscordViews.ToDo:
                return DiscordViews.ToDoCard(_database.GetOpenItems(), today, page);
            case DiscordViews.Lists:
                return DiscordViews.ListsCard(_database.GetListOverview(), page);
            case DiscordViews.Waiting:
                return DiscordViews.WaitingCard(_database.GetWaiting(), today, page);
            case DiscordViews.Notes:
                return NotesIndexCard(page);
        }

        var colon = target.IndexOf(':');
        var kind = colon < 0 ? target : target[..(colon + 1)];
        var rest = colon < 0 ? "" : target[(colon + 1)..];
        long.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var id);
        switch (kind)
        {
            case PinnedViews.ListPrefix or DiscordViews.ListHashPrefix:
                return ListForTarget(target) is { } list ? DiscordViews.ListCard(list, page, target) : null;
            case HistoryTarget + ":":
                return HistoryPage(rest, page);
            case FolderTarget:
                return rest == Unfiled ? FolderCard(null, page) : id > 0 ? FolderCard(id, page) : null;
            case NoteTarget:
                return NoteCard(id, page);
            case NoteSearchTarget:
                return Uncarry(rest) is { } noteWords ? NoteSearchCard(noteWords, page) : null;
            case SearchTarget:
                return Uncarry(rest) is { } words ? SearchCard(words, page) : null;
            case PurchasesTarget:
                return PurchasesPage(rest, page);
            default:
                return null;
        }
    }

    /// <summary>The commands for the phone's newer views. Null when the name isn't one of them.</summary>
    private CommandReply? OnPhoneViewCommand(SlashCommand command)
    {
        string? Option(string name) => command.Options.GetValueOrDefault(name)?.Trim() is { Length: > 0 } value ? value : null;
        switch (command.Name)
        {
            case "notes":
                return NotesCommand(command);
            case "history":
                return HistoryCommand(command);
            case "search":
                return new CommandReply(SearchCard(Option("words") ?? "", 0), true);
            case "new":
                return NewItemCommand(command);
            case "settings":
                return new CommandReply(SettingsCard(null), true);
            case "order":
                return OrderCommand(command);
            case "purchases":
                return new CommandReply(PurchasesCard(DateOnly.FromDateTime(_clock()), 0), true);
            default:
                return null;
        }
    }

    /// <summary>Any pop-up form but Save my spot's was sent. Its ID says which.</summary>
    public CommandReply OnFormSent(string id, IReadOnlyDictionary<string, string> values)
    {
        string? Box(string name) => values.GetValueOrDefault(name)?.Trim() is { Length: > 0 } value ? value : null;
        if (id == NoteSearch)
        {
            return new CommandReply(NoteSearchCard(Box("words") ?? "", 0), true);
        }

        if (id.StartsWith(NewNote, StringComparison.Ordinal))
        {
            return SaveNewNote(id[NewNote.Length..], values);
        }

        if (id.StartsWith(NoteEdit, StringComparison.Ordinal))
        {
            return SavePartEdit(id[NoteEdit.Length..], values);
        }

        if (id.StartsWith(HistorySearch, StringComparison.Ordinal))
        {
            var filter = Enum.TryParse<HistoryFilter>(id[HistorySearch.Length..], out var picked) ? picked : HistoryFilter.All;
            return new CommandReply(HistoryCard(filter, 0, Box("words")), true);
        }

        if (id.StartsWith(NewItemForm, StringComparison.Ordinal))
        {
            var added = SaveNewItem(id[NewItemForm.Length..], values);
            _host.Changed();
            ViewsChanged();
            return added;
        }

        if (OnSettingsForm(id, values) is { } settings)
        {
            return settings;
        }

        return new CommandReply(DiscordText.Notice("That form has run out", "Try the command again.", CardTone.Attention), true);
    }
}
