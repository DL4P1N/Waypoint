using System;
using System.Collections.Generic;
using System.ComponentModel;
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

namespace Waypoint.App;

/// <summary>
/// One list, edited by hand. It sits on the Notes page, whose column picks the list and passes its search in, so
/// this only shows the open list: its title, Start over, the list's options, the undo bar, the entries, and Add item.
/// Rows only rebuild when something changed and nobody is working in them, and an entry being edited changes in
/// place, so a refresh can't take the text or the focus away.
/// In server mode the lists are on the server. While it's away the page shows what it last had, and a change says
/// it can't be made yet and leaves what was typed where it was.
/// </summary>
public partial class ListsView : UserControl
{
    private readonly RefreshGate _entriesGate = new();
    private IListsData _lists = null!;
    private string _selected = WaypointDatabase.ShoppingList;
    private string _search = "";
    private EntryRow? _editing;

    public ListsView() => InitializeComponent();

    /// <summary>Something changed the lists' names, order, or counts, so the column that shows them should catch up.</summary>
    public event Action? ListsChanged;

    /// <summary>This page opened a different list by itself, such as Shopping after the open list was deleted, or a renamed list under its new name.</summary>
    public event Action<string>? ListOpened;

    public void Connect(WaypointDatabase database) => Connect(new LocalListsData(database));

    public void Connect(IListsData lists) => _lists = lists;

    /// <summary>Every list, Shopping first, for the column that picks one.</summary>
    public IReadOnlyList<ListOverview> GetLists() => _lists.GetListOverview();

    /// <summary>The list on show, or the one that shows when this page is next opened.</summary>
    public string Selected => _selected;

    /// <summary>
    /// Opens a list with the column's search. Moving to another list finishes an edit or rename first, the way
    /// picking another folder saves a note.
    /// </summary>
    public void Open(string name, string search)
    {
        var changed = name != _selected || search != _search;
        if (name != _selected)
        {
            Close();
        }

        _selected = name;
        _search = search;
        Refresh(force: changed);
    }

    /// <summary>The column's search changed while this list is open. Only entries with those words show.</summary>
    public void SetSearch(string search)
    {
        _search = search;
        ShowEntries(_lists.GetListOverview(), false, force: true);
    }

    /// <summary>Finishes anything half done and drops a message with no Undo, for when the page moves to notes or another list.</summary>
    public void Close()
    {
        if (_editing is { } editing)
        {
            Commit(editing);
        }

        FinishRename();
        Bar.ClearPlain();
    }

    /// <summary>For Find anywhere: opens the entry's list with no search hiding it, and puts focus on the entry.</summary>
    public void Reveal(ListEntry entry)
    {
        if (_editing is { } editing)
        {
            Commit(editing);
        }

        FinishRename();
        _selected = entry.List;
        _search = "";
        Refresh(force: true);

        Dispatcher.InvokeAsync(() =>
        {
            Entries.UpdateLayout();
            var row = Entries.Items.OfType<EntryRow>().FirstOrDefault(found => found.Entry.Id == entry.Id);
            if (row is not null && Entries.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container)
            {
                container.BringIntoView();
                FindBox(container)?.Focus();
            }
        }, DispatcherPriority.Loaded);
    }

    private static CheckBox? FindBox(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if ((child as CheckBox ?? FindBox(child)) is { } box)
            {
                return box;
            }
        }

        return null;
    }

    /// <summary>Puts the keyboard in Add item, for right after a new list opens. It waits for the page to be laid out.</summary>
    public void FocusAddItem() => Dispatcher.BeginInvoke(() => AddItem.Focus(), DispatcherPriority.Input);

    public void Refresh(bool force = false)
    {
        if (force)
        {
            _entriesGate.Invalidate();
        }

        ShowEntries(_lists.GetListOverview(), !force && (_editing is not null || Entries.IsKeyboardFocusWithin));
    }

    /// <summary>Finishes anything half done, for when the page or the window goes away.</summary>
    public void Leave()
    {
        if (_editing is { } row)
        {
            Commit(row);
        }

        FinishRename();
        Bar.Clear();
    }

    /// <summary>After this page changed something: the entries redraw, and so does the column, since counts and names may have moved.</summary>
    private void Redraw()
    {
        Refresh(force: true);
        ListsChanged?.Invoke();
    }

    /// <summary>Opens another list from here and tells the column, so it picks the same one.</summary>
    private void Pick(string name)
    {
        _selected = name;
        ListOpened?.Invoke(name);
    }

    /// <summary>The open list's entries. A search shows only the entries with those words, like search on the other pages.</summary>
    private void ShowEntries(IReadOnlyList<ListOverview> lists, bool busy, bool force = false)
    {
        if (force)
        {
            _entriesGate.Invalidate();
        }

        var search = _search.Trim();
        var list = lists.FirstOrDefault(found => found.Name == _selected);
        var entries = (list?.Entries ?? []).Where(entry => search.Length == 0 || entry.Text.Contains(search, StringComparison.CurrentCultureIgnoreCase)).ToList();
        // Shopping drops what's checked, so only the other lists can be run again.
        StartOver.Visibility = _selected != WaypointDatabase.ShoppingList && (list?.Entries ?? []).Any(entry => entry.Done) ? Visibility.Visible : Visibility.Collapsed;
        var signature = string.Join('\n', entries.Select(entry => $"{entry.Id}\t{entry.Text}\t{entry.Done}").Prepend(search).Prepend(_selected));
        if (!_entriesGate.ShouldRebuild(signature, busy))
        {
            return;
        }

        ListTitle.Text = _selected;
        ListActions.Visibility = _selected == WaypointDatabase.ShoppingList ? Visibility.Collapsed : Visibility.Visible;
        Entries.ItemsSource = entries.Select(entry => new EntryRow(entry)).ToList();
        Empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Text = search.Length > 0 ? "Nothing matches that." : EmptyText(_selected);
    }

    /// <summary>What an empty list says: where to type, and the same thing said to Ask.</summary>
    internal static string EmptyText(string list) =>
        $"Nothing on this list yet. Type below to add something, or say \"add {(list == WaypointDatabase.ShoppingList ? "milk" : "something")} to {list.ToLowerInvariant()}\" in Ask.";

    /// <summary>
    /// New list from the Notes page's +. Gives the name it was made with, or says why it wasn't, such as a list
    /// that already has that name or the server being away.
    /// </summary>
    public (string? Name, string? Problem) MakeList(string name)
    {
        if (Try(() => _lists.CreateList(name, DateTime.Now), out var made) is { } away)
        {
            return (null, away);
        }

        if (made.Problem is { } problem)
        {
            return (null, problem);
        }

        // The Notes page picks the new list in its column and redraws it, so only the entries redraw here.
        Close();
        Bar.Clear();
        _selected = made.Name;
        _search = "";
        Refresh(force: true);
        return (made.Name, null);
    }

    /// <summary>Saves the lists' new order from the column, which already shows it. Gives why it couldn't, such as the server being away.</summary>
    public string? OrderLists(IReadOnlyList<string> names)
    {
        var problem = Try(() =>
        {
            _lists.OrderLists(names);
            return true;
        }, out _);
        if (problem is not null)
        {
            // The column goes back to the order that was saved.
            Dispatcher.BeginInvoke(Redraw, DispatcherPriority.Background);
        }

        return problem;
    }

    /// <summary>Rename from the column's right-click menu or F2, as on the other pages. Gives why it didn't happen, for the column to say.</summary>
    public string? RenameList(string name, string newName)
    {
        if (Try(() => _lists.RenameList(name, newName, DateTime.Now), out var renamed) is { } away)
        {
            return away;
        }

        if (renamed.Problem is { } problem)
        {
            return problem;
        }

        if (_selected == name)
        {
            Pick(renamed.Name);
        }

        Redraw();
        return null;
    }

    private void OnAddItemKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (!Change(() => _lists.AddListEntry(_selected, AddItem.Text, DateTime.Now)))
        {
            return;
        }

        AddItem.Clear();
        Bar.Clear();
        Redraw();
    }

    private void OnEntryClicked(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        var entry = ((EntryRow)box.DataContext).Entry;
        var done = box.IsChecked == true;

        // Rows fill their boxes from the saved state, and putting a box back isn't a change either.
        if (done == entry.Done)
        {
            return;
        }

        if (!Change(() => _lists.SetEntryDone(entry, done, DateTime.Now)))
        {
            box.IsChecked = entry.Done;
            return;
        }

        // A checked shopping entry leaves the list, so it needs a way back.
        if (done && entry.List == WaypointDatabase.ShoppingList)
        {
            Bar.Show($"Checked off {entry.Text}", () =>
            {
                if (Change(() => _lists.SetEntryDone(entry, false, DateTime.Now)))
                {
                    Redraw();
                }
            });
        }

        Redraw();
    }

    private void OnDeleteEntryClicked(object sender, RoutedEventArgs e) =>
        DeleteEntry(((EntryRow)((FrameworkElement)sender).DataContext).Entry);

    private void DeleteEntry(ListEntry entry)
    {
        if (!Change(() => _lists.DeleteEntry(entry, DateTime.Now)))
        {
            return;
        }

        Bar.Show($"Deleted {entry.Text}", () =>
        {
            if (Change(() => _lists.RestoreEntry(entry, DateTime.Now)))
            {
                Redraw();
            }
        });
        Redraw();
        AddItem.Focus();
    }

    private void OnEntryTextPressed(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            BeginEdit((EntryRow)((FrameworkElement)sender).DataContext);
        }
    }

    private void OnEntryKey(object sender, KeyEventArgs e)
    {
        var row = (EntryRow)((FrameworkElement)sender).DataContext;
        if (row.Editing)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Commit(row);
                AddItem.Focus();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                row.Editing = false;
                _editing = null;
                AddItem.Focus();
            }

            return;
        }

        if (e.OriginalSource is CheckBox && e.Key == Key.Enter)
        {
            e.Handled = true;
            BeginEdit(row);
        }
        else if (e.OriginalSource is CheckBox && e.Key == Key.Delete)
        {
            e.Handled = true;
            DeleteEntry(row.Entry);
        }
    }

    private void BeginEdit(EntryRow row)
    {
        if (_editing is { } other && !ReferenceEquals(other, row))
        {
            Commit(other);
        }

        row.Draft = row.Text;
        row.Editing = true;
        _editing = row;
    }

    /// <summary>Focus can only land once the box has been laid out.</summary>
    private void OnEditShown(object sender, DependencyPropertyChangedEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.IsVisible)
        {
            box.Dispatcher.BeginInvoke(() =>
            {
                box.Focus();
                box.SelectAll();
            }, DispatcherPriority.Input);
        }
    }

    private void OnEditLeft(object sender, KeyboardFocusChangedEventArgs e) =>
        Commit((EntryRow)((FrameworkElement)sender).DataContext);

    /// <summary>
    /// Changes the row in place instead of rebuilding, since this can run in the middle of a click
    /// on the row beside it. A refresh picks up the stored text afterwards.
    /// </summary>
    private void Commit(EntryRow row)
    {
        if (!row.Editing)
        {
            return;
        }

        row.Editing = false;
        _editing = null;
        var text = row.Draft.Trim();
        if (text.Length > 0 && text != row.Text && Change(() => _lists.EditEntry(row.Entry, text)))
        {
            row.Text = text;
        }
    }

    /// <summary>Start over unchecks the whole list, the same as typing "start my morning routine", and says where to begin.</summary>
    private void OnStartOverClicked(object sender, RoutedEventArgs e)
    {
        if (_editing is { } editing)
        {
            Commit(editing);
        }

        var name = _selected;
        if (!Change(() => _lists.StartList(name), out var said))
        {
            return;
        }

        Bar.Show(said);
        Redraw();
    }

    /// <summary>The ... beside the title: what's done to the list as a whole.</summary>
    private void OnListActionsClicked(object sender, RoutedEventArgs e) =>
        RowMenu.Open(ListActions, System.Windows.Controls.Primitives.PlacementMode.Bottom, ListMenu(_selected, inColumn: false), () => { });

    /// <summary>
    /// The list's own actions, for the ... button and the list's right-click in the Notes column. The column already
    /// offers Rename itself, so it only gets the rest. A list deleted from the column while notes are showing says so,
    /// with its Undo, in <paramref name="bar"/>, the bar that can be seen.
    /// </summary>
    internal IEnumerable<RowMenuEntry> ColumnMenu(string name, UndoBar? bar) => ListMenu(name, inColumn: true, bar);

    private IEnumerable<RowMenuEntry> ListMenu(string name, bool inColumn, UndoBar? bar = null)
    {
        if (name == WaypointDatabase.ShoppingList)
        {
            yield break;
        }

        if (!inColumn)
        {
            yield return new RowMenuEntry("Rename", BeginRename);
        }

        yield return new RowMenuEntry("Delete list", () => DeleteList(name, bar ?? Bar));
    }

    private void BeginRename()
    {
        RenameBox.Text = _selected;
        RenameBox.Visibility = Visibility.Visible;
        ListTitle.Visibility = Visibility.Hidden;
        RenameBox.Dispatcher.BeginInvoke(() =>
        {
            RenameBox.Focus();
            RenameBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnRenameKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            FinishRename();
            AddItem.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HideRename();
            AddItem.Focus();
        }
    }

    private void OnRenameLeft(object sender, KeyboardFocusChangedEventArgs e) => FinishRename();

    /// <summary>
    /// Saves a rename typed over the title. This can run as focus leaves the box, in the middle of a click, so the
    /// column catches up afterwards rather than now.
    /// </summary>
    private void FinishRename()
    {
        if (RenameBox.Visibility != Visibility.Visible)
        {
            return;
        }

        var text = RenameBox.Text;
        HideRename();
        if (text.Trim() == _selected)
        {
            return;
        }

        var name = _selected;
        if (!Change(() => _lists.RenameList(name, text, DateTime.Now), out var renamed))
        {
            return;
        }

        if (renamed.Problem is { } problem)
        {
            Bar.Show(problem);
            return;
        }

        _selected = renamed.Name;
        Dispatcher.BeginInvoke(() =>
        {
            if (_selected == renamed.Name)
            {
                Pick(renamed.Name);
            }

            Redraw();
        }, DispatcherPriority.Background);
    }

    private void HideRename()
    {
        RenameBox.Visibility = Visibility.Collapsed;
        ListTitle.Visibility = Visibility.Visible;
    }

    /// <summary>Removes a list, the open one or another from the column, with Undo to bring it back and open it.</summary>
    private void DeleteList(string name, UndoBar bar)
    {
        if (_editing is { } editing)
        {
            Commit(editing);
        }

        if (!Change(() => _lists.DeleteList(name, DateTime.Now), bar))
        {
            return;
        }

        bar.Show($"Removed {name}", () =>
        {
            if (Change(() => _lists.CreateList(name, DateTime.Now), bar))
            {
                Pick(name);
                Redraw();
            }
        });
        if (_selected == name)
        {
            Pick(WaypointDatabase.ShoppingList);
        }

        Redraw();
    }

    /// <summary>
    /// Runs a change, which takes away an earlier message's Undo. If the server is away, the page says so and nothing
    /// else happens. <paramref name="bar"/> is where that's said, this page's own bar unless another is given.
    /// </summary>
    private bool Change(Action change, UndoBar? bar = null) => Change(() =>
    {
        change();
        return true;
    }, out _, bar);

    private bool Change<T>(Func<T> change, out T result, UndoBar? bar = null)
    {
        bar ??= Bar;
        try
        {
            result = change();
            bar.Changed();
            return true;
        }
        catch (ServerUnavailableException away)
        {
            result = default!;
            bar.Show(PageMessages.CantChange(away));
            return false;
        }
    }

    /// <summary>A change asked for from the column, which says itself why it didn't happen. Gives that reason, or null once it's made.</summary>
    private string? Try<T>(Func<T> change, out T result)
    {
        try
        {
            result = change();
            Bar.Changed();
            return null;
        }
        catch (ServerUnavailableException away)
        {
            result = default!;
            return PageMessages.CantChange(away);
        }
    }

    private sealed class EntryRow(ListEntry entry) : INotifyPropertyChanged
    {
        private string _text = entry.Text;
        private bool _editing;

        public event PropertyChangedEventHandler? PropertyChanged;

        public ListEntry Entry => entry;

        public string Text
        {
            get => _text;
            set
            {
                _text = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }

        public string Draft { get; set; } = entry.Text;

        public bool Done => entry.Done;

        public bool Editing
        {
            get => _editing;
            set
            {
                _editing = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Draft)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TextVisibility)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditVisibility)));
            }
        }

        public Visibility TextVisibility => _editing ? Visibility.Collapsed : Visibility.Visible;

        public Visibility EditVisibility => _editing ? Visibility.Visible : Visibility.Collapsed;

        public TextDecorationCollection? Strike => entry.Done ? TextDecorations.Strikethrough : null;

        public Brush TextBrush => (Brush)Application.Current.Resources[entry.Done ? "SecondaryText" : "PrimaryText"];

        /// <summary>What a screen reader says for the row, rather than the class name.</summary>
        public override string ToString() => Text;
    }
}
