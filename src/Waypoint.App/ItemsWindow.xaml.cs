using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;
using Waypoint.Core.Remote;
using Waypoint.Core.Schedule;

namespace Waypoint.App;

public partial class ItemsWindow : Window
{
    private const string PlaceKey = "items_window_place";
    private const string PageKey = "last_page";

    private readonly WaypointDatabase _database;
    private readonly bool _localSessions;
    private ISessionData _sessions;
    private readonly Action<string> _submitted;
    private readonly Action<string[]> _filesDropped;
    private readonly ObservableCollection<ChatRow> _chat = [];
    private ActivationSession? _session;
    private readonly IToDoData _toDo;
    private readonly IFeedbackData _feedback;
    private StoredItem? _afterThis;
    private string? _didItOn;
    private BedtimeStatus? _tonight;
    private Page _page;
    private bool _choosingPage;
    private bool _draftsDiscarded;
    private Action? _openKept;
    private Action? _dismissKept;
    private string? _noticeKey;
    private string? _choice;
    private string? _dismissedNotice;

    /// <summary>Below this window width Ask gets narrower, so the page beside it keeps room for its content.</summary>
    public const double NarrowWindow = 1200;

    /// <summary>
    /// Ask's width: its usual 320, or 260 in a narrow window, so the page beside it keeps room, and while To do shows
    /// the week, so the seven days have room to read.
    /// </summary>
    public static double AskWidthFor(double windowWidth, bool weekShown = false) => windowWidth < NarrowWindow || weekShown ? 260 : 320;

    /// <summary>Below this window height the top bar gets slimmer, so the page under it keeps a few more rows.</summary>
    public const double ShortWindow = 600;

    /// <summary>The top bar's height: its usual 64, or 48 in a short window.</summary>
    public static double TopBarHeightFor(double windowHeight) => windowHeight < ShortWindow ? 48 : 64;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        FitAsk();
        var top = TopBarHeightFor(ActualHeight);
        if (TopBar.Height != top)
        {
            TopBar.Height = top;
            if (WindowChrome.GetWindowChrome(this) is { } chrome)
            {
                var resized = (WindowChrome)chrome.Clone();
                resized.CaptionHeight = top;
                WindowChrome.SetWindowChrome(this, resized);
            }
        }
    }

    /// <summary>
    /// <paramref name="toDo"/> is the server in server mode. Then To do and Ask use it, Budget and the cards above
    /// Ask use <paramref name="budget"/> and <paramref name="sessions"/>, History uses <paramref name="history"/>,
    /// and Settings uses the server's settings, so every page works the same in both modes. <paramref name="notes"/>
    /// and <paramref name="lists"/> are the Notes page's data, since the lists sit in its column, the server's in
    /// server mode.
    /// </summary>
    /// <param name="server">The server in server mode, so clicks and page switches don't wait on it. See <see cref="PageWork"/>.</param>
    public ItemsWindow(WaypointDatabase database, Action<string> submitted, Action importCsv, Action<string[]> filesDropped, IToDoData? toDo = null, IBudgetData? budget = null, ISessionData? sessions = null, IListsData? lists = null, IHistoryData? history = null, INotesData? notes = null, IFeedbackData? feedback = null, ServerClient? server = null)
    {
        _localSessions = sessions is null;
        _sessions = sessions ?? new LocalSessionData(database);
        _filesDropped = filesDropped;
        _database = database;
        _submitted = submitted;
        InitializeComponent();
        WindowStyling.Apply(this);
        RestorePlace();
        TodayPage.Work = PageWorkFor(server);
        NotesPage.Work = PageWorkFor(server);
        _askWork = PageWorkFor(server);
        PageWork.SavingChanged += ShowSaving;
        Closed += (_, _) => PageWork.SavingChanged -= ShowSaving;
        Chat.ItemsSource = _chat;
        Examples.ItemsSource = AskExamples;
        toDo ??= new LocalToDoData(database);
        _toDo = toDo;
        _feedback = feedback ?? new LocalFeedbackData(database);
        TodayPage.FeedbackData = _feedback;
        SettingsPage.FeedbackData = _feedback;
        SessionFeedback.Kept = () => _feedback.AddFeedback(Feedback.Session, _session?.TaskRef, DateTime.Now);
        SessionFeedback.Noted = (id, note) => _feedback.NoteFeedback(id, note);
        AfterThisFeedback.Kept = () => _feedback.AddFeedback(Feedback.AfterThisCard, _afterThis is { } held ? ItemRef.Format(held) : null, DateTime.Now);
        AfterThisFeedback.Noted = (id, note) => _feedback.NoteFeedback(id, note);

        // Shown once means once: closing the window puts it away for good, even if it wasn't answered.
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                PutAwayHeld();
            }
        };
        lists ??= new LocalListsData(database);
        notes ??= new LocalNotesData(database);
        budget ??= new LocalBudgetData(database);
        history ??= new LocalHistoryData(database);
        TodayPage.Connect(toDo, () => ShowPage(Page.Budget), submitted, ShowReply, OnDataChanged);
        TodayPage.Sessions = _sessions;
        TodayPage.WeekShownChanged += FitAsk;
        TodayPage.SettingsRequested += section =>
        {
            ShowPage(Page.Settings);
            SettingsPage.ShowSection(section);
        };
        TodayPage.PayScheduleRequested += () =>
        {
            ShowPage(Page.Budget);
            MoneyPage.OpenPaySchedule();
        };
        NotesPage.Connect(notes, lists);

        // Study from notes: an exam on To do opens Notes and makes practice questions there.
        TodayPage.StudyRequested += exam =>
        {
            ShowPage(Page.Notes);
            NotesPage.StudyForExam(exam);
        };
        NotesPage.Replied += text =>
        {
            ShowReply(text);
            OnDataChanged();
        };
        MoneyPage.Work = PageWorkFor(server);
        MoneyPage.Connect(budget, importCsv);
        HistoryPage.Connect(history, () => TodayPage.Refresh(_tonight));
        FindPage.Connect(new FindSources(toDo, lists, notes, history, budget));
        FindPage.Chosen += ShowFound;
        FindPage.Closed += CloseFind;
        ShowPage(LastPage());

        // The Lists page is gone, and its lists are in Notes. One left open there opens on its list instead.
        if (_database.GetPreference(PageKey) == OldListsPage)
        {
            NotesPage.OpenList(OldOpenList(lists));
            _database.SetPreference(PageKey, nameof(Page.Notes));
        }
    }

    private const string OldListsPage = "Lists";

    /// <summary>The list the old Lists page had open, or Shopping when it can't be told.</summary>
    private static string OldOpenList(IListsData lists)
    {
        try
        {
            return lists.GetPreference("lists_open") is { Length: > 0 } open ? open : WaypointDatabase.ShoppingList;
        }
        catch (Waypoint.Core.Remote.ServerUnavailableException)
        {
            return WaypointDatabase.ShoppingList;
        }
    }

    private enum Page
    {
        ToDo,
        Notes,
        Budget,
        History,
        Settings,
    }

    /// <summary>Raised after a page changes something by hand, so the tray, theme, and tonight's card catch up.</summary>
    public event Action? DataChanged;

    /// <summary>
    /// The Settings page needs what only the app has: where its settings live, the running bedtime schedule, and
    /// what's connected.
    /// </summary>
    public void ConnectSettings(ISettingsData settings, BedtimeSchedule? bedtime, BedtimeSettings fileBedtime, Func<IReadOnlyList<ConnectionStatus>> connections)
    {
        TodayPage.Bedtime = bedtime;
        if (_localSessions)
        {
            // Tonight's card checks steps against the running schedule. In server mode the server's decides.
            _sessions = new LocalSessionData(_database, bedtime);
            TodayPage.Sessions = _sessions;
        }

        SettingsPage.Connect(settings, _database, bedtime, fileBedtime, connections, () => DataChanged?.Invoke());
    }

    public void ConnectNotesFolder(Func<string> current, Action<string> move) => SettingsPage.ConnectNotesFolder(current, move);

    /// <summary>For the cards above Ask, which read the session after a page changed something.</summary>
    private readonly PageWork _askWork;

    private void OnDataChanged()
    {
        _askWork.Show(ShowSession);
        DataChanged?.Invoke();
    }

    /// <summary>Opens on To do. If the window is already up, it stays on whatever page is in use.</summary>
    public void ShowList() => Open(Page.ToDo);

    /// <summary>Opens on Notes, for a recording dropped on Ask.</summary>
    public void ShowNotes()
    {
        if (_page != Page.Notes)
        {
            ShowPage(Page.Notes);
        }

        Open(Page.Notes);
    }

    /// <summary>After a card or bank import adds purchases, Budget opens on Purchases, so they're seen where they went.</summary>
    public void ShowPurchases()
    {
        if (_page != Page.Budget)
        {
            ShowPage(Page.Budget);
        }

        MoneyPage.ShowPurchases();
        Open(Page.Budget);
    }

    /// <summary>For starting Waypoint again from the Start menu, which picks up where things were left.</summary>
    public void ShowLastPage() => Open(LastPage());

    /// <summary>The running class schedule, for the week view on To do and for Settings, which can change it.</summary>
    public ClassSchedule? Classes
    {
        get => TodayPage.Classes;
        init
        {
            TodayPage.Classes = value;
            SettingsPage.Classes = value;
        }
    }

    /// <summary>Shows the running class schedule again after it changed in place, such as from the server.</summary>
    public void ClassesChanged() => TodayPage.Classes = TodayPage.Classes;

    private readonly List<PageWork> _works = [];

    private PageWork PageWorkFor(ServerClient? server)
    {
        if (server is null)
        {
            return PageWork.Local;
        }

        var work = new PageWork(server);
        _works.Add(work);
        return work;
    }

    /// <summary>
    /// For closing: waits up to <paramref name="limit"/> in all for changes still on their way to the server, and
    /// returns those that didn't finish.
    /// </summary>
    public IReadOnlyList<string> FinishWork(TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        var left = new List<string>();
        foreach (var work in _works)
        {
            var remaining = until - DateTime.UtcNow;
            left.AddRange(work.Finish(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero));
        }

        return left;
    }

    /// <summary>A quiet Saving... under the date while a change is on its way to a slow server, after a moment so a quick one shows nothing.</summary>
    private void ShowSaving()
    {
        if (!PageWork.Saving)
        {
            _savingDelay?.Stop();
            SavingLabel.Visibility = Visibility.Collapsed;
            return;
        }

        if (_savingDelay is null)
        {
            _savingDelay = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _savingDelay.Tick += (_, _) =>
            {
                _savingDelay.Stop();
                SavingLabel.Visibility = PageWork.Saving ? Visibility.Visible : Visibility.Collapsed;
            };
        }

        _savingDelay.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _savingDelay;

    /// <summary>Saves anything half typed, for when the window closes or the app exits.</summary>
    public void SaveDrafts()
    {
        if (_draftsDiscarded)
        {
            return;
        }

        NotesPage.Leave();
    }

    /// <summary>
    /// After a restore, anything still held in a page was typed before it, so it must never be written into the
    /// restored database. Drafts stay unsaved until Waypoint starts again, which the restore asks for.
    /// </summary>
    public void DiscardDrafts() => _draftsDiscarded = true;

    private void Open(Page page)
    {
        if (!IsVisible)
        {
            ShowPage(page);

            // Opening the window opens Ask, which is a natural point to bring back something put off earlier.
            BringBackHeld();
        }

        Show();
        Activate();

        // A reply can open the window while a list, note, or search is being typed in, so focus stays put then.
        if (!NotesPage.IsKeyboardFocusWithin && !TodayPage.IsKeyboardFocusWithin)
        {
            Input.Focus();
        }
    }

    // Pages saved before the renames still open where they were. Lists is in Notes now.
    private Page LastPage() => _database.GetPreference(PageKey) switch
    {
        "Today" => Page.ToDo,
        "Money" => Page.Budget,
        OldListsPage => Page.Notes,
        var saved => Enum.TryParse<Page>(saved, out var page) ? page : Page.ToDo,
    };

    private void ShowPage(Page page)
    {
        SaveDrafts();
        _page = page;
        TodayPage.Visibility = PageVisibility(Page.ToDo);
        NotesPage.Visibility = PageVisibility(Page.Notes);
        MoneyPage.Visibility = PageVisibility(Page.Budget);
        HistoryPage.Visibility = PageVisibility(Page.History);
        SettingsPage.Visibility = PageVisibility(Page.Settings);
        FindPage.Visibility = Visibility.Collapsed;

        _choosingPage = true;
        Menu.SelectedIndex = (int)page;
        _choosingPage = false;
        RefreshPage();
        if (page == Page.Settings)
        {
            SettingsPage.Load();
        }

        Visibility PageVisibility(Page shown) => shown == page ? Visibility.Visible : Visibility.Collapsed;
        FitAsk();
    }

    /// <summary>The one rule for Ask's width, kept up as the window resizes, the page changes, or To do switches view.</summary>
    private void FitAsk() =>
        AskWidth.Width = new GridLength(AskWidthFor(ActualWidth > 0 ? ActualWidth : Width, _page == Page.ToDo && TodayPage.WeekShown));

    /// <summary>
    /// Only the page on screen refreshes. The others catch up when they're opened. A refresh every so often leaves a
    /// page alone while a change from it is still on its way, so it can't show what was there before the change.
    /// </summary>
    private void RefreshPage(bool every = false)
    {
        switch (_page)
        {
            case Page.ToDo:
                if (!every || !TodayPage.Work.IsBusy)
                {
                    TodayPage.Work.Show(() => TodayPage.Refresh(_tonight));
                }

                break;
            case Page.Notes:
                if (!every || !NotesPage.Work.IsBusy)
                {
                    NotesPage.Work.Show(() => NotesPage.Refresh(), () => NotesPage.Refresh());
                }

                break;
            case Page.Budget:
                if (!every || !MoneyPage.Work.IsBusy)
                {
                    MoneyPage.Work.Show(MoneyPage.Refresh);
                }

                break;
            case Page.Settings:
                SettingsPage.Refresh();
                break;
            default:
                HistoryPage.Refresh();
                break;
        }
    }

    private void OnPageChosen(object sender, SelectionChangedEventArgs e)
    {
        if (_choosingPage || Menu.SelectedIndex < 0)
        {
            return;
        }

        var page = (Page)Menu.SelectedIndex;
        ShowPage(page);
        _database.SetPreference(PageKey, page.ToString());
        if (page == Page.ToDo)
        {
            BringBackHeld();
        }
    }

    private void OnFindClicked(object sender, RoutedEventArgs e) => OpenFind();

    /// <summary>Find anywhere takes the page's place until a result is picked or it's closed. Half-typed text is saved first.</summary>
    private void OpenFind()
    {
        SaveDrafts();
        foreach (var page in new FrameworkElement[] { TodayPage, NotesPage, MoneyPage, HistoryPage, SettingsPage })
        {
            page.Visibility = Visibility.Collapsed;
        }

        FindPage.Visibility = Visibility.Visible;
        FindPage.Search();
        FindPage.FocusSearch();
    }

    /// <summary>Back to the page that was open.</summary>
    private void CloseFind() => ShowPage(_page);

    /// <summary>Opens the page a result is on and shows it there.</summary>
    private void ShowFound(FoundThing thing)
    {
        switch (thing)
        {
            case { Item: { } item }:
                ShowPage(Page.ToDo);
                TodayPage.Reveal(item);
                break;
            case { Entry: { } entry }:
                ShowPage(Page.Notes);
                NotesPage.RevealListEntry(entry);
                break;
            case { NoteId: { } noteId }:
                ShowPage(Page.Notes);
                NotesPage.ShowNote(noteId);
                break;
            case { History: { } history }:
                ShowPage(Page.History);
                HistoryPage.Reveal(history);
                break;
            case { Purchase: { } purchase }:
                ShowPage(Page.Budget);
                MoneyPage.RevealPurchase(purchase);
                break;
        }
    }

    /// <summary>Ctrl+N adds the page's usual kind (a task, an expense, or a note), Ctrl+F is its search, Ctrl+K finds anywhere, and Ctrl+1 to Ctrl+5 pick a page.</summary>
    private void OnShortcutKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && HandleShortcut(e.Key == Key.System ? e.SystemKey : e.Key))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// A Ctrl shortcut, and whether it was used. In a note Ctrl+K is the link box, as in Word and Google Docs, so it's
    /// left to the note there; Find anywhere is still on the side menu.
    /// </summary>
    private bool HandleShortcut(Key key)
    {
        var number = key switch
        {
            >= Key.D1 and <= Key.D9 => key - Key.D1,
            >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1,
            _ => -1,
        };

        if (number >= 0)
        {
            if (number < Menu.Items.Count)
            {
                Menu.SelectedIndex = number;
                return true;
            }

            return false;
        }

        if (key == Key.K)
        {
            if (Keyboard.FocusedElement is DependencyObject focused && InNoteEditor(focused))
            {
                return false;
            }

            OpenFind();
        }
        else if (key == Key.N)
        {
            if (FindPage.IsVisible)
            {
                CloseFind();
            }

            if (_page == Page.ToDo)
            {
                TodayPage.AddByShortcut();
            }
            else
            {
                FindColumn()?.AddByShortcut();
            }
        }
        else if (key == Key.F)
        {
            if (FindPage.IsVisible)
            {
                FindPage.FocusSearch();
            }
            else if (_page == Page.ToDo)
            {
                TodayPage.FocusSearch();
            }
            else if (FindColumn() is { } column)
            {
                column.FocusSearch();
            }
            else
            {
                FindSearchBox()?.Focus();
            }
        }
        else
        {
            return false;
        }

        return true;
    }

    /// <summary>Whether the focus is in a note's text, formatted or as Markdown.</summary>
    private static bool InNoteEditor(DependencyObject focused)
    {
        for (DependencyObject? at = focused; at is not null; at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
        {
            if (at is MarkdownBox or MarkdownSourceBox)
            {
                return true;
            }
        }

        return false;
    }

    private FrameworkElement CurrentPage => _page switch
    {
        Page.ToDo => TodayPage,
        Page.Notes => NotesPage,
        Page.Budget => MoneyPage,
        Page.History => HistoryPage,
        _ => SettingsPage,
    };

    /// <summary>Pages built on the shared column all answer the shortcuts the same way, found in what's on screen.</summary>
    private NestedColumn? FindColumn() => Descendants(CurrentPage).OfType<NestedColumn>().FirstOrDefault(column => column.IsVisible);

    /// <summary>For a page with its own search box and no shared column.</summary>
    private TextBox? FindSearchBox() => Descendants(CurrentPage).OfType<TextBox>().FirstOrDefault(box =>
        box.IsVisible && (AutomationProperties.GetName(box).StartsWith("Search", StringComparison.OrdinalIgnoreCase)
            || (box.Tag as string)?.StartsWith("Search", StringComparison.OrdinalIgnoreCase) == true));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var deeper in Descendants(child))
            {
                yield return deeper;
            }
        }
    }

    /// <summary>Opens with the start of a note typed in, ready to finish.</summary>
    public void StartTyping(string text)
    {
        ShowList();
        Input.Text = text;
        Input.CaretIndex = text.Length;
        Input.Focus();
    }

    /// <summary>The tray's Save my spot: To do's own form, over the window.</summary>
    public void OpenSaveSpot()
    {
        ShowList();
        TodayPage.OpenSaveSpot();
    }

    /// <summary>A reply with one link under it, such as Open for something kept on this PC.</summary>
    public void ShowReply(string text, string linkLabel, Action open) =>
        AddToChat(new ChatRow(text, ChatKind.Reply, [new ChatLink(linkLabel, open)]));

    /// <summary>A message as it was sent, with "Sorting..." under it until its reply comes.</summary>
    public void ShowPrompt(string text)
    {
        AddToChat(_choice is { } choice ? new ChatRow($"You chose {choice}", ChatKind.Choice) : new ChatRow(text.Trim(), ChatKind.Prompt));

        // Make it smaller and Break down wait on Claude, so they say what they're doing, on the card too.
        var working = Activation.WorkingLine(text, _session is not null);
        if (working == Activation.SmallerWorking)
        {
            SessionAction.Text = working;
        }

        AddToChat(new ChatRow(working ?? WorkingText, ChatKind.Working));
    }

    public const string WorkingText = "Sorting...";

    public void ShowReply(string text) => AddToChat(new ChatRow(text, ChatKind.Reply));

    /// <summary>
    /// A reply with a link under it for each thing it made, which opens that thing where it went: To do, the Shopping
    /// list, Budget's purchases, or the note. Several things on Shopping or in Budget share one link.
    /// </summary>
    public void ShowReply(string text, IReadOnlyList<StoredItem> made) =>
        AddToChat(new ChatRow(text, ChatKind.Reply, ReplyLinks(made).Select(item => new ChatLink(LinkLabel(item), () => Reveal(item))).ToList()));

    /// <summary>One link per thing made, except Shopping and Budget, which each get one for all of theirs.</summary>
    public static IReadOnlyList<StoredItem> ReplyLinks(IEnumerable<StoredItem> made) =>
        made.GroupBy(item => item.Kind is ItemKind.Shopping or ItemKind.Expense ? item.Kind.ToString() : $"item:{ItemRef.Format(item)}")
            .Select(group => group.First())
            .ToList();

    public static string LinkLabel(StoredItem item)
    {
        switch (item.Kind)
        {
            case ItemKind.Shopping:
                return "Show the Shopping list";
            case ItemKind.Expense:
                return "Show Purchases in Budget";
            case ItemKind.Note:
                return $"Open {item.Title} in Notes";
        }

        var when = item.DueAt is { } at
            ? at.LocalDateTime.ToString("ddd h:mm tt", CultureInfo.CurrentCulture)
            : item.DueDate?.ToString("ddd MMM d", CultureInfo.CurrentCulture);
        return when is null ? $"Show {item.Title} on To do" : $"Show {item.Title}, {when}";
    }

    private void Reveal(StoredItem item)
    {
        switch (item.Kind)
        {
            case ItemKind.Shopping:
                Open(Page.Notes);
                ShowPage(Page.Notes);
                NotesPage.OpenList(WaypointDatabase.ShoppingList);
                break;
            case ItemKind.Expense:
                Open(Page.Budget);
                ShowPage(Page.Budget);
                MoneyPage.ShowPurchases();
                break;
            case ItemKind.Note:
                Open(Page.Notes);
                ShowPage(Page.Notes);
                NotesPage.ShowNote(item.Id);
                break;
            default:
                Open(Page.ToDo);
                ShowPage(Page.ToDo);
                TodayPage.Reveal(item);
                break;
        }
    }

    private void OnStatusOpenClicked(object sender, RoutedEventArgs e) => _openKept?.Invoke();

    private void OnStatusDismissClicked(object sender, RoutedEventArgs e) => _dismissKept?.Invoke();

    private void OnChatLinkClicked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ChatLink link)
        {
            link.Open();
        }
    }

    private void AddToChat(ChatRow row)
    {
        // A reply takes the place of the oldest "Sorting...", since replies come in the order messages were sent.
        if (row.Kind == ChatKind.Reply && _chat.FirstOrDefault(shown => shown.Kind == ChatKind.Working) is { } working)
        {
            _chat.Remove(working);
        }

        _chat.Add(row);
        ChatHint.Visibility = Visibility.Collapsed;
        ShowThingsToTry();
        ChatScroll.UpdateLayout();
        ChatScroll.ScrollToEnd();
    }

    /// <param name="openKept">Shows what the server couldn't save, when there's any, through an Open link beside the status.</param>
    public void Refresh(int pending, string? sortError, string? calendarError, string? nudgeError, string? discordError, ClassOccurrence? nextClass, BedtimeStatus? tonight = null, string? serverProblem = null, Action? openKept = null, Action? dismissKept = null)
    {
        _openKept = openKept;
        _dismissKept = dismissKept;
        StatusOpen.Visibility = openKept is null ? Visibility.Collapsed : Visibility.Visible;
        StatusDismiss.Visibility = dismissKept is null ? Visibility.Collapsed : Visibility.Visible;
        TodayLabel.Text = DateTime.Now.ToString("dddd, MMMM d", CultureInfo.InvariantCulture);
        ShowNextClass(nextClass);
        _tonight = tonight;
        RefreshPage(every: true);
        ShowSession();
        ShowPayProposal();

        ShowSortNotice(pending, sortError is not null);

        var messages = new[]
        {
            serverProblem,
            // Plain words only. What went wrong is under Connections on Settings, and in errors.log.
            calendarError is not null ? "Couldn't update the calendar, so it may be out of date. It tries again every hour, and Settings shows more under Connections." : null,
            nudgeError is not null ? "Couldn't plan assignment reminders yet. Waypoint will try again on its own." : null,
            discordError is not null ? "Couldn't reach Discord or email. Settings shows which one under Connections." : null,
        };

        Status.Text = string.Join("\n", messages.Where(message => message is not null));
        StatusBar.Visibility = Status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Runs when Try again is chosen under a failed sort.</summary>
    public Action? RetrySorting { get; set; }

    /// <summary>
    /// Said once, in Ask, above the box: how many messages are waiting, or that sorting failed and they're saved.
    /// Never the error itself, which goes to errors.log. Closing it keeps it away until something about it changes.
    /// </summary>
    private void ShowSortNotice(int pending, bool failed)
    {
        // Once sorting failed or nothing is waiting, no reply is on its way, so "Sorting..." goes.
        if (failed || pending == 0)
        {
            ClearWorking();
        }

        // While "Sorting..." shows under a message, it says the same as the waiting count would.
        var working = _chat.Any(row => row.Kind == ChatKind.Working);
        var text = failed || !working ? SortStatus.Notice(pending, failed) : null;
        var key = $"{failed}|{pending}";
        if (key != _dismissedNotice)
        {
            _dismissedNotice = null;
        }

        SortNoticeText.Text = text ?? "";
        SortRetry.Visibility = failed && RetrySorting is not null ? Visibility.Visible : Visibility.Collapsed;
        SortNotice.Visibility = text is not null && _dismissedNotice is null ? Visibility.Visible : Visibility.Collapsed;
        _noticeKey = key;
    }

    private void ClearWorking()
    {
        foreach (var row in _chat.Where(row => row.Kind == ChatKind.Working).ToList())
        {
            _chat.Remove(row);
        }
    }

    private void OnSortNoticeClosed(object sender, RoutedEventArgs e)
    {
        _dismissedNotice = _noticeKey;
        SortNotice.Visibility = Visibility.Collapsed;
    }

    private void OnSortRetryClicked(object sender, RoutedEventArgs e)
    {
        SortNotice.Visibility = Visibility.Collapsed;
        RetrySorting?.Invoke();
    }

    /// <summary>Stays up until answered, even across a restart, so nothing changes without a yes.</summary>
    private void ShowPayProposal()
    {
        var proposal = _sessions.DescribePayProposal(DateTime.Now);
        PayCard.Visibility = proposal is null ? Visibility.Collapsed : Visibility.Visible;
        if (proposal is not null)
        {
            PayCardText.Text = proposal;
        }
    }

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (hasFiles)
        {
            AskColumn.SetResourceReference(Border.BackgroundProperty, "Surface");
        }
    }

    private void OnFileDragLeave(object sender, DragEventArgs e) => AskColumn.Background = Brushes.Transparent;

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        AskColumn.Background = Brushes.Transparent;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            _filesDropped(paths);
        }
    }

    private void ShowSession()
    {
        // Its buttons are typed words, so in server mode they go to the server like anything typed in Ask.
        var before = _session;
        _session = _sessions.GetActiveSession(DateTime.Now);
        if (_session?.Action != before?.Action || _session?.Id != before?.Id)
        {
            SessionFeedback.Reset();
        }

        // Did it that ends the session is a natural point to bring back something put off earlier. A next step means it isn't over.
        if (_didItOn is { } pressedOn)
        {
            if (_session is null)
            {
                _didItOn = null;
                BringBackHeld();
            }
            else if (_session.Action != pressedOn)
            {
                _didItOn = null;
            }
        }

        RecheckHeld();
        SessionCard.Visibility = _session is null ? Visibility.Collapsed : Visibility.Visible;
        if (_session is { } session)
        {
            SessionTask.Text = session.TaskTitle;
            SessionAction.Text = session.Action;
            TrialButton.Visibility = session.CanTrial ? Visibility.Visible : Visibility.Collapsed;
            OpenWorkButton.Visibility = SafeResource.Check(session.Resource) is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>The card's buttons are typed words underneath, but Ask shows them as the choice made, not as typing.</summary>
    private void OnSessionWordClicked(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        _didItOn = (string)button.Tag == "did it" ? _session?.Action : null;
        _choice = button.Content as string;
        try
        {
            _submitted((string)button.Tag);
        }
        finally
        {
            _choice = null;
        }
    }

    /// <summary>Ends the session through the same call as "cancel" in Ask, without going through sorting.</summary>
    private void OnSessionEndClicked(object sender, RoutedEventArgs e)
    {
        _didItOn = null;
        AddToChat(new ChatRow("You chose End", ChatKind.Choice));
        ShowReply(_sessions.CancelSession(DateTime.Now).Text);
        OnDataChanged();
    }

    /// <summary>
    /// Takes the one item held from earlier, if there is one and none is showing, and shows it above Ask with Start and
    /// Not now. Taking it is what drops the hold, so whatever's chosen, it doesn't come back.
    /// </summary>
    public void BringBackHeld()
    {
        // While its own reminder card is still up, the card already says it, so it isn't shown twice.
        if (_afterThis is null && _sessions.TakeAfterThis(DateTime.Now) is { } held && CardUpFor?.Invoke(held) != true)
        {
            _afterThis = held;
            AfterThisLine.Text = AfterThis.Line(held);
            AfterThisFeedback.Reset();
        }

        AfterThisCard.Visibility = _afterThis is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Whether an item's reminder card is up on this PC. Set by the app, which shows those cards.</summary>
    public Func<StoredItem, bool>? CardUpFor { get; set; }

    private void PutAwayHeld()
    {
        _afterThis = null;
        AfterThisCard.Visibility = Visibility.Collapsed;
    }

    /// <summary>The held item is still open, so Start can't begin on something already finished, deleted, or started.</summary>
    private bool HeldStillOpen() =>
        _afterThis is { } held
        && _toDo.GetOpenItems().Any(item => item.Source == held.Source && item.Id == held.Id)
        && _session?.TaskRef != ItemRef.Format(held);

    /// <summary>Puts the card away once its item was finished, deleted, or started some other way.</summary>
    private void RecheckHeld()
    {
        if (_afterThis is not null && !HeldStillOpen())
        {
            PutAwayHeld();
        }
    }

    /// <summary>The same Start as a reminder's, so its own steps or saved spot come first.</summary>
    private void OnAfterThisStartClicked(object sender, RoutedEventArgs e)
    {
        if (_afterThis is not { } held)
        {
            return;
        }

        var open = HeldStillOpen();
        PutAwayHeld();
        if (!open)
        {
            return;
        }

        AddToChat(new ChatRow("You chose Start", ChatKind.Choice));
        ShowReply(_toDo.StartActivation(ItemRef.Format(held), Activation.ReminderMethod, DateTime.Now).Text);
        OnDataChanged();
    }

    /// <summary>It stays on the list. The hold is already gone, so nothing brings it back.</summary>
    private void OnAfterThisNotNowClicked(object sender, RoutedEventArgs e) => PutAwayHeld();

    /// <summary>Checked again here, since the file can change after it was attached.</summary>
    private void OnOpenWorkClicked(object sender, RoutedEventArgs e)
    {
        if (SafeResource.Check(_session?.Resource) is not { } target)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException)
        {
            ShowReply("Windows couldn't open that. It may not have an app for this kind of file.");
        }
    }

    private void ShowNextClass(ClassOccurrence? nextClass)
    {
        // The recording indicator takes the space while it's up.
        NextClassPanel.Visibility = nextClass is null || RecordingBar.IsVisible ? Visibility.Collapsed : Visibility.Visible;
        if (nextClass is null)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var meeting = nextClass.Meeting;
        var day = ItemGroups.DayLabel(DateOnly.FromDateTime(nextClass.Start), today);
        var leaveAt = nextClass.Start.AddMinutes(-meeting.TravelMinutes);

        NextClassName.Text = $"{meeting.Course}, {day} {Time(nextClass.Start)}";
        NextClassDetails.Text = $"{meeting.Room}  ·  Leave by {Time(leaveAt)}";
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowThingsToTry();
    }

    /// <summary>What the empty Ask column offers to try. Clicking one fills the box, so it can be sent or changed.</summary>
    public static readonly IReadOnlyList<string> AskExamples =
    [
        "What do I do now?",
        "I can't start",
        "paper due friday",
        "I finished the paper",
        "spent $14 at Blue Plate",
    ];

    private void ShowThingsToTry() =>
        ThingsToTry.Visibility = _chat.Count > 0 && Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnExampleClicked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is string example)
        {
            FillInput(example);
        }
    }

    private void OnThingsToTryClicked(object sender, RoutedEventArgs e) =>
        RowMenu.Open(ThingsToTry, System.Windows.Controls.Primitives.PlacementMode.Top, AskExamples.Select(example => new RowMenuEntry(example, () => FillInput(example))), () => { });

    private void FillInput(string text)
    {
        Input.Text = text;
        Input.CaretIndex = text.Length;
        Input.Focus();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            var text = Input.Text;
            Input.Clear();
            _submitted(text);
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        SaveDrafts();
        SavePlace();
        Hide();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        SaveDrafts();
        SavePlace();
        Hide();
    }

    /// <summary>Remembers where the window was and how big, so it opens there again after Waypoint restarts.</summary>
    public void SavePlace()
    {
        if (!IsLoaded)
        {
            return;
        }

        var place = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (place.IsEmpty || place.Width <= 0 || place.Height <= 0)
        {
            return;
        }

        var text = string.Join(',', new[] { place.Left, place.Top, place.Width, place.Height }.Select(value => Math.Round(value).ToString(CultureInfo.InvariantCulture)));
        _database.SetPreference(PlaceKey, WindowState == WindowState.Maximized ? text + ",max" : text);
    }

    /// <summary>
    /// Opens where it was last time, unless that's no longer on any screen (a monitor unplugged), in which case
    /// it stays centered. The title strip has to be on screen, so it can always be dragged back.
    /// </summary>
    private void RestorePlace()
    {
        var parts = _database.GetPreference(PlaceKey)?.Split(',') ?? [];
        if (parts.Length < 4 || !parts.Take(4).All(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            return;
        }

        var values = parts.Take(4).Select(part => double.Parse(part, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
        var place = new Rect(values[0], values[1], Math.Max(values[2], MinWidth), Math.Max(values[3], MinHeight));
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var title = new Rect(place.Left, place.Top, place.Width, 40);
        title.Intersect(screen);
        if (title.IsEmpty || title.Width < 100)
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = place.Left;
        Top = place.Top;
        Width = place.Width;
        Height = place.Height;
        if (parts.Length > 4 && parts[4] == "max")
        {
            WindowState = WindowState.Maximized;
        }
    }

    private static string Time(DateTime time) => time.ToString("h:mm tt", CultureInfo.CurrentCulture);

    // The recording indicator

    /// <summary>Runs when Stop is chosen in the recording indicator.</summary>
    public Action? StopRecording { get; set; }

    /// <summary>Runs when Mark is chosen, with the line typed beside it or null.</summary>
    public Action<string?>? MarkRecording { get; set; }

    /// <summary>Shows the indicator, or brings it up to date. A warning shows in place of the note line while it lasts.</summary>
    public void ShowRecording(TimeSpan elapsed, float level, string? warning, string note)
    {
        RecordingBar.Visibility = Visibility.Visible;
        NextClassPanel.Visibility = Visibility.Collapsed;
        RecordingTime.Text = $"Recording {Waypoint.Core.Recording.Recorder.Clock(elapsed)}";
        RecordingLevel.Value = Math.Clamp(Math.Sqrt(level), 0, 1);
        RecordingNote.Text = warning ?? note;
        RecordingNote.ToolTip = RecordingNote.Text;
        RecordingNote.SetResourceReference(TextBlock.ForegroundProperty, warning is null ? "SecondaryText" : "Urgent");
    }

    public void HideRecording()
    {
        RecordingBar.Visibility = Visibility.Collapsed;
        MarkLine.Clear();
    }

    private void OnMarkClicked(object sender, RoutedEventArgs e) => MarkTyped();

    private void OnStopRecordingClicked(object sender, RoutedEventArgs e) => StopRecording?.Invoke();

    private void OnMarkLineKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            MarkTyped();
        }
    }

    private void MarkTyped()
    {
        var line = MarkLine.Text;
        MarkLine.Clear();
        MarkRecording?.Invoke(string.IsNullOrWhiteSpace(line) ? null : line);
    }

    /// <summary>A link under a reply.</summary>
    private sealed record ChatLink(string Label, Action Open)
    {
        /// <summary>What a screen reader says for the link, rather than the record's fields.</summary>
        public override string ToString() => Label;
    }

    private enum ChatKind
    {
        Prompt,
        Reply,

        /// <summary>"Sorting..." under a message until its reply comes.</summary>
        Working,

        /// <summary>A button pressed on a card, said quietly rather than as if it were typed.</summary>
        Choice,
    }

    private sealed class ChatRow(string text, ChatKind kind, IReadOnlyList<ChatLink>? links = null)
    {
        public string Text => text;

        public ChatKind Kind => kind;

        public IReadOnlyList<ChatLink> Links => links ?? [];

        public Visibility LinksVisibility => links is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;

        public HorizontalAlignment Alignment => kind is ChatKind.Prompt or ChatKind.Choice ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        public Thickness Inset => kind == ChatKind.Prompt ? new Thickness(11, 8, 11, 8) : new Thickness(0, 2, 0, 2);

        public Brush Fill => kind == ChatKind.Prompt ? (Brush)Application.Current.Resources["Surface"] : Brushes.Transparent;

        private bool Quiet => kind is ChatKind.Working or ChatKind.Choice;

        public double TextSize => Quiet ? 12 : 13;

        public Brush TextBrush => (Brush)Application.Current.Resources[Quiet ? "SecondaryText" : "PrimaryText"];

        /// <summary>What a screen reader says for the row, rather than the class name.</summary>
        public override string ToString() => Text;
    }
}
