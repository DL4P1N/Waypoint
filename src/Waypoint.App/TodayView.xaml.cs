using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Waypoint.Core;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Remote;
using Waypoint.Core.Schedule;
using Waypoint.Core.Theme;

namespace Waypoint.App;

/// <summary>
/// What's next, plus one line each on money and tonight. Everything else has its own page.
/// The column filters it by class, Personal, or the user's own filters, and + adds by hand with no sorter.
/// </summary>
public partial class TodayView : UserControl
{
    private const string FilterKey = "todo_filter";
    private const string HideFinishedKey = "todo_hide_finished";
    private const string AllKey = "all";
    private const string NewFilterKey = "filter";
    private const string StartMethod = "menu";
    private const string EnergyMethod = Activation.EnergyMethod;
    private const string ViewKey = "todo_view";
    private const string WeekValue = "week";
    private const string ListValue = "list";
    private const string SkippedKey = "todo_suggestion_skipped";
    private const string SuggestedMethod = "suggested";

    private const string SetupKey = "todo_setup_card";
    private const string SetupOpenedKey = "todo_setup_opened";
    private const string SetupShowing = "showing";
    private const string SetupDone = "done";

    /// <summary>What an empty To do says, with where to start.</summary>
    internal const string EmptyHint = "Nothing on To do yet. Type anything in Ask on the right, or press +.";

    // While the setup card's first line says to add a task with + or Ask, the empty line doesn't say it again.
    private const string EmptyBesideSetup = "Nothing on To do yet.";

    /// <summary>What Start and the energy buttons say when there's nothing to pick, pointing at where things get added.</summary>
    internal const string NothingToPick = "Nothing to pick yet. Type anything in Ask, or use + to add a task.";

    private static readonly ColumnAddChoice[] AddChoices =
    [
        new(nameof(ItemKind.Task), "Task"),
        new(nameof(ItemKind.Deadline), "Deadline"),
        new(nameof(ItemKind.Reminder), "Reminder"),
        new(nameof(ItemKind.Event), "Event"),
        new(nameof(ItemKind.Exam), "Exam"),
        new(WaitingKey, "Waiting on", ShortcutName: "waiting-on item"),
        new(NewFilterKey, "Filter", InColumnMenu: true),
    ];

    private readonly RefreshGate _rowsGate = new();
    private IReadOnlyList<ToDoFilter> _filters = [];
    private IReadOnlyDictionary<long, ItemPlacement> _placements = new Dictionary<long, ItemPlacement>();
    private IToDoData _database = null!;
    private Action _openMoney = null!;
    private Action<string> _submit = null!;
    private Action<string> _reply = null!;
    private Action _changed = null!;
    private BedtimeStatus? _tonight;
    private bool _showingHideFinished;

    /// <summary>Tonight's steps as last saved, so a box filled in from the list isn't saved again.</summary>
    private readonly Dictionary<string, bool> _marks = new(StringComparer.Ordinal);
    /// <summary>The widest the rows get, margins included, so a big window doesn't spread a row across the screen.</summary>
    public const double RowsMaxWidth = 812;

    private StudyOpportunity? _study;

    /// <summary>The study window when it's what the suggestion card shows.</summary>
    private StudyOpportunity? _suggestedStudy;
    private bool _menuOpen;

    /// <summary>A rebuild asked for while a menu was open, so it runs once the menu has gone.</summary>
    private bool _rebuildWaiting;
    private bool _weekShown;
    private ClassSchedule? _classes;
    private StoredItem? _suggested;
    private bool _sessionGoing;

    /// <summary>The last thing this page said in Ask, so the same empty answer isn't repeated. Any change forgets it.</summary>
    private string? _lastSaid;

    // Kept by item ref rather than by row, so a refresh that rebuilds the rows keeps what was selected.
    private readonly HashSet<string> _selected = [];

    // Rows opened with Show full text, kept the same way, so a refresh doesn't fold them back up.
    private readonly HashSet<string> _expanded = [];
    private List<ItemRow> _rowOrder = [];
    private string? _anchor;

    public TodayView() => InitializeComponent();

    /// <summary>How the page reads and changes its data: straight away on this PC, or without holding the window in server mode.</summary>
    internal PageWork Work { get; set; } = PageWork.Local;

    /// <summary>
    /// What a box was last clicked to, while that change is on its way, by item. A second click on the same row before
    /// the first is saved compares with this, not the row's saved state, so it isn't mistaken for the row filling in.
    /// </summary>
    private readonly Dictionary<string, (StoredItem Item, bool Done)> _doneWanted = new(StringComparer.Ordinal);

    /// <summary>Raised for "Study for this exam", so the window can open Notes and make practice questions there.</summary>
    public event Action<StoredItem>? StudyRequested;

    /// <summary>Raised by the setup card's lines that live on Settings, with the section to open, such as "work".</summary>
    public event Action<string>? SettingsRequested;

    /// <summary>The setup card's pay line: Budget with its pay schedule form open, the same form as + then Pay schedule.</summary>
    public event Action? PayScheduleRequested;

    /// <summary>The class schedule for the week view, handed in from the settings the app loaded at startup.</summary>
    public ClassSchedule? Classes
    {
        get => _classes;
        set
        {
            _classes = value;
            if (_database is not null)
            {
                Work.Show(() =>
                {
                    ShowItems(force: true);
                    ShowSuggestion();
                }, () =>
                {
                    ShowItems();
                    ShowSuggestion();
                });
            }
        }
    }

    /// <summary>Tonight's bedtime schedule, so study windows stop where the wind-down starts.</summary>
    public BedtimeSchedule? Bedtime { get; set; }

    private WaypointDatabase? _localDatabase;
    private ISessionData? _sessions;

    /// <summary>
    /// Tonight's card and paused sessions, through the same calls as the assistant's ops. The window gives the
    /// server's in server mode. Without one, this PC's database is used with <see cref="Bedtime"/>.
    /// </summary>
    public ISessionData? Sessions
    {
        get => _sessions ?? (_localDatabase is { } database ? new LocalSessionData(database, Bedtime) : null);
        set => _sessions = value;
    }

    private IFeedbackData? _feedback;

    /// <summary>Where This didn't help is kept: the server's in server mode, set by the window, or else this PC's database.</summary>
    public IFeedbackData? FeedbackData
    {
        get => _feedback ?? (_localDatabase is { } database ? new LocalFeedbackData(database) : null);
        set => _feedback = value;
    }

    public void Connect(WaypointDatabase database, Action openMoney, Action<string> submit, Action<string> reply, Action changed)
    {
        _localDatabase = database;
        Connect(new LocalToDoData(database), openMoney, submit, reply, changed);
    }

    /// <summary>The page reads and changes everything through <paramref name="database"/>, which is this PC's database or the server.</summary>
    public void Connect(IToDoData database, Action openMoney, Action<string> submit, Action<string> reply, Action changed)
    {
        _database = database;
        _openMoney = openMoney;
        _submit = submit;
        _reply = reply;
        _changed = changed;

        _showingHideFinished = true;
        HideFinished.IsChecked = Work.Peek(() => database.GetPreference(HideFinishedKey)) == "1";
        _showingHideFinished = false;
        Column.Work = Work;
        Work.Peek(() => Column.Connect(database, FilterKey));
        Column.AddChoices = AddChoices;
        Column.QuickAdd = AddChoices[0];
        Column.QuickAddIn = new Dictionary<string, ColumnAddChoice> { [WaitingKey] = AddChoices.Single(choice => choice.Key == WaitingKey) };
        Column.ExtraMenu = FilterMenu;
        Column.SelectionChanged += _ => Work.Show(() => ShowItems(force: true, toTop: true), () => ShowItems());
        Column.SearchChanged += _ => Work.Show(() => ShowItems(force: true, toTop: true), () => ShowItems());
        Column.AddChosen += OnAddChosen;
        Column.Reordered += (_, keys) =>
        {
            var ids = keys.Select(FilterId).OfType<long>().ToList();
            Work.Fire(() => _database.SetToDoFilterOrder(ids));
        };
        Column.RenameRequested += OnFilterRenamed;
        SuggestionFeedback.Keeping = () =>
        {
            // Taken as it's pressed, since the card may move on before the server has it.
            var feedback = FeedbackData!;
            var reference = _suggested is { } shown ? ItemRef.Format(shown) : null;
            var at = DateTime.Now;
            return () => feedback.AddFeedback(Feedback.Suggestion, reference, at);
        };
        SuggestionFeedback.Noted = (id, note) => FeedbackData!.NoteFeedback(id, note);
        RowFeedback.Noted = (id, note) => FeedbackData!.NoteFeedback(id, note);
        SuggestionFeedback.Work = Work;
        RowFeedback.Work = Work;

        Work.Fire(DecideSetupCard);
        _weekShown = Work.Peek(() => database.GetPreference(ViewKey)) == WeekValue;
        WeekPage.Connect(OpenItemMenu);
        WeekPage.WeekChanged += () => Work.Show(() => ShowItems(force: true), () => ShowItems());
        WeekPage.UndatedClicked += ShowUndated;
        ShowView();
    }

    private void OnViewToggleClicked(object sender, RoutedEventArgs e)
    {
        _weekShown = !_weekShown;
        SaveView();
        ShowView();
        Work.Show(() => ShowItems(force: true), () => ShowItems());
    }

    /// <summary>Remembers the list or the week, whichever is showing now.</summary>
    private void SaveView()
    {
        var view = _weekShown ? WeekValue : ListValue;
        Work.Fire(() => _database.SetPreference(ViewKey, view));
    }

    /// <summary>Switches to the list and scrolls to its No date group, which is where the week leaves those items.</summary>
    private void ShowUndated()
    {
        _weekShown = false;
        SaveView();
        ShowView();
        Work.Show(() => ShowItems(force: true), () => ShowItems());

        // Queued after ShowItems puts the old place back, so this one wins.
        Dispatcher.InvokeAsync(() =>
        {
            var label = ItemGroups.DayLabel(null, DateOnly.FromDateTime(DateTime.Now));
            var group = Groups.Items.OfType<GroupRow>().FirstOrDefault(row => row.Label == label);
            if (group is not null && Groups.ItemContainerGenerator.ContainerFromItem(group) is UIElement container
                && ItemsScroll.Content is UIElement content)
            {
                ItemsScroll.ScrollToVerticalOffset(container.TranslatePoint(new Point(0, 0), content).Y);
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Shows one item, for a link in a reply: an event on its week in the week view, anything else selected in the
    /// list and scrolled to. A filter or search that would hide it goes back to All.
    /// </summary>
    public void Reveal(StoredItem item)
    {
        if (SelectedFilter is not null || ShowingWaiting)
        {
            Column.Select(AllKey);
        }

        if (!ToDoFilters.MatchesSearch(Column.SearchText, item, null))
        {
            Column.SetSearch("");
        }

        var key = ItemRef.Format(item);
        if (item.DueDate is { } date && (item.Kind == ItemKind.Event || ItemGroups.IsScheduled(item)))
        {
            _weekShown = true;
            SaveView();
            ShowView();
            WeekPage.ShowWeekOf(date);
            Work.Show(() => ShowItems(force: true), () => ShowItems());
            return;
        }

        _weekShown = false;
        SaveView();
        ShowView();
        _selected.Clear();
        _selected.Add(key);
        _anchor = key;
        Work.Show(() => ShowItems(force: true), () => ShowItems());

        // Queued after ShowItems puts the old place back, so this one wins.
        Dispatcher.InvokeAsync(() =>
        {
            if (_rowOrder.FirstOrDefault(row => row.Key == key) is { } row && Shown(Groups, row) is { } element)
            {
                element.BringIntoView();
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The element on screen showing a row, found by its data.</summary>
    private static FrameworkElement? Shown(DependencyObject root, object data)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { DataContext: var context } element && ReferenceEquals(context, data))
            {
                return element;
            }

            if (Shown(child, data) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    /// <summary>Raised when the page switches between the list and the week, so the window can give the week more room.</summary>
    public event Action? WeekShownChanged;

    /// <summary>Whether the week is showing rather than the list.</summary>
    public bool WeekShown => _weekShown;

    private void ShowView()
    {
        WeekShownChanged?.Invoke();
        ViewToggle.Content = _weekShown ? "List view" : "Week view";
        WeekPage.Visibility = _weekShown ? Visibility.Visible : Visibility.Collapsed;
        ItemsScroll.Visibility = _weekShown ? Visibility.Collapsed : Visibility.Visible;
        if (_weekShown)
        {
            Empty.Visibility = Visibility.Collapsed;
            ClearSelection();
        }
    }

    public void Refresh(BedtimeStatus? tonight)
    {
        _lastSaid = null;
        ShowFilters();
        ShowItems();
        ShowMoney();
        ShowTonight(tonight);
        FindStudy(tonight);
        ShowSuggestion();
        ShowSetup();
    }

    private ToDoFilter? SelectedFilter => FilterId(Column.SelectedKey) is { } id ? _filters.FirstOrDefault(filter => filter.Id == id) : null;

    private ToDoFilter Personal => _filters.First(filter => filter.Source == FilterSource.Personal);

    private static string Key(ToDoFilter filter) => $"f{filter.Id}";

    private static long? FilterId(string? key) =>
        key is ['f', .. var digits] && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>All stays at the top. Classes and the user's own filters each keep the order they were dragged into.</summary>
    private void ShowFilters(bool force = false)
    {
        _filters = _database.GetToDoFilters();
        _placements = _database.GetItemPlacements();
        var open = _database.GetOpenItems().Where(item => item.ParentRef is null && ItemGroups.IsToDo(item)).ToList();

        // A line sets the classes and filters apart from All and Waiting on, so a wait doesn't read as a class.
        var entries = _filters.Select((filter, index) =>
        {
            var count = open.Count(item => ToDoFilters.Matches(filter, item, Placement(item)));
            var group = filter.Source switch
            {
                FilterSource.Class => "class",
                FilterSource.Custom => "custom",
                _ => null,
            };
            return new ColumnEntry(Key(filter), count > 0 ? $"{filter.Name}  ·  {count}" : filter.Name, group, CanRename: true, filter.Name, RuleAbove: index == 0);
        });

        LoadWaiting();
        Column.SetEntries(entries.Prepend(WaitingEntry()).Prepend(new ColumnEntry(AllKey, "All")).ToList(), force);
    }

    private ItemPlacement? Placement(StoredItem item) =>
        item.Source == ItemSource.Capture ? _placements.GetValueOrDefault(item.Id) : null;

    private IEnumerable<RowMenuEntry> FilterMenu(ColumnEntry entry) =>
        FilterId(entry.Key) is { } id && _filters.FirstOrDefault(filter => filter.Id == id) is { Source: FilterSource.Custom } filter
            ? [new RowMenuEntry("Remove filter", () => RemoveFilter(filter))]
            : [];

    private async void RemoveFilter(ToDoFilter filter)
    {
        if (!await Work.Run(() => _database.RemoveToDoFilter(filter.Id, DateTime.Now)))
        {
            return;
        }

        Bar.Show($"Removed {filter.Name}. Its items are under {Personal.Name} now.", async () =>
        {
            await Work.Run(() => _database.RestoreToDoFilter(filter.Id));
            FiltersChanged();
        });
        FiltersChanged();
    }

    /// <summary>A rename done at once says its problem to the column as it asks. One on its way to the server says it once it's back.</summary>
    private async void OnFilterRenamed(object? sender, ColumnRenameEventArgs e)
    {
        if (FilterId(e.Entry.Key) is not { } id)
        {
            return;
        }

        var name = e.NewName;
        var renaming = Work.Run(() => _database.RenameToDoFilter(id, name));
        if (renaming.IsCompletedSuccessfully)
        {
            e.Problem = renaming.Result.Problem;
            FiltersChanged();
            return;
        }

        var renamed = await renaming;
        Column.ShowProblem(renamed.Problem);
        FiltersChanged();
    }

    private void OnAddChosen(string? choice)
    {
        if (choice == NewFilterKey)
        {
            NewFilter();
        }
        else if (choice == WaitingKey)
        {
            AddWaiting();
        }
        else if (Enum.TryParse<ItemKind>(choice, out var kind))
        {
            AddItem(kind);
        }
    }

    private async void NewFilter()
    {
        var name = new RenameWindow(Window.GetWindow(this), "", "Add", "Filter name, such as Work", "New filter");
        if (name.ShowDialog() != true)
        {
            return;
        }

        var newName = name.NewName;
        var made = await Work.Run(() => _database.CreateToDoFilter(newName));
        Column.ShowProblem(made.Problem);
        if (made.Filter is { } filter)
        {
            Column.Select(Key(filter));
            FiltersChanged();
        }
    }

    /// <summary>The form starts in whichever class or filter is open, so a new item lands where it's being looked for.</summary>
    private void AddItem(ItemKind kind)
    {
        var personal = new FormPlace(Personal.Name);
        var places = _filters
            .Where(filter => filter.Source != FilterSource.Personal)
            .Select(filter => filter.Source == FilterSource.Class ? new FormPlace(filter.Name, Course: filter.Course) : new FormPlace(filter.Name, FilterId: filter.Id))
            .Prepend(personal)
            .ToList();
        var place = SelectedFilter is { } selected ? places.FirstOrDefault(found => found.Label == selected.Name) : null;

        var form = AddItemWindow.Saving(Window.GetWindow(this), kind, DateOnly.FromDateTime(DateTime.Now), places, place ?? personal, async item =>
        {
            var added = await Work.Run(() => _database.AddByHand(item, DateTime.Now));
            if (added.Item is { } stored)
            {
                Bar.Show($"Added {stored.Title}", about: About(stored));
            }

            return added.Problem;
        });

        if (form.ShowDialog() == true)
        {
            FiltersChanged();
            _changed();
        }
    }

    private void FiltersChanged() => Work.ShowAfterChanges(nameof(FiltersChanged), () =>
    {
        ShowFilters(force: true);
        ShowItems(force: true);
    }, () =>
    {
        ShowFilters();
        ShowItems();
    });

    /// <summary>On any change, so a screen reader's toggle saves too. Showing the saved choice isn't a change.</summary>
    private void OnHideFinishedClicked(object sender, RoutedEventArgs e)
    {
        if (_showingHideFinished)
        {
            return;
        }

        var hide = HideFinished.IsChecked == true ? "1" : "0";
        Work.Fire(() => _database.SetPreference(HideFinishedKey, hide));
        Work.Show(() => ShowItems(force: true), () => ShowItems());
    }

    /// <summary>"Hide 2 finished", counting the finished rows this filter and search would show, so it's clear what the box hides.</summary>
    private void ShowFinishedCount(IReadOnlyList<StoredItem> everything, ToDoFilter? filter, string search, DateOnly today)
    {
        var finished = everything.Count(item => item.Done && item.ParentRef is null
            && ToDoFilters.Matches(filter, item, Placement(item)) && PassesWaiting(item, search, today)
            && ToDoFilters.MatchesSearch(search, item, Placement(item), ItemGroups.StepsOf(item, everything)));
        HideFinishedText.Text = finished > 0 ? $"Hide {finished} finished" : "Hide finished";
    }

    /// <summary>Rows wait while one has focus, so they catch up here.</summary>
    private void OnRowsLeft(object sender, KeyboardFocusChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => Work.Show(() => ShowItems()), DispatcherPriority.Background);

    /// <summary>
    /// The money line on To do, or null when there's no budget set up or nothing is left today. A $0.00 isn't a thing
    /// to do, so it stays on Budget, which explains it, rather than being the first thing read each morning.
    /// </summary>
    public static string? SafeToSpendLine(SpendSummary money) =>
        money.SafeToSpend is { } cents && cents > 0
            ? $"Safe to spend today: {Briefing.Money(cents)}{(money.SafeToSpendReason is { } reason ? $". {reason}" : "")}"
            : null;

    private void ShowMoney()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var money = _database.GetSpendSummary(today);
        var line = SafeToSpendLine(money);
        SafeLine.Visibility = line is null ? Visibility.Collapsed : Visibility.Visible;
        SafeLine.Content = line;

        // What needs a look on Budget stays on Budget, so To do keeps to one money line.
    }

    /// <summary>
    /// Rows only rebuild when something changed and nobody is working in them. Steps stay in the list
    /// so they can show under their parent, and the filter and search only ask about top rows.
    /// </summary>
    private void ShowItems(bool force = false, bool toTop = false)
    {
        // Rebuilding the rows would take the open menu's row away from under it. A menu's own action runs before
        // the menu has finished closing, so the rebuild it asks for waits here and runs as soon as it's gone.
        if (_menuOpen)
        {
            _rebuildWaiting = true;
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var hideFinished = HideFinished.IsChecked == true;
        var filter = SelectedFilter;
        LoadWaiting();
        var search = Column.SearchText;
        // Purchases and shopping are stored open but aren't things to do, so neither the rows, the counts, nor the week show them.
        var everything = ItemGroups.WithFinished(_database.GetOpenItems().Where(ItemGroups.IsToDo), _database.GetFinishedForList(DateTime.Now));
        var all = everything
            .Where(item => !hideFinished || !item.Done)
            .ToList();
        ShowFinishedCount(everything, filter, search, today);
        var items = all
            .Where(item => item.ParentRef is not null
                || (ToDoFilters.Matches(filter, item, Placement(item)) && PassesWaiting(item, search, today) && ToDoFilters.MatchesSearch(search, item, Placement(item), ItemGroups.StepsOf(item, all))))
            .ToList();

        var signature = string.Join('\n', items
            .Select(item => $"{ItemRef.Format(item)}\t{item.Title}\t{item.DueDate}\t{item.DueAt}\t{item.Done}\t{item.Kind}\t{item.ParentRef}\t{Placement(item)}")
            .Prepend($"{today}\t{filter?.Name}\t{search}\t{hideFinished}\t{Column.SelectedKey}\t{WaitingSignature()}"));
        if (force)
        {
            _rowsGate.Invalidate();
        }

        PageTitle.Text = ShowingWaiting ? "Waiting on" : filter?.Name ?? "Up next";
        if (_weekShown)
        {
            ShowWeek(everything, items, filter, search, signature);
            return;
        }

        if (!_rowsGate.ShouldRebuild(signature, !force && ItemsScroll.IsKeyboardFocusWithin))
        {
            return;
        }

        var groups = ItemGroups.ByDay(items, today).Select(group => new GroupRow(group, items, _placements, WaitingFor)).ToList();
        ShowSelection(groups);

        // Every row is rebuilt, so the place in the list is put back once they've been laid out.
        var offset = toTop ? 0 : ItemsScroll.VerticalOffset;
        Groups.ItemsSource = groups;
        Dispatcher.InvokeAsync(() => ItemsScroll.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
        Empty.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Text = search.Trim().Length > 0 ? "Nothing matches that search."
            : ShowingWaiting ? "You're not waiting on anyone. Use + to add one."
            : filter is not null ? $"Nothing for {filter.Name}."
            : AllEmptyText;
    }

    /// <summary>
    /// The same items as the list, laid out by time. Classes and shifts follow the filter too: a class filter shows
    /// that class, Personal shows work, and All shows both. Free time counts everything with a time, shown or not.
    /// The grid has nothing to type in, so it rebuilds whenever it changed, which also moves the free time along with the clock.
    /// </summary>
    private void ShowWeek(IReadOnlyList<StoredItem> everything, IReadOnlyList<StoredItem> items, ToDoFilter? filter, string search, string itemSignature)
    {
        var now = DateTime.Now;
        var start = WeekPage.Start;
        var last = start.AddDays(6);
        bool InWeek(StoredItem item) => item.ParentRef is null && ItemGroups.IsToDo(item) && item.DueDate is { } date && date >= start && date <= last;
        WeekEntry? Timed(StoredItem item) => WeekLayout.ItemEntry(item, Placement(item)?.EndTime);

        var classes = WeekLayout.ClassEntries(_classes, start);
        var shifts = WeekLayout.ShiftEntries(Enumerable.Range(0, 7).SelectMany(offset => _database.GetShifts(start.AddDays(offset))));
        var shownClasses = classes.Where(entry => (filter is null || (filter.Source == FilterSource.Class && filter.Course == entry.Title)) && MatchesSearch(search, entry));
        var shownShifts = shifts.Where(entry => (filter is null || filter.Source == FilterSource.Personal) && MatchesSearch(search, entry));

        var weekItems = items.Where(InWeek).ToList();
        var shown = weekItems.Select(Timed).OfType<WeekEntry>().Concat(shownClasses).Concat(shownShifts).ToList();
        var busy = everything.Where(InWeek).Select(Timed).OfType<WeekEntry>().Concat(classes).Concat(shifts);
        var plan = WeekLayout.Build(start, shown, weekItems.Where(WeekLayout.IsAllDay), busy, now);

        var undated = items.Count(item => item.ParentRef is null && ItemGroups.IsToDo(item) && item.DueDate is null);
        var signature = string.Join('\n', shown.Select(entry => $"{entry.Kind}\t{entry.Date}\t{entry.Start}\t{entry.End}\t{entry.Title}")
            .Prepend($"week\t{start}\t{now:yyyy-MM-dd HH}:{now.Minute / 15}\t{undated}\t{itemSignature}"));
        if (!_rowsGate.ShouldRebuild(signature, busy: false))
        {
            return;
        }

        WeekPage.Show(plan, now, undated);
    }

    /// <summary>A class or a shift matches when every word typed is in its name or room.</summary>
    private static bool MatchesSearch(string search, WeekEntry entry)
    {
        var text = $"{entry.Title} {entry.Detail}";
        return search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The "I can't start" path with no sorter: the most pressing item, its own steps or a fixed tiny action.</summary>
    private async void OnStartClicked(object sender, RoutedEventArgs e)
    {
        var started = await Work.Run(() => _database.StartActivation(null, StartMethod, DateTime.Now));
        if (started.Text == Activation.NothingToPick)
        {
            ReplyNothing();
            return;
        }

        Say(started.Text);
        Acted();
    }

    /// <summary>The same "nothing to pick" twice in a row says nothing new, so the second press stays quiet.</summary>
    private void ReplyNothing()
    {
        if (_lastSaid != NothingToPick)
        {
            Say(NothingToPick);
        }
    }

    private void Say(string text)
    {
        _lastSaid = text;
        _reply(text);
    }

    private void OnFifteenClicked(object sender, RoutedEventArgs e) => PickForEnergy(EnergyLevel.FifteenMinutes, EnergyPicks.Heading(EnergyLevel.FifteenMinutes));

    private void OnLowEnergyClicked(object sender, RoutedEventArgs e) => PickForEnergy(EnergyLevel.LowEnergy, EnergyPicks.Heading(EnergyLevel.LowEnergy));

    /// <summary>What was just turned down with Not now, which the suggestion and the energy buttons pass over for a while.</summary>
    private IReadOnlySet<string> Declined(DateTime now) => NotNowDeclines.Read(_database.GetPreference(NotNowDeclines.Preference), now);

    /// <summary>
    /// Picked locally from dates and sizes, then started like Start, so the session card in Ask opens on it with Did it,
    /// Make it smaller and Not now. The reply only says why this one, since the card already shows the first action.
    /// </summary>
    private async void PickForEnergy(EnergyLevel level, string heading)
    {
        // Picked from what's open now, read where the change is made, so it never starts on something already gone.
        var pick = await Work.Run(() =>
        {
            var now = DateTime.Now;
            if (EnergyPicks.Pick(_database.WithoutQuietWaiting(_database.GetOpenItems(), DateOnly.FromDateTime(now)), level, DateOnly.FromDateTime(now), Declined(now)) is not { } found)
            {
                return null;
            }

            _database.StartActivation(ItemRef.Format(found.Item), EnergyMethod, now);
            return found;
        });
        if (pick is null)
        {
            ReplyNothing();
            return;
        }

        Say($"{heading}: {pick.Item.Title}. {pick.Reason}");
        Acted();
    }

    /// <summary>For Ctrl+F.</summary>
    public void FocusSearch() => Column.FocusSearch();

    /// <summary>For Ctrl+N.</summary>
    public void OpenAdd() => Column.OpenAdd();

    /// <summary>Ctrl+N: a new task straight away, or a new wait while Waiting on is open. + still has every kind.</summary>
    public void AddByShortcut() => Column.AddByShortcut();

    private void OnMoneyClicked(object sender, RoutedEventArgs e) => _openMoney();

    private async void OnItemChecked(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        var item = ((ItemRow)box.DataContext).Item;
        var key = ItemRef.Format(item);
        var done = box.IsChecked == true;

        // Rows fill their boxes from the saved state, which isn't a change to save. A row clicked again before its
        // last click was saved goes by that click instead.
        var shown = _doneWanted.TryGetValue(key, out var wanted) && ReferenceEquals(wanted.Item, item) ? wanted.Done : item.Done;
        if (done == shown)
        {
            return;
        }

        _doneWanted[key] = (item, done);
        try
        {
            await Work.Run(() => _database.SetDone(item, done));
        }
        finally
        {
            if (_doneWanted.TryGetValue(key, out var last) && last.Done == done && ReferenceEquals(last.Item, item))
            {
                _doneWanted.Remove(key);
            }
        }

        Bar.Forget(About(item));
        Work.ShowAfterChanges(nameof(OnItemChecked), () =>
        {
            ShowFilters();
            ShowItems(force: true);
            ShowSuggestion();
        }, () =>
        {
            ShowFilters();
            ShowItems();
            ShowSuggestion();
        });
    }

    private void OnRowMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var box = (FrameworkElement)sender;
        var row = (ItemRow)box.DataContext;
        if (_selected.Count > 1 && _selected.Contains(row.Key))
        {
            e.Handled = true;
            OpenSeveralMenu(box, e);
            return;
        }

        // Right-clicking outside the selection means this row on its own.
        ClearSelection();
        e.Handled = true;
        OpenItemMenu(box, e.CursorLeft < 0 ? PlacementMode.Bottom : PlacementMode.MousePoint, row.Item);
    }

    /// <summary>A row's menu, also used by the week view's blocks so both act the same way.</summary>
    private async void OpenItemMenu(FrameworkElement box, PlacementMode placement, StoredItem item)
    {
        // Only a calendar item can have been moved from its feed date, and that's asked of the server, so in server
        // mode its menu opens once the answer comes, unless another menu was asked for meanwhile.
        // On this PC's database the answer is there at once, and the menu opens on the row as before.
        var asked = ++_menusAsked;
        var reading = item.Source == ItemSource.Calendar ? Work.Read(() => _database.IsMovedFromFeed(item)) : System.Threading.Tasks.Task.FromResult(false);
        var waited = !reading.IsCompleted;
        var moved = await reading;
        if (waited && (asked != _menusAsked || PresentationSource.FromVisual(box) is null))
        {
            return;
        }

        var entries = Work.Peek(() => ItemMenu(item, moved));
        _menuOpen = true;
        RowMenu.Open(box, placement, entries, MenuClosed);
    }

    private int _menusAsked;

    private IEnumerable<RowMenuEntry> ItemMenu(StoredItem item, bool moved)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var canFile = item.Source == ItemSource.Capture && ToDoFilters.CourseOf(item, Placement(item)) is null && _filters.Any(filter => filter.Source == FilterSource.Custom);
        var actions = RowActions.For(item, today, moved, canFile);
        IEnumerable<RowMenuEntry> Part(RowActionGroup group) => actions.Where(action => RowActions.GroupOf(action) == group).Select(action => Entry(action, item, today));
        var waiting = WaitingMenu(item, today).ToList();
        // "Waiting on someone..." is one more way to change it. A wait's own follow-up lines get a part of their own.
        List<RowMenuEntry> markWaiting = waiting.Count == 1 ? waiting : [];
        List<RowMenuEntry> followUp = waiting.Count == 1 ? [] : waiting;

        // Doing it, changing it, following up a wait, copying, and Delete apart at the bottom.
        return RowMenuEntry.Parts(ResumeMenu(item).Concat(Part(RowActionGroup.Do)), Part(RowActionGroup.Change).Concat(markWaiting), followUp, FullTextMenu(item).Concat(Part(RowActionGroup.Copy)), FeedbackMenu(item), Part(RowActionGroup.Remove)).ToList();
    }

    /// <summary>The words on the row menu's This didn't help, which opens a line to add a note.</summary>
    public const string FeedbackMenuLabel = CardLabels.DidntHelp + "...";

    /// <summary>This didn't help, kept as soon as it's chosen, then a line above the list for an optional note.</summary>
    private IEnumerable<RowMenuEntry> FeedbackMenu(StoredItem item)
    {
        if (item.Done || FeedbackData is not { } feedback)
        {
            return [];
        }

        return [new RowMenuEntry(FeedbackMenuLabel, async () =>
        {
            try
            {
                var reference = ItemRef.Format(item);
                RowFeedback.Open(await Work.Run(() => feedback.AddFeedback(Feedback.Row, reference, DateTime.Now)), item.Title);
            }
            catch (Exception exception) when (exception is ServerUnavailableException or ServerTimedOutException or InvalidOperationException)
            {
                _reply("Couldn't keep that while the server can't be reached.");
            }
        })];
    }

    /// <summary>Catches up on a rebuild the menu held back. It's forced, since focus is still in the rows after the menu key.</summary>
    private void MenuClosed()
    {
        _menuOpen = false;
        if (_rebuildWaiting)
        {
            _rebuildWaiting = false;
            Work.Show(() => ShowItems(force: true), () => ShowItems());
        }
    }

    /// <summary>
    /// A long capture keeps its whole text under a short title. It shows under the title while the row is selected,
    /// and this keeps it open, or folds it up again, without selecting.
    /// </summary>
    private IEnumerable<RowMenuEntry> FullTextMenu(StoredItem item)
    {
        if (item.Body is null)
        {
            return [];
        }

        var key = ItemRef.Format(item);
        return _expanded.Contains(key)
            ? [new RowMenuEntry("Hide full text", () => Expand(key, false))]
            : [new RowMenuEntry("Show full text", () => Expand(key, true))];
    }

    private void Expand(string key, bool open)
    {
        if (open)
        {
            _expanded.Add(key);
        }
        else
        {
            _expanded.Remove(key);
        }

        MarkSelected();
    }

    /// <summary>Ctrl-click adds or takes away one row, Shift-click selects from the last one clicked. A plain click works as always.</summary>
    private void OnRowPressed(object sender, MouseButtonEventArgs e)
    {
        var box = (CheckBox)sender;
        var row = (ItemRow)box.DataContext;
        var modifiers = Keyboard.Modifiers;
        var ctrl = modifiers.HasFlag(ModifierKeys.Control);
        var shift = modifiers.HasFlag(ModifierKeys.Shift);
        if (!ctrl && !shift)
        {
            ClearSelection();
            return;
        }

        // Selecting isn't checking, so the box mustn't toggle.
        e.Handled = true;
        box.Focus();
        if (shift && _anchor is not null && _rowOrder.FindIndex(found => found.Key == _anchor) is var from and >= 0)
        {
            var to = _rowOrder.FindIndex(found => found.Key == row.Key);
            if (!ctrl)
            {
                _selected.Clear();
            }

            foreach (var inRange in _rowOrder.Skip(Math.Min(from, to)).Take(Math.Abs(to - from) + 1))
            {
                _selected.Add(inRange.Key);
            }
        }
        else
        {
            Toggle(row);
        }

        MarkSelected();
    }

    /// <summary>Ctrl+Space selects the focused row, and Escape lets go of the selection.</summary>
    private void OnRowsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && _selected.Count > 0)
        {
            ClearSelection();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Space && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && (e.OriginalSource as FrameworkElement)?.DataContext is ItemRow row)
        {
            Toggle(row);
            MarkSelected();
            e.Handled = true;
        }
    }

    private void Toggle(ItemRow row)
    {
        if (!_selected.Remove(row.Key))
        {
            _selected.Add(row.Key);
        }

        _anchor = row.Key;
    }

    private void ClearSelection()
    {
        if (_selected.Count == 0)
        {
            return;
        }

        _selected.Clear();
        _anchor = null;
        MarkSelected();
    }

    private void MarkSelected()
    {
        foreach (var row in _rowOrder)
        {
            row.IsSelected = _selected.Contains(row.Key);
            row.IsExpanded = _expanded.Contains(row.Key);
        }
    }

    /// <summary>After a rebuild, the new rows take over the selection. Anything no longer shown drops out of it, so an action can't reach a hidden item.</summary>
    private void ShowSelection(IReadOnlyList<GroupRow> groups)
    {
        _rowOrder = groups
            .SelectMany(group => group.ScheduledRows.Concat(group.ToDoRows))
            .SelectMany(row => row.Steps.Prepend(row))
            .ToList();
        var shown = _rowOrder.Select(row => row.Key).ToHashSet();
        _selected.IntersectWith(shown);
        _expanded.IntersectWith(shown);
        if (_anchor is not null && !shown.Contains(_anchor))
        {
            _anchor = null;
        }

        MarkSelected();
    }

    private IReadOnlyList<StoredItem> SelectedItems => _rowOrder.Where(row => _selected.Contains(row.Key)).Select(row => row.Item).ToList();

    /// <summary>Done, Reschedule, Drop, and Delete for every selected row, each through the same path as its own menu, with one undo for all of them.</summary>
    private void OpenSeveralMenu(FrameworkElement box, ContextMenuEventArgs e)
    {
        var items = SelectedItems;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var count = items.Count;
        var entries = RowActions.ForSeveral(items, today).Select(action => action switch
        {
            RowAction.Done => new RowMenuEntry($"Mark {count} done", async () => Several(await Work.Run(() => SeveralRows.Finish(_database, items)), $"Marked {count} done.")),
            RowAction.Reschedule => new RowMenuEntry($"Reschedule {count}", Children:
            [
                new("Today", () => MoveSeveral(items, today)),
                new("Tomorrow", () => MoveSeveral(items, today.AddDays(1))),
                new("Next week", () => MoveSeveral(items, today.AddDays(7))),
                new("Pick a date...", () => PickDateForSeveral(items, today)),
            ]),
            _ => new RowMenuEntry($"Delete {count}", async () => Several(await Work.Run(() => SeveralRows.Remove(_database, items, DateTime.Now)), $"Deleted {count}.")),
        }).ToList();

        if (entries.Count == 0)
        {
            entries.Add(new RowMenuEntry("Some of these are finished, so they can only be changed one at a time"));
        }

        entries.Add(RowMenuEntry.Separator);
        entries.Add(new RowMenuEntry("Clear selection", ClearSelection));
        _menuOpen = true;
        RowMenu.Open(box, e, entries, MenuClosed);
    }

    private void PickDateForSeveral(IReadOnlyList<StoredItem> items, DateOnly today)
    {
        // No time box, so each one keeps its own time, and repeating ones keep their usual time.
        var first = items.Select(item => item.DueDate).Where(date => date is not null).Min() ?? today;
        var picker = new PickDateWindow(Window.GetWindow(this), $"{items.Count} items", first, null, asksForTime: false);
        if (picker.ShowDialog() == true)
        {
            MoveSeveral(items, picker.Date);
        }
    }

    private async void MoveSeveral(IReadOnlyList<StoredItem> items, DateOnly date)
    {
        var result = await Work.Run(() => SeveralRows.Move(_database, items, date, DateTime.Now));
        var day = date.ToString("dddd, MMM d", CultureInfo.CurrentCulture);
        Several(result, result.Undo is null
            ? $"Moved {result.Changed} to {day}. Undo isn't available, because some had no date before."
            : $"Moved {result.Changed} to {day}.");
    }

    private void Several(SeveralChanged result, string message)
    {
        ClearSelection();
        Bar.Show(message, result.Undo is { } undo ? async () =>
        {
            await Work.Run(undo);
            Acted();
        } : null);
        Acted();
    }

    private RowMenuEntry Entry(RowAction action, StoredItem item, DateOnly today) => action switch
    {
        RowAction.Done => item.Dose ? new(CardLabels.TookIt, () => TookIt(item)) : new("Mark done", () => SetDone(item, true)),
        RowAction.Reopen => new("Reopen", () => SetDone(item, false)),
        RowAction.Start => new("Start now", () => Start(item)),
        RowAction.SmallerStep => new("Start with a tiny step", () => StartSmaller(item)),
        RowAction.Study => new("Study for this exam", () => StudyRequested?.Invoke(item)),
        RowAction.Reschedule => new("Reschedule", Children:
        [
            new("Today", () => Move(item, today, null)),
            new("Tomorrow", () => Move(item, today.AddDays(1), null)),
            new("Next week", () => Move(item, today.AddDays(7), null)),
            new("Pick a date...", () => PickDate(item, today)),
        ]),
        RowAction.UseFeedDate => new("Use feed date", () => Change(item, ChangeAction.UseFeedDate)),
        RowAction.Rename => new("Rename", () => Rename(item)),
        RowAction.BreakDown => new("Break down", () => _submit($"break down {item.Title}")),
        RowAction.File => new("Move to filter", Children: FileChoices(item).ToList()),
        RowAction.StopReminders => new("Stop reminders", () => Change(item, ChangeAction.StopReminders)),
        RowAction.Dose => new(item.Dose ? "Not a dose" : "Mark as a dose", () => MarkDose(item, !item.Dose)),
        RowAction.Cancel => new("Delete", () => Remove(item)),
        RowAction.Delete => new("Delete", () => Delete(item)),
        _ => new("Copy title", () => RowMenu.Copy(item.Title)),
    };

    /// <summary>An open item's Delete. It's kept in History as canceled, and the bar's Undo reopens it.</summary>
    private async void Remove(StoredItem item)
    {
        Bar.Forget(About(item));
        var applied = await Work.Run(() => _database.ChangeItem(new ItemChange(item.Source, item.Id, ChangeAction.Cancel), DateTime.Now));
        if (!applied.Applied)
        {
            Report(applied);
            return;
        }

        Bar.Show($"Deleted {item.Title}", async () =>
        {
            await Work.Run(() => _database.SetDone(item, false));
            Acted();
        });
        Acted();
    }

    /// <summary>A finished item's Delete, which takes it out of History too.</summary>
    private async void Delete(StoredItem item)
    {
        if (await Work.Run(() => _database.DeleteItem(item, DateTime.Now)) is not { } deleted)
        {
            return;
        }

        Bar.Show($"Deleted {item.Title}", async () =>
        {
            await Work.Run(() => _database.UndoDelete(deleted, DateTime.Now));
            Acted();
        });
        Acted();
    }

    /// <summary>Every filter it could go in except the one it's in now.</summary>
    private IEnumerable<RowMenuEntry> FileChoices(StoredItem item)
    {
        var current = Placement(item)?.FilterId;
        return _filters
            .Where(filter => filter.Source is FilterSource.Personal or FilterSource.Custom)
            .Where(filter => filter.Source == FilterSource.Personal ? current is not null : filter.Id != current)
            .Select(filter => new RowMenuEntry(filter.Name, () => File(item, filter.Source == FilterSource.Personal ? null : filter)));
    }

    private async void File(StoredItem item, ToDoFilter? filter)
    {
        if (await Work.Run(() => _database.AssignToFilter(item, filter?.Id)))
        {
            Bar.Show($"Moved {item.Title} to {filter?.Name ?? Personal.Name}");
        }

        FiltersChanged();
    }

    private async void SetDone(StoredItem item, bool done)
    {
        await Work.Run(() => _database.SetDone(item, done));
        Bar.Forget(About(item));
        Acted();
    }

    /// <summary>A dose's Took it. It's kept once for its day, so a second one says when it was already marked.</summary>
    private async void TookIt(StoredItem item)
    {
        Bar.Forget(About(item));
        _reply((await Work.Run(() => _database.TakeDose(item, DateTime.Now))).Text);
        Acted();
    }

    private async void MarkDose(StoredItem item, bool dose)
    {
        _reply(await Work.Run(() => _database.SetDose(item, dose))
            ? dose ? $"{item.Title} is now a dose. Its reminder says {CardLabels.TookIt}." : $"{item.Title} is a plain reminder again."
            : "Only a repeating reminder can be a dose.");
        Acted();
    }

    /// <summary>What a message about this item is tied to, so finishing or changing it clears that message.</summary>
    private static object About(StoredItem item) => (item.Source, item.Id);

    private async void Start(StoredItem item)
    {
        var reference = ItemRef.Format(item);
        _reply((await Work.Run(() => _database.StartActivation(reference, StartMethod, DateTime.Now))).Text);
        Acted();
    }

    /// <summary>Uses the fixed actions, so it works offline and never waits on the sorter.</summary>
    private async void StartSmaller(StoredItem item)
    {
        _reply((await Work.Run(() => _database.StartSmaller(item, StartMethod, DateTime.Now))).Text);
        Acted();
    }

    private async void Move(StoredItem item, DateOnly date, TimeOnly? time) => Report(await Work.Run(() => _database.RescheduleItem(item, date, time, DateTime.Now)));

    private void PickDate(StoredItem item, DateOnly today)
    {
        var time = item.DueAt is { } dueAt ? TimeOnly.FromDateTime(dueAt.LocalDateTime) : (TimeOnly?)null;
        var picker = new PickDateWindow(Window.GetWindow(this), item.Title, item.DueDate ?? today, time, asksForTime: item.Recurs is null);
        if (picker.ShowDialog() == true)
        {
            Move(item, picker.Date, picker.Time);
        }
    }

    private void Rename(StoredItem item)
    {
        var rename = new RenameWindow(Window.GetWindow(this), item.Title);
        if (rename.ShowDialog() == true)
        {
            Change(item, ChangeAction.Update, rename.NewName);
        }
    }

    private async void Change(StoredItem item, ChangeAction action, string? title = null)
    {
        Bar.Forget(About(item));
        Report(await Work.Run(() => _database.ChangeItem(new ItemChange(item.Source, item.Id, action, title), DateTime.Now)));
    }

    private void Report(AppliedChange applied)
    {
        _reply(ItemGroups.ChangeSummary(applied));
        Acted();
    }

    private void Acted()
    {
        // An earlier message's Undo may no longer hold once something else has changed.
        _lastSaid = null;
        Bar.Changed();
        Work.ShowAfterChanges(nameof(Acted), () =>
        {
            ShowFilters();
            ShowItems(force: true);
            ShowSuggestion();
        }, () =>
        {
            ShowFilters();
            ShowItems();
            ShowSuggestion();
        });
        _changed();
    }

    private static string Time(DateTime time) => time.ToString("h:mm tt", CultureInfo.CurrentCulture);

    /// <summary>One line on the setup card. Key is what's remembered once it's been opened.</summary>
    private sealed record SetupStep(string Key, string Label)
    {
        /// <summary>What a screen reader says for it, rather than the record's fields.</summary>
        public override string ToString() => Label;
    }

    /// <summary>Rows are made once per rebuild, so the ones on screen are the ones the selection marks.</summary>
    private sealed class GroupRow
    {
        public GroupRow(ItemGroup group, IReadOnlyList<StoredItem> items, IReadOnlyDictionary<long, ItemPlacement> placements, Func<StoredItem, WaitingItem?>? waiting = null)
        {
            Label = group.Label;
            var rows = group.Items
                .Select(item => new ItemRow(item, ItemGroups.StepsOf(item, items), item.Source == ItemSource.Capture ? placements.GetValueOrDefault(item.Id) : null, WaitingDetail(waiting?.Invoke(item))))
                .ToList();
            ScheduledRows = rows.Where(row => ItemGroups.IsScheduled(row.Item)).OrderBy(row => row.Item.Done).ThenBy(row => row.Item.DueAt ?? DateTimeOffset.MaxValue).ToList();
            ToDoRows = rows.Where(row => !ItemGroups.IsScheduled(row.Item)).ToList();
        }

        public string Label { get; }

        public IReadOnlyList<ItemRow> ScheduledRows { get; }

        public IReadOnlyList<ItemRow> ToDoRows { get; }

        public Visibility ScheduledVisibility => ScheduledRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>The heading only earns its place when there's a schedule to tell it apart from.</summary>
        public Visibility ToDoHeadingVisibility => ScheduledRows.Count > 0 && ToDoRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>What a screen reader says for the group, rather than the class name.</summary>
        public override string ToString() => Label;
    }

    private sealed class ItemRow(StoredItem item, IReadOnlyList<StoredItem>? steps = null, ItemPlacement? placement = null, string? waiting = null) : INotifyPropertyChanged
    {
        private IReadOnlyList<ItemRow>? _steps;
        private bool _selected;
        private bool _expanded;

        public event PropertyChangedEventHandler? PropertyChanged;

        private IEnumerable<StoredItem> OpenSteps => (steps ?? []).Where(step => !step.Done);

        public StoredItem Item => item;

        public string Key { get; } = ItemRef.Format(item);

        public bool IsSelected
        {
            get => _selected;
            set
            {
                if (_selected != value)
                {
                    _selected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedBrush)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FullTextVisibility)));
                }
            }
        }

        /// <summary>Opened with Show full text from the row's menu.</summary>
        public bool IsExpanded
        {
            get => _expanded;
            set
            {
                if (_expanded != value)
                {
                    _expanded = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FullTextVisibility)));
                }
            }
        }

        /// <summary>The whole text of a long capture, kept on the item when its title was shortened.</summary>
        public string FullText => item.Body?.Trim() ?? "";

        /// <summary>Shown under the title while the row is selected or opened, so the list stays short otherwise.</summary>
        public Visibility FullTextVisibility => (_selected || _expanded) && FullText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>The accent, faint enough that the row's own colors still read on it, in either theme.</summary>
        public Brush SelectedBrush => _selected && Resource("Accent") is SolidColorBrush accent
            ? new SolidColorBrush(Color.FromArgb(0x38, accent.Color.R, accent.Color.G, accent.Color.B))
            : Brushes.Transparent;

        public bool Done => item.Done;

        public Brush TitleBrush => Resource(item.Done ? "FinishedText" : "PrimaryText");

        public Brush DetailBrush => Resource(item.Done ? "FinishedDetail" : "SecondaryText");

        public TextDecorationCollection? Strike => item.Done ? TextDecorations.Strikethrough : null;

        /// <summary>Only the next open step shows, so a big item stays one small thing. Finished ones stay to be unchecked.</summary>
        public IReadOnlyList<ItemRow> Steps => _steps ??= (steps ?? []).Where(step => step.Done).Concat(OpenSteps.Take(1)).Select(step => new ItemRow(step)).ToList();

        public string MoreSteps => $"{OpenSteps.Count() - 1} more after this";

        public Visibility MoreStepsVisibility => OpenSteps.Count() > 1 ? Visibility.Visible : Visibility.Collapsed;

        public string Title => item.Title;

        public Brush Mark => item.Done ? Resource("FinishedDetail") : ThemeBrushes.For(ItemMarks.For(item, DateOnly.FromDateTime(DateTime.Now)));

        /// <summary>What the bar's color means, shown on hover. Null shows no tip.</summary>
        public string? MarkTip => item.Done ? null : ItemMarks.Describe(ItemMarks.For(item, DateOnly.FromDateTime(DateTime.Now)));

        public string Details
        {
            get
            {
                var parts = new[]
                {
                    item.Kind is ItemKind.Deadline or ItemKind.Task ? null : item.Kind.ToString(),
                    item.DueAt is { } dueAt ? Time(dueAt.LocalDateTime) + (placement?.EndTime is { } end ? $" to {Time(DateTime.Today.Add(end.ToTimeSpan()))}" : "") : null,
                    item.Recurs?.Label,
                    item.AmountCents is { } cents ? (cents / 100m).ToString("C", CultureInfo.CurrentCulture) : null,
                    ToDoFilters.CourseOf(item, placement),
                    waiting,
                };
                return string.Join("  ·  ", parts.Where(part => part is not null));
            }
        }

        public Visibility DetailsVisibility => Details.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// What a screen reader says for the row: the title, what's under it, such as the time, and Overdue for
        /// something open from before today, which the eye gets from the group heading. The box says if it's checked.
        /// </summary>
        public string SpokenName
        {
            get
            {
                var overdue = !item.Done && item.DueDate < DateOnly.FromDateTime(DateTime.Now) ? ItemGroups.DayLabel(item.DueDate, DateOnly.FromDateTime(DateTime.Now)) : null;
                return string.Join(", ", new[] { Title, Details.Replace("  ·  ", ", ", StringComparison.Ordinal), overdue }.Where(part => !string.IsNullOrEmpty(part)));
            }
        }

        public override string ToString() => SpokenName;

        private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
    }
}
