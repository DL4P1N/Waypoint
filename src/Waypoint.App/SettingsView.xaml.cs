using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Waypoint.Core.Background;
using Waypoint.Core.Budget;
using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Remote;
using Waypoint.Core.Schedule;
using Waypoint.Core.Theme;

namespace Waypoint.App;

/// <summary>
/// Work hours, classes, bedtime, theme, and what's connected. Every change goes through the same database methods
/// as typing it, so nothing here calls the sorter. Bedtime values and classes are kept in preferences, laid over settings.json.
/// In server mode work hours, pay, classes, bedtime, Discord, and the calendar feed are the server's, and while it's away a change says it
/// can't be made yet. Theme, recording, and the notes folder are this PC's own either way.
/// </summary>
public partial class SettingsView : UserControl
{
    private static readonly DayOfWeek[] Week =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    private const string DiscordName = "Discord";
    private const string NotSetUp = "Not set up.";
    private const string SectionKey = "settings_section";

    /// <summary>
    /// The column's entries, Look first since it's what's changed most, with words a search should find each by.
    /// Searching opens the first section that has the word, rather than hiding entries.
    /// </summary>
    public static readonly IReadOnlyList<SettingsSection> Sections =
    [
        new("look", "Look", "theme color colour accent gray grey blue amber teal mono see-through blur transparent solid"),
        new("work", "Work", "work hours shifts shift day off commute remote home from home hourly pay rate paycheck"),
        new("classes", "Classes", "class classes schedule course courses term semester days start end time place room travel leave"),
        new("bedtime", "Bedtime", "sleep ready dim wind-down steps name quiet evening routine"),
        new("notes", "Notes and recording", "notes folder obsidian markdown files record recording class lecture"),
        new("discord", "Discord", "bot token user id channel capture alerts budget test message phone"),
        new("connections", "Connections", "calendar feed ics course link alert email outlook app id payment app repayment forwarded"),
        new("feedback", "Feedback", "this didn't help didnt feedback notes clear"),
    ];

    /// <summary>In server mode, what the page can't change is set up with the server itself, not in a file on this PC.</summary>
    public const string ServerSetUpElsewhere = "Anything else not on this page is set up on the server when Waypoint is installed there.";

    private ISettingsData _settings = null!;
    private WaypointDatabase _database = null!;
    private BedtimeSchedule? _bedtime;
    private BedtimeSettings _fileBedtime = new();
    private Func<IReadOnlyList<ConnectionStatus>> _connections = () => [];
    private Action _changed = () => { };
    private Func<string> _notesFolder = () => "";
    private Action<string> _moveNotesFolder = _ => { };
    private DateOnly _shiftDate = DateOnly.FromDateTime(DateTime.Now);
    private List<ClassMeeting> _classes = [];

    /// <summary>The classes as last saved or read, for going back to when a change couldn't be saved.</summary>
    private List<ClassMeeting> _savedClasses = [];

    /// <summary>Class changes asked for and not finished yet. While any is, a read doesn't replace the classes shown.</summary>
    private int _classSaves;
    /// <summary>
    /// The class the form is changing, found again by what it is rather than where it was, so a class removed or put
    /// back above it can't make Save change another one.
    /// </summary>
    private ClassMeeting? _editingClass;
    private DateOnly? _termStart;
    private DateOnly? _termEnd;
    private bool _loading;

    /// <summary>Said by a section's Save when what's typed there was kept over what's saved, since it isn't saved yet.</summary>
    public const string NotSavedYet = "Not saved yet. Press Save to keep what's typed here.";

    private readonly Unsaved _commuteEdits;
    private readonly Unsaved _rateEdits;
    private readonly Unsaved _bedtimeEdits;
    private readonly Unsaved _classEdits;
    private readonly Unsaved _discordEdits;
    private readonly Unsaved _feedEdits;
    private readonly Unsaved _emailEdits;

    public SettingsView()
    {
        InitializeComponent();
        Days.ItemsSource = Week.Select(day => day.ToString()).ToList();
        ClassDays.ItemsSource = Week.Select(day => day.ToString()).ToList();
        ShowShiftDate();
        _commuteEdits = new(WorkStatus, () => Commute.Text);
        _rateEdits = new(WorkStatus, () => HourlyRate.Text);
        _bedtimeEdits = new(BedtimeNote, () => Unsaved.Join(SleepHours.Text, ReadyMinutes.Text, LatestBedtime.Text, RoutineSteps.Text, EveningStepName.Text, WindDownStepName.Text));
        _classEdits = new(ClassesStatus, () => Unsaved.Join(
            [ClassName.Text, ClassStart.Text, ClassEnd.Text, ClassPlace.Text, ClassTravel.Text, _editingClass is { } editing ? ClassLabel(editing) : "", .. ClassDays.SelectedItems.Cast<string>()]));
        _discordEdits = new(DiscordNote, () => Unsaved.Join(
            DiscordOwner.Text, DiscordCapture.Text, DiscordAlerts.Text, DiscordBudget.Text, DiscordNotesForum.Text, DiscordListsForum.Text, DiscordToken.Password));
        _feedEdits = new(CalendarStatus, () => CalendarFeed.Text);
        _emailEdits = new(EmailNote, () => Unsaved.Join(EmailClientId.Text, EmailOwnAddress.Text));
        Column.SelectionChanged += entry => ShowSection(entry.Key);
        Column.SearchChanged += OnSearched;

        // The payment app goes by the name its bank profile gives.
        var app = BankProfiles.Current.PaymentApp.Name;
        EmailOwnAddressLabel.Text = $"{app} emails are forwarded from (optional)";
        System.Windows.Automation.AutomationProperties.SetName(EmailOwnAddress, $"Address {app} emails are forwarded from");
    }

    /// <summary>
    /// This PC's running class schedule. In server mode the server keeps the classes, so a save here is copied into
    /// it, the way bedtime is, and the week view, recording, and class start offer here use them straight away.
    /// </summary>
    public ClassSchedule? Classes { get; set; }

    /// <summary>The section open now.</summary>
    public string? SectionShown => Column.SelectedKey;

    /// <summary>Shows one section, as the other pages show one entry. The page scrolls back to its top.</summary>
    public void ShowSection(string key)
    {
        if (Sections.All(section => section.Key != key))
        {
            key = Sections[0].Key;
        }

        if (Column.SelectedKey != key)
        {
            Column.Select(key);
        }

        LookSection.Visibility = Shown("look");
        WorkSection.Visibility = Shown("work");
        ClassesSection.Visibility = Shown("classes");
        BedtimeSection.Visibility = Shown("bedtime");
        NotesSection.Visibility = Shown("notes");
        DiscordSection.Visibility = Shown("discord");
        ConnectionsSection.Visibility = Shown("connections");
        FeedbackSection.Visibility = Shown("feedback");
        Scroll.ScrollToTop();

        Visibility Shown(string section) => section == key ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The first section a search's words fit, by its name or what's in it, or null when none does.</summary>
    public static SettingsSection? Find(string search)
    {
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0
            ? null
            : Sections.FirstOrDefault(section => words.All(word => $"{section.Label} {section.Words}".Contains(word, StringComparison.OrdinalIgnoreCase)));
    }

    private void OnSearched(string search)
    {
        if (search.Trim().Length == 0)
        {
            Column.ShowProblem(null);
            return;
        }

        if (Find(search) is not { } found)
        {
            Column.ShowProblem("No setting matches that.");
            return;
        }

        Column.ShowProblem(null);
        ShowSection(found.Key);
    }

    /// <param name="settings">Work hours, pay, bedtime, and Discord: this PC's in local mode, the server's in server mode.</param>
    /// <param name="database">This PC's database, for the theme and recording, which stay on this PC.</param>
    /// <param name="bedtime">This PC's bedtime schedule, kept up to date so the wind-down dims the window on time.</param>
    /// <param name="fileBedtime">The bedtime values from settings.json, which the page's choices sit on top of.</param>
    /// <param name="changed">Runs after anything is saved, so the rest of the window and the theme catch up.</param>
    public void Connect(ISettingsData settings, WaypointDatabase database, BedtimeSchedule? bedtime, BedtimeSettings fileBedtime, Func<IReadOnlyList<ConnectionStatus>> connections, Action changed)
    {
        _settings = settings;
        _database = database;
        _bedtime = bedtime;
        _fileBedtime = fileBedtime;
        _connections = connections;
        _changed = changed;
        Column.Connect(database, SectionKey);
        Column.SetEntries(Sections.Select(section => new ColumnEntry(section.Key, section.Label)).ToList(), force: true);
        ShowSection(Column.SelectedKey ?? Sections[0].Key);
        DiscordWhere.Text = settings.OnServer
            ? "What's saved here is used on the server. The token is sent to the server over the paired connection and kept there, readable only by the Waypoint service. It's never sent back."
            : "What's saved here is used on this PC, and the token is kept encrypted to your Windows user.";
        if (settings.OnServer)
        {
            SetUpElsewhere.Text = ServerSetUpElsewhere;
        }
    }

    private void LoadDiscord()
    {
        var form = _settings.GetDiscord();
        DiscordTokenNote.Visibility = form.HasToken ? Visibility.Visible : Visibility.Collapsed;
        DiscordNote.Visibility = Visibility.Collapsed;
        if (_discordEdits.Edited)
        {
            return;
        }

        DiscordOwner.Text = form.OwnerUserId ?? "";
        DiscordCapture.Text = form.CaptureChannelId ?? "";
        DiscordAlerts.Text = form.AlertsChannelId ?? "";
        DiscordBudget.Text = form.BudgetChannelId ?? "";
        DiscordNotesForum.Text = form.NotesForumChannelId ?? "";
        DiscordListsForum.Text = form.ListsForumChannelId ?? "";
        DiscordToken.Clear();
        _discordEdits.Filled();
    }

    /// <summary>Saving starts Discord again, which on the server can take a while, so the window isn't held up waiting.</summary>
    private async void OnDiscordSaved(object sender, RoutedEventArgs e)
    {
        var form = new DiscordForm(DiscordOwner.Text, DiscordCapture.Text, DiscordAlerts.Text, DiscordBudget.Text, false, DiscordNotesForum.Text, DiscordListsForum.Text);
        var token = DiscordToken.Password;
        var settings = _settings;
        string reply;
        ShowDiscord("Saving...");
        try
        {
            // On this PC the worker starts Discord again itself, so only the server's wait is moved off the window's thread.
            reply = settings.OnServer ? await Task.Run(() => settings.SaveDiscord(form, token)) : settings.SaveDiscord(form, token);
        }
        catch (ServerUnavailableException away)
        {
            ShowDiscord(PageMessages.CantChange(away));
            return;
        }
        catch (ServerTimedOutException timedOut)
        {
            ShowDiscord(timedOut.Message);
            return;
        }

        ShowDiscord(reply);
        DiscordToken.Clear();
        if (reply.StartsWith("Saved", StringComparison.Ordinal))
        {
            _discordEdits.Filled();
        }

        // Whether a token is kept now is read in the page's order, so the window isn't held for it in server mode.
        var hasToken = await Work.Read(() => settings.GetDiscord().HasToken);
        DiscordTokenNote.Visibility = hasToken ? Visibility.Visible : Visibility.Collapsed;
        ShowAfterChanges();
    }

    private async void OnDiscordTested(object sender, RoutedEventArgs e)
    {
        DiscordTest.IsEnabled = false;
        ShowDiscord("Sending...");
        try
        {
            ShowDiscord(await _settings.TestDiscordAsync());
        }
        catch (ServerUnavailableException away)
        {
            ShowDiscord(PageMessages.CantChange(away));
        }
        catch (ServerTimedOutException timedOut)
        {
            ShowDiscord(timedOut.Message);
        }
        finally
        {
            DiscordTest.IsEnabled = true;
            Refresh();
        }
    }

    private void ShowDiscord(string text) => ShowNote(DiscordNote, text);

    private void LoadAlertEmail()
    {
        var form = _settings.GetAlertEmail();
        EmailNote.Visibility = Visibility.Collapsed;
        if (!_emailEdits.Edited)
        {
            EmailClientId.Text = form.ClientId ?? "";
            EmailOwnAddress.Text = form.OwnAddress ?? "";
            _emailEdits.Filled();
        }
    }

    /// <summary>Saved where email is read: on this PC in local mode, on the server in server mode. Neither value is a secret.</summary>
    private async void OnAlertEmailSaved(object sender, RoutedEventArgs e)
    {
        var form = new AlertEmailForm(EmailClientId.Text, EmailOwnAddress.Text);
        var (ok, reply) = await Change(() => _settings.SaveAlertEmail(form), text => ShowNote(EmailNote, text));
        if (!ok)
        {
            return;
        }

        ShowNote(EmailNote, reply);
        if (reply.StartsWith("Saved", StringComparison.Ordinal))
        {
            _emailEdits.Filled();
        }

        ShowAfterChanges();
    }

    /// <summary>
    /// One section's note at a time: saving or testing in one section replaces what another section last said,
    /// so an old "saved" or problem isn't left on screen next to the new one.
    /// </summary>
    private void ShowNote(TextBlock note, string text)
    {
        foreach (var other in new[] { WeeklyStatus, ShiftStatus, WorkStatus, ClassesStatus, BedtimeNote, DiscordNote, CalendarStatus, EmailNote })
        {
            if (!ReferenceEquals(other, note))
            {
                other.Visibility = Visibility.Collapsed;
            }
        }

        note.Text = text;
        note.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (text.Length > 0)
        {
            LiveRegion.Announce(note);
        }
    }

    /// <param name="current">Where the notes are kept as .md files right now.</param>
    /// <param name="move">Switches to another folder. Every note is written there.</param>
    public void ConnectNotesFolder(Func<string> current, Action<string> move)
    {
        _notesFolder = current;
        _moveNotesFolder = move;
        NotesFolderPath.Text = current();
    }

    /// <summary>How the page reads and changes its data: straight away on this PC, or in order without holding the window in server mode.</summary>
    internal PageWork Work { get; set; } = PageWork.Local;

    /// <summary>The lists and status lines again once this page's changes are done.</summary>
    private void ShowAfterChanges() => Work.ShowAfterChanges(nameof(Refresh), Refresh);

    /// <summary>
    /// For the page opened in server mode: filled at once from what was kept, then again from what's read, unless
    /// something on the page has the keyboard, so what's being typed is never replaced.
    /// </summary>
    public void LoadSoon() => Work.Show(Load, () =>
    {
        if (IsKeyboardFocusWithin)
        {
            Refresh();
        }
        else
        {
            Load();
        }
    });

    /// <summary>Only lists and status lines, so a refresh never replaces something being typed.</summary>
    public void Refresh()
    {
        if (_settings is null)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var weekly = _settings.GetWeeklyHours();
        WeeklyRows.ItemsSource = weekly.Select(hours => new WorkRow($"{hours.Day}  {Hours(hours.Start, hours.End)}", $"Remove {hours.Day}", weekly: hours)).ToList();
        NoWeekly.Visibility = weekly.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var dates = _settings.GetWorkDates(today);
        DateRows.ItemsSource = dates.Select(date => new WorkRow(
            $"{date.Date.ToString("ddd, MMM d", CultureInfo.InvariantCulture)}  {(date.IsDayOff ? "off" : Hours(date.Start!.Value, date.End!.Value))}",
            $"Remove {date.Date.ToString("MMM d", CultureInfo.InvariantCulture)}",
            dates: dates.Where(other => other.Date == date.Date).ToList())).ToList();
        NoDates.Visibility = dates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        Tonight.Text = _settings.DescribeTonight(DateTime.Now);
        var connections = _connections();
        Connections.ItemsSource = ConnectionRows(connections);
        DiscordState.Text = connections.FirstOrDefault(connection => connection.Name == DiscordName)?.State ?? NotSetUp;
        NotesFolderPath.Text = _notesFolder();
        ShowTheme();
        ShowFeedback();
    }

    /// <summary>Where This didn't help is kept. The window sets it: the server's in server mode, or this PC's.</summary>
    public IFeedbackData? FeedbackData { get; set; }

    private void ShowFeedback()
    {
        var entries = FeedbackData?.GetFeedback() ?? [];
        FeedbackRows.ItemsSource = entries.Select(Feedback.Describe).ToList();
        NoFeedback.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FeedbackClear.Visibility = entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Clears the list, with the usual Undo to put it all back. In server mode it's sent off the window's thread, and
    /// Clear can't be pressed again until it's back, so what Undo puts back is what was cleared.
    /// </summary>
    private async void OnFeedbackCleared(object sender, RoutedEventArgs e)
    {
        var feedback = FeedbackData;
        IReadOnlyList<FeedbackEntry> cleared;
        FeedbackClear.IsEnabled = false;
        try
        {
            cleared = await Work.Run(() =>
            {
                var cleared = feedback?.GetFeedback() ?? [];
                feedback?.ClearFeedback();
                return cleared;
            });
        }
        catch (Exception exception) when (exception is ServerUnavailableException or ServerTimedOutException or InvalidOperationException)
        {
            NoFeedback.Text = "Couldn't clear these while the server can't be reached. Try again in a moment.";
            NoFeedback.Visibility = Visibility.Visible;
            return;
        }
        finally
        {
            FeedbackClear.IsEnabled = true;
        }

        NoFeedback.Text = "Nothing yet.";
        Work.Show(ShowFeedback);
        if (cleared.Count > 0)
        {
            FeedbackBar.Show(cleared.Count == 1 ? "Cleared 1 entry." : $"Cleared {cleared.Count} entries.", () => RestoreFeedback(cleared));
        }
    }

    private async void RestoreFeedback(IReadOnlyList<FeedbackEntry> cleared)
    {
        var feedback = FeedbackData;
        try
        {
            await Work.Run(() => feedback?.RestoreFeedback(cleared));
        }
        catch (Exception exception) when (exception is ServerUnavailableException or ServerTimedOutException or InvalidOperationException)
        {
            FeedbackBar.Show("Couldn't put these back while the server can't be reached.");
            return;
        }

        Work.Show(ShowFeedback);
    }

    /// <summary>
    /// The Connections list, with a Discord line always first, set up or not. Its full status and form are in the
    /// Discord section, so here it's the first sentence and where to find the rest, as short as the other lines.
    /// </summary>
    public static IReadOnlyList<ConnectionStatus> ConnectionRows(IReadOnlyList<ConnectionStatus> connections)
    {
        var state = connections.FirstOrDefault(connection => connection.Name == DiscordName)?.State;
        var end = state?.IndexOf(". ", StringComparison.Ordinal) ?? -1;
        var first = string.IsNullOrEmpty(state) ? NotSetUp : end >= 0 ? state[..(end + 1)] : state;
        return connections
            .Where(connection => connection.Name != DiscordName)
            .Prepend(new ConnectionStatus(DiscordName, $"{first} Details are under Discord."))
            .ToList();
    }

    private void OnNotesFolderOpened(object sender, RoutedEventArgs e)
    {
        var folder = _notesFolder();
        if (System.IO.Directory.Exists(folder))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
    }

    /// <summary>The old folder is left as it is, so nothing in it is lost.</summary>
    private void OnNotesFolderChanged(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Keep notes in this folder", InitialDirectory = _notesFolder() };
        if (FileDialogs.Shared.Show(() => dialog.ShowDialog(Window.GetWindow(this)), false) == true && dialog.FolderName != _notesFolder())
        {
            _moveNotesFolder(dialog.FolderName);
            NotesFolderPath.Text = _notesFolder();
        }
    }

    /// <summary>
    /// Fills the boxes from what's saved, for when the page is opened. A section with something typed and not saved
    /// keeps it, and says by its Save that it isn't saved yet, rather than having it quietly replaced.
    /// </summary>
    public void Load()
    {
        if (_settings is null)
        {
            return;
        }

        var kept = new[] { _commuteEdits, _rateEdits, _bedtimeEdits, _classEdits, _discordEdits, _feedEdits, _emailEdits }.Where(part => part.Edited).ToList();
        if (_commuteEdits.Edited)
        {
            WorkRemote.IsChecked = _settings.GetCommuteMinutes() == 0;
        }
        else
        {
            ShowCommute();
        }

        if (!_rateEdits.Edited)
        {
            HourlyRate.Text = _settings.GetHourlyRate().ToString("0.00", CultureInfo.InvariantCulture);
            _rateEdits.Filled();
        }

        var settings = _settings.GetBedtime();
        var keepBedtime = _bedtimeEdits.Edited;
        if (!keepBedtime)
        {
            SleepHours.Text = settings.SleepHours.ToString(CultureInfo.CurrentCulture);
            ReadyMinutes.Text = settings.ReadyMinutes.ToString(CultureInfo.CurrentCulture);
            LatestBedtime.Text = settings.LatestBedtime is { } latest ? latest.ToString("h:mm tt", CultureInfo.InvariantCulture) : "";
        }

        _loading = true;
        EveningStepOn.IsChecked = settings.EveningStepMinutes is not null;
        WindDownStepOn.IsChecked = settings.WindDownStepMinutes is not null;
        QuietOn.IsChecked = settings.QuietAfterWrapUp;
        LateCheckInsOn.IsChecked = settings.LateCheckIns;
        _loading = false;
        if (!keepBedtime)
        {
            RoutineSteps.Text = string.Join(Environment.NewLine, settings.RoutineSteps ?? []);
            EveningStepName.Text = settings.EveningStepLabel();
            WindDownStepName.Text = settings.WindDownStepLabel();
            _bedtimeEdits.Filled();
        }

        WeeklyStatus.Visibility = Visibility.Collapsed;
        ShiftStatus.Visibility = Visibility.Collapsed;
        WorkStatus.Visibility = Visibility.Collapsed;
        BedtimeNote.Visibility = Visibility.Collapsed;
        _loading = true;
        OfferRecordOn.IsChecked = Waypoint.Core.Recording.ClassStartOffer.IsOn(_database.GetPreference(Waypoint.Core.Recording.ClassStartOffer.Preference));
        _loading = false;
        LoadClasses();
        LoadDiscord();
        if (!_feedEdits.Edited)
        {
            CalendarFeed.Clear();
            _feedEdits.Filled();
        }

        CalendarStatus.Visibility = Visibility.Collapsed;
        ShowCalendarFeed();
        LoadAlertEmail();
        Refresh();
        foreach (var part in kept)
        {
            part.Note.Text = NotSavedYet;
            part.Note.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// What's saved, without the link itself. Remove only shows for a link saved here, since one in the setup file
    /// can only be changed there.
    /// </summary>
    private void ShowCalendarFeed()
    {
        var form = _settings.GetCalendarFeed();
        CalendarRemove.Visibility = form.SavedHere ? Visibility.Visible : Visibility.Collapsed;
        CalendarFeedNote.Text = form switch
        {
            { SavedHere: true } => "A feed link is saved. Paste a new one to replace it.",
            { HasFeed: true } => "A calendar link from the setup file is in use. One saved here is used instead.",
            _ => "",
        };
        CalendarFeedNote.Visibility = CalendarFeedNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnCalendarFeedSaved(object sender, RoutedEventArgs e)
    {
        var link = CalendarFeed.Text;
        var (ok, reply) = await Change(() => _settings.SaveCalendarFeed(link), ShowCalendar);
        if (!ok)
        {
            return;
        }

        // A link that wasn't taken stays in the box to fix. One that was is cleared, since it's never shown again.
        if (reply.StartsWith("Saved", StringComparison.Ordinal))
        {
            CalendarFeed.Clear();
            _feedEdits.Filled();
        }

        CalendarFeedChanged(reply);
    }

    /// <summary>Removed straight away, like a class, with an Undo that saves the same link again where it was kept.</summary>
    private async void OnCalendarFeedRemoved(object sender, RoutedEventArgs e)
    {
        CalendarBar.Changed();
        var (ok, reply) = await Change(() => _settings.RemoveCalendarFeed(), ShowCalendar);
        if (ok)
        {
            ShowCalendar("");
            CalendarBar.Show(reply, PutBackCalendarFeed);
            CalendarFeedChanged();
        }
    }

    private async void PutBackCalendarFeed()
    {
        var (ok, reply) = await Change(() => _settings.PutBackCalendarFeed(), ShowCalendar);
        if (ok)
        {
            CalendarFeedChanged(reply);
        }
    }

    private void CalendarFeedChanged(string reply)
    {
        ShowCalendar(reply);
        CalendarFeedChanged();
    }

    private void CalendarFeedChanged()
    {
        Work.Peek(ShowCalendarFeed);
        ShowAfterChanges();
        _changed();
    }

    private void ShowCalendar(string text) => ShowNote(CalendarStatus, text);

    /// <summary>Off unless turned on here. Even on, the card only asks.</summary>
    private void OnOfferRecordChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _database is null)
        {
            return;
        }

        _database.SetPreference(Waypoint.Core.Recording.ClassStartOffer.Preference, OfferRecordOn.IsChecked == true ? "yes" : "no");
    }

    private void ShowTheme()
    {
        var current = _database.GetTheme();
        var swatches = Themes.All.Select(theme => new Swatch(theme.Name, theme.Accent)).ToList();
        _loading = true;
        Swatches.ItemsSource = swatches;
        Swatches.SelectedItem = swatches.FirstOrDefault(swatch => swatch.Name == current.Name);
        LessSeeThroughOn.IsChecked = current.LessSeeThrough;
        CardSoundOn.IsChecked = CornerCards.SoundOn(_database.GetPreference(CornerCards.SoundPreference));
        _loading = false;
    }

    /// <summary>Kept on this PC like the theme, and applied straight away.</summary>
    private void OnLessSeeThroughChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _database is null)
        {
            return;
        }

        _database.SetLessSeeThrough(LessSeeThroughOn.IsChecked == true);
        _changed();
    }

    /// <summary>Kept on this PC. On unless turned off here.</summary>
    private void OnCardSoundChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _database is null)
        {
            return;
        }

        _database.SetPreference(CornerCards.SoundPreference, CardSoundOn.IsChecked == true ? "yes" : "no");
    }

    /// <summary>The same call as typing "use the amber theme".</summary>
    private void OnThemePicked(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Swatches.SelectedItem is not Swatch swatch)
        {
            return;
        }

        _database.SetTheme(swatch.Name);
        _changed();
    }

    private async void OnWeeklySaved(object sender, RoutedEventArgs e)
    {
        WorkBar.Changed();
        var days = Days.SelectedItems.Cast<string>().ToList();
        if (days.Count == 0)
        {
            ShowWeekly("Pick at least one day.");
            return;
        }

        if (ReadHours(WeeklyStart.Text, WeeklyEnd.Text) is not ({ } start, { } end))
        {
            ShowWeekly(TimesHint);
            return;
        }

        var (ok, reply) = await Change(() => _settings.SetWeeklyHours(days, $"{Clock(start)}-{Clock(end)}"), ShowWeekly);
        if (!ok)
        {
            return;
        }

        // The days stay picked, so the same days can be given other hours straight after.
        if (!reply.IsAnswer)
        {
            WeeklyStart.Clear();
            WeeklyEnd.Clear();
        }

        WorkSaved(reply, ShowWeekly);
    }

    private void OnShiftDateClicked(object sender, RoutedEventArgs e)
    {
        var picker = new PickDateWindow(Window.GetWindow(this), "Work", _shiftDate, null, asksForTime: false, action: "Choose");
        if (picker.ShowDialog() == true)
        {
            _shiftDate = picker.Date;
            ShowShiftDate();
        }
    }

    private async void OnShiftAdded(object sender, RoutedEventArgs e)
    {
        WorkBar.Changed();
        if (ReadHours(ShiftStart.Text, ShiftEnd.Text) is not ({ } start, { } end))
        {
            ShowShift(TimesHint);
            return;
        }

        var day = _shiftDate.ToDateTime(TimeOnly.MinValue);
        var (ok, reply) = await Change(() => _settings.AddWorkShift(day.Add(start.ToTimeSpan()), day.Add(end.ToTimeSpan())), ShowShift);
        if (!ok)
        {
            return;
        }

        if (!reply.IsAnswer)
        {
            ShiftStart.Clear();
            ShiftEnd.Clear();
        }

        WorkSaved(reply, ShowShift);
    }

    private void OnDayOffAdded(object sender, RoutedEventArgs e)
    {
        var day = _shiftDate.ToDateTime(TimeOnly.MinValue);
        WorkSaved(() => _settings.SetDayOff(day), ShowShift);
    }

    private async void OnCommuteSaved(object sender, RoutedEventArgs e)
    {
        var commute = Commute.Text;
        await FieldSaved(() => _settings.SetCommute(commute), CommuteHint, "Try a number of minutes, like 15.", _commuteEdits);
        WorkRemote.IsChecked = Work.Peek(() => _settings.GetCommuteMinutes()) == 0;
    }

    /// <summary>Working from home is a commute of 0, so the box and the checkbox are two views of the one saved number.</summary>
    private async void OnWorkRemoteClicked(object sender, RoutedEventArgs e)
    {
        var minutes = WorkRemote.IsChecked == true ? 0 : WorkShift.DefaultCommuteMinutes;
        await FieldSaved(() => _settings.SetCommute(minutes.ToString(CultureInfo.InvariantCulture)), CommuteHint, "Try a number of minutes, like 15.", _commuteEdits);
        Work.Peek(ShowCommute);
    }

    private void ShowCommute()
    {
        var minutes = _settings.GetCommuteMinutes();
        Commute.Text = minutes.ToString(CultureInfo.CurrentCulture);
        WorkRemote.IsChecked = minutes == 0;
        _commuteEdits.Filled();
    }

    private async void OnHourlyRateSaved(object sender, RoutedEventArgs e)
    {
        var rate = BudgetFormWindow.ParseCents(HourlyRate.Text) is { } cents ? cents / 100m : (decimal?)null;
        await FieldSaved(() => _settings.SetHourlyRate(rate), HourlyRateHint, "Try an amount like 20.00.", _rateEdits);
    }

    /// <summary>
    /// Something the field can't use is answered under it with an example, the way the other forms do, rather than
    /// with the question the chat would ask.
    /// </summary>
    private async Task FieldSaved(Func<OpReply> change, TextBlock hint, string tryThis, Unsaved part)
    {
        WorkBar.Changed();
        hint.Visibility = Visibility.Collapsed;
        var (ok, reply) = await Change(change, ShowWork);
        if (!ok)
        {
            return;
        }

        // The setters only answer with a question when what was typed couldn't be used, and then nothing is saved.
        if (reply.IsAnswer)
        {
            hint.Text = tryThis;
            hint.Visibility = Visibility.Visible;
            return;
        }

        part.Filled();
        WorkSaved(reply, ShowWork);
    }

    /// <summary>
    /// Removed straight away, like a class, with an Undo that sets the same hours again: a day's regular hours, or
    /// every shift or the day off that date had, since removing one takes the whole date back to the usual hours.
    /// </summary>
    private async void OnWorkRowRemoved(object sender, RoutedEventArgs e)
    {
        var row = (WorkRow)((FrameworkElement)sender).DataContext;
        WorkBar.Changed();
        if (row.Weekly is { } hours)
        {
            var (ok, reply) = await Change(() => _settings.RemoveWeeklyHours([hours.Day.ToString()]), ShowWeekly);
            if (ok)
            {
                WorkRemoved(reply, ShowWeekly, () => WorkSaved(() => PutWeeklyHoursBack(hours), ShowWeekly));
            }
        }
        else if (row.Dates is [{ Date: var date }, ..] dates)
        {
            var (ok, reply) = await Change(() => _settings.RemoveWorkDate(date), ShowShift);
            if (ok)
            {
                WorkRemoved(reply, ShowShift, () => WorkSaved(() => PutWorkDateBack(dates), ShowShift));
            }
        }
    }

    /// <summary>The message goes in the Undo bar rather than under the list, so there's one place that says it.</summary>
    private void WorkRemoved(OpReply reply, Action<string> show, Action undo)
    {
        if (reply.IsAnswer)
        {
            WorkSaved(reply, show);
            return;
        }

        show("");
        WorkBar.Show(reply.Text, undo);
        ShowAfterChanges();
        _changed();
    }

    /// <summary>Only into a day that's still empty, so Undo never replaces hours set since the remove.</summary>
    private OpReply PutWeeklyHoursBack(WeeklyHours hours) =>
        _settings.GetWeeklyHours().Any(found => found.Day == hours.Day)
            ? new OpReply($"{hours.Day} has other hours now, so those were kept.", true)
            : _settings.SetWeeklyHours([hours.Day.ToString()], $"{Clock(hours.Start)}-{Clock(hours.End)}");

    /// <summary>Only onto a date that's still back to the usual hours, so Undo never adds to what was set since.</summary>
    private OpReply PutWorkDateBack(IReadOnlyList<WorkDate> dates)
    {
        var removed = dates[0].Date;
        if (_settings.GetWorkDates(removed).Any(found => found.Date == removed))
        {
            return new OpReply($"{removed.ToString("dddd, MMM d", CultureInfo.InvariantCulture)} has something else set now, so that was kept.", true);
        }

        var reply = new OpReply("");
        foreach (var date in dates)
        {
            var day = date.Date.ToDateTime(TimeOnly.MinValue);
            reply = date is { Start: { } start, End: { } end }
                ? _settings.AddWorkShift(day.Add(start.ToTimeSpan()), day.Add(end.ToTimeSpan()))
                : _settings.SetDayOff(day);
        }

        return reply;
    }

    /// <summary>Runs a work change and says how it went under the part of the section it was made in.</summary>
    private async void WorkSaved(Func<OpReply> change, Action<string> show)
    {
        WorkBar.Changed();
        var (ok, reply) = await Change(change, show);
        if (ok)
        {
            WorkSaved(reply, show);
        }
    }

    private void WorkSaved(OpReply reply, Action<string> show)
    {
        show(reply.Text);
        ShowAfterChanges();
        _changed();
    }

    private void ShowWeekly(string text) => ShowNote(WeeklyStatus, text);

    private void ShowShift(string text) => ShowNote(ShiftStatus, text);

    private void ShowWork(string text) => ShowNote(WorkStatus, text);

    private async void OnBedtimeSaved(object sender, RoutedEventArgs e)
    {
        double? sleep = null;
        if (SleepHours.Text.Trim().Length > 0)
        {
            if (!double.TryParse(SleepHours.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var hours))
            {
                ShowBedtime("Hours of sleep should be a number, like 8 or 7.5.");
                return;
            }

            sleep = hours;
        }

        int? ready = null;
        if (ReadyMinutes.Text.Trim().Length > 0)
        {
            if (!int.TryParse(ReadyMinutes.Text.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out var minutes))
            {
                ShowBedtime("Minutes to get ready should be a whole number, like 45.");
                return;
            }

            ready = minutes;
        }

        string? latest = null;
        if (LatestBedtime.Text.Trim().Length > 0)
        {
            if (ParseTime(LatestBedtime.Text) is not { } time)
            {
                ShowBedtime("Try a latest bedtime like 1:00 AM.");
                return;
            }

            latest = BedtimeChoices.FormatTime(time);
        }

        var choices = new BedtimeChoices(
            sleep,
            ready,
            latest,
            EveningStepOn.IsChecked == true,
            WindDownStepOn.IsChecked == true,
            BedtimeChoices.CleanSteps(RoutineSteps.Text.Split('\n')),
            QuietOn.IsChecked == true,
            LateCheckInsOn.IsChecked == true,
            BedtimeChoices.CleanName(EveningStepName.Text),
            BedtimeChoices.CleanName(WindDownStepName.Text));

        if (await SaveBedtime(choices))
        {
            _bedtimeEdits.Filled();
            ShowBedtime("Bedtime saved.");
        }
    }

    /// <summary>
    /// A tick saves straight away, like Less see-through, so leaving the page never drops one. It goes with the
    /// times and steps as they were saved, so anything typed above still waits for Save.
    /// </summary>
    private async void OnBedtimeTicked(object sender, RoutedEventArgs e)
    {
        if (_loading || _settings is null)
        {
            return;
        }

        // What was last saved, read where the change is made, so two quick ticks each keep the other's.
        var (evening, windDown, quiet, late) = (EveningStepOn.IsChecked == true, WindDownStepOn.IsChecked == true, QuietOn.IsChecked == true, LateCheckInsOn.IsChecked == true);
        var saved = await Work.Read(() => _settings.GetBedtime());
        var choices = new BedtimeChoices(
            saved.SleepHours,
            saved.ReadyMinutes,
            saved.LatestBedtime is { } latest ? BedtimeChoices.FormatTime(latest) : null,
            evening,
            windDown,
            saved.RoutineSteps ?? [],
            quiet,
            late,
            saved.EveningStepName,
            saved.WindDownStepName);
        if (await SaveBedtime(choices))
        {
            BedtimeNote.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Saves the bedtime choices and has reminders use them. False, with why shown, if they weren't saved.</summary>
    private async Task<bool> SaveBedtime(BedtimeChoices choices)
    {
        var (ok, problem) = await Change(() => _settings.SetBedtimeChoices(choices), ShowBedtime);
        if (!ok)
        {
            return false;
        }

        if (problem is not null)
        {
            ShowBedtime(problem);
            return false;
        }

        if (_bedtime is not null)
        {
            _bedtime.Settings = choices.ApplyTo(_fileBedtime);
        }

        ShowAfterChanges();
        _changed();
        return true;
    }

    private void ShowBedtime(string text) => ShowNote(BedtimeNote, text);

    /// <summary>Shows the classes in use and the term's days, with the add form empty.</summary>
    private void LoadClasses()
    {
        var form = _settings.GetClasses();
        _classes = (form.Classes ?? []).ToList();
        _savedClasses = _classes.ToList();
        _termStart = form.TermStart;
        _termEnd = form.TermEnd;
        if (!_classEdits.Edited)
        {
            ClearClassForm();
            _classEdits.Filled();
            ClassesStatus.Visibility = Visibility.Collapsed;
        }

        ShowClasses();
    }

    private void ShowClasses()
    {
        // A class being changed that's gone now: what's typed stays, and Save adds it as a new class.
        if (_editingClass is { } editing && FindClass(_classes, editing) < 0)
        {
            _editingClass = null;
            ClassFormTitle.Text = "Add a class";
            ClassEditCancel.Visibility = Visibility.Collapsed;
        }

        ClassRows.ItemsSource = _classes.Select((meeting, index) => new ClassRow(ClassLabel(meeting), meeting.Course, index)).ToList();
        NoClasses.Visibility = _classes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TermStartButton.Content = _termStart is { } start ? $"First day: {start.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}" : "First day: not set";
        TermEndButton.Content = _termEnd is { } end ? $"Last day: {end.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}" : "Last day: not set";
    }

    /// <summary>A class as one line: its name, days, times, and place.</summary>
    public static string ClassLabel(ClassMeeting meeting)
    {
        var days = string.Join(", ", Week.Where(meeting.Days.Contains).Select(day => day.ToString()[..3]));
        var place = string.IsNullOrWhiteSpace(meeting.Room) ? "" : $"  {meeting.Room.Trim()}";
        return $"{meeting.Course}  {days}  {Hours(meeting.Start, meeting.End)}{place}";
    }

    /// <summary>Where the class is in the list, by its name, place, days and times, or -1 when it isn't there.</summary>
    private static int FindClass(IReadOnlyList<ClassMeeting> classes, ClassMeeting meeting)
    {
        for (var index = 0; index < classes.Count; index++)
        {
            var found = classes[index];
            if (found.Course == meeting.Course && (found.Room ?? "") == (meeting.Room ?? "") && found.Start == meeting.Start
                && found.End == meeting.End && found.TravelMinutes == meeting.TravelMinutes && found.Days.Order().SequenceEqual(meeting.Days.Order()))
            {
                return index;
            }
        }

        return -1;
    }

    private void ClearClassForm()
    {
        _editingClass = null;
        ClassName.Clear();
        ClassDays.SelectedItems.Clear();
        ClassStart.Clear();
        ClassEnd.Clear();
        ClassPlace.Clear();
        ClassTravel.Clear();
        ClassFormTitle.Text = "Add a class";
        ClassEditCancel.Visibility = Visibility.Collapsed;
    }

    private bool ClassTyped =>
        new[] { ClassName.Text, ClassStart.Text, ClassEnd.Text, ClassPlace.Text, ClassTravel.Text }.Any(text => text.Trim().Length > 0)
        || ClassDays.SelectedItems.Count > 0;

    /// <summary>
    /// The one Save for the section: it keeps the class typed in the form, added or changed, along with the
    /// term's days. With no class typed it saves just the term's days.
    /// </summary>
    private void OnClassesSaved(object sender, RoutedEventArgs e)
    {
        var classes = _classes.ToList();
        if (ClassTyped)
        {
            if (ReadClass() is not { } meeting)
            {
                return;
            }

            if (_editingClass is { } editing && FindClass(classes, editing) is >= 0 and var index)
            {
                classes[index] = meeting;
            }
            else
            {
                classes.Add(meeting);
            }
        }

        SaveClasses(classes, fromForm: true);
    }

    /// <summary>The class in the form, or null after saying what to fix.</summary>
    private ClassMeeting? ReadClass()
    {
        var name = ClassName.Text.Trim();
        if (name.Length == 0)
        {
            ShowClassesStatus("Give the class a name, like CS 101.");
            return null;
        }

        var days = ClassDays.SelectedItems.Cast<string>().Select(Enum.Parse<DayOfWeek>).ToArray();
        if (days.Length == 0)
        {
            ShowClassesStatus("Pick the days it meets.");
            return null;
        }

        if (ReadHours(ClassStart.Text, ClassEnd.Text) is not ({ } start, { } end))
        {
            ShowClassesStatus("Try times like 9:00 AM and 10:15 AM, or 9-10:15 in the first box.");
            return null;
        }

        var travel = 0;
        if (ClassTravel.Text.Trim().Length > 0
            && !int.TryParse(ClassTravel.Text.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out travel))
        {
            ShowClassesStatus("Minutes to get there should be a whole number, like 15.");
            return null;
        }

        return new ClassMeeting(name, ClassPlace.Text.Trim(), days, start, end, travel);
    }

    /// <summary>
    /// Saves the classes. They're shown straight away, so a quick second change starts from this one rather than
    /// from the classes before it, and a read that comes back while a later change is still on its way is left
    /// unshown, so it can't bring back what that change took out.
    /// </summary>
    /// <param name="fromForm">Saved from the class form, so what's typed there is saved and the form can be emptied.</param>
    private async void SaveClasses(List<ClassMeeting> classes, string? done = null, Action? undo = null, bool fromForm = false)
    {
        ClassesBar.Changed();
        var form = new ClassesForm(_termStart, _termEnd, classes);
        _classes = classes;
        ShowClasses();
        _classSaves++;
        var last = false;
        try
        {
            var (ok, reply) = await Change(() => _settings.SaveClasses(form), ShowClassesStatus);
            if (!ok)
            {
                return;
            }

            if (!reply.StartsWith("Saved", StringComparison.Ordinal))
            {
                ShowClassesStatus(reply);
                return;
            }

            _savedClasses = classes.ToList();
            if (fromForm)
            {
                _classEdits.Filled();
            }

            // The classes as the server saved them, read off the window's thread, then shown from that.
            var saved = await Work.Read(() => _settings.GetClasses());
            last = _classSaves == 1;
            if (last)
            {
                Work.Peek(LoadClasses);
                if (_settings.OnServer && Classes is { } here)
                {
                    here.Use(ClassSetup.FromForm(saved, here));
                    _database.ScheduleCourses = here.Meetings.Select(meeting => meeting.Course).ToList();
                }
            }

            ShowClassesStatus(reply);
            if (done is not null)
            {
                ClassesBar.Show(done, undo);
            }

            ShowAfterChanges();
            _changed();
        }
        finally
        {
            _classSaves--;
            if (_classSaves == 0 && !last)
            {
                // Nothing after this one will show the classes, and this one wasn't saved: back to what was.
                _classes = _savedClasses.ToList();
                ShowClasses();
            }
        }
    }

    private void OnClassEdited(object sender, RoutedEventArgs e)
    {
        var row = (ClassRow)((FrameworkElement)sender).DataContext;
        if (row.Index >= _classes.Count)
        {
            return;
        }

        var meeting = _classes[row.Index];
        ClearClassForm();
        _editingClass = meeting;
        ClassName.Text = meeting.Course;
        foreach (var day in meeting.Days.Select(day => day.ToString()))
        {
            ClassDays.SelectedItems.Add(day);
        }

        ClassStart.Text = meeting.Start.ToString("h:mm tt", CultureInfo.InvariantCulture);
        ClassEnd.Text = meeting.End.ToString("h:mm tt", CultureInfo.InvariantCulture);
        ClassPlace.Text = meeting.Room ?? "";
        ClassTravel.Text = meeting.TravelMinutes > 0 ? meeting.TravelMinutes.ToString(CultureInfo.CurrentCulture) : "";
        ClassFormTitle.Text = $"Change {meeting.Course}";
        ClassEditCancel.Visibility = Visibility.Visible;
        ClassesStatus.Visibility = Visibility.Collapsed;
        ClassName.Focus();
    }

    private void OnClassEditCancelled(object sender, RoutedEventArgs e) => ClearClassForm();

    /// <summary>Removed straight away, like a work row, with an Undo that puts the class back as it was, in its place.</summary>
    private void OnClassRemoved(object sender, RoutedEventArgs e)
    {
        var row = (ClassRow)((FrameworkElement)sender).DataContext;
        if (row.Index >= _classes.Count)
        {
            return;
        }

        var meeting = _classes[row.Index];
        var at = row.Index;
        SaveClasses(_classes.Where((_, index) => index != at).ToList(), $"Removed {meeting.Course}", () => PutClassBack(meeting, at));
    }

    private void PutClassBack(ClassMeeting meeting, int at)
    {
        if (_classes.Contains(meeting))
        {
            return;
        }

        var classes = _classes.ToList();
        classes.Insert(Math.Min(at, classes.Count), meeting);
        SaveClasses(classes);
    }

    private void OnTermStartClicked(object sender, RoutedEventArgs e)
    {
        if (PickTermDay("First day of the term", _termStart) is { } day)
        {
            _termStart = day;
            ShowClasses();
        }
    }

    private void OnTermEndClicked(object sender, RoutedEventArgs e)
    {
        if (PickTermDay("Last day of the term", _termEnd) is { } day)
        {
            _termEnd = day;
            ShowClasses();
        }
    }

    private DateOnly? PickTermDay(string title, DateOnly? current)
    {
        var picker = new PickDateWindow(Window.GetWindow(this), title, current ?? DateOnly.FromDateTime(DateTime.Now), null, asksForTime: false, action: "Choose");
        return picker.ShowDialog() == true ? picker.Date : null;
    }

    private void ShowClassesStatus(string text) => ShowNote(ClassesStatus, text);

    /// <summary>Runs a change. If the server is away, the section's note says so and nothing else happens.</summary>
    /// <summary>
    /// Runs a change, off the window's thread in server mode, after the page's earlier changes. If the server is away,
    /// or too slow to answer, <paramref name="show"/> says so and nothing else happens.
    /// </summary>
    private async Task<(bool Ok, T Result)> Change<T>(Func<T> change, Action<string> show, [System.Runtime.CompilerServices.CallerMemberName] string what = "")
    {
        try
        {
            return (true, await Work.Run(change, what));
        }
        catch (ServerUnavailableException away)
        {
            show(PageMessages.CantChange(away));
            return (false, default!);
        }
        catch (ServerTimedOutException slow)
        {
            show(slow.Message);
            return (false, default!);
        }
        catch (ServerRefusedException refused)
        {
            show(Waypoint.Core.PlainProblem.Report(refused));
            return (false, default!);
        }
    }

    private void ShowShiftDate() => ShiftDate.Content = _shiftDate.ToString("ddd, MMM d", CultureInfo.InvariantCulture);

    private const string TimesHint = "Try times like 9:00 AM and 5:00 PM, or 9-5 in the first box.";

    /// <summary>
    /// The hours typed in a start and end box. The start box can also hold the whole range, such as "9-5",
    /// "9am-5pm", "10:30 to 2" or "13:00-17:00", with the end box left empty.
    /// </summary>
    public static (TimeOnly Start, TimeOnly End)? ReadHours(string startText, string endText)
    {
        if (endText.Trim().Length == 0 && ReadRange(startText) is { } range)
        {
            return range;
        }

        return ParseTime(startText) is { } start && ParseTime(endText) is { } end ? (start, end) : null;
    }

    /// <summary>
    /// A range in one box. Without AM or PM it's read as work hours: a start from 1 to 6 is in the afternoon, and an
    /// end that would come before the start is in the afternoon too, so "9-5" is 9:00 AM to 5:00 PM.
    /// </summary>
    private static (TimeOnly Start, TimeOnly End)? ReadRange(string text)
    {
        var parts = System.Text.RegularExpressions.Regex.Split(text.Trim(), @"\s*(?:-|\u2013|\bto\b|\buntil\b|\btil\b)\s*", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (parts.Length != 2 || ReadClock(parts[0]) is not { } first || ReadClock(parts[1]) is not { } second)
        {
            return null;
        }

        var start = first.Time;
        var end = second.Time;
        if (first.Meridiem is null && second.Meridiem is null)
        {
            if (start.Hour is >= 1 and <= 6)
            {
                start = start.AddHours(12);
            }

            if (end <= start && end.Hour < 12)
            {
                end = end.AddHours(12);
            }
        }
        else if (first.Meridiem is null && start.Hour < 12 && start.AddHours(12) < end)
        {
            // "1-5pm" is 1 PM to 5 PM, while "9-5pm" stays 9 AM to 5 PM.
            start = start.AddHours(12);
        }
        else if (second.Meridiem is null && end <= start && end.Hour < 12)
        {
            end = end.AddHours(12);
        }

        return start == end ? null : (start, end);
    }

    /// <summary>One time from a range: "9", "9:30", "9am", "5 pm" or "17:00", and whether it said AM or PM.</summary>
    private static (TimeOnly Time, string? Meridiem)? ReadClock(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text.Trim(), @"^(\d{1,2})(?::(\d{2}))?\s*([ap])?\.?\s*m?\.?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var hour = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        var meridiem = match.Groups[3].Success ? match.Groups[3].Value.ToUpperInvariant() : null;
        if (minute > 59 || hour > 23 || (meridiem is not null && hour is < 1 or > 12))
        {
            return null;
        }

        if (meridiem == "P" && hour < 12)
        {
            hour += 12;
        }
        else if (meridiem == "A" && hour == 12)
        {
            hour = 0;
        }

        return (new TimeOnly(hour, minute), meridiem);
    }

    private static TimeOnly? ParseTime(string text) =>
        TimeOnly.TryParseExact(text.Trim().ToUpperInvariant(), PickDateWindow.TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;

    private static string Clock(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Hours(TimeOnly start, TimeOnly end) =>
        $"{start.ToString("h:mm tt", CultureInfo.InvariantCulture)} to {end.ToString("h:mm tt", CultureInfo.InvariantCulture)}";

    /// <summary>One entry in the Settings column. Words are what a search finds it by, besides its label.</summary>
    public sealed record SettingsSection(string Key, string Label, string Words);

    /// <summary>
    /// What a part of the page with its own Save showed when last filled from what's saved, or just saved. When it
    /// shows something else now, something was typed there and not saved, and filling the page again leaves it.
    /// </summary>
    private sealed class Unsaved(TextBlock note, Func<string> state)
    {
        private string? _filled;

        /// <summary>Where the section says how saving went, which is where "Not saved yet" goes.</summary>
        public TextBlock Note => note;

        public bool Edited => _filled is not null && state() != _filled;

        public void Filled() => _filled = state();

        public static string Join(params IEnumerable<string> parts) => string.Join('\u001f', parts);
    }

    private sealed class WorkRow(string label, string removeLabel, WeeklyHours? weekly = null, IReadOnlyList<WorkDate>? dates = null)
    {
        public string Label => label;

        public string RemoveLabel => removeLabel;

        public WeeklyHours? Weekly => weekly;

        /// <summary>Everything set for the row's date, since removing it takes them all away.</summary>
        public IReadOnlyList<WorkDate>? Dates => dates;

        /// <summary>What a screen reader says for the row, rather than the class name.</summary>
        public override string ToString() => Label;
    }

    private sealed class ClassRow(string label, string name, int index)
    {
        public string Label => label;

        public string EditLabel => $"Change {name}";

        public string RemoveLabel => $"Remove {name}";

        public int Index => index;

        /// <summary>What a screen reader says for the row, rather than the class name.</summary>
        public override string ToString() => Label;
    }

    private sealed class Swatch(string name, string accent)
    {
        public string Name => name;

        public Brush Fill { get; } = new SolidColorBrush((Color)ColorConverter.ConvertFromString(accent));

        /// <summary>What a screen reader says for the swatch, rather than the class name.</summary>
        public override string ToString() => Name;
    }
}
