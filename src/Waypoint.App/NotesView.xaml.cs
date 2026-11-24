using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Threading;
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

                // It goes on as another note, so a late answer about the one it was is dropped.
                _opens++;

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
        _leaves++;

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
        // Opening a note is asking to see it, even the one that's open behind a list that took its place.
        _listAskedFor = false;
        FitList();
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
        public long Id => note.Id;

        public long? FolderId => note.FolderId;

        public string Title => note.Title;

        public string Line
        {
            get
            {
                var first = NoteListing.Preview(note.Title, note.Body) is { Length: > 0 } line ? line : null;
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
