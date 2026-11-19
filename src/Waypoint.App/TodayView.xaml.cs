using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
