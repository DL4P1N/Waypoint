using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Waypoint.App;

namespace Waypoint.App.Tests;

public sealed class NotesViewTests
{
    private const string NoteDragFormat = "Waypoint.NoteId";

    private sealed record Page(NotesView View, NestedColumn Column, ListBox Folders, ListBox Notes, TextBox Title, MarkdownBox Body, TextBox Search);

    private static Page Make(UiTest ui)
    {
        var view = new NotesView();
        view.Connect(ui.Database);
        ui.Host(view);
        view.Refresh(force: true);
        ui.Pump();
        var column = UiTest.Named<NestedColumn>(view, "Column");
        return new Page(
            view,
            column,
            UiTest.Named<ListBox>(column, "Entries"),
            UiTest.Named<ListBox>(view, "NotesList"),
            UiTest.Named<TextBox>(view, "TitleBox"),
            UiTest.Named<MarkdownBox>(view, "BodyBox"),
            UiTest.Named<TextBox>(column, "SearchBox"));
    }

    /// <summary>Picks the note in the list. Notes open formatted, which is where these tests type.</summary>
    private static void OpenInSource(UiTest ui, Page page, long id)
    {
        page.Notes.SelectedItem = page.Notes.Items.Cast<object>().Single(row => UiTest.Prop<long>(row, "Id") == id);
        ui.Pump();
        var mode = UiTest.Named<Button>(page.View, "ModeButton");
        if (page.Body.Visibility != Visibility.Visible)
        {
            mode.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, mode));
            ui.Pump();
        }

        Assert.Equal(Visibility.Visible, page.Body.Visibility);
    }

    /// <summary>Replaces the editor's text the way typing or pasting would, so the page sees an edit.</summary>
    private static void Type(MarkdownSourceBox body, string text)
    {
        body.SelectAll();
        body.Selection.Text = text;
        body.CaretIndex = body.Text.Length;
    }

    private static void Type(MarkdownBox body, string text)
    {
        body.SelectAll();
        body.Selection.Text = text;
        body.CaretIndex = body.Text.Length;
    }

    private static ColumnEntry Entry(ListBox folders, string label) =>
        folders.Items.Cast<ColumnEntry>().Single(entry => entry.Label.TrimStart().StartsWith(label, StringComparison.Ordinal));

    [Fact]
    public void The_app_asks_and_tells_in_its_own_window_with_staying_put_as_the_default() => Ui.Run(ui =>
    {
        var ask = new NoteConfirmWindow(null, "Replace everything?", "A copy is saved first.", "Restore", "Cancel", "Restore a backup");
        Assert.Equal("Restore a backup", ask.Title);
        Assert.Equal("Cancel", UiTest.Named<Button>(ask, "CancelButton").Content);
        Assert.True(UiTest.Named<Button>(ask, "CancelButton").IsDefault);
        Assert.Equal(Visibility.Visible, UiTest.Named<Button>(ask, "GoButton").Visibility);
        ask.Close();

        var tell = new NoteConfirmWindow(null, "Restored.", "", null, "OK", "Restore a backup");
        Assert.Equal(Visibility.Collapsed, UiTest.Named<Button>(tell, "GoButton").Visibility);
        Assert.Equal("OK", UiTest.Named<Button>(tell, "CancelButton").Content);
        tell.Close();
    });

    [Fact]
    public void Deleting_audio_and_stopping_a_transcription_ask_under_their_own_title_and_buttons() => Ui.Run(ui =>
    {
        var audio = NotesView.AskDeleteAudio(null);
        Assert.Equal("Delete audio", audio.Title);
        Assert.Equal("Keep the audio", UiTest.Named<Button>(audio, "CancelButton").Content);
        Assert.Equal("Delete audio", UiTest.Named<Button>(audio, "GoButton").Content);
        audio.Close();

        var stop = NotesView.AskStopTranscribing(null, "lecture.m4a");
        Assert.Equal("Stop transcribing", stop.Title);
        Assert.Equal("Keep going", UiTest.Named<Button>(stop, "CancelButton").Content);
        stop.Close();

        // Nothing falls back to the notes-replacing title any more.
        var plain = new NoteConfirmWindow(null, "Sure?", "", "Go");
        Assert.NotEqual("Replace the notes?", plain.Title);
        plain.Close();
    });

    [Fact]
    public void Show_Markdown_sits_beside_the_title_and_says_its_shortcut() => Ui.Run(ui =>
    {
        var page = Make(ui);
        var mode = UiTest.Named<Button>(page.View, "ModeButton");

        Assert.Same(page.Title.Parent, mode.Parent);
        Assert.Contains("Ctrl+E", mode.ToolTip as string, StringComparison.Ordinal);
    });

    [Fact]
    public void Saved_spots_is_set_apart_from_the_folders_and_says_where_they_come_from() => Ui.Run(ui =>
    {
        ui.Database.CreateNoteFolder("MATH 120", null, DateTime.Now);
        var page = Make(ui);

        var spots = Entry(page.Folders, "Saved spots");
        Assert.True(spots.RuleAbove);
        Assert.Equal("From Save my spot", spots.Subtitle);
        Assert.False(Entry(page.Folders, "MATH 120").RuleAbove);
    });

    private sealed class NeverAsked : Waypoint.Core.Notes.IClaudeRunner
    {
        public Task<string> RunAsync(string systemPrompt, string schema, string input, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Nothing should be written in this test.");
    }

    [Fact]
    public void Studying_for_an_exam_with_no_class_offers_the_folders_instead_of_stopping() => Ui.Run(ui =>
    {
        ui.Database.CreateNoteFolder("MATH 120", null, DateTime.Now);
        var view = new NotesView();
        view.Connect(new Waypoint.Core.Notes.LocalNotesData(ui.Database, studyRunner: new NeverAsked()));
        view.ConnectStudy(new Waypoint.Core.Notes.NoteAudio(ui.Database, Path.GetTempPath()));
        ui.Host(view);
        view.Refresh(force: true);
        ui.Pump();

        view.StudyForExam(new Waypoint.Core.Capture.StoredItem(5, Waypoint.Core.Capture.ItemKind.Exam, "Calc midterm", null, null, false));
        ui.Pump();

        var bar = UiTest.Named<UndoBar>(view, "Bar");
        Assert.Contains("Pick a folder of notes to study from", UiTest.Named<TextBlock>(bar, "Message").Text, StringComparison.Ordinal);
        Assert.Equal("Pick a folder...", UiTest.Named<Button>(bar, "UndoButton").Content);
        var menu = Assert.Single(ui.Menus);
        Assert.Contains(menu.Items.Cast<MenuItem>(), item => (string)item.Header == "MATH 120");
        menu.IsOpen = false;
    });

    [Fact]
    public void A_class_folder_shows_a_study_button_and_other_folders_do_not() => Ui.Run(ui =>
    {
        ui.Database.CreateClassFolder("BIO 101", DateTime.Now);
        ui.Database.CreateNoteFolder("Recipes", null, DateTime.Now);
        var view = new NotesView();
        view.Connect(new Waypoint.Core.Notes.LocalNotesData(ui.Database, studyRunner: new NeverAsked()));
        view.ConnectStudy(new Waypoint.Core.Notes.NoteAudio(ui.Database, Path.GetTempPath()));
        ui.Host(view);
        view.Refresh(force: true);
        ui.Pump();
        var column = UiTest.Named<NestedColumn>(view, "Column");
        var study = UiTest.Named<Button>(view, "StudyButton");

        var entries = UiTest.Named<ListBox>(column, "Entries");
        Assert.Equal(Visibility.Collapsed, study.Visibility);
        entries.SelectedItem = Entry(entries, "BIO 101");
        ui.Pump();
        Assert.Equal(Visibility.Visible, study.Visibility);
        entries.SelectedItem = Entry(entries, "Recipes");
        ui.Pump();
        Assert.Equal(Visibility.Collapsed, study.Visibility);
    });

    [Fact]
    public void In_a_narrow_window_the_notes_list_steps_aside_for_an_open_note_and_can_come_back() => Ui.Run(ui =>
    {
        var id = ui.Database.CreateNote("Lecture 14", "malloc and free", DateTime.Now);
        var page = Make(ui);
        page.View.Width = NotesView.NarrowWidth - 100;
        ui.Pump();
        var list = UiTest.Named<Border>(page.View, "ListPane");
        var back = UiTest.Named<Button>(page.View, "ShowListButton");
        Assert.Equal(Visibility.Visible, list.Visibility);

        page.Notes.SelectedItem = page.Notes.Items.Cast<object>().Single(row => UiTest.Prop<long>(row, "Id") == id);
        ui.Pump();
        Assert.Equal(Visibility.Collapsed, list.Visibility);
        Assert.Equal(Visibility.Visible, back.Visibility);

        back.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, back));
        ui.Pump();
        Assert.Equal(Visibility.Visible, list.Visibility);
        Assert.Equal(Visibility.Collapsed, back.Visibility);

        // With room to spare, the list always stays.
        page.View.Width = double.NaN;
        page.Notes.SelectedItem = null;
        ui.Pump();
        page.Notes.SelectedItem = page.Notes.Items.Cast<object>().Single(row => UiTest.Prop<long>(row, "Id") == id);
        ui.Pump();
        Assert.Equal(Visibility.Visible, list.Visibility);
    });

    [Fact]
    public void At_the_smallest_window_the_notes_list_takes_the_notes_place_and_the_note_is_never_squeezed() => Ui.Run(ui =>
    {
        // 1040 less the side menu and the narrower Ask is what Notes gets at the window's smallest.
        var id = ui.Database.CreateNote("Lecture 14", "malloc and free", DateTime.Now);
        var page = Make(ui);
        page.View.Width = 1040 - 128 - 260;
        page.Notes.SelectedItem = page.Notes.Items.Cast<object>().Single(row => UiTest.Prop<long>(row, "Id") == id);
        ui.Pump();
        var list = UiTest.Named<Border>(page.View, "ListPane");
        var noteSide = UiTest.Named<DockPanel>(page.View, "NoteSide");
        var show = UiTest.Named<Button>(page.View, "ShowListButton");
        var back = UiTest.Named<Button>(page.View, "BackToNoteButton");
        Assert.Equal(Visibility.Collapsed, list.Visibility);
        Assert.True(page.Body.ActualWidth >= NotesView.TightEditorWidth, $"The note is {page.Body.ActualWidth:0} wide");

        show.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, show));
        ui.Pump();
        Assert.Equal(Visibility.Visible, list.Visibility);
        Assert.Equal(Visibility.Collapsed, noteSide.Visibility);
        Assert.Equal(Visibility.Visible, back.Visibility);

        back.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, back));
        ui.Pump();
        Assert.Equal(Visibility.Collapsed, list.Visibility);
        Assert.Equal(Visibility.Visible, noteSide.Visibility);
        Assert.Equal("Lecture 14", page.Title.Text);
        Assert.True(page.Body.ActualWidth >= NotesView.TightEditorWidth, $"The note is {page.Body.ActualWidth:0} wide");
        var strip = UiTest.Named<WrapPanel>(UiTest.Named<NoteToolbar>(page.View, "Toolbar"), "Strip");
        Assert.True(strip.ActualHeight < 80, $"The toolbar wrapped to {strip.ActualHeight:0} high");
    });

    [Theory]
    [InlineData(1280 - 128 - 320, false)]
    [InlineData(NotesView.NarrowWidth + 10, true)]
    public void A_note_gets_room_for_its_toolbar_on_one_line_and_the_list_stays_only_when_it_fits(double width, bool listStays) => Ui.Run(ui =>
    {
        // 1280 less the side menu and Ask is what Notes gets in the window's usual size.
        var id = ui.Database.CreateNote("Lecture 14", "malloc and free", DateTime.Now);
        var page = Make(ui);
        page.View.Width = width;
        page.Notes.SelectedItem = page.Notes.Items.Cast<object>().Single(row => UiTest.Prop<long>(row, "Id") == id);
        ui.Pump();

        Assert.Equal(listStays ? Visibility.Visible : Visibility.Collapsed, UiTest.Named<Border>(page.View, "ListPane").Visibility);
        Assert.True(page.Body.ActualWidth >= NotesView.MinEditorWidth, $"The note is {page.Body.ActualWidth:0} wide");
        var strip = UiTest.Named<WrapPanel>(UiTest.Named<NoteToolbar>(page.View, "Toolbar"), "Strip");
        Assert.True(strip.ActualHeight < 40, $"The toolbar wrapped to {strip.ActualHeight:0} high");
    });

    [Fact]
    public void Ask_is_narrower_in_a_small_window_so_the_page_keeps_its_room()
    {
        Assert.Equal(260, ItemsWindow.AskWidthFor(1040));
        Assert.Equal(320, ItemsWindow.AskWidthFor(1280));
        Assert.Equal(260, ItemsWindow.AskWidthFor(1280, weekShown: true));
    }

    [Fact]
    public void The_editor_is_framed_like_the_other_fields_not_in_the_system_blue() => Ui.Run(ui =>
    {
        var page = Make(ui);

        // The system template draws its own blue border on hover and focus. The shared one names its border Frame.
        Assert.Same(Application.Current.Resources["RichFieldBox"], page.Body.Style);
        page.Body.ApplyTemplate();
        Assert.NotNull(page.Body.Template.FindName("Frame", page.Body));
    });

    [Fact]
    public void A_note_opens_formatted_with_the_toolbar_and_stays_that_way_when_clicked_away_from() => Ui.Run(ui =>
    {
        var id = ui.Database.CreateNote("Lab", "# Results\nThe sample turned blue", DateTime.Now);
        var page = Make(ui);
        page.Notes.SelectedItem = page.Notes.Items.Cast<object>().Single(row => UiTest.Prop<long>(row, "Id") == id);
        ui.Pump();
        var source = UiTest.Named<MarkdownSourceBox>(page.View, "SourceBox");
        var toolbar = UiTest.Named<NoteToolbar>(page.View, "Toolbar");

        Assert.Equal(Visibility.Visible, page.Body.Visibility);
        Assert.Equal(Visibility.Visible, toolbar.Visibility);
        Assert.Equal(Visibility.Collapsed, source.Visibility);
        ui.Focus(page.Body);
        ui.Focus(page.Search);
        Assert.Equal(Visibility.Visible, page.Body.Visibility);
    });

    [Fact]
    public void Show_Markdown_swaps_in_the_raw_text_and_back_without_losing_an_edit() => Ui.Run(ui =>
    {
        var id = ui.Database.CreateNote("Lab", "# Results\nThe **sample** turned blue", DateTime.Now);
        var page = Make(ui);
        page.Notes.SelectedItem = page.Notes.Items.Cast<object>().Single(row => UiTest.Prop<long>(row, "Id") == id);
        ui.Pump();
        var source = UiTest.Named<MarkdownSourceBox>(page.View, "SourceBox");
        var toolbar = UiTest.Named<NoteToolbar>(page.View, "Toolbar");
        var mode = UiTest.Named<Button>(page.View, "ModeButton");
        Assert.Equal("Show Markdown", mode.Content);

        mode.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, mode));
        ui.Pump();

        Assert.Equal(Visibility.Visible, source.Visibility);
        Assert.Equal(Visibility.Collapsed, page.Body.Visibility);
        Assert.Equal(Visibility.Collapsed, toolbar.Visibility);
        Assert.Equal("Show formatted", mode.Content);
        Assert.Equal("# Results\nThe **sample** turned blue", source.Text.Replace("\r\n", "\n"));

        Type(source, "# Results\nThe **sample** turned green");
        ui.Pump();
        Assert.True(page.View.HandleEditorKey(System.Windows.Input.Key.E, System.Windows.Input.ModifierKeys.Control));
        ui.Pump();

        Assert.Equal(Visibility.Visible, page.Body.Visibility);
        Assert.Equal("# Results\nThe **sample** turned green", page.Body.Text.Replace("\r\n", "\n"));
    });

    private static void SwitchMode(UiTest ui, Page page)
    {
        Assert.True(page.View.HandleEditorKey(System.Windows.Input.Key.E, System.Windows.Input.ModifierKeys.Control));
        ui.Pump();
    }

    [Fact]
    public void Undo_and_redo_go_on_across_Show_Markdown_through_changes_made_in_either_view() => Ui.Run(ui =>
    {
        var id = ui.Database.CreateNote("Lab", "alpha", DateTime.Now);
        var page = Make(ui);
        OpenInSource(ui, page, id);
        var source = UiTest.Named<MarkdownSourceBox>(page.View, "SourceBox");

        Type(page.Body, "alpha beta");
        ui.Pump();
        SwitchMode(ui, page);
        Type(source, "alpha beta gamma");
        ui.Pump();
        SwitchMode(ui, page);
        Assert.Equal("alpha beta gamma", page.Body.Text);

        // Back in the formatted note, Ctrl+Z undoes what was typed in the Markdown, then what was typed here.
        ApplicationCommands.Undo.Execute(null, page.Body);
        Assert.Equal("alpha beta", page.Body.Text);
        SwitchMode(ui, page);
        Assert.Equal("alpha beta", source.Text);
        ApplicationCommands.Undo.Execute(null, source);
        Assert.Equal("alpha", source.Text);
        Assert.False(ApplicationCommands.Undo.CanExecute(null, source));

        // Redo carries over the same way.
        ApplicationCommands.Redo.Execute(null, source);
        Assert.Equal("alpha beta", source.Text);
        SwitchMode(ui, page);
        ApplicationCommands.Redo.Execute(null, page.Body);
        Assert.Equal("alpha beta gamma", page.Body.Text);

        page.View.Leave();
        Assert.Equal("alpha beta gamma", ui.Database.GetNote(id)!.Body);
    });

    [Fact]
    public void Undoing_every_change_across_Show_Markdown_gives_the_note_back_exactly() => Ui.Run(ui =>
    {
        const string note = "a\r\nb\nc";
        var id = ui.Database.CreateNote("Lab", note, DateTime.Now);
        var page = Make(ui);
        OpenInSource(ui, page, id);
        var source = UiTest.Named<MarkdownSourceBox>(page.View, "SourceBox");

        Type(page.Body, "a\nb\nc changed");
        ui.Pump();
        SwitchMode(ui, page);
        ApplicationCommands.Undo.Execute(null, source);

        Assert.Equal(note, source.Text);
        SwitchMode(ui, page);
        Assert.Equal(note, page.Body.Text);
    });

    [Fact]
    public void A_link_typed_in_the_Markdown_that_is_not_a_web_address_becomes_text_and_the_note_s_own_links_stay() => Ui.Run(ui =>
    {
        const string note = "[old](file:///C:/a.pdf)";
        var id = ui.Database.CreateNote("Lab", note, DateTime.Now);
        var page = Make(ui);
        OpenInSource(ui, page, id);
        var source = UiTest.Named<MarkdownSourceBox>(page.View, "SourceBox");

        SwitchMode(ui, page);
        SwitchMode(ui, page);
        Assert.Equal(note, page.Body.Text);

        SwitchMode(ui, page);
        Type(source, note + "\n\n[new](javascript:alert(1)) and [web](https://example.com)");
        ui.Pump();
        SwitchMode(ui, page);

        Assert.Equal(note + "\n\nnew and [web](https://example.com)", page.Body.Text.Replace("\r\n", "\n"));
        page.View.Leave();
        Assert.Equal(note + "\n\nnew and [web](https://example.com)", ui.Database.GetNote(id)!.Body.Replace("\r\n", "\n"));
    });

    [Fact]
    public void Typing_in_the_editor_is_not_wiped_by_a_refresh_bringing_newer_stored_text() => Ui.Run(ui =>
    {
        var id = ui.Database.CreateNote("Lab", "first draft", DateTime.Now);
        var page = Make(ui);
        OpenInSource(ui, page, id);
        ui.Focus(page.Body);
        Type(page.Body, "first draft, and what I'm typing");
        page.Body.CaretIndex = page.Body.Text.Length;

        // The same note changed elsewhere (typing in Ask, say), and the refresh comes round.
        ui.Database.SaveNote(id, "Lab", "changed elsewhere", DateTime.Now);
        page.View.Refresh();
        ui.Pump();

        Assert.Equal("first draft, and what I'm typing", page.Body.Text);
        Assert.Equal(page.Body.Text.Length, page.Body.CaretIndex);
        Assert.True(page.Body.IsKeyboardFocused);

        // Even unfocused, a draft with unsaved typing isn't replaced, and leaving saves it.
        ui.Focus(page.Search);
        page.View.Refresh();
        ui.Pump();
        Assert.Equal("first draft, and what I'm typing", page.Body.Text);

        page.View.Leave();
        Assert.Equal("first draft, and what I'm typing", ui.Database.GetNote(id)!.Body);

        // What changed elsewhere isn't lost under the save: it's kept as its own note, as the .md sync does.
        Assert.Contains(ui.Database.GetNotes(), note => note.Title == "Lab (edited elsewhere)" && note.Body == "changed elsewhere");
    });

    [Fact]
    public void A_file_edit_synced_in_while_the_editor_had_focus_is_kept_when_typing_goes_on() => Ui.Run(ui =>
    {
        var id = ui.Database.CreateNote("Lab", "first draft", DateTime.Now);
        var page = Make(ui);
        OpenInSource(ui, page, id);
        ui.Focus(page.Body);

        // The .md file was edited and synced in while the editor sat focused and clean, so it didn't reload.
        ui.Database.SaveNote(id, "Lab", "edited in VS Code", DateTime.Now);
        page.View.Refresh();
        ui.Pump();
        Type(page.Body, "first draft, more");
        page.View.Leave();

        Assert.Equal("first draft, more", ui.Database.GetNote(id)!.Body);
        Assert.Contains(ui.Database.GetNotes(), note => note.Title == "Lab (edited elsewhere)" && note.Body == "edited in VS Code");

        // Saving again with nothing new elsewhere makes no more copies.
        Type(page.Body, "first draft, more and more");
        page.View.Leave();
        Assert.Single(ui.Database.GetNotes(), note => note.Title.EndsWith("(edited elsewhere)", StringComparison.Ordinal));
    });

    [Fact]
    public void A_focused_clean_editor_waits_and_reloads_once_focus_leaves() => Ui.Run(ui =>
    {
        var id = ui.Database.CreateNote("Lab", "", DateTime.Now);
        var page = Make(ui);
        OpenInSource(ui, page, id);
        ui.Focus(page.Title);

        ui.Database.SaveNote(id, "Lab", "added by typing in Ask", DateTime.Now);
        page.View.Refresh();
        ui.Pump();
        Assert.Equal("", page.Body.Text);
        Assert.True(page.Title.IsKeyboardFocused);

        ui.Focus(page.Search);
        page.View.Refresh();
        ui.Pump();
        Assert.Equal("added by typing in Ask", page.Body.Text);
        Assert.Equal("Lab", page.Title.Text);
    });

    [Fact]
    public void A_note_removed_elsewhere_while_typing_comes_back_on_save() => Ui.Run(ui =>
    {
        var id = ui.Database.CreateNote("Scratch", "", DateTime.Now);
        var page = Make(ui);
        OpenInSource(ui, page, id);
        ui.Focus(page.Body);
        Type(page.Body, "keep this");

        ui.Database.DeleteNote(id, DateTime.Now);
        page.View.Refresh();
        ui.Pump();
        Assert.Equal("keep this", page.Body.Text);

        page.View.Leave();
        Assert.Contains(ui.Database.GetNotes(), note => note.Body == "keep this");
    });

    [Fact]
    public void Folders_inside_folders_are_indented_and_move_among_their_siblings_with_their_children() => Ui.Run(ui =>
    {
        var school = ui.Database.CreateNoteFolder("School", null, DateTime.Now).Id;
        ui.Database.CreateNoteFolder("Math", school, DateTime.Now);
        var home = ui.Database.CreateNoteFolder("Home", null, DateTime.Now).Id;
        var page = Make(ui);

        var math = Entry(page.Folders, "Math");
        Assert.Equal("Math", math.Label);
        Assert.Equal(1, math.Depth);

        // Home's neighbor in the column is Math, but it moves above School, the folder beside it.
        var menu = ui.RightClick(ui.Container(page.Folders, Entry(page.Folders, "Home")))!;
        Assert.Contains("Move up", UiTest.Labels(menu));
        ui.Choose(menu, "Move up");
        ui.Pump();

        Assert.Equal([home, school], ui.Database.GetNoteFolders().Select(folder => folder.Id));
        Assert.Equal(["Home", "School", "Math"], page.Folders.Items.Cast<ColumnEntry>().Where(entry => entry.MoveGroup is not null).Select(entry => entry.Name));
    });

    [Fact]
    public void Dropping_a_note_on_a_folder_moves_it() => Ui.Run(ui =>
    {
        var school = ui.Database.CreateNoteFolder("School", null, DateTime.Now).Id;
        var id = ui.Database.CreateNote("Lecture 3", "body", DateTime.Now);
        var page = Make(ui);

        var target = ui.Container(page.Folders, Entry(page.Folders, "School"));
        var effect = ui.DragOnto(target, new DataObject(NoteDragFormat, id));

        Assert.Equal(DragDropEffects.Move, effect);
        Assert.Equal(school, ui.Database.GetNote(id)!.FolderId);
        Assert.Equal(1, ui.Database.CountNotesInFolder(school));
        Assert.Equal(0, ui.Database.CountNotesInFolder(null));
        Assert.Contains("School", Entry(page.Folders, "School").Label);
        Assert.Contains("1", Entry(page.Folders, "School").Label);

        // And back to Unfiled.
        ui.DragOnto(ui.Container(page.Folders, Entry(page.Folders, "Unfiled")), new DataObject(NoteDragFormat, id));
        Assert.Null(ui.Database.GetNote(id)!.FolderId);
    });

    [Fact]
    public void All_and_saved_spots_refuse_a_dropped_note() => Ui.Run(ui =>
    {
        var school = ui.Database.CreateNoteFolder("School", null, DateTime.Now).Id;
        var id = ui.Database.CreateNote("Lecture 3", "body", DateTime.Now, school);
        var page = Make(ui);

        Assert.Equal(DragDropEffects.None, ui.DragOnto(ui.Container(page.Folders, Entry(page.Folders, "All")), new DataObject(NoteDragFormat, id)));
        Assert.Equal(DragDropEffects.None, ui.DragOnto(ui.Container(page.Folders, Entry(page.Folders, "Saved spots")), new DataObject(NoteDragFormat, id)));

        Assert.Equal(school, ui.Database.GetNote(id)!.FolderId);
    });

    [Fact]
    public void A_note_being_typed_in_keeps_its_text_when_dropped_on_a_folder() => Ui.Run(ui =>
    {
        var school = ui.Database.CreateNoteFolder("School", null, DateTime.Now).Id;
        var id = ui.Database.CreateNote("Lecture 3", "", DateTime.Now);
        var page = Make(ui);
        OpenInSource(ui, page, id);
        Type(page.Body, "unsaved words");

        ui.DragOnto(ui.Container(page.Folders, Entry(page.Folders, "School")), new DataObject(NoteDragFormat, id));

        var stored = ui.Database.GetNote(id)!;
        Assert.Equal(school, stored.FolderId);
        Assert.Equal("unsaved words", stored.Body);
        Assert.Equal("unsaved words", page.Body.Text);
    });

    [Fact]
    public void An_open_note_kept_as_a_copy_keeps_saving_to_the_copy() => Ui.Run(ui =>
    {
        // What was typed while the server was away went to a copy, since the note changed there too.
        var id = ui.Database.CreateNote("Groceries", "milk from the phone", DateTime.Now);
        var copy = ui.Database.CreateNote("Groceries" + Waypoint.Core.Remote.OfflineNotes.KeptBothSuffix, "milk from the pc", DateTime.Now);
        var page = Make(ui);
        OpenInSource(ui, page, id);
        ui.Focus(page.Body);
        Type(page.Body, "milk from the pc, and more");

        page.View.FollowSent([new Waypoint.Core.Remote.OfflineNoteSent(id, copy, KeptBoth: true)]);
        page.View.Leave();

        Assert.Equal("milk from the phone", ui.Database.GetNote(id)!.Body);
        Assert.Equal("milk from the pc, and more", ui.Database.GetNote(copy)!.Body);
        Assert.Equal("Groceries" + Waypoint.Core.Remote.OfflineNotes.KeptBothSuffix, ui.Database.GetNote(copy)!.Title);
        Assert.Equal("milk from the pc, and more", page.Body.Text);
    });

    /// <summary>Rewrite sits with Add and Skip, so all three are the small row action used on rows elsewhere.</summary>
    [Fact]
    public void Rewrite_is_the_same_row_action_as_add_and_skip() => Ui.Run(ui =>
    {
        var page = Make(ui);
        var small = (Style)page.View.FindResource("SmallButton");

        Assert.Same(small, UiTest.Named<Button>(page.View, "ChangeButton").Style);
        var suggestion = (DataTemplate)UiTest.Named<ItemsControl>(page.View, "SuggestionsList").ItemTemplate;
        var row = (FrameworkElement)suggestion.LoadContent();
        var buttons = UiTest.Descendants<Button>(row).ToList();
        if (buttons.Count == 0)
        {
            buttons = LogicalTreeHelper.GetChildren(row).OfType<Button>().ToList();
        }

        Assert.Equal(["Skip", "Add"], buttons.Select(button => (string)button.Content));
        Assert.All(buttons, button => Assert.Same(small, button.Style));
    });

    /// <summary>Make new questions, Try again, and Stop sit on the same note as Edit and Delete note, so they're the same size.</summary>
    [Fact]
    public void The_status_buttons_are_sized_like_the_note_buttons() => Ui.Run(ui =>
    {
        var page = Make(ui);
        var mode = UiTest.Named<Button>(page.View, "ModeButton");

        foreach (var name in new[] { "StatusButton", "StopButton", "StudyStartButton", "AttachButton", "AudioButton" })
        {
            var button = UiTest.Named<Button>(page.View, name);
            Assert.Equal(mode.Padding, button.Padding);
            Assert.Equal(mode.FontSize, button.FontSize);
            Assert.Equal(mode.Style, button.Style);
        }
    });

    [Fact]
    public void A_note_opened_from_find_is_picked_in_the_list() => Ui.Run(ui =>
    {
        var first = ui.Database.CreateNote("Lecture 14", "malloc", DateTime.Now.AddDays(-2));
        ui.Database.CreateNote("Lecture 15", "pages", DateTime.Now.AddDays(-1));
        var page = Make(ui);

        page.View.ShowNote(first);
        ui.Pump();

        Assert.Equal("Lecture 14", page.Title.Text);
        Assert.Equal(first, UiTest.Prop<long>(page.Notes.SelectedItem!, "Id"));
    });
}
