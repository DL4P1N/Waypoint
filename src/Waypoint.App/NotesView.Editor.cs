using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Threading.Tasks;
using System.Windows.Threading;
using Waypoint.Core.Capture;
using Waypoint.Core.Notes;
using Waypoint.Core.Transcription;

namespace Waypoint.App;

/// <summary>The note editor: opening a note, the formatted and Markdown views, typing, a new note, and deleting a note.</summary>
public partial class NotesView
{
    /// <summary>Every note opens ready to type in, formatted. Show Markdown (Ctrl+E) swaps in the raw text.</summary>
    private void Open(NoteDetail? note, bool keepMode = false)
    {
        _opening = null;
        _newNote = null;
        if (note?.Id != _draft.Id)
        {
            _opens++;
        }

        _draft.Load(note);
        if (note is not null)
        {
            _draftFolder = note.FolderId;
            _draftPlacement = null;
        }

        _loading = true;
        TitleBox.Text = _draft.Title;
        TitleBox.Tag = _draftPlacement?.Title ?? TitleHint;
        if (!keepMode)
        {
            _showSource = false;
        }

        BodyText = _draft.Body;
        _loading = false;
        SavedLabel.Text = "";

        ShowMode();
        ShowNoteExtras();
        ShowPane(Editor);

        // A note opened from elsewhere, such as Find anywhere or a new recording, takes the page back from a list.
        if (note is not null && !keepMode && (ListPicked is not null || ListShown))
        {
            if (ListPicked is not null)
            {
                Column.Select(AllKey);
            }

            SyncListPanel();
            ShowList(force: true);
        }
    }

    private void CloseEditor()
    {
        _draftPlacement = null;
        Open(null);
        ShowPane(NothingOpen);
    }

    private void ShowPane(UIElement pane)
    {
        Editor.Visibility = pane == Editor ? Visibility.Visible : Visibility.Collapsed;
        SpotView.Visibility = pane == SpotView ? Visibility.Visible : Visibility.Collapsed;
        NothingOpen.Visibility = pane == NothingOpen ? Visibility.Visible : Visibility.Collapsed;
        FitList();
    }

    /// <summary>The narrowest a note is written in before the notes list steps aside, so its toolbar keeps to one line.</summary>
    public const double MinEditorWidth = 550;

    /// <summary>
    /// Below this width, a note open beside the folders (210) and the notes list (240), with its margins (48), would
    /// be narrower than <see cref="MinEditorWidth"/>, so the list steps aside while something is open. In the
    /// window's usual 1280 the page is about 830 wide, so the list steps aside there too.
    /// </summary>
    public const double NarrowWidth = 210 + 240 + 48 + MinEditorWidth;

    /// <summary>Set when the list was asked back while narrow. Opening a note puts it away again.</summary>
    private bool _listAskedFor;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => FitList();

    private void FitList()
    {
        if (ListShown)
        {
            ListColumn.Width = new GridLength(0);
            ListPane.Visibility = Visibility.Collapsed;
            ShowListButton.Visibility = Visibility.Collapsed;
            return;
        }

        var open = Editor.Visibility == Visibility.Visible || SpotView.Visibility == Visibility.Visible;
        var narrow = ActualWidth > 0 && ActualWidth < NarrowWidth;
        var hide = narrow && open && !_listAskedFor;
        ListColumn.Width = hide ? new GridLength(0) : new GridLength(240);
        ListPane.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        ShowListButton.Visibility = hide ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnShowListClicked(object sender, RoutedEventArgs e)
    {
        _listAskedFor = true;
        FitList();
        var list = ShowingSpots ? SpotsList : NotesList;
        list.Dispatcher.BeginInvoke(() => list.Focus(), DispatcherPriority.Input);
    }

    /// <summary>The note's text lives in whichever box is showing. The other is filled when it's switched to.</summary>
    private string BodyText
    {
        get => _showSource ? SourceBox.Text : BodyBox.Text;
        set
        {
            if (_showSource)
            {
                SourceBox.Text = value;
            }
            else
            {
                BodyBox.Text = value;
            }
        }
    }

    private int BodyCaret
    {
        get => _showSource ? SourceBox.CaretIndex : BodyBox.CaretIndex;
        set
        {
            if (_showSource)
            {
                SourceBox.CaretIndex = value;
            }
            else
            {
                BodyBox.CaretIndex = value;
            }
        }
    }

    /// <summary>Shows the formatted note or its Markdown text, and the toolbar only while the note is formatted.</summary>
    private void ShowMode()
    {
        BodyBox.Visibility = _showSource ? Visibility.Collapsed : Visibility.Visible;
        SourceBox.Visibility = _showSource ? Visibility.Visible : Visibility.Collapsed;
        Toolbar.Visibility = _showSource ? Visibility.Collapsed : Visibility.Visible;
        ModeButton.Content = _showSource ? "Show formatted" : "Show Markdown";
        BodyHint.Visibility = BodyText.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Ctrl+E: carries the text, the caret, and undo and redo over to the other box, so nothing is lost or moved and
    /// Ctrl+Z goes on undoing the last change, whichever box it was made in.
    /// </summary>
    private void ToggleMode()
    {
        var text = BodyText;
        var caret = BodyCaret;
        var history = _showSource ? SourceBox.History : BodyBox.History;
        _switchingToText = true;
        try
        {
            _loading = true;
            _showSource = !_showSource;
            if (_showSource)
            {
                SourceBox.Switch(text, history);
            }
            else
            {
                BodyBox.Switch(text, history);
            }

            _loading = false;

            // A link typed in the Markdown that isn't a web or mail address became its text, so that's saved.
            if (BodyText != text)
            {
                Edited();
            }

            ShowMode();
            BodyCaret = caret;
            FocusBody();
        }
        finally
        {
            _loading = false;
            _switchingToText = false;
        }
    }

    private void FocusBody()
    {
        if (_showSource)
        {
            SourceBox.UpdateLayout();
            SourceBox.Focus();
        }
        else
        {
            BodyBox.UpdateLayout();
            BodyBox.Focus();
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_showSource)
            {
                SourceBox.Focus();
                SourceBox.ScrollToCaret();
            }
            else
            {
                BodyBox.Focus();
                BodyBox.ScrollToCaret();
            }
        }, DispatcherPriority.Input);
    }

    private void OnModeClicked(object sender, RoutedEventArgs e) => ToggleMode();

    private void OnEditorKey(object sender, KeyEventArgs e)
    {
        if (HandleEditorKey(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
        }
    }

    /// <summary>Ctrl+E shows the note's Markdown text, or the formatted note again. True when the key was used.</summary>
    internal bool HandleEditorKey(Key key, ModifierKeys modifiers)
    {
        if (key == Key.E && modifiers == ModifierKeys.Control)
        {
            ToggleMode();
            return true;
        }

        return false;
    }

    private void OnBodyEdited(object? sender, EventArgs e)
    {
        BodyHint.Visibility = BodyText.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Edited();
    }

    private void OnTextEdited(object sender, TextChangedEventArgs e) => Edited();

    private void Edited()
    {
        if (_loading)
        {
            return;
        }

        _draft.Edit(TitleBox.Text, BodyText);
        SavedLabel.Text = "";
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>A new note goes in the folder that's open, or Unfiled from All. Saved spots are read only, so it opens Unfiled.</summary>
    private void NewNote()
    {
        Flush();
        Bar.ClearPlain();
        if (ShowingSpots)
        {
            Column.Select(UnfiledKey);
            ShowList(force: true);
        }
        else if (ListPicked is not null)
        {
            // From a list, a new note starts as it would from All.
            Column.Select(AllKey);
            SyncListPanel();
            ShowList(force: true);
        }

        // A picked folder wins, and so does Unfiled. Only from All, or from the class's own folder, is a note
        // started during class linked to that class and given its title.
        _draftFolder = SelectedFolder?.Folder.Id;
        var picked = Column.SelectedKey != AllKey;
        Task<NotePlacement>? placing = null;
        if (Work.RunsNow || _maker is not { } maker)
        {
            _draftPlacement = _maker?.PlaceNewNote(_draftFolder, picked, DateTime.Now) is { Course: not null } placement ? placement : null;
        }
        else
        {
            // In server mode it opens at once, and where it goes is read while the first words are typed.
            _draftPlacement = null;
            var folder = _draftFolder;
            var now = DateTime.Now;
            placing = Work.Read(() => maker.PlaceNewNote(folder, picked, now));
        }

        _listAskedFor = false;
        Open(null);
        if (placing is not null)
        {
            _newNote = new NewNoteSave(long.MinValue + ++_newNotes) { Placing = placing };
            TakePlacement(_newNote);
        }
        _loading = true;
        NotesList.SelectedItem = null;
        _loading = false;
        ShowSoon();
        TitleBox.Focus();
    }

    /// <summary>The click that took focus may still be under way, so the rows wait for it to finish.</summary>
    private void OnEditorLeft(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Editor.IsKeyboardFocusWithin || _switchingToText)
        {
            return;
        }

        if (Flush())
        {
            Dispatcher.BeginInvoke(() => Refresh(), DispatcherPriority.Background);
        }
    }

    private bool _switchingToText;

    private void OnDeleteClicked(object sender, RoutedEventArgs e) => FlushThen(() =>
    {
        if (_draft.Id is not { } id)
        {
            CloseEditor();
            return;
        }

        DeleteNote(id);
    });

    /// <summary>
    /// Saves what's typed, then carries on with the open note's ID: straight away, or in server mode once a new
    /// note's first save is back with it. Nothing is done if another note was opened meanwhile.
    /// </summary>
    private void FlushThen(Action then)
    {
        Flush();
        if (_draft.Id is null && _newNote is { } plan && _saves.LastOrDefault(save => save.Id == plan.Key) is { } making)
        {
            ThenOnceMade(plan, making, then);
            return;
        }

        then();
    }

    private async void ThenOnceMade(NewNoteSave plan, BackgroundSave making, Action then)
    {
        try
        {
            await making.Saving;
        }
        catch (Exception)
        {
            // Said once the save is marked done, the way any save's problem is.
        }

        // Marked saved first, since that was waiting on it before this. A first save that failed leaves the note open
        // with what was typed, its problem said, so it can be tried again rather than the text dropped.
        if (_newNote == plan && _draft.Id is not null)
        {
            then();
        }
    }

    /// <summary>A new note's place, once it's read, while nothing has been saved yet: its class title shows as the hint.</summary>
    private async void TakePlacement(NewNoteSave plan)
    {
        var placement = await plan.Placing!.ContinueWith(Placed, TaskScheduler.Default);
        if (_newNote == plan && _draft.Id is null && !plan.Sent)
        {
            _draftPlacement = placement is { Course: not null } ? placement : null;
            TitleBox.Tag = _draftPlacement?.Title ?? TitleHint;
        }
    }

    /// <summary>Where a read said a new note goes, or nowhere in particular if it couldn't be read.</summary>
    private static NotePlacement? Placed(Task<NotePlacement>? placing) =>
        placing is { IsCompletedSuccessfully: true } ? placing.Result : null;

    private async void DeleteNote(long id)
    {
        var typed = _draft.Title;
        if (_draft.Id == id)
        {
            // What was typed is saved first, in the page's order, so it's there to bring back with Undo.
            Flush();
            CloseEditor();
        }

        var title = await Work.Run(() =>
        {
            var stored = _notes.GetNote(id)?.Title;
            _notes.DeleteNote(id, DateTime.Now);
            return stored;
        }) ?? typed;

        Bar.Show($"Deleted {title}", async () =>
        {
            Flush();
            await Work.Run(() => _notes.RestoreNote(id, DateTime.Now));
            OpenSoon(id);
            ShowAfterChanges();
        });
        ShowAfterChanges();
    }
}
