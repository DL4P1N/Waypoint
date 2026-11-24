using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Threading.Tasks;
using System.Windows.Threading;
using Waypoint.Core.Capture;
using Waypoint.Core.Notes;
using Waypoint.Core.Remote;
using Waypoint.Core.Transcription;

namespace Waypoint.App;

/// <summary>The notes list and Saved spots: showing them, picking a note, the note menu, moving a note to a folder, and dragging a note.</summary>
public partial class NotesView
{
    // Notes list

    private void ShowList(bool force = false)
    {
        // A list is showing instead, so the notes list catches up when it's back.
        if (ListPicked is not null)
        {
            return;
        }

        if (ShowingSpots)
        {
            ShowSpots(force);
            return;
        }

        NotesList.Visibility = Visibility.Visible;
        SpotsList.Visibility = Visibility.Collapsed;
        if (force)
        {
            _gate.Invalidate();
        }

        var search = Column.SearchText.Trim();
        var key = Column.SelectedKey;
        var folder = SelectedFolder;
        var notes = key == UnfiledKey
            ? _notes.SearchNotesInFolder(search, null)
            : folder is not null
                ? search.Length == 0 ? _notes.GetNotesInFolder(folder.Folder.Id) : _notes.SearchNotesInFolder(search, folder.Folder.Id, includeSubfolders: true)
                : search.Length == 0 ? _notes.GetNotes() : _notes.SearchNotes(search);

        var names = _folders.ToDictionary(found => found.Folder.Id, found => found.Folder.Name);
        var rows = notes.Select(note => new NoteRow(
            note,
            note.FolderId is { } id && id != folder?.Folder.Id ? names.GetValueOrDefault(id) : null,
            _transcribing.TryGetValue(note.Id, out var job) ? TranscriptionQueue.ListLabel(job) : null)).ToList();
        var signature = string.Join('\n', rows.Select(row => $"{row.Id}\t{row.Title}\t{row.Line}").Prepend($"{key}\t{search}"));
        if (!_gate.ShouldRebuild(signature, !force && (NotesList.IsKeyboardFocusWithin || _noteMenuOpen || _draggingNote)))
        {
            return;
        }

        ListTitle.Text = key == UnfiledKey ? "Unfiled" : folder?.Folder.Name ?? "All notes";
        StudyButton.Visibility = folder is not null && CanStudyFrom(folder.Folder) ? Visibility.Visible : Visibility.Collapsed;
        _loading = true;
        NotesList.ItemsSource = rows;
        NotesList.SelectedItem = rows.FirstOrDefault(row => row.Id == _draft.Id);
        _loading = false;
        NoNotes.Text = search.Length > 0 ? "No notes match that search." : folder is not null ? "No notes in this folder yet. Drag one here, or add one with +." : "No notes here.";
        NoNotes.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Editor.Visibility != Visibility.Visible)
        {
            NothingOpen.Text = NothingOpenText;
        }
    }

    private void ShowSpots(bool force)
    {
        NotesList.Visibility = Visibility.Collapsed;
        SpotsList.Visibility = Visibility.Visible;
        if (force)
        {
            _spotsGate.Invalidate();
        }

        var search = Column.SearchText.Trim();
        var spots = _notes.GetSavedSpots(search);
        var rows = spots.Select(spot => new SpotRow(spot)).ToList();
        var signature = string.Join('\n', rows.Select(row => $"{row.Spot.Id}\t{row.Title}\t{row.Line}\t{row.Spot.Next}").Prepend(search));
        if (!_spotsGate.ShouldRebuild(signature, !force && SpotsList.IsKeyboardFocusWithin))
        {
            return;
        }

        ListTitle.Text = "Saved spots";
        _loading = true;
        SpotsList.ItemsSource = rows;
        var selected = rows.FirstOrDefault(row => row.Spot.Id == _spotId);
        SpotsList.SelectedItem = selected;
        _loading = false;

        NoNotes.Text = search.Length > 0 ? "No saved spots match that search." : "No saved spots yet. Save my spot keeps one.";
        NoNotes.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selected is not null)
        {
            ShowSpot(selected.Spot);
        }
        else
        {
            NothingOpen.Text = "Pick a saved spot to see where you stopped.";
            ShowPane(NothingOpen);
        }
    }

    private void OnSpotPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SpotsList.SelectedItem is not SpotRow row)
        {
            return;
        }

        _spotId = row.Spot.Id;
        _listAskedFor = false;
        ShowSpot(row.Spot);
    }

    private void ShowSpot(Handoff spot)
    {
        SpotProject.Text = spot.Project;
        SpotWhen.Text = $"Saved {spot.CreatedAt.ToString("ddd, MMM d 'at' h:mm tt", CultureInfo.CurrentCulture)}";
        var parts = new List<SpotPart>
        {
            new("Stopped at", spot.Stopped ?? "Not written down"),
            new("Next step", spot.Next ?? "Not written down"),
        };
        if (spot.Links.Count > 0)
        {
            parts.Add(new SpotPart("Links", string.Join(Environment.NewLine, spot.Links)));
        }

        if (spot.Thoughts.Count > 0)
        {
            parts.Add(new SpotPart("Loose thoughts", string.Join(Environment.NewLine, spot.Thoughts)));
        }

        SpotParts.ItemsSource = parts;

        // Save my spot asks for the next step when there isn't one. While that question is open, it can be answered here.
        SpotNextButton.Visibility = spot.Next is null && _notes.GetWaitingSpot(DateTime.Now)?.Id == spot.Id ? Visibility.Visible : Visibility.Collapsed;
        ShowPane(SpotView);
    }

    private async void OnSpotNextClicked(object sender, RoutedEventArgs e)
    {
        var next = new RenameWindow(Window.GetWindow(this), "", "Save", "What's the next thing to do when you come back?", "Next step");
        if (next.ShowDialog() != true)
        {
            return;
        }

        var step = next.NewName;
        var reply = await Work.Run(() => _notes.SetSpotNext(step, DateTime.Now));
        Bar.Show(reply.Text.Length > 0 ? reply.Text : "That spot isn't waiting for a next step anymore.");
        Work.ShowAfterChanges(nameof(ShowList), () => ShowList(force: true), () => ShowList());
    }

    private void OnNotePicked(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || NotesList.SelectedItem is not NoteRow row || row.Id == _draft.Id)
        {
            return;
        }

        Flush();
        Bar.ClearPlain();
        _listAskedFor = false;
        OpenSoon(row.Id);
    }

    private void OnListLeft(object sender, KeyboardFocusChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => ShowSoon(), DispatcherPriority.Background);

    private void OnNoteMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        var found = e.OriginalSource as DependencyObject;
        while (found is not null and not ListBoxItem)
        {
            found = found is Visual ? VisualTreeHelper.GetParent(found) : LogicalTreeHelper.GetParent(found);
        }

        if (found is not ListBoxItem { DataContext: NoteRow row } item)
        {
            return;
        }

        var places = new List<RowMenuEntry>();
        if (row.FolderId is not null)
        {
            places.Add(new RowMenuEntry("Unfiled", () => MoveNote(row.Id, null, "Unfiled")));
        }

        places.AddRange(_folders
            .Where(shown => shown.Folder.Id != row.FolderId)
            .Select(shown => new RowMenuEntry(string.Concat(Enumerable.Repeat(Indent, shown.Depth)) + shown.Folder.Name, () => MoveNote(row.Id, shown.Folder.Id, shown.Folder.Name))));

        var lines = new List<RowMenuEntry>();
        if (places.Count > 0)
        {
            lines.Add(new RowMenuEntry("Move to folder", Children: places));
        }
        else
        {
            lines.Add(new RowMenuEntry("New folder for this note", () => NewFolderFor(row.Id)));
        }

        lines.Add(new RowMenuEntry("Delete note", () => DeleteNote(row.Id)));

        _noteMenuOpen = true;
        RowMenu.Open(item, e, lines, () =>
        {
            _noteMenuOpen = false;
            Dispatcher.BeginInvoke(() => ShowSoon(), DispatcherPriority.Background);
        });
    }

    private async void NewFolderFor(long noteId)
    {
        var name = new RenameWindow(Window.GetWindow(this), "", "Add", "Folder name", "New folder");
        if (name.ShowDialog() != true)
        {
            return;
        }

        var folderName = name.NewName;
        var made = await Work.Run(() => _notes.CreateNoteFolder(folderName, null, DateTime.Now));
        Column.ShowProblem(made.Problem);
        if (made.Problem is null)
        {
            MoveNote(noteId, made.Id, made.Name);
        }
    }

    /// <summary>Saves the open note first, so moving it never loses what was just typed.</summary>
    private async void MoveNote(long noteId, long? folderId, string folderName)
    {
        if (_draft.Id == noteId)
        {
            Flush();
        }

        // Read where the move is made, after anything saved before it, so it goes by the note as it is then.
        var before = await Work.Run(() => _notes.GetNote(noteId) is { } found && found.FolderId != folderId && _notes.MoveNoteToFolder(noteId, folderId) ? found : null);
        if (before is null)
        {
            return;
        }

        if (_draft.Id == noteId)
        {
            _draftFolder = folderId;
        }

        Bar.Show($"Moved {before.Title} to {folderName}", async () =>
        {
            await Work.Run(() => _notes.MoveNoteToFolder(noteId, before.FolderId));
            if (_draft.Id == noteId)
            {
                _draftFolder = before.FolderId;
            }

            ShowAfterChanges();
        });
        Dispatcher.BeginInvoke(() => ShowAfterChanges(), DispatcherPriority.Background);
    }

    private void OnNotePressed(object sender, MouseButtonEventArgs e)
    {
        var found = e.OriginalSource as DependencyObject;
        while (found is not null and not ListBoxItem)
        {
            found = found is Visual ? VisualTreeHelper.GetParent(found) : LogicalTreeHelper.GetParent(found);
        }

        _pressedNote = (found as ListBoxItem)?.DataContext as NoteRow;
        _pressedAt = e.GetPosition(NotesList);
    }

    /// <summary>
    /// A click on the note that's open, while the list has taken its place in a narrow window, goes back to it. Any
    /// other note opens by being picked.
    /// </summary>
    private void OnNoteReleased(object sender, MouseButtonEventArgs e)
    {
        if (_pressedNote is { } row && row.Id == _draft.Id && NoteSide.Visibility != Visibility.Visible && !ListShown)
        {
            _listAskedFor = false;
            FitList();
        }
    }

    private void OnNoteMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedNote is not { } row || e.LeftButton != MouseButtonState.Pressed)
        {
            _pressedNote = null;
            return;
        }

        var moved = e.GetPosition(NotesList) - _pressedAt;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _pressedNote = null;
        _draggingNote = true;
        try
        {
            DragDrop.DoDragDrop(NotesList, new DataObject(NoteDragFormat, row.Id), DragDropEffects.Move);
        }
        finally
        {
            _draggingNote = false;
        }
    }
}
