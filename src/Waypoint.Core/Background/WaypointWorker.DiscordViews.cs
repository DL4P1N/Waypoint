using System.Globalization;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Background;

/// <summary>
/// Discord as the phone's way into the app: pinned views kept up to date, and slash commands for the same manual
/// controls the PC has. Every one of them calls the same database methods the pages do, and none waits on the sorter.
/// </summary>
public sealed partial class WaypointWorker
{
    /// <summary>Views are drawn again this often at most, unless something changed from Discord. An unchanged view isn't sent.</summary>
    public static readonly TimeSpan ViewRefreshInterval = TimeSpan.FromMinutes(1);

    private const int MaxSuggestions = 25;

    private readonly SemaphoreSlim _viewsRunning = new(1, 1);
    private volatile bool _viewsDirty = true;
    private DateTime? _viewsDrawnAt;

    /// <summary>The views' messages, kept so each is edited in place.</summary>
    public PinnedViews Views { get; }

    /// <summary>
    /// Brings the pinned views up to date. It runs at most once a minute, or sooner after a change made from
    /// Discord, and only views whose contents changed are edited. A call made while one runs waits for it, then
    /// only runs itself if something changed meanwhile, so rounds never overlap and changes are never lost.
    /// </summary>
    public async Task RefreshDiscordViewsAsync()
    {
        if (Discord is not { } discord || DiscordSettings is not { } settings || !ViewsDue())
        {
            return;
        }

        var drawn = false;
        await _viewsRunning.WaitAsync();
        try
        {
            if (ViewsDue())
            {
                _viewsDirty = false;
                _viewsDrawnAt = _clock();
                await Views.SyncAsync(discord, BuildViews(settings, _viewsDrawnAt.Value));
                drawn = true;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _viewsDirty = true;
            _logError(exception);
        }
        finally
        {
            _viewsRunning.Release();
        }

        // The forum posts follow once the pinned views are let go, so a slow round of posts never holds those up.
        if (drawn)
        {
            try
            {
                await SyncDiscordThreadsAsync();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _viewsDirty = true;
                _logError(exception);
            }
        }
    }

    private bool ViewsDue() => _viewsDirty || _viewsDrawnAt is not { } drawn || _clock() - drawn >= ViewRefreshInterval;

    /// <summary>Every view that has a channel, in the order they should sit in it.</summary>
    public IReadOnlyList<PinnedView> BuildViews(DiscordSettings settings, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var views = new List<PinnedView>();
        if (PinnedViews.ChannelFor(_database, settings, DiscordViews.ToDo) is { } toDo)
        {
            views.Add(new PinnedView(DiscordViews.ToDo, toDo, DiscordViews.ToDoCard(_database.GetOpenItems(), today)));
        }

        if (PinnedViews.ChannelFor(_database, settings, DiscordViews.Waiting) is { } waiting)
        {
            views.Add(new PinnedView(DiscordViews.Waiting, waiting, DiscordViews.WaitingCard(_database.GetWaiting(), today)));
        }

        if (PinnedViews.ChannelFor(_database, settings, DiscordViews.Budget) is { } budget)
        {
            views.Add(new PinnedView(DiscordViews.Budget, budget, BudgetWithPurchases(today)));
        }

        if (PinnedViews.ChannelFor(_database, settings, DiscordViews.Lists) is { } lists)
        {
            views.AddRange(_database.GetListOverview(includeEmptyShopping: true)
                .Select(list => new PinnedView(PinnedViews.ListPrefix + list.Name, lists, DiscordViews.ListCard(list))));
        }

        views.AddRange(PhoneViews(settings, now));
        return views;
    }

    /// <summary>Safe to spend, balances, and what needs a look, for /budget and the budget view.</summary>
    private List<string> BudgetLines(DateOnly today)
    {
        var lines = new List<string>();
        if (_database.GetSafeToSpend(today) is { } cents)
        {
            lines.Add($"Safe to spend today: {Briefing.Money(cents)}");
        }

        lines.AddRange(BudgetReport.BalanceLines(_database.GetCurrentAccounts(today)));
        lines.AddRange(BudgetReport.Attention(_database.GetBudgetReport(today)));
        return lines;
    }

    /// <summary>Something changed from Discord, so the views are drawn again now rather than within the minute. It runs aside, so a reply to Discord is never held up.</summary>
    private void ViewsChanged()
    {
        _viewsDirty = true;
        _ = Task.Run(RefreshDiscordViewsAsync);
    }

    /// <summary>
    /// A pick from a view's menu. The view is drawn again with the change and handed back to replace itself, so
    /// the message keeps its place. Returns null when there's nothing to change.
    /// </summary>
    public DiscordCard? OnViewPicked(string menu, string messageId, IReadOnlyList<string> values)
    {
        var now = _clock();
        var today = DateOnly.FromDateTime(now);
        var key = Views.ViewFor(messageId);
        if (OnPhonePick(menu, key, values) is { } phone)
        {
            return phone;
        }

        // The card that comes back keeps the page the pick was made on. The view itself is noted as its first page,
        // which is what the next round draws, so that round leaves the message on the page it's on.
        DiscordCard card;
        DiscordCard firstPage;
        if (menu == DiscordViews.DoneMenu)
        {
            var finished = _database.FindItems(values);
            var page = finished.Count > 0 ? DiscordViews.ToDoPageOf(_database.GetOpenItems(), today, finished[0].Id) : 0;
            foreach (var item in finished)
            {
                _database.SetDone(item, true);
            }

            // The menu also comes on /todo's answer, which isn't a pinned view, so that one is simply drawn again.
            var open = _database.GetOpenItems();
            card = DiscordViews.ToDoCard(open, today, page);
            firstPage = DiscordViews.ToDoCard(open, today);
        }
        else if (menu == DiscordViews.CheckMenu || menu.StartsWith(DiscordViews.CheckMenu + ":", StringComparison.Ordinal))
        {
            // The menu says which list it's on. An older one doesn't, and is on a pinned list view.
            var target = menu == DiscordViews.CheckMenu ? key : menu[(DiscordViews.CheckMenu.Length + 1)..];
            if (target is null || ListForTarget(target) is not { } before)
            {
                return null;
            }

            var picked = values.Select(value => long.TryParse(value, CultureInfo.InvariantCulture, out var id) ? id : -1).ToHashSet();
            var page = DiscordViews.ListPageOf(before, picked.FirstOrDefault(-1));
            foreach (var entry in _database.GetListEntries(before.Name).Where(entry => picked.Contains(entry.Id) && !entry.Done))
            {
                _database.SetEntryDone(entry, true, now);
            }

            if (_database.GetListOverview(includeEmptyShopping: true).FirstOrDefault(list => list.Name == before.Name) is not { } list)
            {
                return null;
            }

            card = DiscordViews.ListCard(list, page, target);
            firstPage = DiscordViews.ListCard(list);

            // On a list's own forum post, the post keeps its Start over and its line about typing there.
            if (_database.FindDiscordThreadByMessage(messageId) is { Kind: WaypointDatabase.ThreadList })
            {
                card = ListPostCard(list, page);
            }
        }
        else
        {
            return null;
        }

        if (key is not null)
        {
            Views.Drawn(key, firstPage);
        }

        _host.Changed();
        ViewsChanged();
        return card;
    }

    /// <summary>The list a button or menu's target names, by its name or its hash. Null when it's gone.</summary>
    private ListOverview? ListForTarget(string target)
    {
        var lists = _database.GetListOverview(includeEmptyShopping: true);
        if (target.StartsWith(PinnedViews.ListPrefix, StringComparison.Ordinal))
        {
            var name = target[PinnedViews.ListPrefix.Length..];
            return lists.FirstOrDefault(list => list.Name == name);
        }

        return target.StartsWith(DiscordViews.ListHashPrefix, StringComparison.Ordinal)
            ? lists.FirstOrDefault(list => DiscordViews.NameHash(list.Name) == target[DiscordViews.ListHashPrefix.Length..])
            : null;
    }

    /// <summary>The commands added for the phone. Null when the name isn't one of them.</summary>
    private CommandReply? OnPhoneCommand(SlashCommand command, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        string? Option(string name) => command.Options.GetValueOrDefault(name) is { } value && value.Trim().Length > 0 ? value.Trim() : null;
        static CommandReply Done(string text, CardTone tone = CardTone.Finished) => new(DiscordText.Notice(text, "", tone), true);
        static CommandReply Problem(string text) => new(DiscordText.Notice("That didn't work", text, CardTone.Attention), true);

        switch (command.Name)
        {
            case "todo":
                return new CommandReply(DiscordViews.ToDoCard(_database.GetOpenItems(), today));
            case "add":
                return AddFromPhone(Option("title") ?? "", Option("date"), Option("time"), Option("kind"), now);
            case "done":
                if (FindOpenItem(Option("item"), out var finishMatches) is not { } finish)
                {
                    return NotExact(finishMatches, FinishPick, "Finish", today);
                }

                return new CommandReply(Finish(finish), true);
            case "took":
                return TookFromPhone(Option("item"), today);
            case "move":
                if (FindOpenItem(Option("item"), out var moveMatches) is not { } moving)
                {
                    return moveMatches.Count == 0
                        ? Problem(NotFound)
                        : Problem($"That could be {Matches(moveMatches, today)}. Pick the one you mean from the suggestions.");
                }

                if (Option("date") is null && Option("time") is null)
                {
                    return Problem("Give it a new day, a new time, or both.");
                }

                var time = TypedDates.ParseTime(Option("time"));
                if (Option("time") is not null && time is null)
                {
                    return Problem("I couldn't read that time. Try 3pm or 15:30.");
                }

                // With only a time it stays on its day, or goes on today if it had none.
                if ((Option("date") is null ? moving.DueDate ?? today : TypedDates.ParseDate(Option("date"), today)) is not { } date)
                {
                    return Problem("I couldn't read that date. Try today, tomorrow, friday, 11/14, or 2026-11-14.");
                }

                return Done(ItemGroups.ChangeSummary(_database.RescheduleItem(moving, date, time, now)));
            case "start":
                if (Option("item") is null)
                {
                    return Activation.PickTask(_database.WithoutQuietWaiting(_database.GetOpenItems(), today), null) is { } next
                        ? new CommandReply(StartFromDiscord(next))
                        : Done("Nothing to start right now");
                }

                if (FindOpenItem(Option("item"), out var startMatches) is not { } starting)
                {
                    return NotExact(startMatches, StartPick, "Start", today);
                }

                return new CommandReply(StartFromDiscord(starting));
            case "spent":
                if (!decimal.TryParse(Option("amount"), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
                {
                    return Problem("Try an amount like 12.50.");
                }

                var spent = _database.AddExpense(Option("what") ?? "", (long)Math.Round(amount * 100, MidpointRounding.AwayFromZero), Option("category"), today, now);
                return spent.IsAnswer ? Problem(spent.Text) : Done(spent.Text, CardTone.Money);
            case "lists":
                return new CommandReply(DiscordViews.ListsCard(_database.GetListOverview()));
            case "check":
                // Nothing checked off means it missed, so it doesn't look like it worked.
                var leftBefore = OpenEntries();
                var checkedOff = _database.SetEntryDone(Option("list"), Option("entry") ?? "", true, now);
                return OpenEntries() < leftBefore ? Done(checkedOff) : Problem(checkedOff);
            case "listadd":
                if (Option("list") is not { } listName || Option("entry") is not { } entry)
                {
                    return Problem("It needs a list and something to add.");
                }

                // A name that isn't a list yet is likely a typo, so a new list is only made when asked for.
                if (_database.GetListNames().FirstOrDefault(name => string.Equals(name, listName, StringComparison.OrdinalIgnoreCase)) is not { } existing)
                {
                    return NewListOffer(listName, entry);
                }

                // Several things can go on at once with commas between, the same as typing in a list's post.
                var adding = ListLines(entry);
                _database.AddListEntries(existing, adding, now);
                return Done($"Added {string.Join(", ", adding)} to {existing}");
            case "waiting":
                return new CommandReply(DiscordViews.WaitingCard(_database.GetWaiting(), today));
            case "pinhere" or "unpin":
                if (Option("view") is not { } view || !DiscordViews.Names.Contains(view))
                {
                    return Problem("Pick a view: To do, Waiting on, Budget, Lists, Notes, or History.");
                }

                if (command.Name == "unpin")
                {
                    PinnedViews.Choose(_database, view, null);
                    return Done($"Stopped keeping the {DiscordViews.Label(view)} view. Its message will be taken away.");
                }

                if (command.ChannelId is not { } channel)
                {
                    return Problem("Use this in the channel the view should go in.");
                }

                PinnedViews.Choose(_database, view, channel);
                return Done($"The {DiscordViews.Label(view)} view will be kept pinned here and updated as things change.");
            default:
                return OnPhoneViewCommand(command);
        }
    }

    /// <summary>
    /// The same add as the To do page's form. With a time and no kind it's a reminder, reminded right when it's due,
    /// and with a time and no date it's today, or tomorrow once that time has passed.
    /// </summary>
    private CommandReply AddFromPhone(string title, string? dateText, string? timeText, string? kindText, DateTime now, HandAddedItem? place = null)
    {
        var today = DateOnly.FromDateTime(now);
        var date = TypedDates.ParseDate(dateText, today);
        if (dateText is not null && date is null)
        {
            return new CommandReply(DiscordText.Notice("That didn't work", "I couldn't read that date. Try today, tomorrow, friday, 11/14, or 2026-11-14.", CardTone.Attention), true);
        }

        var time = TypedDates.ParseTime(timeText);
        if (timeText is not null && time is null)
        {
            return new CommandReply(DiscordText.Notice("That didn't work", "I couldn't read that time. Try 3pm or 15:30.", CardTone.Attention), true);
        }

        var kind = Enum.TryParse<ItemKind>(kindText, ignoreCase: true, out var chosen) ? chosen : time is not null ? ItemKind.Reminder : ItemKind.Task;
        if (time is { } at && date is null)
        {
            date = TimeOnly.FromDateTime(now) < at ? today : today.AddDays(1);
        }

        var remind = kind == ItemKind.Reminder && time is not null ? TimeSpan.Zero : (TimeSpan?)null;
        var added = _database.AddByHand(new HandAddedItem(kind, title, date, time, place?.EndTime, place?.Course, remind, place?.FilterId), now);
        if (added.Item is not { } item)
        {
            return new CommandReply(DiscordText.Notice("That didn't work", added.Problem ?? "Nothing was added.", CardTone.Attention), true);
        }

        // Said the way Ask says it, "Added Lab report to To do, for Friday", with the day as a sentence would say it.
        var atTime = time is { } clock ? " at " + clock.ToString("h:mm tt", CultureInfo.InvariantCulture) : "";
        var when = item.DueDate is { } due
            ? $"{(item.Kind == ItemKind.Deadline ? "due" : "for")} {DiscordViews.InSentence(due, today)}{atTime}"
            : "with no date";
        return new CommandReply(DiscordText.Notice($"Added {item.Title} to To do, {when}", "", CardTone.Finished), true);
    }

    private const string NotFound = "I couldn't find that on the list. Start typing and pick it from the suggestions.";
    private const int MaxPicks = 4;

    /// <summary>
    /// An item picked from the suggestions, or one whose whole title is what was typed. Anything less is only a
    /// guess, so it's never acted on: the titles it could be come back in <paramref name="matches"/> to ask about.
    /// </summary>
    private StoredItem? FindOpenItem(string? text, out List<StoredItem> matches)
    {
        matches = [];
        if (text is null)
        {
            return null;
        }

        if (ItemRef.TryParse(text, out _, out _))
        {
            return _database.FindItems([text]).FirstOrDefault(item => !item.Done);
        }

        var open = PhoneItems();
        var exact = open.Where(item => string.Equals(item.Title, text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }

        matches = exact.Count > 1 ? exact : open.Where(item => item.Title.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        return null;
    }

    /// <summary>
    /// /took: marks a dose taken, once for its day. With nothing typed it's the only dose there is, and otherwise it
    /// asks which one, the same as /done.
    /// </summary>
    private CommandReply TookFromPhone(string? named, DateOnly today)
    {
        var doses = PhoneItems().Where(item => item is { Dose: true, Recurs: not null }).ToList();
        if (doses.Count == 0)
        {
            return new CommandReply(DiscordText.Notice("Nothing is marked as a dose yet", "Right-click a repeating reminder on To do and choose Mark as a dose, or type something like \"take vitamin D at 9 every morning\".", CardTone.Attention), true);
        }

        StoredItem? dose;
        if (named is null)
        {
            dose = doses.Count == 1 ? doses[0] : null;
            if (dose is null)
            {
                return NotExact(doses, FinishPick, "Took", today);
            }
        }
        else if (FindOpenItem(named, out var matches) is { } found)
        {
            dose = found is { Dose: true, Recurs: not null } ? found : null;
            if (dose is null)
            {
                return new CommandReply(DiscordText.Notice("That didn't work", $"{found.Title} isn't a dose.", CardTone.Attention), true);
            }
        }
        else
        {
            return NotExact(matches.Where(item => item is { Dose: true, Recurs: not null }).ToList(), FinishPick, "Took", today);
        }

        return new CommandReply(Finish(dose), true);
    }

    /// <summary>Nothing matched exactly, so it asks: one button per thing it could be, or says nothing matched.</summary>
    private static CommandReply NotExact(List<StoredItem> matches, string pick, string verb, DateOnly today)
    {
        if (matches.Count == 0)
        {
            return new CommandReply(DiscordText.Notice("That didn't work", NotFound, CardTone.Attention), true);
        }

        var shown = matches.Take(MaxPicks).ToList();
        var more = matches.Count > MaxPicks ? $"\nand {matches.Count - MaxPicks} more. Type more of the name to find the one you mean." : "";
        var title = matches.Count == 1 ? "Did you mean this one?" : "Which one did you mean?";
        var lines = string.Join("\n", shown.Select(item => $"{item.Title} ({DiscordViews.DayLabel(item.DueDate, today)})")) + more;
        var card = DiscordText.Reply($"{title}\n{lines}") with
        {
            Actions = shown.Select(item => new CardAction($"{verb} {item.Title}", pick + ItemRef.Format(item))).ToList(),
        };
        return new CommandReply(card, true);
    }

    private static string Matches(List<StoredItem> matches, DateOnly today) =>
        string.Join(", ", matches.Take(MaxPicks).Select(item => $"{item.Title} ({DiscordViews.DayLabel(item.DueDate, today)})"))
        + (matches.Count > MaxPicks ? $", and {matches.Count - MaxPicks} more" : "");

    private int OpenEntries() => _database.GetListOverview(includeEmptyShopping: true).Sum(list => list.Entries.Count(entry => !entry.Done));

    /// <summary>What can be finished, moved, or started from the phone, soonest first.</summary>
    private List<StoredItem> PhoneItems() =>
        _database.GetOpenItems().Where(item => item.Kind is not (ItemKind.Note or ItemKind.Expense or ItemKind.Shopping)).ToList();

    /// <summary>Matches for an option as it's typed: items, lists, list entries, or budget categories.</summary>
    public IReadOnlyList<MenuChoice> OnDiscordSuggest(SlashCommand command, string option)
    {
        var typed = command.Options.GetValueOrDefault(option)?.Trim() ?? "";
        bool Matches(string text) => typed.Length == 0 || text.Contains(typed, StringComparison.OrdinalIgnoreCase);
        var today = DateOnly.FromDateTime(_clock());

        switch (option)
        {
            case "item":
                return PhoneItems()
                    .Where(item => command.Name != "took" || item is { Dose: true, Recurs: not null })
                    .Where(item => Matches(item.Title))
                    .Take(MaxSuggestions)
                    .Select(item => new MenuChoice($"{item.Title} · {DiscordViews.DayLabel(item.DueDate, today)}", ItemRef.Format(item)))
                    .ToList();
            case "list":
                return _database.GetListOverview(includeEmptyShopping: true)
                    .Where(list => Matches(list.Name))
                    .Take(MaxSuggestions)
                    .Select(list => new MenuChoice(list.Name, list.Name))
                    .ToList();
            case "entry" when command.Name == "check":
                var chosen = command.Options.GetValueOrDefault("list")?.Trim();
                return _database.GetListOverview(includeEmptyShopping: true)
                    .Where(list => string.IsNullOrEmpty(chosen) || string.Equals(list.Name, chosen, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(list => list.Entries.Where(entry => !entry.Done && Matches(entry.Text)).Select(entry => (list.Name, entry.Text)))
                    .Take(MaxSuggestions)
                    .Select(found => new MenuChoice(string.IsNullOrEmpty(chosen) ? $"{found.Text} ({found.Name})" : found.Text, found.Text))
                    .ToList();
            case "folder":
                return FolderSuggestions(typed).Take(MaxSuggestions).ToList();
            case "category":
                return _database.GetCategories()
                    .Where(category => Matches(category.Name))
                    .Take(MaxSuggestions)
                    .Select(category => new MenuChoice(category.Name, category.Name))
                    .ToList();
            default:
                return [];
        }
    }
}
