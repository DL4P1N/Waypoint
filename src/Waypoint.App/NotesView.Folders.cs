using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;
using Waypoint.Core.Remote;
using Waypoint.Core.Transcription;

namespace Waypoint.App;

/// <summary>The folder column: its folders and lists, adding, moving, renaming and deleting folders, and dropping a note on a folder.</summary>
public partial class NotesView
{
    // Column

    /// <summary>
    /// All and Unfiled stay at the top and Saved spots at the bottom. Folders keep the order they were dragged into,
    /// inside their parent. The lists come after the folders, Shopping first and fixed, then the others in the order
    /// they were put in, which they can be dragged among.
    /// </summary>
    private void ShowFolders(bool force = false)
    {
        var shown = new List<ShownFolder>();
        void Walk(IEnumerable<NoteFolder> level, int depth)
        {
            foreach (var folder in level)
            {
                shown.Add(new ShownFolder(folder, depth));
                Walk(folder.Children, depth + 1);
            }
        }

        Walk(_notes.GetNoteFolders(), 0);
        _folders = shown;

        var unfiled = _notes.CountNotesInFolder(null);
        var entries = new List<ColumnEntry>
        {
            new(AllKey, "All"),
            new(UnfiledKey, unfiled > 0 ? $"Unfiled  ·  {unfiled}" : "Unfiled"),
        };
        entries.AddRange(shown.Select(found =>
        {
            var name = found.Folder.Name;
            var label = found.Folder.NoteCount > 0 ? $"{name}  ·  {found.Folder.NoteCount}" : name;
            return new ColumnEntry(EntryKey(found.Folder.Id), label, Group(found.Folder.ParentId), CanRename: true, name, Depth: found.Depth);
        }));
        if (_hasLists)
        {
            var lists = ListsPanel.GetLists();
            entries.AddRange(lists.Select((list, index) =>
            {
                var shopping = list.Name == WaypointDatabase.ShoppingList;
                var label = list.Entries.Count == 0 ? list.Name : $"{list.Name}  ·  {list.OpenCount} left";
                return new ColumnEntry(ListKey(list.Name), label, shopping ? null : ListsGroup, CanRename: !shopping, list.Name, RuleAbove: index == 0, Heading: index == 0 ? "Lists" : null);
            }));

            // A list that's gone, open here or remembered from before, gives way to the one the list page moved to,
            // such as a renamed list, or else to Shopping.
            if (ListPicked is { } open && lists.All(list => list.Name != open))
            {
                var instead = lists.Any(list => list.Name == ListsPanel.Selected) ? ListsPanel.Selected : WaypointDatabase.ShoppingList;
                Column.Select(ListKey(instead));
            }
        }

        entries.Add(new ColumnEntry(SpotsKey, "Saved spots", Subtitle: "From Save my spot", RuleAbove: true));

        Column.SetEntries(entries, force);
    }

    /// <summary>
    /// Shows the list picked in the column in place of the notes list and the note, or puts the notes back. What's
    /// typed in a note is saved before a list takes its place, and an entry being edited is saved before the list goes.
    /// </summary>
    private void SyncListPanel()
    {
        if (ListPicked is { } name)
        {
            if (!ListShown)
            {
                Flush();
                Bar.ClearPlain();
            }

            if (!ListShown || ListsPanel.Selected != name)
            {
                ListsPanel.Open(name, Column.SearchText);
            }

            ListsPanel.Visibility = Visibility.Visible;
            NoteSide.Visibility = Visibility.Collapsed;
        }
        else if (ListShown)
        {
            ListsPanel.Close();
            ListsPanel.Visibility = Visibility.Collapsed;
            NoteSide.Visibility = Visibility.Visible;
        }

        Column.SearchHint = ListShown ? ListSearchHint : NotesSearchHint;
        FitList();
    }

    /// <summary>Opens a list here, for a reply's link to the Shopping list.</summary>
    public void OpenList(string name)
    {
        Column.Select(ListKey(name));
        Work.Show(() =>
        {
            ShowFolders(force: true);
            SyncListPanel();
        }, () =>
        {
            ShowFolders();
            SyncListPanel();
            ListsPanel.Refresh();
        });
    }

    /// <summary>For Find anywhere: opens the entry's list with no search hiding it, and puts focus on the entry.</summary>
    public void RevealListEntry(ListEntry entry)
    {
        Column.SetSearch("");
        OpenList(entry.List);
        ListsPanel.Reveal(entry);
    }

    /// <summary>New list from +, the same as on the old Lists page: it asks for a name, then opens the new list.</summary>
    private async void NewList()
    {
        var name = new RenameWindow(Window.GetWindow(this), "", "Add", "List name, such as Packing", "New list");
        if (name.ShowDialog() != true)
        {
            return;
        }

        var (made, problem) = await ListsPanel.MakeList(name.NewName);
        Column.ShowProblem(problem);
        if (made is null)
        {
            return;
        }

        Column.SetSearch("");
        OpenList(made);
        ListsPanel.FocusAddItem();
    }

    private void OnPlacePicked()
    {
        if (ListPicked is not null)
        {
            SyncListPanel();
            return;
        }

        SyncListPanel();
        if (ShowingSpots)
        {
            Flush();
            CloseEditor();
        }
        else if (SpotView.Visibility == Visibility.Visible)
        {
            ShowPane(NothingOpen);
        }

        Bar.ClearPlain();

        // Picking a folder is asking to see what's in it, so a list that stepped aside comes back.
        _listAskedFor = true;
        FitList();
        ShowList(force: true);
    }

    private IEnumerable<RowMenuEntry> FolderMenu(ColumnEntry entry)
    {
        // A list's Delete says what it did on the bar that's showing, so its Undo can be seen.
        if (_hasLists && ListName(entry.Key) is { } list)
        {
            return ListsPanel.ColumnMenu(list, ListShown ? null : Bar).ToList();
        }

        if (FolderId(entry.Key) is not { } id || _folders.FirstOrDefault(found => found.Folder.Id == id) is not { } shown)
        {
            return [];
        }

        var folder = shown.Folder;
        var lines = new List<RowMenuEntry>();
        var places = new List<RowMenuEntry>();
        if (folder.ParentId is not null)
        {
            places.Add(new RowMenuEntry("Top level", () => MoveFolderInto(folder, null)));
        }

        var inside = InsideOf(folder.Id);
        places.AddRange(_folders
            .Where(found => !inside.Contains(found.Folder.Id) && found.Folder.Id != folder.ParentId)
            .Select(found => new RowMenuEntry(string.Concat(Enumerable.Repeat(Indent, found.Depth)) + found.Folder.Name, () => MoveFolderInto(folder, found.Folder.Id))));
        if (places.Count > 0)
        {
            lines.Add(new RowMenuEntry("Move folder into", Children: places));
        }

        lines.AddRange(StudyMenu(folder));
        lines.Add(new RowMenuEntry("New folder inside", () => NewFolder(folder.Id)));
        lines.Add(new RowMenuEntry("Delete folder", () => DeleteFolder(folder)));
        return lines;
    }

    /// <summary>The folder and every folder inside it, which it can't be moved into.</summary>
    private HashSet<long> InsideOf(long folderId)
    {
        var inside = new HashSet<long> { folderId };
        foreach (var found in _folders)
        {
            if (found.Folder.ParentId is { } parent && inside.Contains(parent))
            {
                inside.Add(found.Folder.Id);
            }
        }

        return inside;
    }

    private async void MoveFolderInto(NoteFolder folder, long? parentId)
    {
        if (await Work.Run(() => _notes.MoveNoteFolder(folder.Id, parentId, int.MaxValue)))
        {
            Column.ShowProblem(null);
        }
        else
        {
            Column.ShowProblem("That folder can't go there.");
        }

        FoldersChanged(refocus: true);
    }

    /// <summary>The column already moved the entry, so this saves the order and redraws the tree around it.</summary>
    private async void OnFoldersReordered(string group, IReadOnlyList<string> keys)
    {
        if (group == ListsGroup)
        {
            Column.ShowProblem(await ListsPanel.OrderLists(keys.Select(ListName).OfType<string>().ToList()));
            return;
        }

        long? parent = group == "top" ? null : long.Parse(group[2..], CultureInfo.InvariantCulture);
        var ids = keys.Select(FolderId).OfType<long>().ToList();
        var hadFocus = Column.ListHasFocus;

        // All at once, so a failure partway never leaves half of the new order. The redraw shows the order kept.
        Column.ShowProblem(await Work.Run(() => _notes.ReorderNoteFolders(parent, ids)) ? null : "Those folders couldn't be put in that order.");

        Dispatcher.BeginInvoke(() => FoldersChanged(hadFocus), DispatcherPriority.Background);
    }

    /// <summary>A rename done at once says its problem to the column as it asks. One on its way to the server says it once it's back.</summary>
    private async void OnFolderRenamed(object? sender, ColumnRenameEventArgs e)
    {
        if (_hasLists && ListName(e.Entry.Key) is { } list)
        {
            var renamingList = ListsPanel.RenameList(list, e.NewName);
            if (renamingList.IsCompletedSuccessfully)
            {
                e.Problem = renamingList.Result;
                return;
            }

            Column.ShowProblem(await renamingList);
            return;
        }

        if (FolderId(e.Entry.Key) is not { } id)
        {
            return;
        }

        var name = e.NewName;
        var renaming = Work.Run(() => _notes.RenameNoteFolder(id, name));
        if (renaming.IsCompletedSuccessfully)
        {
            e.Problem = renaming.Result.Problem;
        }
        else
        {
            Column.ShowProblem((await renaming).Problem);
        }

        FoldersChanged(refocus: true);
    }

    /// <summary>Redraws the column and the list right after this page changed a folder, keeping focus on the folder if it was there.</summary>
    private void FoldersChanged(bool refocus)
    {
        Bar.Changed();
        Work.ShowAfterChanges(nameof(FoldersChanged), () =>
        {
            ShowFolders(force: true);
            ShowList(force: true);
        }, () =>
        {
            ShowFolders();
            ShowList();
        });
        if (refocus)
        {
            Dispatcher.BeginInvoke(Column.FocusSelected, DispatcherPriority.Input);
        }
    }

    private async void DeleteFolder(NoteFolder folder)
    {
        var notes = await Work.Read(() => _notes.CountNotesInFolder(folder.Id));
        var choice = NotesInFolder.Refuse;
        if (notes > 0)
        {
            var parent = folder.ParentId is { } parentId ? _folders.FirstOrDefault(found => found.Folder.Id == parentId)?.Folder.Name : null;
            var ask = new DeleteFolderWindow(Window.GetWindow(this), folder.Name, notes, parent);
            if (ask.ShowDialog() != true)
            {
                return;
            }

            choice = ask.Choice;
        }

        var result = await Work.Run(() => _notes.DeleteNoteFolder(folder.Id, choice));
        if (result == FolderDeleteResult.HasNotes)
        {
            Column.ShowProblem($"{folder.Name} has notes in it now, so it wasn't deleted.");
        }
        else if (result == FolderDeleteResult.Deleted)
        {
            if (_draftFolder == folder.Id)
            {
                _draftFolder = choice == NotesInFolder.MoveToParent ? folder.ParentId : null;
            }

            Bar.Show(notes == 0 ? $"Deleted the folder {folder.Name}" : $"Deleted the folder {folder.Name}. Its notes were kept.");
        }

        FoldersChanged(refocus: false);
    }

    private void OnAddChosen(string? choice)
    {
        switch (choice)
        {
            case NewNoteKey:
                NewNote();
                break;
            case NewFolderKey:
                NewFolder(null);
                break;
            case NewClassFolderKey:
                PickClass();
                break;
            case AnotherClassKey:
                TypeClass();
                break;
            case not null when choice.StartsWith(ClassKeyPrefix, StringComparison.Ordinal):
                MakeClassFolder(choice[ClassKeyPrefix.Length..]);
                break;
            case NewFromFileKey:
                PickFiles(null);
                break;
            case NewFromAudioKey:
                PickAudio();
                break;
            case NewRecordingKey:
                RecordRequested?.Invoke();
                break;
            case NewListKey:
                NewList();
                break;
        }
    }

    private async void NewFolder(long? parentId)
    {
        var name = new RenameWindow(Window.GetWindow(this), "", "Add", parentId is null ? "Folder name" : "Name of the folder inside", "New folder");
        if (name.ShowDialog() != true)
        {
            return;
        }

        var folderName = name.NewName;
        ShowMadeFolder(await Work.Run(() => _notes.CreateNoteFolder(folderName, parentId, DateTime.Now)));
    }

    private async void MakeClassFolder(string course) => ShowMadeFolder(await Work.Run(() => _notes.CreateClassFolder(course, DateTime.Now)));

    /// <summary>Lists the classes from settings and the feed. One that already has a folder just opens it.</summary>
    private async void PickClass()
    {
        var choices = await Work.Read(() => _notes.GetClassNames()
            .Select(course => new ColumnAddChoice(ClassKeyPrefix + course, _notes.FindClassFolder(course) is null ? course : $"{course}  ·  has a folder"))
            .ToList());
        if (choices.Count == 0)
        {
            TypeClass();
            return;
        }

        Column.OpenAddMenu(choices.Append(new ColumnAddChoice(AnotherClassKey, "Another class...")));
    }

    private void TypeClass()
    {
        var name = new RenameWindow(Window.GetWindow(this), "", "Add", "Class name, such as CS 101", "New class");
        if (name.ShowDialog() == true)
        {
            MakeClassFolder(name.NewName);
        }
    }

    private void ShowMadeFolder(FolderEdit made)
    {
        Column.ShowProblem(made.Problem);
        if (made.Problem is not null)
        {
            return;
        }

        Bar.Changed();

        Column.Select(EntryKey(made.Id));
        FoldersChanged(refocus: false);
        OnPlacePicked();
    }

    // Dropping a note on a folder

    /// <summary>A note dragged from the list goes to Unfiled or a folder. All and Saved spots aren't places a note can go.</summary>
    private void OnColumnDataDropped(object? sender, ColumnDropEventArgs e)
    {
        if (!e.Data.GetDataPresent(NoteDragFormat) || PlaceFor(e.Entry.Key) is not { } place)
        {
            return;
        }

        e.Accepted = true;
        if (e.IsDrop && e.Data.GetData(NoteDragFormat) is long noteId)
        {
            MoveNote(noteId, place.FolderId, place.Name);
        }
    }

    private Place? PlaceFor(string key) =>
        key == UnfiledKey ? new Place(null, "Unfiled")
        : FolderId(key) is { } id && _folders.FirstOrDefault(found => found.Folder.Id == id) is { } shown ? new Place(id, shown.Folder.Name)
        : null;
}
