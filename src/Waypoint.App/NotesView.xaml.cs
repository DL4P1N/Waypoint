using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Waypoint.Core;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;
using Waypoint.Core.Remote;
using Waypoint.Core.Transcription;

namespace Waypoint.App;

/// <summary>
/// Notes, edited by hand and saved shortly after typing stops. Only <see cref="NoteDraft"/> decides
/// whether stored text may replace the editor, so a refresh never touches what's being typed.
/// The column holds All, Unfiled, the folders, the lists, and a read-only Saved spots view. Notes show as
/// formatted Markdown, with Edit switching to the text itself. Picking a list puts <see cref="ListsView"/> where
/// the notes list and the note were, and the column's search then looks through that list.
/// </summary>
public partial class NotesView : UserControl
{
    private const string FolderPreference = "notes_folder";
    private const string AllKey = "all";
    private const string UnfiledKey = "unfiled";
    private const string SpotsKey = "spots";
    private const string NewNoteKey = "note";
    private const string NewFolderKey = "folder";
    private const string NewClassFolderKey = "classfolder";
    private const string ClassKeyPrefix = "class:";
    private const string AnotherClassKey = "anotherclass";
    private const string NewFromFileKey = "fromfile";
    private const string NewFromAudioKey = "fromaudio";
    private const string NewRecordingKey = "record";
    private const string NewListKey = "list";
    private const string ListKeyPrefix = "list:";
    private const string ListsGroup = "lists";
    private const string NotesSearchHint = "Search notes";
    private const string ListSearchHint = "Search this list";
    private const string AudioFilter = "Recordings|*.mp3;*.m4a;*.mp4;*.wav;*.wma;*.aac;*.flac;*.m4v;*.mov;*.wmv;*.3gp;*.ogg;*.opus|All files|*.*";
    private const string NothingOpenText = "Pick a note, or start a new one with +. Drop a PDF, slides, or a recording here to make notes from them.";
    private const string TitleHint = "Title";
    private const string FileFilter = "PDFs, slides, and documents|*.pdf;*.pptx;*.docx;*.txt;*.md|All files|*.*";
    private const string NoteDragFormat = "Waypoint.NoteId";
    private const string Indent = "   ";

    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    private static readonly ColumnAddChoice[] AddChoices =
    [
        new(NewNoteKey, "Note"),
        new(NewFromFileKey, "Notes from a file..."),
        new(NewRecordingKey, "Record lecture..."),
        new(NewFromAudioKey, "Import audio..."),
        new(NewFolderKey, "Folder", InColumnMenu: true),
        new(NewClassFolderKey, "Folder for a class"),
        new(NewListKey, "List", InColumnMenu: true),
    ];

    private readonly RefreshGate _gate = new();
    private readonly RefreshGate _spotsGate = new();
    private readonly NoteDraft _draft = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = SaveDelay };
    private INotesData _notes = null!;
    private bool _hasLists;
    private IReadOnlyList<ShownFolder> _folders = [];
    private long? _draftFolder;
    private long? _spotId;
    private bool _showSource;
    private bool _loading;
    private bool _noteMenuOpen;
    private bool _draggingNote;
    private NoteRow? _pressedNote;
    private Point _pressedAt;

    // Notes from files. Writing runs one note at a time, and each note's state lives here so leaving and
    // coming back shows it again.
    private readonly Dictionary<long, WriteJob> _jobs = [];
    private readonly Dictionary<long, string> _failures = [];
    private readonly SemaphoreSlim _writeTurn = new(1, 1);
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private TranscriptionQueue? _transcription;
    private IReadOnlyDictionary<long, TranscriptionJob> _transcribing = new Dictionary<long, TranscriptionJob>();
    private NotePlacement? _draftPlacement;
    private NewNoteSave? _newNote;
    private long _newNotes;
    private long? _extrasFor;
    private string _sourcesShown = "";
    private int _sourceCount;

    private PageWork _work = PageWork.Local;

    /// <summary>
    /// How the page reads and changes its data: straight away on this PC, or in order without holding the window in
    /// server mode. The column and the lists on this page go in the same order.
    /// </summary>
    internal PageWork Work
    {
        get => _work;
        set
        {
            _work = value;
            Column.Work = value;
            ListsPanel.Work = value;
        }
    }

    /// <summary>Draws the page again, at once from what was kept in server mode, then from what's read.</summary>
    private void ShowSoon(bool force = false) => Work.Show(() => Refresh(force), () => Refresh());

    /// <summary>Draws the page again once this page's changes are done, so it never shows a moment between two of them.</summary>
    private void ShowAfterChanges(bool force = true) => Work.ShowAfterChanges(nameof(Refresh), () => Refresh(force), () => Refresh());

    public NotesView()
    {
        InitializeComponent();
        Toolbar.Attach(BodyBox);
        _saveTimer.Tick += (_, _) => SaveSoon();
        _clock.Tick += (_, _) => ShowStatus();
    }

    /// <summary>Notes from files and class linking. Without them the page still works for typed notes.</summary>
    private INotesData? _maker => _notes is { CanMakeNotes: true } notes ? notes : null;

    /// <summary>Turns on making notes from recordings. Changes come from the background, so they're shown on the page's thread.</summary>
    public void ConnectTranscription(TranscriptionQueue queue)
    {
        _transcription = queue;
        _transcribing = queue.OpenJobsByNote();
        queue.Changed += change => Dispatcher.BeginInvoke(() => OnTranscriptionChanged(change));
        if (_transcribing.Values.Any(job => job.IsActive))
        {
            _clock.Start();
        }
    }

    public void Connect(WaypointDatabase database) => Connect(new LocalNotesData(database), new LocalListsData(database));

    /// <summary>
    /// The notes and lists on this PC, or on the server in server mode. Both work the same. Without
    /// <paramref name="lists"/> the column has no lists.
    /// </summary>
    public void Connect(INotesData notes, IListsData? lists = null)
    {
        _notes = notes;
        _hasLists = lists is not null;
        if (lists is not null)
        {
            ListsPanel.Connect(lists);
            ListsPanel.ListsChanged += () =>
            {
                ShowFolders(force: true);
                SyncListPanel();
            };
            ListsPanel.ListOpened += name =>
            {
                if (ListShown)
                {
                    Column.Select(ListKey(name));
                }
            };
        }

        Column.Connect(notes, FolderPreference);
        Column.AddChoices = AddChoices.Where(choice => _hasLists || choice.Key != NewListKey).ToList();
        Column.QuickAdd = AddChoices[0];
        Column.ExtraMenu = FolderMenu;
        Column.SelectionChanged += _ => OnPlacePicked();
        Column.SearchChanged += text =>
        {
            if (ListShown)
            {
                ListsPanel.SetSearch(text);
            }
            else
            {
                Work.Show(() => ShowList(force: true), () => ShowList());
            }
        };
        Column.AddChosen += OnAddChosen;
        Column.Reordered += OnFoldersReordered;
        Column.RenameRequested += OnFolderRenamed;
        Column.DataDropped += OnColumnDataDropped;
    }

    public void Refresh(bool force = false)
    {
        if (_draft.Id is { } id)
        {
            var stored = _notes.GetNote(id);
            if (_draft.ShouldReload(stored, Editor.IsKeyboardFocusWithin))
            {
                if (stored is null)
                {
                    CloseEditor();
                }
                else
                {
                    Open(stored, keepMode: true);
                }
            }
        }

        ShowFolders(force);
        var listWasShown = ListShown;
        SyncListPanel();
        if (ListShown)
        {
            if (listWasShown)
            {
                ListsPanel.Refresh(force);
            }
        }
        else
        {
            ShowList(force || listWasShown);
        }
    }

    /// <summary>
    /// After typing kept while the server was away was saved. A note that changed there too kept what was typed
    /// here as a copy, so an editor still open on it goes on as the copy, and never saves over the server's version.
    /// </summary>
    public void FollowSent(IReadOnlyList<Waypoint.Core.Remote.OfflineNoteSent> sent)
    {
        foreach (var moved in sent.Where(note => note.KeptBoth))
        {
            if (_draft.Id == moved.Id)
            {
                _draft.MoveTo(moved.NoteId);

                // The copy's title says where it came from, unless a new title was typed meanwhile.
                if (_notes.GetNote(moved.NoteId) is { } copy
                    && (string.IsNullOrWhiteSpace(_draft.Title) || copy.Title == _draft.Title.Trim() + Waypoint.Core.Remote.OfflineNotes.KeptBothSuffix))
                {
                    _loading = true;
                    TitleBox.Text = copy.Title;
                    _loading = false;
                    _draft.Edit(copy.Title, _draft.Body);
                }
            }

            (_notes as Waypoint.Core.Remote.ServerNotesData)?.Followed(moved.Id);
        }
    }

    /// <summary>Saves anything waiting, for when the page or the window goes away.</summary>
    public void Leave()
    {
        // Closing: a save still on its way that the server turned down is noted in the log rather than stopping the
        // rest of closing, and what it sent counts as not saved, so it's saved again here.
        var finished = false;
        if (Work.RunsNow)
        {
            foreach (var pending in _saves.ToList())
            {
                try
                {
                    finished |= FinishBackgroundSave(pending) == true;
                }
                catch (Exception problem)
                {
                    PlainProblem.Report(problem);
                }
            }
        }

        if (Flush() || finished)
        {
            Refresh();
        }

        Bar.Clear();
        if (_hasLists)
        {
            ListsPanel.Leave();
        }
    }

    private bool ShowingSpots => Column.SelectedKey == SpotsKey;

    /// <summary>The folder that's open, or null for All, Unfiled, and Saved spots.</summary>
    private ShownFolder? SelectedFolder => FolderId(Column.SelectedKey) is { } id ? _folders.FirstOrDefault(folder => folder.Folder.Id == id) : null;

    private static string EntryKey(long folderId) => $"f{folderId}";

    private static long? FolderId(string? key) =>
        key is ['f', .. var digits] && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    private static string Group(long? parentId) => parentId is { } parent ? $"in{parent}" : "top";

    private static string ListKey(string name) => ListKeyPrefix + name;

    private static string? ListName(string? key) =>
        key is not null && key.StartsWith(ListKeyPrefix, StringComparison.Ordinal) ? key[ListKeyPrefix.Length..] : null;

    /// <summary>The list picked in the column, or null when it's a place for notes.</summary>
    private string? ListPicked => _hasLists ? ListName(Column.SelectedKey) : null;

    /// <summary>True while a list is on the page in place of the notes.</summary>
    private bool ListShown => ListsPanel.Visibility == Visibility.Visible;

    /// <summary>Raised by Record lecture on +. The app's recorder asks before anything records.</summary>
    public event Action? RecordRequested;

    /// <summary>For Find anywhere: saves what's being typed, then opens the note.</summary>
    public void ShowNote(long noteId)
    {
        Flush();
        FoldersChanged(refocus: false);
        OpenSoon(noteId, pickRow: true, keepWhenGone: true);
    }

    /// <summary>The note being opened while it's read from the server, when nothing was kept for it yet.</summary>
    private long? _opening;

    /// <summary>
    /// Opens a note. In server mode it opens at once from what was kept, and again from what's read if nobody has
    /// started on it meanwhile, by the same rule a refresh follows. With nothing kept the editor waits while it's
    /// read, so nothing is typed into the wrong note.
    /// </summary>
    private async void OpenSoon(long id, bool pickRow = false, bool keepWhenGone = false)
    {
        _opening = id;
        void Opened(NoteDetail? note)
        {
            if (note is null && keepWhenGone)
            {
                _opening = null;
                return;
            }

            Open(WithPendingSave(note));
            if (pickRow)
            {
                PickOpenRow();
            }
        }

        Work.Show(() =>
        {
            if (_opening == id && _notes.GetNote(id) is var kept && (kept is not null || Work.RunsNow))
            {
                Opened(kept);
            }
        }, () => ReloadIfAllowed(id));
        if (_opening != id)
        {
            return;
        }

        CloseEditor();
        _opening = id;
        NothingOpen.Text = "Opening the note...";
        NoteDetail? read;
        try
        {
            read = await Work.Read(() => _notes.GetNote(id));
        }
        catch
        {
            if (_opening == id)
            {
                _opening = null;
                NothingOpen.Text = NothingOpenText;
            }

            throw;
        }

        if (_opening == id)
        {
            NothingOpen.Text = NothingOpenText;
            Opened(read);
        }
    }

    /// <summary>The note on show, read again, replaces the editor only when the editor's own rule allows it.</summary>
    private void ReloadIfAllowed(long id)
    {
        if (_draft.Id != id)
        {
            return;
        }

        if (WithPendingSave(_notes.GetNote(id)) is { } stored && _draft.ShouldReload(stored, Editor.IsKeyboardFocusWithin))
        {
            Open(stored, keepMode: true);
        }
        else
        {
            ShowNoteExtras();
        }
    }

    /// <summary>
    /// The note as it will be once the saves still on their way for it are done, so a note opened again while its
    /// save is held shows what was typed, and goes on from that text rather than taking it for a change made elsewhere.
    /// </summary>
    private NoteDetail? WithPendingSave(NoteDetail? note)
    {
        if (note is null)
        {
            return null;
        }

        var last = _saves.LastOrDefault(save => save.Id == note.Id || (_madeAgain.TryGetValue(save.Id, out var again) && again == note.Id));
        return last is null ? note : note with { Title = last.SentTitle, Body = last.Body };
    }

    /// <summary>Picks the open note's row in the list, as clicking it would, when it was opened from elsewhere.</summary>
    private void PickOpenRow()
    {
        if (NotesList.Items.Cast<object>().OfType<NoteRow>().FirstOrDefault(row => row.Id == _draft.Id) is not { } row)
        {
            return;
        }

        _loading = true;
        NotesList.SelectedItem = row;
        _loading = false;
        NotesList.ScrollIntoView(row);
    }

    /// <summary>Opens the note a finished recording became, while it's transcribed.</summary>
    public async void ShowRecordedNote(long noteId)
    {
        Flush();
        RefreshTranscriptions();
        FoldersChanged(refocus: false);
        var (note, place) = await Work.Read(() =>
        {
            var note = _notes.GetNote(noteId);
            return (note, PlaceName(note?.FolderId));
        });

        // Anything typed while it was read is saved before it opens.
        Flush();
        Open(note);
        Bar.Show($"Recording saved as {note?.Title} in {place}. It's being turned into notes on this PC.");
    }

    /// <summary>The name of the folder a note was put in, for saying where it went. Asks the server in server mode.</summary>
    private string PlaceName(long? folderId) => folderId is { } folder ? _notes.GetNoteFolder(folder)?.Name ?? "Unfiled" : "Unfiled";

    /// <summary>Every note opens ready to type in, formatted. Show Markdown (Ctrl+E) swaps in the raw text.</summary>
    private void Open(NoteDetail? note, bool keepMode = false)
    {
        _opening = null;
        _newNote = null;
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

    // Notes from files

    private void PickFiles(long? attachTo)
    {
        if (_maker is null)
        {
            Bar.Show("Notes from files aren't available right now.");
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = FileFilter,
            Multiselect = attachTo is null,
            Title = attachTo is null ? "Choose files to make notes from" : "Choose a file to add to this note",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        if (attachTo is { } id)
        {
            AttachFiles(id, dialog.FileNames);
        }
        else
        {
            MakeNotesFrom(dialog.FileNames);
        }
    }

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        e.Effects = _maker is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (_maker is not null && Editor.Visibility != Visibility.Visible)
        {
            NothingOpen.Text = "Let go to make notes from this.";
        }
    }

    private void OnFileDragLeave(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && Editor.Visibility != Visibility.Visible && !ShowingSpots)
        {
            NothingOpen.Text = NothingOpenText;
        }
    }

    /// <summary>A file dropped anywhere on the page makes a new note. Adding to an open note is the note's own "Add a file".</summary>
    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        e.Handled = true;
        OnFileDragLeave(sender, e);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            // Lets the drag finish in Explorer before anything slow starts. Recordings are transcribed, and other files read.
            var recordings = paths.Where(TranscriptionQueue.IsAudioFile).ToList();
            var files = paths.Where(path => !TranscriptionQueue.IsAudioFile(path)).ToList();
            Dispatcher.BeginInvoke(() =>
            {
                if (recordings.Count > 0)
                {
                    ImportAudio(recordings);
                }

                if (files.Count > 0)
                {
                    MakeNotesFrom(files);
                }
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// One note per file. Each file's text is saved on its note before Haiku is asked anything, so a failed
    /// call loses nothing. The last note made opens while its notes are written.
    /// </summary>
    private async void MakeNotesFrom(IReadOnlyList<string> paths)
    {
        if (_maker is not { } maker)
        {
            Bar.Show("Notes from files aren't available right now.");
            return;
        }

        Flush();
        var picked = SelectedFolder?.Folder.Id;
        var problems = new List<string>();
        var made = new List<StartedNote>();
        Bar.Show(paths.Count == 1 ? $"Reading {System.IO.Path.GetFileName(paths[0])}..." : $"Reading {paths.Count} files...");
        foreach (var path in paths)
        {
            var file = await Task.Run(() => ImageText.ReadFile(path));
            var started = await Work.Run(() => maker.StartFromFile(file, picked, picked is not null, DateTime.Now));
            if (started.Problem is { } problem)
            {
                problems.Add(problem);
                continue;
            }

            made.Add(started);
            _ = WriteNotesAsync(started.NoteId, null);
        }

        if (made.LastOrDefault() is { } last)
        {
            // Anything typed while the files were read is saved before the new note opens.
            Flush();
            var note = await Work.Read(() => _notes.GetNote(last.NoteId));
            Flush();
            Open(note);
        }

        FoldersChanged(refocus: false);
        var lines = (await Work.Read(() => made.Select(started => $"Made {started.Title} in {PlaceName(started.FolderId)}.").ToList())).Concat(problems).ToList();
        if (made.Count > 0)
        {
            lines.Add("The notes are being written now.");
        }

        Bar.Show(string.Join(" ", lines));
    }

    private void OnAttachClicked(object sender, RoutedEventArgs e) => FlushThen(() =>
    {
        if (_draft.Id is { } id)
        {
            PickFiles(id);
        }
        else
        {
            // Nothing typed yet, so there's no note to add to. The file makes one instead.
            PickFiles(null);
        }
    });

    private async void AttachFiles(long noteId, IReadOnlyList<string> paths)
    {
        if (_maker is not { } maker)
        {
            return;
        }

        var lines = new List<string>();
        foreach (var path in paths)
        {
            var file = await Task.Run(() => ImageText.ReadFile(path));
            var attached = await Work.Run(() => maker.AttachFile(noteId, file, DateTime.Now));
            lines.Add(attached.Problem ?? $"Added {file.Name} to Sources.");
        }

        ShowExtrasIfOpen(noteId);
        var generation = await Work.Read(() => _notes.GetNoteGeneration(noteId));
        if (generation is { SourceCount: > 0 } && lines.Any(line => line.StartsWith("Added", StringComparison.Ordinal)))
        {
            lines.Add(generation.WasGenerated ? "Choose Write again to use it in the notes." : "Choose Write notes to make notes from it.");
        }

        Bar.Show(string.Join(" ", lines));
        ShowSoon(force: true);
    }

    /// <summary>The open note's status, sources and suggestions drawn again, if it's this one, from what's read in server mode.</summary>
    private void ShowExtrasIfOpen(long noteId) => Work.Show(() =>
    {
        if (_draft.Id == noteId)
        {
            ShowNoteExtras();
        }
    });

    /// <summary>
    /// Asks before notes replace text the user has in the note. Text they changed after notes were written is
    /// replaced only on a yes. Text typed by hand before any notes were written is kept as a source.
    /// </summary>
    private async Task<bool> ConfirmReplace(long noteId)
    {
        if (await Work.Read(() => _notes.GetNoteGeneration(noteId)) is not { EditedSince: true } generation)
        {
            return true;
        }

        var ask = generation.WasGenerated
            ? new NoteConfirmWindow(
                Window.GetWindow(this),
                "Replace your changes to these notes?",
                "You changed these notes after they were written. New notes are written from the files in Sources, so your changes would be replaced. You can undo right after.",
                "Replace them",
                title: "Replace your changes")
            : new NoteConfirmWindow(
                Window.GetWindow(this),
                "Write notes from the files?",
                "This note already has your own text. It's kept in Sources and used along with the files, so nothing you typed is lost.",
                "Write notes",
                title: "Write notes");
        return ask.ShowDialog() == true;
    }

    /// <summary>The user said yes before this is called, or there was nothing of theirs to replace.</summary>
    private async Task WriteNotesAsync(long noteId, string? request)
    {
        if (_maker is not { } maker || _jobs.ContainsKey(noteId))
        {
            return;
        }

        var job = new WriteJob(DateTime.Now, request);
        _jobs[noteId] = job;
        _failures.Remove(noteId);
        _clock.Start();
        if (_draft.Id == noteId)
        {
            ShowNoteExtras();
        }

        NoteWriteOutcome outcome;
        try
        {
            await _writeTurn.WaitAsync(job.Stop.Token);
            try
            {
                job.StartedAt = DateTime.Now;
                ShowStatus();
                outcome = await Task.Run(() => NoteJobText.GuardNotes(() => maker.WriteNotesAsync(noteId, request, replaceEdits: true, DateTime.Now, job.Stop.Token), job.Stop.Token));
            }
            finally
            {
                _writeTurn.Release();
            }
        }
        catch (OperationCanceledException)
        {
            outcome = new NoteWriteOutcome(NoteWriteStatus.Failed, "Stopped before the notes were written. The files are saved in Sources, so you can try again.");
        }
        finally
        {
            _jobs.Remove(noteId);
            job.Stop.Dispose();
            if (_jobs.Count == 0 && !_transcribing.Values.Any(found => found.IsActive))
            {
                _clock.Stop();
            }
        }

        await Finish(noteId, outcome);
    }

    private async Task Finish(long noteId, NoteWriteOutcome outcome)
    {
        var title = (await Work.Read(() => _notes.GetNote(noteId)))?.Title ?? "The note";
        switch (outcome.Status)
        {
            case NoteWriteStatus.Written:
                var before = outcome.Before;
                Action? undo = before is not null && !string.IsNullOrWhiteSpace(before.Body)
                    ? () => RestoreBody(noteId, before)
                    : null;
                Bar.Show($"{title}: {outcome.Message}", undo);
                break;

            case NoteWriteStatus.ChangedWhileWriting when outcome.Notes is { } notes && _maker is { } maker:
                if (_draft.Id == noteId)
                {
                    Flush();
                }

                var ask = new NoteConfirmWindow(
                    Window.GetWindow(this),
                    $"{title} changed while new notes were being written. Use the new notes?",
                    "Keep my version leaves your text as it is. The files stay in Sources, so you can rewrite later.",
                    "Use the new notes",
                    title: "New notes are ready");
                if (ask.ShowDialog() == true)
                {
                    await Finish(noteId, await Work.Run(() => maker.SaveWrittenAnyway(noteId, notes, DateTime.Now)));
                    return;
                }

                Bar.Show($"Kept your version of {title}.");
                break;

            case NoteWriteStatus.Failed:
                _failures[noteId] = outcome.Message;
                if (_draft.Id != noteId)
                {
                    Bar.Show($"{title}: {outcome.Message}");
                }

                break;

            default:
                Bar.Show(outcome.Message);
                break;
        }

        ReloadIfOpen(noteId);
        ShowSoon(force: true);
    }

    /// <summary>Undo for notes written over a note: its text from before goes back, after anything saved ahead of it.</summary>
    private async void RestoreBody(long noteId, NoteBodyState before)
    {
        await Work.Run(() => _notes.RestoreNoteBody(noteId, before, DateTime.Now));
        ReloadIfOpen(noteId);
        ShowSoon(force: true);
    }

    /// <summary>
    /// The editor is read only while a note's notes are written, so nothing typed is waiting to be saved here. In
    /// server mode it's read after anything saved ahead of it, and opened again unless something was typed meanwhile.
    /// </summary>
    private async void ReloadIfOpen(long noteId)
    {
        if (_draft.Id != noteId)
        {
            return;
        }

        if (_draft.IsDirty)
        {
            Flush();
        }

        var (title, body) = (_draft.Title, _draft.Body);
        var stored = await Work.Read(() => _notes.GetNote(noteId));
        if (stored is not null && _draft.Id == noteId && _draft.Title == title && _draft.Body == body)
        {
            Open(WithPendingSave(stored), keepMode: true);
        }
    }

    /// <summary>The status line, the change box, the suggestions, and Sources for the open note.</summary>
    private void ShowNoteExtras()
    {
        var id = _draft.Id;
        var opened = id != _extrasFor;
        _extrasFor = id;
        if (id is not { } noteId || _maker is null)
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            ChangePanel.Visibility = Visibility.Collapsed;
            SuggestionsPanel.Visibility = Visibility.Collapsed;
            SourcesSection.Visibility = Visibility.Collapsed;
            SourcesSection.IsExpanded = false;
            AttachButton.Visibility = _maker is null ? Visibility.Collapsed : Visibility.Visible;
            TitleBox.IsReadOnly = BodyBox.IsReadOnly = SourceBox.IsReadOnly = false;
            _sourceCount = 0;
            _sourcesShown = "";
            ShowNoteActions(null);
            return;
        }

        var sources = _notes.GetNoteSources(noteId);
        _sourceCount = sources.Count;
        var writing = _jobs.ContainsKey(noteId) || TranscriptionFor(noteId) is { Status: TranscriptionStatus.Writing };
        TitleBox.IsReadOnly = BodyBox.IsReadOnly = SourceBox.IsReadOnly = writing;
        AttachButton.Visibility = Visibility.Visible;
        AttachButton.IsEnabled = !writing;

        ShowStatus();
        ChangePanel.Visibility = sources.Count > 0 && !writing && _notes.GetNoteGeneration(noteId)?.WasGenerated == true ? Visibility.Visible : Visibility.Collapsed;

        List<SuggestionRow> suggestions = writing ? [] : _notes.GetNoteSuggestions(noteId).Select(found => new SuggestionRow(found)).ToList();
        SuggestionsList.ItemsSource = suggestions;
        SuggestionsPanel.Visibility = suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Sources start closed every time a note opens, so only the notes show.
        if (opened)
        {
            SourcesSection.IsExpanded = false;
        }

        var shown = string.Join('\n', sources.Select(source => source.Id).Prepend(noteId));
        if (shown != _sourcesShown)
        {
            _sourcesShown = shown;
            SourcesList.ItemsSource = SourceRows(noteId, sources);
        }

        SourcesHeader.Text = sources.Count == 1 ? "Sources (1 file)" : $"Sources ({sources.Count} files)";
        SourcesSection.Visibility = sources.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowNoteActions(noteId);
    }

    /// <summary>Cheap enough to run every second while notes are written, so the waiting time shows.</summary>
    private void ShowStatus()
    {
        if (_draft.Id is not { } noteId || _maker is null)
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            return;
        }

        StopButton.Visibility = Visibility.Collapsed;
        StatusButton.Visibility = Visibility.Visible;
        var files = _sourceCount == 1 ? "the file" : $"{_sourceCount} files";
        if (!_jobs.ContainsKey(noteId) && TranscriptionFor(noteId) is { } transcribing && _transcription is { } queue)
        {
            StatusText.Text = queue.Describe(transcribing, DateTime.Now);
            var waitingOnDownload = transcribing.Status == TranscriptionStatus.Queued && !queue.IsReady && queue.Setup is not { Running: true };
            StatusButton.Content = "Try again";
            StatusButton.Visibility = transcribing.Status == TranscriptionStatus.Failed || waitingOnDownload ? Visibility.Visible : Visibility.Collapsed;
            StopButton.Content = "Cancel";
            StopButton.Visibility = transcribing.IsActive ? Visibility.Visible : Visibility.Collapsed;
            StatusPanel.Visibility = Visibility.Visible;
            return;
        }

        StopButton.Content = "Stop";
        if (_jobs.TryGetValue(noteId, out var job))
        {
            StopButton.Visibility = Visibility.Visible;
            StatusButton.Visibility = Visibility.Collapsed;
            if (job.StartedAt is not { } started)
            {
                StatusText.Text = "Waiting for other notes to finish first. This note's files are saved.";
            }
            else
            {
                var waited = DateTime.Now - started;
                var doing = job.Request is { } request ? $"Rewriting the notes ({request})" : $"Writing notes from {files}";
                StatusText.Text = $"{doing} with Claude. {(int)waited.TotalMinutes}:{waited.Seconds:00} so far. Long files can take a few minutes. You can read the note, and editing opens again when this is done.";
            }

            StatusPanel.Visibility = Visibility.Visible;
            return;
        }

        var generation = _notes.GetNoteGeneration(noteId);
        if (_failures.TryGetValue(noteId, out var failure))
        {
            StatusText.Text = failure;
            StatusButton.Content = "Try again";
        }
        else if (_sourceCount > 0 && generation is { WasGenerated: false })
        {
            StatusText.Text = $"Notes haven't been written from {files} yet. The text is saved in Sources.";
            StatusButton.Content = "Write notes";
        }
        else if (ShowStudyStatus(noteId))
        {
            return;
        }
        else if (generation is { GeneratedAt: { } at })
        {
            var newer = _notes.GetNoteSources(noteId).Any(source => source.CreatedAt > at);
            var text = $"Written by Claude from {files} in Sources on {at.ToString("MMM d 'at' h:mm tt", CultureInfo.CurrentCulture)}. Check anything important against them.";
            if (generation.EditedSince)
            {
                text += " You've changed it since.";
            }

            if (newer)
            {
                text += " A file was added after that.";
            }

            StatusText.Text = text;
            StatusButton.Content = "Write again";
            StatusButton.Visibility = newer ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            return;
        }

        StatusPanel.Visibility = Visibility.Visible;
    }

    private async void OnStatusButtonClicked(object sender, RoutedEventArgs e)
    {
        if (_draft.Id is { } id && !_jobs.ContainsKey(id) && TranscriptionFor(id) is { } transcribing && _transcription is { } queue)
        {
            if (transcribing.Status == TranscriptionStatus.Failed)
            {
                queue.Retry(transcribing.Id);
            }
            else
            {
                queue.StartSetup();
            }

            RefreshTranscriptions();
            return;
        }

        if (_draft.Id is { } studyId && StudyStatusClicked(studyId))
        {
            return;
        }

        await Rewrite(null);
    }

    /// <summary>Asks before a transcription is stopped, titled for that, with keeping it going as the default.</summary>
    public static NoteConfirmWindow AskStopTranscribing(Window? owner, string name) => new(
        owner,
        $"Stop transcribing {name}?",
        "Your file stays where it is, so you can import it again later. Anything you typed in this note is kept.",
        "Stop transcribing",
        "Keep going",
        "Stop transcribing");

    private void OnStopClicked(object sender, RoutedEventArgs e)
    {
        if (_draft.Id is { } id && _jobs.TryGetValue(id, out var job))
        {
            job.Stop.Cancel();
        }
        else if (_draft.Id is { } noteId && TranscriptionFor(noteId) is { IsActive: true } transcribing && _transcription is { } queue)
        {
            if (AskStopTranscribing(Window.GetWindow(this), transcribing.Name).ShowDialog() == true)
            {
                queue.Cancel(transcribing.Id);
                RefreshTranscriptions();
            }
        }
    }

    // Notes from recordings

    private TranscriptionJob? TranscriptionFor(long noteId) => _transcribing.GetValueOrDefault(noteId);

    private void PickAudio()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = AudioFilter,
            Multiselect = true,
            Title = "Choose a recording to make notes from",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            ImportAudio(dialog.FileNames);
        }
    }

    /// <summary>
    /// Asks once, with the "Keep the audio" choice and, the first time, the download. Each recording gets its note
    /// right away, filed like a file would be, and is transcribed in the background one at a time.
    /// </summary>
    public async void ImportAudio(IReadOnlyList<string> paths)
    {
        if (_transcription is not { } queue || _maker is null)
        {
            Bar.Show("Notes from recordings aren't available right now.");
            return;
        }

        var needsDownload = !queue.IsReady;
        var ask = new AudioImportWindow(Window.GetWindow(this), paths.Select(path => System.IO.Path.GetFileName(path)).ToList(), needsDownload ? queue.DownloadBytes : null);
        if (ask.ShowDialog() != true)
        {
            Bar.Show(needsDownload ? "Nothing was downloaded or transcribed." : "Nothing was transcribed.");
            return;
        }

        if (needsDownload)
        {
            queue.AgreeToDownload();
        }

        Flush();
        var picked = SelectedFolder?.Folder.Id;
        var keepAudio = ask.KeepTheAudio;
        var lines = new List<string>();
        TranscriptionJob? last = null;
        foreach (var path in paths)
        {
            if (!System.IO.File.Exists(path))
            {
                lines.Add($"{System.IO.Path.GetFileName(path)} isn't there anymore.");
                continue;
            }

            // Its note is made on the server in server mode, so that's sent off the window's thread.
            var (job, line) = await Work.Run(() =>
            {
                var job = queue.Import(path, keepAudio, picked, picked is not null);
                var note = _notes.GetNote(job.NoteId);
                return (job, $"Made {note?.Title ?? job.Name} in {PlaceName(note?.FolderId)}.");
            });
            last = job;
            lines.Add(line);
        }

        RefreshTranscriptions();
        if (last is not null)
        {
            Flush();
            var lastId = last.NoteId;
            var note = await Work.Read(() => _notes.GetNote(lastId));

            // Anything typed while it was read is saved before it opens.
            Flush();
            Open(note);
            lines.Add(needsDownload ? "Transcribing starts once the download is done." : "It's being transcribed on this PC now.");
        }

        FoldersChanged(refocus: false);
        Bar.Show(string.Join(" ", lines));
    }

    private void OnTranscriptionChanged(TranscriptionChange change)
    {
        var before = change.NoteId is { } id ? TranscriptionFor(id) : null;
        RefreshTranscriptions();
        if (change.NoteId is { } noteId)
        {
            var after = TranscriptionFor(noteId);
            if (after?.Status != before?.Status || change.Outcome is not null)
            {
                // The notes were written, or the job moved to a step that changes what can be done with the note.
                if (after is null || after.Status == TranscriptionStatus.Writing)
                {
                    ReloadIfOpen(noteId);
                }

                ShowExtrasIfOpen(noteId);
                ShowSoon(force: true);
            }
            else if (after?.Status == TranscriptionStatus.Transcribing)
            {
                // Progress only. The list line shows the percent, and the gate skips a rebuild while the list is in use.
                ShowList();
            }
        }

        if (change.Outcome is { } outcome)
        {
            Bar.Show(outcome);
        }
    }

    private void RefreshTranscriptions()
    {
        if (_transcription is null)
        {
            return;
        }

        _transcribing = _transcription?.OpenJobsByNote() ?? new Dictionary<long, TranscriptionJob>();
        if (_transcribing.Values.Any(job => job.IsActive))
        {
            _clock.Start();
        }
        else if (_jobs.Count == 0)
        {
            _clock.Stop();
        }

        ShowStatus();
    }

    private async void OnChangeClicked(object sender, RoutedEventArgs e)
    {
        var request = ChangeBox.Text.Trim();
        if (request.Length == 0)
        {
            ChangeBox.Focus();
            return;
        }

        // Cleared once it's on its way, unless something else was typed there while the server was asked.
        if (await Rewrite(request) && ChangeBox.Text.Trim() == request)
        {
            ChangeBox.Text = "";
        }
    }

    private void OnChangeKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OnChangeClicked(sender, e);
        }
    }

    /// <summary>Writes the open note again from its sources. False if the user kept their version.</summary>
    private async Task<bool> Rewrite(string? request)
    {
        Flush();
        if (_draft.Id is not { } id || _jobs.ContainsKey(id))
        {
            return false;
        }

        if (!await ConfirmReplace(id))
        {
            Bar.Show("Kept your version. Nothing was rewritten.");
            return false;
        }

        _ = WriteNotesAsync(id, request);
        return true;
    }

    private async void OnSuggestionAdded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SuggestionRow row)
        {
            return;
        }

        var added = await Work.Run(() => _notes.AcceptNoteSuggestion(row.Id, DateTime.Now));
        Bar.Show(added.Problem ?? $"Added {row.Title} to To do.");
        ShowNoteExtras();
    }

    private async void OnSuggestionSkipped(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SuggestionRow row)
        {
            await Work.Run(() => _notes.SkipNoteSuggestion(row.Id));
            ShowNoteExtras();
        }
    }

    /// <summary>One note's notes being written. Stop cancels it, and StartedAt stays null while it waits its turn.</summary>
    private sealed class WriteJob(DateTime queuedAt, string? request)
    {
        public DateTime QueuedAt { get; } = queuedAt;

        public string? Request { get; } = request;

        public CancellationTokenSource Stop { get; } = new();

        public DateTime? StartedAt { get; set; }
    }

    private sealed class SuggestionRow(NoteSuggestion suggestion)
    {
        public long Id => suggestion.Id;

        public string Title => suggestion.Item.Title;

        public string Text
        {
            get
            {
                var item = suggestion.Item;
                var kind = item.Kind switch
                {
                    ItemKind.Exam => "Exam",
                    ItemKind.Deadline => "Due",
                    ItemKind.Event => "Event",
                    _ => "Task",
                };
                var when = item.Date is { } date
                    ? item.Time is { } time
                        ? $", {date.ToString("ddd MMM d", CultureInfo.CurrentCulture)} at {time.ToString("h:mm tt", CultureInfo.CurrentCulture)}"
                        : $", {date.ToString("ddd MMM d", CultureInfo.CurrentCulture)}"
                    : "";
                return $"{kind}: {item.Title}{when}";
            }
        }

        public string AddName => $"Add {suggestion.Item.Title} to To do";

        public string SkipName => $"Skip {suggestion.Item.Title}";

        /// <summary>What a screen reader says for the row, rather than the class name.</summary>
        public override string ToString() => Text;
    }

    private sealed record ShownFolder(NoteFolder Folder, int Depth)
    {
        /// <summary>What a screen reader says for the folder, rather than the record's fields.</summary>
        public override string ToString() => Folder.Name;
    }

    private sealed record Place(long? FolderId, string Name)
    {
        /// <summary>What a screen reader says for the place, rather than the record's fields.</summary>
        public override string ToString() => Name;
    }

    private sealed record SpotPart(string Heading, string Text)
    {
        /// <summary>What a screen reader says for the part, rather than the record's fields.</summary>
        public override string ToString() => $"{Heading}, {Text}";
    }

    private sealed class NoteRow(NoteDetail note, string? folder, string? transcribing = null)
    {
        private static readonly char[] LineBreaks = ['\r', '\n'];

        /// <summary>Heading, list, and quote marks, so the preview line reads as words.</summary>
        private static readonly char[] MarkdownMarks = ['#', '>', '-', '*', '+', ' '];

        public long Id => note.Id;

        public long? FolderId => note.FolderId;

        public string Title => note.Title;

        public string Line
        {
            get
            {
                var first = note.Body.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(line => line.TrimStart(MarkdownMarks))
                    .FirstOrDefault(line => line.Length > 0 && !note.Title.StartsWith(line, StringComparison.OrdinalIgnoreCase));
                var date = note.At.ToString("MMM d", CultureInfo.InvariantCulture);
                var start = folder is null ? date : $"{date}  ·  {folder}";
                if (transcribing is { Length: > 0 })
                {
                    start = $"{start}  ·  {transcribing}";
                }

                return first is null ? start : $"{start}  ·  {first}";
            }
        }

        /// <summary>What a screen reader says for the row, rather than the class name.</summary>
        public override string ToString() => $"{Title}, {Line}";
    }

    private sealed class SpotRow(Handoff spot)
    {
        public Handoff Spot => spot;

        public string Title => spot.Project;

        public string Line
        {
            get
            {
                var date = spot.CreatedAt.ToString("MMM d", CultureInfo.InvariantCulture);
                return spot.Stopped is { Length: > 0 } stopped ? $"{date}  ·  {stopped}" : date;
            }
        }

        /// <summary>What a screen reader says for the row, rather than the class name.</summary>
        public override string ToString() => $"{Title}, {Line}";
    }
}
