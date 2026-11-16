using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Waypoint.Core;
using Waypoint.Core.Data;
using Waypoint.Core.Remote;

namespace Waypoint.App;

/// <summary>What the column's + button does.</summary>
public enum ColumnAddMode
{
    /// <summary>No + button, for pages where nothing is added.</summary>
    None,

    /// <summary>One kind of new thing, so + raises <see cref="NestedColumn.AddChosen"/> with a null key.</summary>
    Direct,

    /// <summary>Several kinds, so + drops down <see cref="NestedColumn.AddChoices"/>.</summary>
    Menu,
}

/// <summary>
/// One entry in the column. Key is stable and is what's remembered, so Label can change freely.
/// Entries sharing a MoveGroup can be reordered among themselves; a null MoveGroup pins the entry where
/// the page put it, such as All at the top. Name is the bare name offered for renaming, if Label adds a count.
/// Subtitle is a short quiet line under the label, and RuleAbove draws a thin line above an entry that isn't
/// like the ones before it, such as Saved spots under the notes folders. Depth indents an entry that sits inside
/// the one above it, such as a folder in a folder. The entries inside an entry come right after it, one deeper,
/// and move along with it.
/// </summary>
/// <remarks>Heading is a small quiet word above the entry, such as "Lists", that names the group it starts. It isn't an entry of its own.</remarks>
public sealed record ColumnEntry(string Key, string Label, string? MoveGroup = null, bool CanRename = false, string? Name = null, string? Subtitle = null, bool RuleAbove = false, int Depth = 0, string? Heading = null)
{
    public string RenameText => Name ?? Label;

    /// <summary>How far the entry sits in from the left, from its depth.</summary>
    public Thickness Indent => new(Depth * 14, 0, 0, 0);

    public Visibility SubtitleVisibility => Subtitle is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility RuleVisibility => RuleAbove ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HeadingVisibility => Heading is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>What a screen reader says for the entry, rather than the record's fields.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// A choice in the + dropdown. InColumnMenu also offers it as "New ..." on the column's right-click menu. ShortcutName
/// is what +'s hover calls it when Ctrl+N adds it, such as "waiting-on item", if the label alone wouldn't read well.
/// </summary>
public sealed record ColumnAddChoice(string Key, string Label, bool InColumnMenu = false, string? ShortcutName = null)
{
    /// <summary>What a screen reader says for the choice, rather than the record's fields.</summary>
    public override string ToString() => Label;
}

/// <summary>A rename asked for from the column. Set Problem to say why it didn't happen.</summary>
public sealed class ColumnRenameEventArgs(ColumnEntry entry, string newName) : EventArgs
{
    public ColumnEntry Entry => entry;

    public string NewName => newName;

    public string? Problem { get; set; }
}

/// <summary>
/// Something other than an entry dragged over an entry, such as a note onto a folder. Raised while it's over the
/// entry, with IsDrop false, and again when it's let go. Set Accepted when it can go there.
/// </summary>
public sealed class ColumnDropEventArgs(ColumnEntry entry, IDataObject data, bool isDrop) : EventArgs
{
    public ColumnEntry Entry => entry;

    public IDataObject Data => data;

    public bool IsDrop => isDrop;

    public bool Accepted { get; set; }
}

/// <summary>
/// The nested column every main page shares: + and search at the top, then entries to pick from.
/// The page owns the data. It hands entries in with <see cref="SetEntries"/> as often as it likes, and
/// a refresh never touches the search text or focus, never rebuilds while someone is in the list, and
/// keeps the selected entry. The selection is remembered in preferences under the page's own key.
/// </summary>
public partial class NestedColumn : UserControl
{
    private readonly ObservableCollection<ColumnEntry> _rows = [];
    private readonly RefreshGate _gate = new();
    private List<ColumnEntry> _all = [];
    private IReadOnlyList<ColumnEntry>? _pending;
    private IPreferences? _database;
    private string _preferenceKey = "";
    private string? _selectedKey;
    private bool _loading;
    private bool _menuOpen;
    private bool _dragging;
    private ColumnEntry? _pressed;
    private Point _pressedAt;
    private ColumnAddMode _addMode = ColumnAddMode.Direct;
    private string _addLabel = "New";

    public NestedColumn()
    {
        InitializeComponent();
        Entries.ItemsSource = _rows;
        AddLabel = _addLabel;
    }

    /// <summary>The page picked a different entry, by hand or because the selected one went away.</summary>
    public event Action<ColumnEntry>? SelectionChanged;

    /// <summary>The search text changed. The page filters its own content with it.</summary>
    public event Action<string>? SearchChanged;

    /// <summary>Something was chosen from + or the right-click menu. The key is the choice's, or null in <see cref="ColumnAddMode.Direct"/>.</summary>
    public event Action<string?>? AddChosen;

    /// <summary>Entries in a move group were dragged or moved with Alt+Up and Alt+Down. Gives the group and its keys in their new order.</summary>
    public event Action<string, IReadOnlyList<string>>? Reordered;

    /// <summary>The user gave an entry a new name. The page saves it, or sets a problem to show.</summary>
    public event EventHandler<ColumnRenameEventArgs>? RenameRequested;

    /// <summary>Something that isn't an entry is being dragged onto one. The page says whether it fits, and takes it on the drop.</summary>
    public event EventHandler<ColumnDropEventArgs>? DataDropped;

    public ColumnAddMode AddMode
    {
        get => _addMode;
        set
        {
            _addMode = value;
            AddButton.Visibility = value == ColumnAddMode.None ? Visibility.Collapsed : Visibility.Visible;
            ShowAddTip();
        }
    }

    /// <summary>What + says to a screen reader and on hover, such as "New list". The hover also gives its shortcut.</summary>
    public string AddLabel
    {
        get => _addLabel;
        set
        {
            _addLabel = value;
            AutomationProperties.SetName(AddButton, value);
            AutomationProperties.SetAcceleratorKey(AddButton, "Ctrl+N");
            ShowAddTip();
        }
    }

    public IReadOnlyList<ColumnAddChoice> AddChoices { get; set; } = [];

    /// <summary>
    /// The kind Ctrl+N adds straight away, such as a task on To do, so the shortcut skips the menu. + still drops
    /// down every kind. With none, Ctrl+N does what + does.
    /// </summary>
    public ColumnAddChoice? QuickAdd
    {
        get => _quickAdd;
        set
        {
            _quickAdd = value;
            ShowAddTip();
        }
    }

    private ColumnAddChoice? _quickAdd;

    /// <summary>
    /// What Ctrl+N adds instead while a given entry is picked, such as a wait while Waiting on is open, so the
    /// shortcut adds what that entry shows.
    /// </summary>
    public IReadOnlyDictionary<string, ColumnAddChoice> QuickAddIn
    {
        get => _quickAddIn;
        set
        {
            _quickAddIn = value;
            ShowAddTip();
        }
    }

    private IReadOnlyDictionary<string, ColumnAddChoice> _quickAddIn = new Dictionary<string, ColumnAddChoice>();

    private ColumnAddChoice? CurrentQuickAdd =>
        _selectedKey is { } key && _quickAddIn.TryGetValue(key, out var here) ? here : _quickAdd;

    private void ShowAddTip() =>
        AddButton.ToolTip = CurrentQuickAdd is { } quick && AddMode == ColumnAddMode.Menu
            ? $"{_addLabel} (Ctrl+N for a new {quick.ShortcutName ?? quick.Label.ToLowerInvariant()})"
            : $"{_addLabel} (Ctrl+N)";

    /// <summary>The search box's hint, such as "Search to do".</summary>
    public string SearchHint
    {
        get => (string)SearchBox.Tag;
        set
        {
            SearchBox.Tag = value;
            SearchBox.ToolTip = $"{value} (Ctrl+F)";
            AutomationProperties.SetName(SearchBox, value);
            AutomationProperties.SetAcceleratorKey(SearchBox, "Ctrl+F");
        }
    }

    /// <summary>True to hide entries whose label doesn't match the search. Pinned entries always stay.</summary>
    public bool SearchFiltersEntries { get; set; }

    /// <summary>Extra lines for an entry's right-click menu, after Rename and the moves.</summary>
    internal Func<ColumnEntry, IEnumerable<RowMenuEntry>>? ExtraMenu { get; set; }

    public string SearchText => SearchBox.Text;

    public ColumnEntry? Selected => _all.FirstOrDefault(entry => entry.Key == _selectedKey);

    public string? SelectedKey => _selectedKey;

    /// <summary>True while the keyboard is in the list of entries, rather than in search or elsewhere on the page.</summary>
    public bool ListHasFocus => Entries.IsKeyboardFocusWithin;

    /// <summary>Puts the keyboard on the selected entry, for after the page redrew the column under it.</summary>
    public void FocusSelected()
    {
        if (Selected is { } selected && Entries.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem row)
        {
            row.Focus();
        }
    }

    /// <summary>Gives the column where its preferences live and the key its selection is kept under, such as "todo_filter".</summary>
    public void Connect(IPreferences database, string preferenceKey)
    {
        _database = database;
        _preferenceKey = preferenceKey;
        _selectedKey = database.GetPreference(preferenceKey);
        ShowAddTip();
    }

    /// <summary>
    /// Shows these entries, unless they're what's already shown or someone is in the list, in which case
    /// they wait until focus leaves. Force is for right after the page itself changed something.
    /// </summary>
    public void SetEntries(IReadOnlyList<ColumnEntry> entries, bool force = false)
    {
        if (force)
        {
            _gate.Invalidate();
        }

        var busy = !force && (Entries.IsKeyboardFocusWithin || _menuOpen || _dragging);
        var signature = string.Join('\n', entries.Select(entry => $"{entry.Key}\t{entry.Label}\t{entry.MoveGroup}\t{entry.CanRename}\t{entry.Subtitle}\t{entry.RuleAbove}\t{entry.Depth}"));
        if (!_gate.ShouldRebuild(signature, busy))
        {
            _pending = busy ? entries : null;
            return;
        }

        _pending = null;
        _all = entries.ToList();
        ShowRows();
    }

    /// <summary>Picks an entry from code, for when the page just made it. One that isn't shown yet is picked once it is.</summary>
    public void Select(string key)
    {
        Choose(key);
        if (_all.Any(entry => entry.Key == key))
        {
            ShowRows();
        }
    }

    /// <summary>A short line under the entries, such as why a rename didn't happen. Null hides it.</summary>
    public void ShowProblem(string? problem)
    {
        Problem.Text = problem ?? "";
        Problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        if (problem is not null)
        {
            LiveRegion.Announce(Problem);
        }
    }

    private void ShowRows()
    {
        var search = SearchBox.Text.Trim();
        var visible = _all
            .Where(entry => !SearchFiltersEntries || entry.MoveGroup is null || entry.Label.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // A selection hidden by the search stays selected. One that's gone falls back to the first entry.
        var selected = visible.FirstOrDefault(entry => entry.Key == _selectedKey);
        var fellBack = selected is null && _all.All(entry => entry.Key != _selectedKey);
        if (fellBack)
        {
            selected = visible.FirstOrDefault();
        }

        _loading = true;
        if (!_rows.SequenceEqual(visible))
        {
            _rows.Clear();
            foreach (var entry in visible)
            {
                _rows.Add(entry);
            }
        }

        Entries.SelectedItem = selected;
        _loading = false;

        if (fellBack && selected is not null)
        {
            Choose(selected.Key);
            SelectionChanged?.Invoke(selected);
        }
    }

    private void Choose(string key)
    {
        _selectedKey = key;
        ShowAddTip();
        try
        {
            _database?.SetPreference(_preferenceKey, key);
        }
        catch (Waypoint.Core.Remote.ServerUnavailableException)
        {
            // Only which entry was picked is lost, until the next pick. In server mode the window is built at startup,
            // before pairing or while the server is away, and this mustn't stop the app from starting.
        }
    }

    private void OnEntryPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Entries.SelectedItem is not ColumnEntry entry || entry.Key == _selectedKey)
        {
            return;
        }

        Choose(entry.Key);
        ShowProblem(null);
        SelectionChanged?.Invoke(entry);
    }

    /// <summary>Entries that waited while the list had focus catch up here.</summary>
    private void OnEntriesLeft(object sender, KeyboardFocusChangedEventArgs e) => CatchUp();

    private void CatchUp()
    {
        if (_pending is { } pending)
        {
            Dispatcher.BeginInvoke(() => SetEntries(pending), DispatcherPriority.Background);
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchFiltersEntries)
        {
            ShowRows();
        }

        SearchChanged?.Invoke(SearchBox.Text);
    }

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SearchBox.Text.Length > 0)
        {
            e.Handled = true;
            SearchBox.Clear();
        }
    }

    /// <summary>Puts words in the search, for Find anywhere showing something on a page that's found by its search.</summary>
    public void SetSearch(string text) => SearchBox.Text = text;

    /// <summary>For Ctrl+F. Selects what's there, so typing replaces the last search.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>Does what clicking + does, and nothing on a page with no +.</summary>
    public void OpenAdd() => OnAddClicked(AddButton, new RoutedEventArgs());

    /// <summary>
    /// For Ctrl+N: the page's usual kind straight away (<see cref="QuickAdd"/>, or <see cref="QuickAddIn"/> for the
    /// picked entry), else what + does.
    /// </summary>
    public void AddByShortcut()
    {
        if (AddMode == ColumnAddMode.Menu && CurrentQuickAdd is { } quick)
        {
            AddChosen?.Invoke(quick.Key);
        }
        else
        {
            OpenAdd();
        }
    }

    /// <summary>Drops down these choices under +, such as a second step after one of <see cref="AddChoices"/>. Picking one raises <see cref="AddChosen"/> with its key.</summary>
    public void OpenAddMenu(IEnumerable<ColumnAddChoice> choices) =>
        RowMenu.Open(AddButton, PlacementMode.Bottom, choices.Select(choice => new RowMenuEntry(choice.Label, () => AddChosen?.Invoke(choice.Key))).ToList(), () => { });

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        if (AddMode == ColumnAddMode.Direct)
        {
            AddChosen?.Invoke(null);
        }
        else if (AddMode == ColumnAddMode.Menu)
        {
            OpenAddMenu(AddChoices);
        }
    }

    private void OnEntriesKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Entries.SelectedItem is not ColumnEntry entry)
        {
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Up or Key.Down)
        {
            e.Handled = true;
            Move(entry, key == Key.Up ? -1 : 1);
        }
        else if (key == Key.F2 && entry.CanRename)
        {
            e.Handled = true;
            Rename(entry);
        }
    }

    private void OnEntriesMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        var row = RowAt(e.OriginalSource);
        var entry = row?.DataContext as ColumnEntry;
        var lines = new List<RowMenuEntry>();

        if (entry is not null)
        {
            if (entry.CanRename)
            {
                lines.Add(new RowMenuEntry("Rename", () => Rename(entry)));
            }

            if (CanMove(entry, -1))
            {
                lines.Add(new RowMenuEntry("Move up", () => Move(entry, -1)));
            }

            if (CanMove(entry, 1))
            {
                lines.Add(new RowMenuEntry("Move down", () => Move(entry, 1)));
            }

            lines.AddRange(ExtraMenu?.Invoke(entry) ?? []);
        }

        if (AddMode == ColumnAddMode.Direct)
        {
            lines.Add(new RowMenuEntry(AddLabel, () => AddChosen?.Invoke(null)));
        }
        else if (AddMode == ColumnAddMode.Menu)
        {
            lines.AddRange(AddChoices.Where(choice => choice.InColumnMenu).Select(choice => new RowMenuEntry($"New {choice.Label.ToLowerInvariant()}", () => AddChosen?.Invoke(choice.Key))));
        }

        if (lines.Count == 0)
        {
            return;
        }

        _menuOpen = true;
        RowMenu.Open((FrameworkElement?)row ?? Entries, e, lines, () =>
        {
            _menuOpen = false;
            CatchUp();
        });
    }

    private void Rename(ColumnEntry entry)
    {
        var rename = new RenameWindow(Window.GetWindow(this), entry.RenameText);
        if (rename.ShowDialog() != true || rename.NewName == entry.RenameText)
        {
            return;
        }

        var args = new ColumnRenameEventArgs(entry, rename.NewName);
        RenameRequested?.Invoke(this, args);
        ShowProblem(args.Problem);
    }

    private List<ColumnEntry> Siblings(ColumnEntry entry) => _all.Where(found => found.MoveGroup == entry.MoveGroup).ToList();

    /// <summary>Where the entries inside the one at start end: the first after it that isn't deeper.</summary>
    private int BlockEnd(int start)
    {
        var end = start + 1;
        while (end < _all.Count && _all[end].Depth > _all[start].Depth)
        {
            end++;
        }

        return end;
    }

    /// <summary>
    /// The next sibling in that direction that can be seen. Entries inside a sibling aren't siblings, and ones the
    /// search hides are stepped over, so a move always shows.
    /// </summary>
    private ColumnEntry? Neighbor(ColumnEntry entry, int step)
    {
        if (entry.MoveGroup is null)
        {
            return null;
        }

        var siblings = Siblings(entry);
        var at = siblings.FindIndex(found => found.Key == entry.Key);
        for (var i = at + step; at >= 0 && i >= 0 && i < siblings.Count; i += step)
        {
            if (_rows.Contains(siblings[i]))
            {
                return siblings[i];
            }
        }

        return null;
    }

    private bool CanMove(ColumnEntry entry, int step) => Neighbor(entry, step) is not null;

    private void Move(ColumnEntry entry, int step)
    {
        if (Neighbor(entry, step) is not { } target)
        {
            return;
        }

        // Up goes just before the neighbor. Down goes just after it, before whichever sibling comes next.
        var siblings = Siblings(entry);
        var before = step < 0 ? target : siblings.ElementAtOrDefault(siblings.FindIndex(found => found.Key == target.Key) + 1);
        Place(entry, before);
    }

    private void OnEntriesPressed(object sender, MouseButtonEventArgs e)
    {
        _pressed = RowAt(e.OriginalSource)?.DataContext is ColumnEntry { MoveGroup: not null } entry ? entry : null;
        _pressedAt = e.GetPosition(Entries);
    }

    private void OnEntriesMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed is not { } entry || e.LeftButton != MouseButtonState.Pressed)
        {
            _pressed = null;
            return;
        }

        var moved = e.GetPosition(Entries) - _pressedAt;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _pressed = null;
        _dragging = true;
        try
        {
            DragDrop.DoDragDrop(Entries, new DataObject(typeof(ColumnEntry), entry), DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
            ShowInsertion(null);
            CatchUp();
        }
    }

    private void OnEntriesDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(typeof(ColumnEntry)))
        {
            ShowInsertion(null);
            e.Effects = OfferToPage(e, isDrop: false) ? DragDropEffects.Move : DragDropEffects.None;
            return;
        }

        if (DropSlot(e) is { } slot)
        {
            e.Effects = DragDropEffects.Move;
            ShowInsertion(slot.LineY);
        }
        else
        {
            e.Effects = DragDropEffects.None;
            ShowInsertion(null);
        }
    }

    private void OnEntriesDragLeave(object sender, DragEventArgs e) => ShowInsertion(null);

    private void OnEntriesDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ShowInsertion(null);
        if (!e.Data.GetDataPresent(typeof(ColumnEntry)))
        {
            e.Effects = OfferToPage(e, isDrop: true) ? DragDropEffects.Move : DragDropEffects.None;
        }
        else if (DropSlot(e) is { } slot)
        {
            Place(slot.Entry, slot.Before);
        }
    }

    /// <summary>Asks the page about something other than an entry, over the entry under the pointer.</summary>
    private bool OfferToPage(DragEventArgs e, bool isDrop)
    {
        if (RowAt(e.OriginalSource)?.DataContext is not ColumnEntry entry || DataDropped is null)
        {
            return false;
        }

        var args = new ColumnDropEventArgs(entry, e.Data, isDrop);
        DataDropped(this, args);
        return args.Accepted;
    }

    /// <summary>Where a drop would put the entry: before <paramref name="Before"/>, or at the end of its group when that's null.</summary>
    private sealed record Slot(ColumnEntry Entry, ColumnEntry? Before, double LineY);

    /// <summary>
    /// Works from the pointer's height among the siblings that show, so a gap between rows or the space under the
    /// last one counts as well as a row. Over a row of another group, or a pinned one, nothing can go.
    /// </summary>
    private Slot? DropSlot(DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ColumnEntry)) is not ColumnEntry carried || _all.FirstOrDefault(found => found.Key == carried.Key) is not { MoveGroup: { } group } dragged)
        {
            return null;
        }

        if (RowAt(e.OriginalSource)?.DataContext is ColumnEntry over && over.MoveGroup != group)
        {
            return null;
        }

        var rest = Siblings(dragged).Where(entry => entry.Key != dragged.Key).ToList();
        var y = e.GetPosition(Entries).Y;
        var others = rest
            .Where(_rows.Contains)
            .Select(entry => (Entry: entry, Box: Bounds(entry)))
            .Where(row => row.Box is not null)
            .Select(row => (row.Entry, Box: row.Box!.Value))
            .ToList();
        if (others.Count == 0)
        {
            return null;
        }

        var after = others.Count(row => row.Box.Top + (row.Box.Height / 2) < y);
        if (after < others.Count)
        {
            return new Slot(dragged, others[after].Entry, others[after].Box.Top);
        }

        // Under the last sibling that shows: after it and what's inside it, and before any hidden sibling that follows.
        var last = others[^1];
        var start = _all.IndexOf(last.Entry);
        var bottom = _all.GetRange(start, BlockEnd(start) - start).Select(Bounds).OfType<Rect>().Select(box => box.Bottom).DefaultIfEmpty(last.Box.Bottom).Max();
        return new Slot(dragged, rest.ElementAtOrDefault(rest.IndexOf(last.Entry) + 1), bottom);
    }

    private Rect? Bounds(ColumnEntry entry) =>
        Entries.ItemContainerGenerator.ContainerFromItem(entry) is ListBoxItem { IsVisible: true } row
            ? new Rect(row.TranslatePoint(new Point(0, 0), Entries), row.RenderSize)
            : null;

    /// <summary>
    /// Moves the entry to just before <paramref name="before"/>, or to the end of its group, taking the entries inside
    /// it along. Keeps the keyboard on it.
    /// </summary>
    private void Place(ColumnEntry entry, ColumnEntry? before)
    {
        var group = Siblings(entry);
        var moving = group.FirstOrDefault(found => found.Key == entry.Key);
        if (moving?.MoveGroup is not { } groupName)
        {
            return;
        }

        var order = group.Where(found => found.Key != entry.Key).ToList();
        var at = before is null ? order.Count : order.FindIndex(found => found.Key == before.Key);
        order.Insert(at < 0 ? order.Count : at, moving);
        if (order.SequenceEqual(group))
        {
            return;
        }

        var hadFocus = Entries.IsKeyboardFocusWithin;
        Arrange(group, order);
        ShowArranged();

        if (hadFocus)
        {
            Dispatcher.BeginInvoke(() => (Entries.ItemContainerGenerator.ContainerFromItem(moving) as ListBoxItem)?.Focus(), DispatcherPriority.Input);
        }

        Reordered?.Invoke(groupName, order.Select(found => found.Key).ToList());
    }

    /// <summary>Puts the group's entries, each with what's inside it, into the new order.</summary>
    private void Arrange(List<ColumnEntry> group, List<ColumnEntry> order)
    {
        var starts = group.Select(found => _all.IndexOf(found)).ToList();
        var blocks = group.Select((found, i) => (found.Key, Block: _all.GetRange(starts[i], BlockEnd(starts[i]) - starts[i]))).ToDictionary(pair => pair.Key, pair => pair.Block);
        var together = starts.Zip(starts.Skip(1)).All(pair => BlockEnd(pair.First) == pair.Second);
        if (together)
        {
            var from = starts[0];
            _all.RemoveRange(from, BlockEnd(starts[^1]) - from);
            _all.InsertRange(from, order.SelectMany(found => blocks[found.Key]));
            return;
        }

        // A group split up by other entries keeps its places in the list; only who sits in them changes.
        for (var i = 0; i < starts.Count; i++)
        {
            _all[starts[i]] = order[i];
        }
    }

    /// <summary>Moves the shown rows to match the new order, rather than rebuilding them, so the list keeps its place.</summary>
    private void ShowArranged()
    {
        var shown = _all.Where(_rows.Contains).ToList();
        _loading = true;
        for (var i = 0; i < shown.Count; i++)
        {
            var from = _rows.IndexOf(shown[i]);
            if (from != i)
            {
                _rows.Move(from, i);
            }
        }

        Entries.SelectedItem = Selected;
        _loading = false;
    }

    private InsertionLine? _insertion;

    /// <summary>A line where a dragged entry would land. Null takes it away.</summary>
    private void ShowInsertion(double? y)
    {
        var layer = AdornerLayer.GetAdornerLayer(Entries);
        if (layer is null)
        {
            return;
        }

        if (y is null)
        {
            if (_insertion is not null)
            {
                layer.Remove(_insertion);
                _insertion = null;
            }

            return;
        }

        if (_insertion is null)
        {
            _insertion = new InsertionLine(Entries);
            layer.Add(_insertion);
        }

        _insertion.Y = y.Value;
    }

    private sealed class InsertionLine(UIElement list) : Adorner(list)
    {
        private double _y;

        public double Y
        {
            get => _y;
            set
            {
                _y = value;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext drawing)
        {
            var brush = (Brush?)TryFindResource("Accent") ?? Brushes.White;
            drawing.DrawRectangle(brush, null, new Rect(4, Y - 1, Math.Max(0, AdornedElement.RenderSize.Width - 8), 2));
        }
    }

    private static ListBoxItem? RowAt(object? source)
    {
        var found = source as DependencyObject;
        while (found is not null and not ListBoxItem)
        {
            found = found is Visual ? VisualTreeHelper.GetParent(found) : LogicalTreeHelper.GetParent(found);
        }

        return found as ListBoxItem;
    }
}
