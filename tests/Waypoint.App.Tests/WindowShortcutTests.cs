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
}
