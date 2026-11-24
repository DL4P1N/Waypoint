using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Waypoint.App;

namespace Waypoint.App.Tests;

public sealed class WindowShortcutTests
{
    private static ItemsWindow Shown(UiTest ui)
    {
        var window = new ItemsWindow(ui.Database, _ => { }, () => { }, _ => { })
        {
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
        };
        window.ShowNotes();
        ui.Pump();
        return window;
    }

    // Keyboard.Modifiers can't be faked without real input, so the window's Ctrl shortcuts are asked for directly.
    private static bool CtrlKey(ItemsWindow window, Key key) => (bool)UiTest.Call(window, "HandleShortcut", key)!;

    [Fact]
    public void Ctrl_K_in_a_note_is_left_for_the_link_box_and_finds_anywhere_elsewhere() => Ui.Run(ui =>
    {
        var id = ui.Database.CreateNote("Lecture 3", "Some words", DateTime.Now);
        var window = Shown(ui);
        var notes = UiTest.Named<NotesView>(window, "NotesPage");
        notes.ShowNote(id);
        ui.Pump();
        var find = UiTest.Named<FindView>(window, "FindPage");

        var body = UiTest.Named<MarkdownBox>(notes, "BodyBox");
        ui.Focus(body);
        Assert.False(CtrlKey(window, Key.K));
        Assert.Equal(Visibility.Collapsed, find.Visibility);

        ui.Focus(UiTest.Named<TextBox>(window, "Input"));
        Assert.True(CtrlKey(window, Key.K));
        Assert.Equal(Visibility.Visible, find.Visibility);
        window.Close();
    });

    [Fact]
    public void Picking_the_page_find_anywhere_covers_goes_back_to_it_by_click_or_Ctrl_number() => Ui.Run(ui =>
    {
        var window = Shown(ui);
        var find = UiTest.Named<FindView>(window, "FindPage");
        var notes = UiTest.Named<NotesView>(window, "NotesPage");
        var menu = UiTest.Named<ListBox>(window, "Menu");

        Assert.True(CtrlKey(window, Key.K));
        ui.Pump();
        Assert.Equal(Visibility.Visible, find.Visibility);
        Assert.Equal(-1, menu.SelectedIndex);

        // Ctrl+2 is Notes, the page Find stood in for.
        Assert.True(CtrlKey(window, Key.D2));
        ui.Pump();
        Assert.Equal(Visibility.Collapsed, find.Visibility);
        Assert.Equal(Visibility.Visible, notes.Visibility);
        Assert.Equal(1, menu.SelectedIndex);

        // A click on Notes in the side menu does the same.
        Assert.True(CtrlKey(window, Key.K));
        ui.Pump();
        menu.SelectedIndex = 1;
        ui.Pump();
        Assert.Equal(Visibility.Collapsed, find.Visibility);
        Assert.Equal(Visibility.Visible, notes.Visibility);
        window.Close();
    });

    [Fact]
    public void Esc_from_find_anywhere_puts_the_keyboard_back_where_it_was() => Ui.Run(ui =>
    {
        var window = Shown(ui);
        var find = UiTest.Named<FindView>(window, "FindPage");
        var ask = UiTest.Named<TextBox>(window, "Input");
        ui.Focus(ask);

        Assert.True(CtrlKey(window, Key.K));
        ui.Pump();
        var search = UiTest.Named<TextBox>(find, "SearchBox");
        Assert.True(search.IsKeyboardFocused);

        Assert.True(ui.Press(search, Key.Escape));
        ui.Pump();
        Assert.Equal(Visibility.Collapsed, find.Visibility);
        Assert.True(ask.IsKeyboardFocused);
        window.Close();
    });
}
