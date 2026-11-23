using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.App.Tests;

/// <summary>Lists live in the Notes page's column, and a picked list shows in place of the notes.</summary>
public sealed class ListsViewTests
{
    private const string Shopping = WaypointDatabase.ShoppingList;
    private const string NoteDragFormat = "Waypoint.NoteId";

    private sealed record Page(NotesView Notes, ListsView View, NestedColumn Column, ListBox Places, TextBox AddItem, ItemsControl Entries)
    {
        public ColumnEntry Entry(string key) => Places.Items.Cast<ColumnEntry>().Single(entry => entry.Key == key);

        public IReadOnlyList<string> Keys => Places.Items.Cast<ColumnEntry>().Select(entry => entry.Key).ToList();

        /// <summary>Picks an entry in the column, as clicking it would.</summary>
        public void Pick(UiTest ui, string key)
        {
            Places.SelectedItem = Entry(key);
            ui.Pump();
        }

        public string Search
        {
            get => UiTest.Named<TextBox>(Column, "SearchBox").Text;
            set => UiTest.Named<TextBox>(Column, "SearchBox").Text = value;
        }
    }

    private static string ListKey(string list) => "list:" + list;

    /// <summary>Opens Notes over the test's data and, unless told otherwise, picks a list in its column.</summary>
    private static Page Make(UiTest ui, string? open = Shopping)
    {
        var notes = new NotesView();
        notes.Connect(ui.Database);
        ui.Host(notes);
        notes.Refresh(force: true);
        ui.Pump();
        var column = UiTest.Named<NestedColumn>(notes, "Column");
        var view = UiTest.Named<ListsView>(notes, "ListsPanel");
        var page = new Page(notes, view, column, UiTest.Named<ListBox>(column, "Entries"), UiTest.Named<TextBox>(view, "AddItem"), UiTest.Named<ItemsControl>(view, "Entries"));
        if (open is not null && column.SelectedKey != ListKey(open))
        {
            page.Pick(ui, ListKey(open));
        }

        return page;
    }

    private static List<string> Shown(ItemsControl entries) =>
        entries.Items.Cast<object>().Select(row => UiTest.Prop<string>(row, "Text")!).ToList();

    [Fact]
    public void Each_entrys_delete_button_says_which_entry_it_deletes() => Ui.Run(ui =>
    {
        ui.Database.AddToList("packing", ["milk", "eggs"], 0, DateTime.Now);
        var page = Make(ui, open: "packing");

        var names = UiTest.Descendants<Button>(page.Entries).Select(button => System.Windows.Automation.AutomationProperties.GetName(button)).ToList();
        Assert.Contains("Delete milk", names);
        Assert.Contains("Delete eggs", names);
        Assert.DoesNotContain("Delete", names);
    });

    [Fact]
    public void Lists_show_in_the_notes_column_after_the_folders_with_shopping_first() => Ui.Run(ui =>
    {
        var school = ui.Database.CreateNoteFolder("School", null, DateTime.Now).Id;
        ui.Database.AddToList("packing", ["charger"], 0, DateTime.Now);
        ui.Database.CreateList("Errands", DateTime.Now);
        var page = Make(ui, open: null);

        Assert.Equal(["all", "unfiled", $"f{school}", ListKey(Shopping), ListKey("Errands"), ListKey("packing"), "spots"], page.Keys);

        // A line sets the lists apart from the folders, and Shopping stays where it is.
        var shopping = page.Entry(ListKey(Shopping));
        Assert.True(shopping.RuleAbove);
        Assert.Null(shopping.MoveGroup);
        Assert.False(shopping.CanRename);
        Assert.False(page.Entry(ListKey("packing")).RuleAbove);
        Assert.True(page.Entry(ListKey("packing")).CanRename);
        Assert.Equal("packing  ·  1 left", page.Entry(ListKey("packing")).Label);
        Assert.True(page.Entry("spots").RuleAbove);

        // A quiet "Lists" above Shopping names the group. It's part of Shopping's row, not an entry to pick.
        Assert.Equal("Lists", shopping.Heading);
        Assert.All(page.Keys.Where(key => key != ListKey(Shopping)), key => Assert.Null(page.Entry(key).Heading));
        Assert.Contains(UiTest.Descendants<TextBlock>(page.Column), text => text.Text == "Lists" && text.IsVisible && !text.IsHitTestVisible);

        // Notes are in charge until a list is picked.
        Assert.Equal(Visibility.Collapsed, page.View.Visibility);
    });

    [Fact]
    public void Picking_a_list_shows_its_entries_in_place_of_the_notes_and_a_folder_brings_them_back() => Ui.Run(ui =>
    {
        ui.Database.AddToList("packing", ["charger", "socks"], 0, DateTime.Now);
        var page = Make(ui, open: null);
        var notesPane = UiTest.Named<Border>(page.Notes, "ListPane");
        var noteSide = UiTest.Named<DockPanel>(page.Notes, "NoteSide");
        var search = UiTest.Named<TextBox>(page.Column, "SearchBox");
        Assert.Equal("Search notes", search.Tag);

        page.Pick(ui, ListKey("packing"));

        Assert.Equal(Visibility.Visible, page.View.Visibility);
        Assert.Equal(Visibility.Collapsed, notesPane.Visibility);
        Assert.Equal(Visibility.Collapsed, noteSide.Visibility);
        Assert.Equal("packing", UiTest.Named<TextBlock>(page.View, "ListTitle").Text);
        Assert.Equal(["charger", "socks"], Shown(page.Entries));
        Assert.Equal("Search this list", search.Tag);
        Assert.Equal(Visibility.Collapsed, UiTest.Named<Button>(page.Notes, "ShowListButton").Visibility);

        page.Pick(ui, "all");

        Assert.Equal(Visibility.Collapsed, page.View.Visibility);
        Assert.Equal(Visibility.Visible, notesPane.Visibility);
        Assert.Equal(Visibility.Visible, noteSide.Visibility);
        Assert.Equal("All notes", UiTest.Named<TextBlock>(page.Notes, "ListTitle").Text);
        Assert.Equal("Search notes", search.Tag);
    });

    [Fact]
    public void Search_looks_through_the_open_list_and_through_notes_once_they_are_back() => Ui.Run(ui =>
    {
        ui.Database.AddListEntry(Shopping, "oat milk", DateTime.Now);
        ui.Database.AddListEntry(Shopping, "eggs", DateTime.Now);
        ui.Database.CreateNote("Milk prices", "cheaper on Tuesdays", DateTime.Now);
        ui.Database.CreateNote("Reading list", "a book", DateTime.Now);
        var page = Make(ui);

        page.Search = "milk";
        ui.Pump();
        Assert.Equal(["oat milk"], Shown(page.Entries));
        page.Search = "";
        ui.Pump();
        Assert.Equal(["oat milk", "eggs"], Shown(page.Entries));

        page.Search = "milk";
        page.Pick(ui, "all");
        var notes = UiTest.Named<ListBox>(page.Notes, "NotesList");
        Assert.Equal(["Milk prices"], notes.Items.Cast<object>().Select(row => UiTest.Prop<string>(row, "Title")));
    });

    [Fact]
    public void New_list_from_the_add_menu_asks_for_a_name_and_opens_it() => Ui.Run(ui =>
    {
        var page = Make(ui, open: null);
        var before = ui.Menus.Count;
        page.Column.OpenAdd();
        ui.Pump();
        var menu = ui.Menus[before];
        Assert.Contains("List", UiTest.Labels(menu));

        ui.RenameAnswer = "camping";
        ui.Choose(menu, "List");
        ui.Pump();

        Assert.Equal(1, ui.RenameDialogs);
        Assert.Contains("camping", ui.Database.GetListOverview().Select(list => list.Name));
        Assert.Equal(ListKey("camping"), page.Column.SelectedKey);
        Assert.Contains(ListKey("camping"), page.Keys);
        Assert.Equal(Visibility.Visible, page.View.Visibility);
        Assert.Equal("camping", UiTest.Named<TextBlock>(page.View, "ListTitle").Text);
        Assert.True(page.AddItem.IsKeyboardFocused);

        // The column's right-click offers it too, beside New folder.
        var rightClick = ui.RightClick(ui.Container(page.Places, page.Entry("all")))!;
        Assert.Contains("New list", UiTest.Labels(rightClick));
        Assert.Contains("New folder", UiTest.Labels(rightClick));
        ui.Close(rightClick);
    });

    [Fact]
    public void A_note_being_typed_is_saved_before_a_list_opens_and_is_still_open_after() => Ui.Run(ui =>
    {
        var id = ui.Database.CreateNote("Groceries plan", "", DateTime.Now);
        var page = Make(ui, open: null);
        var notes = UiTest.Named<ListBox>(page.Notes, "NotesList");
        notes.SelectedItem = notes.Items.Cast<object>().Single(row => UiTest.Prop<long>(row, "Id") == id);
        ui.Pump();
        var title = UiTest.Named<TextBox>(page.Notes, "TitleBox");
        title.Text = "Groceries plan for Sunday";

        page.Pick(ui, ListKey(Shopping));

        Assert.Equal("Groceries plan for Sunday", ui.Database.GetNote(id)!.Title);
        Assert.Equal(Visibility.Visible, page.View.Visibility);

        page.Pick(ui, "all");

        Assert.Equal(Visibility.Visible, UiTest.Named<DockPanel>(page.Notes, "Editor").Visibility);
        Assert.Equal("Groceries plan for Sunday", title.Text);
    });

    [Fact]
    public void In_a_narrow_window_a_list_has_the_room_and_the_notes_list_comes_back_after() => Ui.Run(ui =>
    {
        var id = ui.Database.CreateNote("Lecture 14", "malloc and free", DateTime.Now);
        var page = Make(ui, open: null);
        page.Notes.Width = NotesView.NarrowWidth - 100;
        ui.Pump();
        var notesPane = UiTest.Named<Border>(page.Notes, "ListPane");
        var back = UiTest.Named<Button>(page.Notes, "ShowListButton");
        var notes = UiTest.Named<ListBox>(page.Notes, "NotesList");
        notes.SelectedItem = notes.Items.Cast<object>().Single(row => UiTest.Prop<long>(row, "Id") == id);
        ui.Pump();
        Assert.Equal(Visibility.Visible, back.Visibility);

        page.Pick(ui, ListKey(Shopping));
        Assert.Equal(Visibility.Visible, page.View.Visibility);
        Assert.Equal(Visibility.Collapsed, notesPane.Visibility);
        Assert.Equal(Visibility.Collapsed, back.Visibility);

        // Picking a place for notes is asking to see them, so the notes list is back beside the open note.
        page.Pick(ui, "all");
        Assert.Equal(Visibility.Visible, notesPane.Visibility);
        Assert.Equal(Visibility.Collapsed, back.Visibility);
    });

    [Fact]
    public void A_note_cant_be_dropped_on_a_list() => Ui.Run(ui =>
    {
        ui.Database.CreateList("Errands", DateTime.Now);
        var id = ui.Database.CreateNote("Groceries plan", "", DateTime.Now);
        var page = Make(ui, open: null);

        Assert.Equal(DragDropEffects.None, ui.DragOnto(ui.Container(page.Places, page.Entry(ListKey("Errands"))), new DataObject(NoteDragFormat, id)));
        Assert.Equal(DragDropEffects.None, ui.DragOnto(ui.Container(page.Places, page.Entry(ListKey(Shopping))), new DataObject(NoteDragFormat, id)));
        Assert.Equal(DragDropEffects.Move, ui.DragOnto(ui.Container(page.Places, page.Entry("unfiled")), new DataObject(NoteDragFormat, id), drop: false));
    });

    [Fact]
    public void Typing_in_add_item_keeps_its_text_and_focus_through_a_refresh() => Ui.Run(ui =>
    {
        var page = Make(ui);
        var addItem = page.AddItem;
        ui.Focus(addItem);
        addItem.Text = "oat mi";
        addItem.CaretIndex = addItem.Text.Length;

        // Something else adds to the list, and the minute refresh comes round mid-word.
        ui.Database.AddListEntry(Shopping, "eggs", DateTime.Now);
        page.Notes.Refresh();
        ui.Pump();

        Assert.Equal("oat mi", addItem.Text);
        Assert.Equal(6, addItem.CaretIndex);
        Assert.True(addItem.IsKeyboardFocused);
        Assert.Equal(["eggs"], Shown(page.Entries));

        addItem.Text = "oat milk";
        Assert.True(ui.Press(addItem, Key.Enter));

        Assert.Equal("", addItem.Text);
        Assert.True(addItem.IsKeyboardFocused);
        Assert.Contains("oat milk", ui.Database.GetListEntries(Shopping).Select(entry => entry.Text));
        Assert.Contains("oat milk", Shown(page.Entries));
        Assert.Equal("Shopping  ·  2 left", page.Entry(ListKey(Shopping)).Label);
    });

    [Fact]
    public void An_entry_says_how_to_change_its_words_to_the_pointer_and_to_a_screen_reader() => Ui.Run(ui =>
    {
        ui.Database.AddListEntry(Shopping, "bread", DateTime.Now);
        var page = Make(ui);
        var bread = ((IList)page.Entries.ItemsSource).Cast<object>().Single(row => UiTest.Prop<string>(row, "Text") == "bread");
        var container = ui.Container(page.Entries, bread);

        var label = UiTest.Descendants<TextBlock>(container).First(block => block.Text == "bread");
        Assert.Equal("Double-click to change it", label.ToolTip);
        Assert.Equal(Cursors.IBeam, label.Cursor);
        var box = UiTest.Descendants<CheckBox>(container).Single();
        Assert.Contains("Enter changes its words", System.Windows.Automation.AutomationProperties.GetHelpText(box), StringComparison.Ordinal);
    });

    [Fact]
    public void An_edited_entry_changes_in_place_and_a_refresh_waits_while_editing() => Ui.Run(ui =>
    {
        ui.Database.AddListEntry(Shopping, "bread", DateTime.Now);
        ui.Database.AddListEntry(Shopping, "jam", DateTime.Now);
        var page = Make(ui);
        var (addItem, entries) = (page.AddItem, page.Entries);
        var rows = (IList)entries.ItemsSource;
        var bread = rows.Cast<object>().Single(row => UiTest.Prop<string>(row, "Text") == "bread");
        var container = ui.Container(entries, bread);
        var box = UiTest.Descendants<CheckBox>(container).Single();
        var editor = UiTest.Descendants<TextBox>(container).Single();
        var label = UiTest.Descendants<TextBlock>(container).First(block => block.Text == "bread");

        // Enter on the row's check box starts editing, and the edit box takes focus.
        ui.Focus(box);
        Assert.True(ui.Press(box, Key.Enter));
        ui.Pump();
        Assert.Equal(Visibility.Visible, editor.Visibility);
        Assert.True(editor.IsKeyboardFocused);

        editor.Text = "sourdough";
        ui.Database.AddListEntry(Shopping, "butter", DateTime.Now);
        page.Notes.Refresh();
        ui.Pump();

        // Nothing rebuilt under the editor.
        Assert.Same(rows, entries.ItemsSource);
        Assert.Equal("sourdough", editor.Text);
        Assert.True(editor.IsKeyboardFocused);

        Assert.True(ui.Press(editor, Key.Enter));

        // Saved, shown on the same row object and container, and focus is back in Add item.
        Assert.Same(rows, entries.ItemsSource);
        Assert.Same(container, entries.ItemContainerGenerator.ContainerFromItem(bread));
        Assert.Equal("sourdough", UiTest.Prop<string>(bread, "Text"));
        Assert.Equal("sourdough", label.Text);
        Assert.Equal(Visibility.Collapsed, editor.Visibility);
        Assert.True(addItem.IsKeyboardFocused);
        Assert.Equal(["sourdough", "jam", "butter"], ui.Database.GetListEntries(Shopping).Select(entry => entry.Text));

        // The waiting refresh comes through afterwards.
        page.Notes.Refresh();
        ui.Pump();
        Assert.Equal(["sourdough", "jam", "butter"], Shown(entries));
    });

    [Fact]
    public void Leaving_the_edit_box_saves_without_rebuilding() => Ui.Run(ui =>
    {
        ui.Database.AddListEntry(Shopping, "rice", DateTime.Now);
        var page = Make(ui);
        var rows = page.Entries.ItemsSource;
        var rice = ((IList)rows).Cast<object>().Single();
        var container = ui.Container(page.Entries, rice);
        var box = UiTest.Descendants<CheckBox>(container).Single();
        var editor = UiTest.Descendants<TextBox>(container).Single();

        ui.Focus(box);
        ui.Press(box, Key.Enter);
        editor.Text = "brown rice";
        ui.Focus(page.AddItem);

        Assert.Same(rows, page.Entries.ItemsSource);
        Assert.Equal("brown rice", UiTest.Prop<string>(rice, "Text"));
        Assert.Equal("brown rice", ui.Database.GetListEntries(Shopping).Single().Text);
    });

    [Fact]
    public void An_entry_being_edited_is_saved_when_a_folder_is_picked() => Ui.Run(ui =>
    {
        ui.Database.AddListEntry(Shopping, "rice", DateTime.Now);
        var page = Make(ui);
        var container = ui.Container(page.Entries, ((IList)page.Entries.ItemsSource)[0]!);
        var box = UiTest.Descendants<CheckBox>(container).Single();
        var editor = UiTest.Descendants<TextBox>(container).Single();
        ui.Focus(box);
        ui.Press(box, Key.Enter);
        editor.Text = "brown rice";

        page.Pick(ui, "unfiled");

        Assert.Equal("brown rice", ui.Database.GetListEntries(Shopping).Single().Text);
        Assert.Equal(Visibility.Collapsed, page.View.Visibility);
    });

    [Fact]
    public void An_undo_stays_while_other_lists_are_looked_at_and_goes_with_the_next_change() => Ui.Run(ui =>
    {
        ui.Database.AddListEntry(Shopping, "eggs", DateTime.Now);
        ui.Database.AddToList("packing", ["charger"], 0, DateTime.Now);
        var page = Make(ui);
        var bar = UiTest.Named<UndoBar>(page.View, "Bar");

        UiTest.Call(page.View, "DeleteEntry", ui.Database.GetListEntries(Shopping).Single());
        ui.Pump();
        Assert.Equal(Visibility.Visible, bar.Visibility);

        // Opening another list changes nothing, so the way back is still there.
        page.Pick(ui, ListKey("packing"));
        Assert.Equal(Visibility.Visible, bar.Visibility);
        Assert.Equal(Visibility.Visible, UiTest.Named<Button>(bar, "UndoButton").Visibility);

        // Adding to the list does change something, and the old Undo goes.
        ui.Focus(page.AddItem);
        page.AddItem.Text = "socks";
        Assert.True(ui.Press(page.AddItem, Key.Enter));
        ui.Pump();
        Assert.Equal(Visibility.Collapsed, bar.Visibility);
    });

    [Fact]
    public void Add_item_sits_right_under_the_entries_and_an_empty_list_says_where_to_type() => Ui.Run(ui =>
    {
        ui.Database.AddToList("packing", ["charger"], 0, DateTime.Now);
        var page = Make(ui);
        var empty = UiTest.Named<TextBlock>(page.View, "Empty");

        // Shopping is empty, and says how to add to it from here or from Ask.
        Assert.Equal(Visibility.Visible, empty.Visibility);
        Assert.Equal("Nothing on this list yet. Type below to add something, or say \"add milk to shopping\" in Ask.", empty.Text);

        // The box follows the entries in the same scrolling panel, not docked at the bottom of the page.
        var panel = Assert.IsType<StackPanel>(page.Entries.Parent);
        Assert.Same(panel, page.AddItem.Parent);
        Assert.True(panel.Children.IndexOf(page.AddItem) > panel.Children.IndexOf(page.Entries));

        page.Pick(ui, ListKey("packing"));
        Assert.Equal(Visibility.Collapsed, empty.Visibility);
    });

    [Fact]
    public void An_entrys_delete_button_is_a_24_px_target_shown_while_the_row_has_focus() => Ui.Run(ui =>
    {
        ui.Database.AddListEntry(Shopping, "eggs", DateTime.Now);
        var page = Make(ui);
        var container = ui.Container(page.Entries, ((IList)page.Entries.ItemsSource)[0]!);
        var delete = UiTest.Descendants<Button>(container).Single();
        var box = UiTest.Descendants<CheckBox>(container).Single();

        Assert.True(delete.ActualWidth >= 24 && delete.ActualHeight >= 24);
        Assert.Equal(0, delete.Opacity);

        ui.Focus(box);
        ui.Pump();
        Assert.Equal(1, delete.Opacity);
    });

    [Fact]
    public void Rename_and_delete_list_are_behind_one_button_and_the_columns_right_click() => Ui.Run(ui =>
    {
        ui.Database.AddToList("packing", ["charger"], 0, DateTime.Now);
        ui.Database.AddToList("camping", ["tent"], 0, DateTime.Now);
        var page = Make(ui);
        var actions = UiTest.Named<Button>(page.View, "ListActions");

        // Shopping can't be renamed or deleted, so there's nothing behind the button.
        Assert.Equal(Visibility.Collapsed, actions.Visibility);

        page.Pick(ui, ListKey("packing"));
        Assert.Equal(Visibility.Visible, actions.Visibility);
        Assert.Equal("List options", System.Windows.Automation.AutomationProperties.GetName(actions));

        var before = ui.Menus.Count;
        actions.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, actions));
        ui.Pump();
        var menu = ui.Menus[before];
        Assert.Equal(["Rename", "Delete list"], UiTest.Labels(menu));
        ui.Close(menu);

        // Right-clicking another list in the column deletes that one, and the open list stays open.
        var camping = ui.RightClick(ui.Container(page.Places, page.Entry(ListKey("camping"))))!;
        Assert.Contains("Rename", UiTest.Labels(camping));
        Assert.Contains("Delete list", UiTest.Labels(camping));
        Assert.DoesNotContain("Delete folder", UiTest.Labels(camping));
        ui.Choose(camping, "Delete list");

        Assert.DoesNotContain("camping", ui.Database.GetListOverview().Select(list => list.Name));
        Assert.DoesNotContain(ListKey("camping"), page.Keys);
        Assert.Equal("packing", UiTest.Named<TextBlock>(page.View, "ListTitle").Text);
        Assert.Equal(["charger"], Shown(page.Entries));
        Assert.Equal(Visibility.Visible, UiTest.Named<Button>(UiTest.Named<UndoBar>(page.View, "Bar"), "UndoButton").Visibility);
    });

    [Fact]
    public void A_list_deleted_from_the_column_while_notes_show_says_so_with_undo_on_the_notes_bar() => Ui.Run(ui =>
    {
        ui.Database.AddToList("camping", ["tent"], 0, DateTime.Now);
        var page = Make(ui, open: null);

        ui.Choose(ui.RightClick(ui.Container(page.Places, page.Entry(ListKey("camping"))))!, "Delete list");

        Assert.DoesNotContain("camping", ui.Database.GetListOverview().Select(list => list.Name));
        Assert.Equal(Visibility.Collapsed, page.View.Visibility);
        var bar = UiTest.Named<UndoBar>(page.Notes, "Bar");
        Assert.Equal(Visibility.Visible, bar.Visibility);
        var undo = UiTest.Named<Button>(bar, "UndoButton");
        undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, undo));
        ui.Pump();
        Assert.Contains("camping", ui.Database.GetListOverview().Select(list => list.Name));
        Assert.Contains(ListKey("camping"), page.Keys);
    });

    [Fact]
    public void Renaming_the_open_list_from_the_column_keeps_it_open() => Ui.Run(ui =>
    {
        ui.Database.AddToList("packing", ["charger"], 0, DateTime.Now);
        var page = Make(ui, "packing");

        ui.RenameAnswer = "Trip";
        ui.Choose(ui.RightClick(ui.Container(page.Places, page.Entry(ListKey("packing"))))!, "Rename");
        ui.Pump();

        Assert.Equal(ListKey("Trip"), page.Column.SelectedKey);
        Assert.Contains(ListKey("Trip"), page.Keys);
        Assert.Equal(Visibility.Visible, page.View.Visibility);
        Assert.Equal("Trip", UiTest.Named<TextBlock>(page.View, "ListTitle").Text);
        Assert.Equal(["charger"], Shown(page.Entries));
    });

    [Fact]
    public void Deleting_the_open_list_opens_shopping_and_undo_brings_it_back() => Ui.Run(ui =>
    {
        ui.Database.AddToList("packing", ["charger"], 0, DateTime.Now);
        var page = Make(ui, "packing");
        var actions = UiTest.Named<Button>(page.View, "ListActions");
        var before = ui.Menus.Count;
        actions.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, actions));
        ui.Pump();
        ui.Choose(ui.Menus[before], "Delete list");

        Assert.Equal(ListKey(Shopping), page.Column.SelectedKey);
        Assert.Equal(Shopping, UiTest.Named<TextBlock>(page.View, "ListTitle").Text);

        var undo = UiTest.Named<Button>(UiTest.Named<UndoBar>(page.View, "Bar"), "UndoButton");
        undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, undo));
        ui.Pump();

        Assert.Equal(ListKey("packing"), page.Column.SelectedKey);
        Assert.Equal("packing", UiTest.Named<TextBlock>(page.View, "ListTitle").Text);
    });

    [Fact]
    public void Start_over_shows_once_something_is_checked_and_unchecks_the_whole_list() => Ui.Run(ui =>
    {
        ui.Database.AddToList("Morning routine", ["water", "stretch"], 0, DateTime.Now);
        var page = Make(ui, "Morning routine");
        var startOver = UiTest.Named<Button>(page.View, "StartOver");
        Assert.Equal(Visibility.Collapsed, startOver.Visibility);

        ui.Database.SetEntryDone(ui.Database.GetListEntries("Morning routine")[0], true, DateTime.Now);
        page.Notes.Refresh(force: true);
        ui.Pump();
        Assert.Equal(Visibility.Visible, startOver.Visibility);

        startOver.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, startOver));
        ui.Pump();

        Assert.All(ui.Database.GetListEntries("Morning routine"), entry => Assert.False(entry.Done));
        Assert.Equal(Visibility.Collapsed, startOver.Visibility);
        var bar = UiTest.Named<UndoBar>(page.View, "Bar");
        Assert.Equal(Visibility.Visible, bar.Visibility);

        // Undo checks again just what was checked, and adds nothing to History.
        var history = ui.Database.GetHistory().Count;
        var undo = UiTest.Named<Button>(bar, "UndoButton");
        Assert.Equal(Visibility.Visible, undo.Visibility);
        undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, undo));
        ui.Pump();
        Assert.Equal([true, false], ui.Database.GetListEntries("Morning routine").Select(entry => entry.Done));
        Assert.Equal(history, ui.Database.GetHistory().Count);
        Assert.Equal(Visibility.Visible, startOver.Visibility);
    });

    [Fact]
    public void Lists_reorder_among_themselves_and_shopping_stays_first() => Ui.Run(ui =>
    {
        ui.Database.CreateList("Packing", DateTime.Now);
        ui.Database.CreateList("Errands", DateTime.Now);
        var home = ui.Database.CreateNoteFolder("Home", null, DateTime.Now).Id;
        var page = Make(ui, open: null);
        string[] Saved() => ui.Database.GetListOverview(includeEmptyShopping: true).Select(list => list.Name).ToArray();
        string[] ListKeys() => page.Keys.Where(key => key.StartsWith("list:", StringComparison.Ordinal)).ToArray();

        var menu = ui.RightClick(ui.Container(page.Places, page.Entry(ListKey("Errands"))))!;
        Assert.Contains("Move down", UiTest.Labels(menu));
        Assert.DoesNotContain("Move up", UiTest.Labels(menu));
        ui.Choose(menu, "Move down");
        ui.Pump();
        Assert.Equal(["Shopping", "Packing", "Errands"], Saved());

        // Dragging works too. Nothing can go above Shopping, onto a folder, or among the folders.
        Assert.Equal(DragDropEffects.None, ui.DragOnto(ui.Container(page.Places, page.Entry(ListKey(Shopping))), new DataObject(typeof(ColumnEntry), page.Entry(ListKey("Errands")))));
        Assert.Equal(DragDropEffects.None, ui.DragOnto(ui.Container(page.Places, page.Entry($"f{home}")), new DataObject(typeof(ColumnEntry), page.Entry(ListKey("Errands")))));
        Assert.Equal(DragDropEffects.None, ui.DragOnto(ui.Container(page.Places, page.Entry(ListKey("Packing"))), new DataObject(typeof(ColumnEntry), page.Entry($"f{home}"))));
        Assert.Equal(["Shopping", "Packing", "Errands"], Saved());

        Assert.Equal(DragDropEffects.Move, ui.DragOnto(ui.Container(page.Places, page.Entry(ListKey("Packing"))), new DataObject(typeof(ColumnEntry), page.Entry(ListKey("Errands")))));
        ui.Pump();
        Assert.Equal(["Shopping", "Errands", "Packing"], Saved());
        Assert.Equal([ListKey(Shopping), ListKey("Errands"), ListKey("Packing")], ListKeys());
        Assert.Equal(["all", "unfiled", $"f{home}"], page.Keys.Take(3));
    });

    [Fact]
    public void The_open_list_is_open_again_after_a_restart() => Ui.Run(ui =>
    {
        ui.Database.AddToList("packing", ["charger"], 0, DateTime.Now);
        Make(ui, "packing");

        // A new page over the same data stands in for Waypoint starting again.
        var again = Make(ui, open: null);

        Assert.Equal(ListKey("packing"), again.Column.SelectedKey);
        Assert.Equal(Visibility.Visible, again.View.Visibility);
        Assert.Equal("packing", UiTest.Named<TextBlock>(again.View, "ListTitle").Text);
        Assert.Equal(["charger"], Shown(again.Entries));
    });

    [Fact]
    public void A_remembered_list_that_was_removed_opens_shopping() => Ui.Run(ui =>
    {
        ui.Database.SetPreference("notes_folder", ListKey("camping"));

        var page = Make(ui, open: null);

        Assert.Equal(Visibility.Visible, page.View.Visibility);
        Assert.Equal(Shopping, UiTest.Named<TextBlock>(page.View, "ListTitle").Text);
        Assert.Equal(ListKey(Shopping), ui.Database.GetPreference("notes_folder"));
    });

    [Fact]
    public void Opening_a_list_from_elsewhere_saves_the_note_and_a_found_note_takes_the_page_back() => Ui.Run(ui =>
    {
        var id = ui.Database.CreateNote("Groceries plan", "", DateTime.Now);
        ui.Database.AddListEntry(Shopping, "eggs", DateTime.Now);
        var page = Make(ui, open: null);

        page.Notes.OpenList(Shopping);
        ui.Pump();
        Assert.Equal(Visibility.Visible, page.View.Visibility);
        Assert.Equal(["eggs"], Shown(page.Entries));

        page.Notes.ShowNote(id);
        ui.Pump();
        Assert.Equal(Visibility.Collapsed, page.View.Visibility);
        Assert.Equal("all", page.Column.SelectedKey);
        Assert.Equal("Groceries plan", UiTest.Named<TextBox>(page.Notes, "TitleBox").Text);
    });

    /// <summary>Lists and the preferences, kept the way the server keeps them. Reads for notes answer with nothing.</summary>
    private sealed class PreferenceServer : System.Net.Http.HttpMessageHandler
    {
        public Dictionary<string, string> Preferences { get; } = [];

        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            object body = new { };
            if (path == "/api/lists")
            {
                body = new[]
                {
                    new ListOverview(Shopping, []),
                    new ListOverview("packing", [new ListEntry(1, "packing", "charger", false, DateTime.Now)]),
                };
            }
            else if (path.StartsWith("/api/preferences/", StringComparison.Ordinal))
            {
                var key = Uri.UnescapeDataString(path["/api/preferences/".Length..]);
                if (request.Method == System.Net.Http.HttpMethod.Put)
                {
                    var sent = System.Text.Json.JsonSerializer.Deserialize<Waypoint.Core.Remote.PreferenceValue>(await request.Content!.ReadAsStringAsync(cancellationToken), Waypoint.Core.Remote.RemoteJson.Options);
                    Preferences[key] = sent!.Value!;
                }

                body = new Waypoint.Core.Remote.PreferenceValue(Preferences.GetValueOrDefault(key));
            }
            else if (path is "/api/notes" or "/api/notes/folders" or "/api/notes/in" or "/api/notes/spots" or "/api/notes/classes" || path.StartsWith("/api/notes/search", StringComparison.Ordinal))
            {
                body = Array.Empty<object>();
            }

            var json = System.Text.Json.JsonSerializer.Serialize(body, body.GetType(), Waypoint.Core.Remote.RemoteJson.Options);
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public void In_server_mode_the_open_list_is_remembered_on_the_server() => Ui.Run(ui =>
    {
        var server = new PreferenceServer();
        (NotesView Notes, ListBox Places) Open()
        {
            var client = new Waypoint.Core.Remote.ServerClient(new System.Net.Http.HttpClient(server) { BaseAddress = new Uri("http://server/") }, "token");
            var notes = new NotesView();
            notes.Connect(new Waypoint.Core.Remote.ServerNotesData(client), new Waypoint.Core.Remote.ServerListsData(client));
            ui.Host(notes);
            notes.Refresh(force: true);
            ui.Pump();
            return (notes, UiTest.Named<ListBox>(UiTest.Named<NestedColumn>(notes, "Column"), "Entries"));
        }

        var (_, places) = Open();
        places.SelectedItem = places.Items.Cast<ColumnEntry>().Single(entry => entry.Key == ListKey("packing"));
        ui.Pump();
        Assert.Equal(ListKey("packing"), server.Preferences["notes_folder"]);

        var (again, _) = Open();
        var view = UiTest.Named<ListsView>(again, "ListsPanel");
        Assert.Equal(Visibility.Visible, view.Visibility);
        Assert.Equal("packing", UiTest.Named<TextBlock>(view, "ListTitle").Text);
        Assert.Null(ui.Database.GetPreference("notes_folder"));
    });
}
