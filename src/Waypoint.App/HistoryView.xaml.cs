using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Waypoint.Core;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Remote;

namespace Waypoint.App;

/// <summary>
/// What's been finished, canceled, reopened, or gone by. The column picks which, and its search narrows them.
/// Unchecking a row puts it back on the list. Nothing is added here, so the column has no +.
/// In server mode it's the server's history, and while the server is away a change says it can't be made yet.
/// </summary>
public partial class HistoryView : UserControl
{
    private const int Shown = 100;
    private const string ViewKey = "history_view";

    private static readonly (HistoryFilter Filter, string Label)[] Views =
    [
        (HistoryFilter.All, "All"),
        (HistoryFilter.Completed, "Done"),
        (HistoryFilter.Canceled, "Canceled"),
        (HistoryFilter.Reopened, "Reopened"),
        (HistoryFilter.Past, "Past"),
    ];

    private readonly RefreshGate _gate = new();
    private IHistoryData? _history;
    private Action _changed = null!;
    private bool _menuOpen;

    public HistoryView() => InitializeComponent();

    private PageWork _work = PageWork.Local;

    /// <summary>How the page reads and changes its data: straight away on this PC, or in order without holding the window in server mode.</summary>
    internal PageWork Work
    {
        get => _work;
        set
        {
            _work = value;
            Column.Work = value;
        }
    }

    /// <summary>Draws the rows again, at once from what was kept in server mode, then from what's read.</summary>
    private void ShowSoon() => Work.Show(() => Refresh(force: true), () => Refresh(force: false));

    /// <summary>Draws the rows again once this page's changes are done, so it never shows a moment between two of them.</summary>
    private void ShowAfterChanges() => Work.ShowAfterChanges(nameof(Refresh), () => Refresh(force: true), () => Refresh(force: false));

    public void Connect(WaypointDatabase database, Action changed) => Connect(new LocalHistoryData(database), changed);

    public void Connect(IHistoryData history, Action changed)
    {
        _history = history;
        _changed = changed;

        Column.Connect(history, ViewKey);
        Column.SetEntries(Views.Select(view => new ColumnEntry(view.Filter.ToString(), view.Label)).ToList());
        Column.SelectionChanged += _ => ShowSoon();
        Column.SearchChanged += _ => ShowSoon();
        Scroll.LostKeyboardFocus += (_, _) => Work.Show(Refresh);
    }

    public void Refresh() => Refresh(force: false);

    /// <summary>For Find anywhere: All, searched for the row's title, with the row brought into view.</summary>
    public void Reveal(HistoryEntry entry)
    {
        Column.Select(HistoryFilter.All.ToString());
        Column.SetSearch(entry.Title);
        ShowSoon();

        Dispatcher.InvokeAsync(() =>
        {
            Entries.UpdateLayout();
            var row = Entries.Items.OfType<HistoryRow>().FirstOrDefault(found => found.Entry.Id == entry.Id);
            if (row is not null && Entries.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container)
            {
                container.BringIntoView();
            }
        }, DispatcherPriority.Loaded);
    }

    private HistoryFilter Filter =>
        Enum.TryParse<HistoryFilter>(Column.SelectedKey, out var filter) ? filter : HistoryFilter.All;

    private void Refresh(bool force)
    {
        if (_history is null || (_menuOpen && !force))
        {
            return;
        }

        var filter = Filter;
        PageTitle.Text = filter == HistoryFilter.All ? "History" : Views.First(view => view.Filter == filter).Label;

        var rows = _history.SearchHistory(Column.SearchText, Shown, filter).Select(found => new HistoryRow(found.Entry, found.CanReopen)).ToList();
        // The empty line names the search, so it changes with the words even when nothing is found either way.
        var signature = $"{filter}\n{(rows.Count == 0 ? Column.SearchText.Trim() : "")}\n{string.Join('\n', rows.Select(row => $"{row.Entry.Id}\t{row.Title}\t{row.Details}\t{row.BoxVisibility}"))}";
        if (!_gate.ShouldRebuild(signature, busy: !force && Scroll.IsKeyboardFocusWithin))
        {
            return;
        }

        Entries.ItemsSource = rows;
        Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Text = EmptyText(filter, Column.SearchText);
    }

    /// <summary>What an empty view says: what would show up there, or, for a search, where else the thing might be.</summary>
    internal static string EmptyText(HistoryFilter filter, string? search)
    {
        var words = search?.Trim() ?? "";
        if (words.Length > 0)
        {
            return $"Nothing in History matches \"{words}\". Open things are on To do, and Ctrl+K looks through every page.";
        }

        return filter switch
        {
            HistoryFilter.Completed => "Things you finish show up here.",
            HistoryFilter.Canceled => "Things you cancel show up here.",
            HistoryFilter.Reopened => "Things you put back on To do show up here.",
            HistoryFilter.Past => "Events that went by without being checked off show up here.",
            _ => "Nothing here yet. Things you finish or cancel show up here.",
        };
    }

    private void OnRowUnchecked(object sender, RoutedEventArgs e) => Reopen((HistoryRow)((CheckBox)sender).DataContext);

    private void OnRowMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        var element = (FrameworkElement)sender;
        var row = (HistoryRow)element.DataContext;
        var entries = new List<RowMenuEntry>();
        if (row.CanReopen)
        {
            entries.Add(new("Reopen", () => Reopen(row)));
        }

        entries.Add(new("Delete", () => Delete(row)));
        entries.Add(new("Copy title", () => RowMenu.Copy(row.Title)));

        _menuOpen = true;
        RowMenu.Open(element, e, entries, () => _menuOpen = false);
    }

    /// <summary>Takes the item out of History, and off the list if it's still there. Undo puts both back.</summary>
    private async void Delete(HistoryRow row)
    {
        var (ok, deleted) = await Change(() => _history!.DeleteHistoryItem(row.Entry, DateTime.Now));
        if (!ok || deleted is null)
        {
            return;
        }

        Bar.Show($"Deleted {row.Title}", async () =>
        {
            var (undone, _) = await Change(() =>
            {
                _history!.UndoDelete(deleted, DateTime.Now);
                return true;
            });
            if (undone)
            {
                ShowAfterChanges();
                _changed();
            }
        });
        ShowAfterChanges();
        _changed();
    }

    private async void Reopen(HistoryRow row)
    {
        // While the server is away the rows are built again, so the box goes back to checked, since nothing was put back.
        var (ok, _) = await Change(() => _history!.ReopenEntry(row.Entry));
        if (!ok)
        {
            _gate.Invalidate();
        }

        ShowAfterChanges();
        _changed();
    }

    /// <summary>
    /// Runs a change, off the window's thread in server mode, after the page's earlier changes. If the server is away,
    /// or too slow to answer, the page says so and nothing else happens.
    /// </summary>
    private async Task<(bool Ok, T Result)> Change<T>(Func<T> change, [System.Runtime.CompilerServices.CallerMemberName] string what = "")
    {
        try
        {
            var result = await Work.Run(change, what);
            Bar.Changed();
            return (true, result);
        }
        catch (ServerUnavailableException away)
        {
            Bar.Show(PageMessages.CantChange(away));
            return (false, default!);
        }
        catch (ServerTimedOutException slow)
        {
            Bar.Show(slow.Message);
            return (false, default!);
        }
    }

    private sealed class HistoryRow(HistoryEntry entry, bool canReopen)
    {
        public HistoryEntry Entry => entry;

        public bool CanReopen => canReopen;

        public Visibility BoxVisibility => canReopen ? Visibility.Visible : Visibility.Hidden;

        /// <summary>A reopened row isn't closed, so it isn't struck through.</summary>
        public TextDecorationCollection? Strike => entry.Action == HistoryAction.Reopened ? null : TextDecorations.Strikethrough;

        public string Title => entry.Title;

        /// <summary>The box's name, so each one says which item it puts back.</summary>
        public string ReopenName => $"Put {entry.Title} back on the list";

        public string Details => $"{Label(entry)} {entry.At.ToString("MMM d", CultureInfo.InvariantCulture)}";

        /// <summary>Done, the one word the PC uses for a finished item, as on To do and History's filter.</summary>
        internal static string Label(HistoryEntry entry) => entry.Action == HistoryAction.Done ? "Done" : entry.Label;

        /// <summary>What a screen reader says for the row, rather than the class name.</summary>
        public override string ToString() => $"{Title}, {Details}";
    }
}
