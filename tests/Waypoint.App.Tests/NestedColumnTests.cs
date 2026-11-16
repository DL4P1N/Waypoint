using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Waypoint.App;

namespace Waypoint.App.Tests;

public sealed class NestedColumnTests
{
    private static List<ColumnEntry> Entries(string suffix = "") =>
    [
        new("all", "All"),
        new("a", "Alpha" + suffix, "g", CanRename: true),
        new("b", "Bravo" + suffix, "g", CanRename: true, Name: "Bravo"),
        new("c", "Charlie" + suffix, "g", CanRename: true),
        new("z", "Saved"),
    ];

    private static (NestedColumn Column, TextBox Search, ListBox List) Make(UiTest ui, bool filters = false)
    {
        var column = new NestedColumn { SearchFiltersEntries = filters, AddMode = ColumnAddMode.None };
        column.Connect(ui.Database, "test_column");
        ui.Host(column);
        column.SetEntries(Entries());
        ui.Pump();
        return (column, UiTest.Named<TextBox>(column, "SearchBox"), UiTest.Named<ListBox>(column, "Entries"));
    }

    private static ColumnEntry Shown(ListBox list, string key) => list.Items.Cast<ColumnEntry>().Single(entry => entry.Key == key);

    /// <summary>A server that can't be reached, or hasn't paired this PC yet.</summary>
    private sealed class AwayServer : Waypoint.Core.Remote.IPreferences
    {
        public string? GetPreference(string key) => null;

        public void SetPreference(string key, string value) =>
            throw new Waypoint.Core.Remote.ServerUnavailableException("The server didn't accept this PC, so nothing can change until it's paired again.");
    }

    [Fact]
    public void Picking_an_entry_works_while_the_server_is_away() => Ui.Run(ui =>
    {
        // The window is built at startup, before pairing. Remembering the pick there must not stop the app starting.
        var column = new NestedColumn { AddMode = ColumnAddMode.None };
        column.Connect(new AwayServer(), "test_column");
        ui.Host(column);

        column.SetEntries(Entries());
        column.Select("b");
        ui.Pump();

        Assert.Equal("b", column.SelectedKey);
    });

    [Fact]
    public void Search_text_focus_and_selection_survive_a_refresh_while_typing_in_search() => Ui.Run(ui =>
    {
        var (column, search, list) = Make(ui);
        column.Select("b");
        ui.Focus(search);
        search.Text = "brav";
        search.CaretIndex = 4;

        column.SetEntries(Entries("  ·  2"));
        ui.Pump();

        Assert.Equal("brav", search.Text);
        Assert.Equal(4, search.CaretIndex);
        Assert.True(search.IsKeyboardFocused);
        Assert.Equal("b", column.SelectedKey);
        Assert.Equal("b", ((ColumnEntry)list.SelectedItem).Key);
        Assert.Equal("Bravo  ·  2", Shown(list, "b").Label);
        Assert.Equal("b", ui.Database.GetPreference("test_column"));
    });

    [Fact]
    public void A_filtering_search_keeps_pinned_entries_and_survives_a_refresh() => Ui.Run(ui =>
    {
        var (column, search, list) = Make(ui, filters: true);
        column.Select("c");
        ui.Focus(search);
        search.Text = "char";
        ui.Pump();
        Assert.Equal(["all", "c", "z"], list.Items.Cast<ColumnEntry>().Select(entry => entry.Key));

        column.SetEntries(Entries(" (new)"));
        ui.Pump();

        Assert.Equal("char", search.Text);
        Assert.True(search.IsKeyboardFocused);
        Assert.Equal(["all", "c", "z"], list.Items.Cast<ColumnEntry>().Select(entry => entry.Key));
        Assert.Equal("c", ((ColumnEntry)list.SelectedItem).Key);
    });

    [Fact]
    public void A_refresh_waits_while_the_list_has_focus_and_catches_up_when_it_leaves() => Ui.Run(ui =>
    {
        var (column, search, list) = Make(ui);
        column.Select("a");
        var row = ui.Container(list, Shown(list, "a"));
        ui.Focus((IInputElement)row);

        column.SetEntries(Entries(" later"));
        ui.Pump();
        Assert.Equal("Alpha", Shown(list, "a").Label);
        Assert.True(row.IsKeyboardFocused);

        ui.Focus(search);
        Assert.Equal("Alpha later", Shown(list, "a").Label);
        Assert.Equal("a", column.SelectedKey);
    });

    [Fact]
    public void A_selected_entry_that_goes_away_falls_back_to_the_first() => Ui.Run(ui =>
    {
        var (column, _, list) = Make(ui);
        column.Select("c");
        ColumnEntry? picked = null;
        column.SelectionChanged += entry => picked = entry;

        column.SetEntries(Entries().Where(entry => entry.Key != "c").ToList());
        ui.Pump();

        Assert.Equal("all", column.SelectedKey);
        Assert.Equal("all", picked?.Key);
        Assert.Equal("all", ((ColumnEntry)list.SelectedItem).Key);
    });

    /// <summary>
    /// Alt+Up and Alt+Down call Move(entry, -1 or 1). Holding Alt can't be faked without touching real input state
    /// (even this thread's key table lets Windows hand over the foreground), so the move itself is called here,
    /// and plain Up is pressed to show that it alone doesn't reorder.
    /// </summary>
    [Fact]
    public void Alt_up_and_alt_down_reorder_within_the_group() => Ui.Run(ui =>
    {
        var (column, _, list) = Make(ui);
        var moves = new List<(string Group, IReadOnlyList<string> Keys)>();
        column.Reordered += (group, keys) => moves.Add((group, keys));
        column.Select("b");
        ui.Focus((IInputElement)ui.Container(list, Shown(list, "b")));

        UiTest.Call(column, "Move", Shown(list, "b"), -1);
        ui.Pump();
        Assert.Equal("g", moves[^1].Group);
        Assert.Equal(["b", "a", "c"], moves[^1].Keys);
        Assert.Equal(["all", "b", "a", "c", "z"], list.Items.Cast<ColumnEntry>().Select(entry => entry.Key));
        Assert.True(ui.Container(list, Shown(list, "b")).IsKeyboardFocused, "focus stays on the moved entry");

        UiTest.Call(column, "Move", Shown(list, "b"), 1);
        UiTest.Call(column, "Move", Shown(list, "b"), 1);
        ui.Pump();
        Assert.Equal(["a", "c", "b"], moves[^1].Keys);
        Assert.Equal("b", column.SelectedKey);

        // The last in its group can't go past the pinned entry below it.
        var count = moves.Count;
        UiTest.Call(column, "Move", Shown(list, "b"), 1);
        Assert.Equal(count, moves.Count);

        // Up without Alt is just moving the selection, not a reorder.
        ui.Press(ui.Container(list, Shown(list, "b")), Key.Up);
        Assert.Equal(count, moves.Count);
    });

    [Fact]
    public void Pinned_entries_cannot_move_by_key_menu_or_drag() => Ui.Run(ui =>
    {
        var (column, _, list) = Make(ui);
        var moves = 0;
        column.Reordered += (_, _) => moves++;

        column.Select("all");
        var all = ui.Container(list, Shown(list, "all"));
        ui.Focus((IInputElement)all);
        UiTest.Call(column, "Move", Shown(list, "all"), 1);

        // A pinned entry with nothing to offer opens no menu at all.
        Assert.Null(ui.RightClick(all));

        var top = ui.RightClick(ui.Container(list, Shown(list, "a")));
        Assert.NotNull(top);
        Assert.Equal(["Rename", "Move down"], UiTest.Labels(top!));
        ui.Close(top!);

        // Dragging into a pinned row, or dragging a pinned row, is refused.
        Assert.Equal(DragDropEffects.None, ui.DragOnto(all, new DataObject(typeof(ColumnEntry), Shown(list, "a"))));
        Assert.Equal(DragDropEffects.None, ui.DragOnto(ui.Container(list, Shown(list, "c")), new DataObject(typeof(ColumnEntry), Shown(list, "z"))));

        Assert.Equal(0, moves);
        Assert.Equal(["all", "a", "b", "c", "z"], list.Items.Cast<ColumnEntry>().Select(entry => entry.Key));
    });

    [Fact]
    public void Dropping_on_the_lower_half_of_a_row_puts_it_after_that_row() => Ui.Run(ui =>
    {
        var (column, _, list) = Make(ui);
        IReadOnlyList<string>? order = null;
        column.Reordered += (_, keys) => order = keys;

        var last = ui.Container(list, Shown(list, "c"));
        var effect = ui.DragOnto(last, new DataObject(typeof(ColumnEntry), Shown(list, "a")), at: new Point(1, last.ActualHeight - 2));

        Assert.Equal(DragDropEffects.Move, effect);
        Assert.Equal(["b", "c", "a"], order);
        Assert.Equal(["all", "b", "c", "a", "z"], list.Items.Cast<ColumnEntry>().Select(entry => entry.Key));
    });

    [Fact]
    public void Dropping_back_where_it_was_changes_nothing() => Ui.Run(ui =>
    {
        var (column, _, list) = Make(ui);
        var moves = 0;
        column.Reordered += (_, _) => moves++;

        var effect = ui.DragOnto(ui.Container(list, Shown(list, "b")), new DataObject(typeof(ColumnEntry), Shown(list, "b")));

        Assert.Equal(DragDropEffects.Move, effect);
        Assert.Equal(0, moves);
        Assert.Equal(["all", "a", "b", "c", "z"], list.Items.Cast<ColumnEntry>().Select(entry => entry.Key));
    });

    [Fact]
    public void Dropping_an_entry_on_another_in_its_group_reorders() => Ui.Run(ui =>
    {
        var (column, _, list) = Make(ui);
        IReadOnlyList<string>? order = null;
        column.Reordered += (_, keys) => order = keys;

        var effect = ui.DragOnto(ui.Container(list, Shown(list, "a")), new DataObject(typeof(ColumnEntry), Shown(list, "c")));

        Assert.Equal(DragDropEffects.Move, effect);
        Assert.Equal(["c", "a", "b"], order);
        Assert.Equal(["all", "c", "a", "b", "z"], list.Items.Cast<ColumnEntry>().Select(entry => entry.Key));
    });

    [Fact]
    public void Move_up_and_down_on_the_menu_reorder() => Ui.Run(ui =>
    {
        var (column, _, list) = Make(ui);
        IReadOnlyList<string>? order = null;
        column.Reordered += (_, keys) => order = keys;

        var menu = ui.RightClick(ui.Container(list, Shown(list, "b")))!;
        Assert.Equal(["Rename", "Move up", "Move down"], UiTest.Labels(menu));
        ui.Choose(menu, "Move down");
        Assert.False(UiTest.Field<bool>(column, "_menuOpen"), $"column menu still open; menus={ui.Menus.Count}");

        Assert.Equal(["a", "c", "b"], order);
    });

    [Fact]
    public void Rename_from_the_menu_raises_rename_requested_with_the_new_name() => Ui.Run(ui =>
    {
        var (column, _, list) = Make(ui);
        var asked = new List<ColumnRenameEventArgs>();
        column.RenameRequested += (_, args) =>
        {
            asked.Add(args);
            args.Problem = "That name is taken.";
        };

        ui.RenameAnswer = "Bravo two";
        ui.Choose(ui.RightClick(ui.Container(list, Shown(list, "b")))!, "Rename");

        Assert.Equal(1, ui.RenameDialogs);
        var args = Assert.Single(asked);
        Assert.Equal("b", args.Entry.Key);
        Assert.Equal("Bravo two", args.NewName);
        var problem = UiTest.Named<TextBlock>(column, "Problem");
        Assert.Equal("That name is taken.", problem.Text);
        Assert.Equal(Visibility.Visible, problem.Visibility);
    });

    [Fact]
    public void Rename_from_F2_and_a_canceled_rename_raises_nothing() => Ui.Run(ui =>
    {
        var (column, _, list) = Make(ui);
        var asked = new List<string>();
        column.RenameRequested += (_, args) => asked.Add(args.NewName);
        column.Select("a");
        var row = ui.Container(list, Shown(list, "a"));
        ui.Focus((IInputElement)row);

        ui.RenameAnswer = null;
        ui.Press(row, Key.F2);
        Assert.Empty(asked);

        ui.RenameAnswer = "Alpha";
        ui.Press(row, Key.F2);
        Assert.Empty(asked);

        ui.RenameAnswer = "Apple";
        ui.Press(row, Key.F2);
        Assert.Equal(["Apple"], asked);
        Assert.Equal(3, ui.RenameDialogs);
    });
    private static List<ColumnEntry> Tree() =>
    [
        new("all", "All"),
        new("p1", "Parent one", "top"),
        new("c1", "Child one", "in-p1", Depth: 1),
        new("c2", "Child two", "in-p1", Depth: 1),
        new("p2", "Parent two", "top"),
        new("z", "Saved"),
    ];

    private static IReadOnlyList<string> Keys(ListBox list) => list.Items.Cast<ColumnEntry>().Select(entry => entry.Key).ToList();

    [Fact]
    public void Nested_entries_are_indented_by_depth_without_changing_their_label() => Ui.Run(ui =>
    {
        var (column, _, list) = Make(ui);
        column.SetEntries(Tree(), force: true);
        ui.Pump();

        Assert.Equal("Child one", Shown(list, "c1").Label);
        Assert.True(Shown(list, "c1").Indent.Left > Shown(list, "p1").Indent.Left);
        Assert.Equal(0, Shown(list, "p1").Indent.Left);
    });

    [Fact]
    public void Moves_go_among_siblings_and_step_over_the_entries_inside_them() => Ui.Run(ui =>
    {
        var (column, _, list) = Make(ui);
        column.SetEntries(Tree(), force: true);
        ui.Pump();
        var moves = new List<(string Group, IReadOnlyList<string> Keys)>();
        column.Reordered += (group, keys) => moves.Add((group, keys));

        // Parent two's neighbor in the list is Child two, but it moves past all of Parent one.
        var menu = ui.RightClick(ui.Container(list, Shown(list, "p2")))!;
        Assert.Equal(["Move up"], UiTest.Labels(menu));
        ui.Choose(menu, "Move up");
        Assert.Equal("top", moves[^1].Group);
        Assert.Equal(["p2", "p1"], moves[^1].Keys);
        Assert.Equal(["all", "p2", "p1", "c1", "c2", "z"], Keys(list));

        // Parent one takes its children along when it moves back down.
        UiTest.Call(column, "Move", Shown(list, "p1"), -1);
        ui.Pump();
        Assert.Equal(["all", "p1", "c1", "c2", "p2", "z"], Keys(list));

        // A child moves only among the children of its parent.
        UiTest.Call(column, "Move", Shown(list, "c2"), -1);
        ui.Pump();
        Assert.Equal("in-p1", moves[^1].Group);
        Assert.Equal(["c2", "c1"], moves[^1].Keys);
        Assert.Equal(["all", "p1", "c2", "c1", "p2", "z"], Keys(list));
        var count = moves.Count;
        UiTest.Call(column, "Move", Shown(list, "c2"), -1);
        Assert.Equal(count, moves.Count);
    });

    [Fact]
    public void Dragging_a_parent_below_its_sibling_takes_its_children_along() => Ui.Run(ui =>
    {
        var (column, _, list) = Make(ui);
        column.SetEntries(Tree(), force: true);
        ui.Pump();
        IReadOnlyList<string>? order = null;
        column.Reordered += (_, keys) => order = keys;

        var below = ui.Container(list, Shown(list, "p2"));
        var effect = ui.DragOnto(below, new DataObject(typeof(ColumnEntry), Shown(list, "p1")), at: new Point(1, below.ActualHeight - 2));

        Assert.Equal(DragDropEffects.Move, effect);
        Assert.Equal(["p2", "p1"], order);
        Assert.Equal(["all", "p2", "p1", "c1", "c2", "z"], Keys(list));
    });

    [Fact]
    public void Moving_while_the_search_hides_some_entries_steps_over_the_hidden_ones() => Ui.Run(ui =>
    {
        var (column, search, list) = Make(ui, filters: true);
        search.Text = "l";
        ui.Pump();
        Assert.Equal(["all", "a", "c", "z"], Keys(list));
        IReadOnlyList<string>? order = null;
        column.Reordered += (_, keys) => order = keys;

        // Bravo is hidden between them, so swapping with it would look like nothing happened.
        var menu = ui.RightClick(ui.Container(list, Shown(list, "a")))!;
        Assert.Contains("Move down", UiTest.Labels(menu));
        ui.Choose(menu, "Move down");

        Assert.Equal(["b", "c", "a"], order);
        Assert.Equal(["all", "c", "a", "z"], Keys(list));

        // The last one shown has nowhere to go that can be seen.
        Assert.DoesNotContain("Move down", UiTest.Labels(ui.RightClick(ui.Container(list, Shown(list, "a")))!));
        ui.Close(ui.Menus[^1]);

        // Dragging works while filtered too.
        var effect = ui.DragOnto(ui.Container(list, Shown(list, "c")), new DataObject(typeof(ColumnEntry), Shown(list, "a")));
        Assert.Equal(DragDropEffects.Move, effect);
        Assert.Equal(["all", "a", "c", "z"], Keys(list));
        search.Text = "";
        ui.Pump();
        Assert.Equal(["all", "b", "a", "c", "z"], Keys(list));
    });

    [Fact]
    public void Other_data_dropped_on_an_entry_goes_to_the_page_which_says_whether_it_fits() => Ui.Run(ui =>
    {
        var (column, _, list) = Make(ui);
        var dropped = new List<(string Key, object? Data)>();
        column.DataDropped += (_, args) =>
        {
            args.Accepted = args.Entry.Key != "all";
            if (args.IsDrop && args.Accepted)
            {
                dropped.Add((args.Entry.Key, args.Data.GetData("Test.Thing")));
            }
        };

        Assert.Equal(DragDropEffects.Move, ui.DragOnto(ui.Container(list, Shown(list, "b")), new DataObject("Test.Thing", 7L)));
        Assert.Equal(DragDropEffects.None, ui.DragOnto(ui.Container(list, Shown(list, "all")), new DataObject("Test.Thing", 8L)));

        Assert.Equal([("b", (object?)7L)], dropped);
        Assert.Equal(["all", "a", "b", "c", "z"], Keys(list));
    });

    [Fact]
    public void Open_add_menu_drops_down_these_choices_under_plus() => Ui.Run(ui =>
    {
        var (column, _, _) = Make(ui);
        var chosen = new List<string?>();
        column.AddChosen += chosen.Add;
        var before = ui.Menus.Count;

        column.OpenAddMenu([new ColumnAddChoice("one", "One"), new ColumnAddChoice("two", "Two")]);
        ui.Pump();

        Assert.Equal(before + 1, ui.Menus.Count);
        Assert.Equal(["One", "Two"], UiTest.Labels(ui.Menus[^1]));
        Assert.Same(UiTest.Named<Button>(column, "AddButton"), ui.Menus[^1].PlacementTarget);
        ui.Choose(ui.Menus[^1], "Two");
        Assert.Equal(["two"], chosen);
    });

    [Fact]
    public void Ctrl_N_adds_the_usual_kind_straight_away_and_plus_keeps_the_menu() => Ui.Run(ui =>
    {
        var column = ui.Host(new NestedColumn { AddLabel = "Add to To do", AddMode = ColumnAddMode.Menu });
        column.AddChoices = [new("Task", "Task"), new("Exam", "Exam")];
        column.QuickAdd = column.AddChoices[0];
        var chosen = new List<string?>();
        column.AddChosen += chosen.Add;
        ui.Pump();
        var before = ui.Menus.Count;

        column.AddByShortcut();
        Assert.Equal(["Task"], chosen);
        Assert.Equal(before, ui.Menus.Count);
        Assert.Equal("Add to To do (Ctrl+N for a new task)", UiTest.Named<Button>(column, "AddButton").ToolTip);

        column.OpenAdd();
        ui.Pump();
        Assert.Equal(["Task", "Exam"], UiTest.Labels(ui.Menus[before]));
    });
}
