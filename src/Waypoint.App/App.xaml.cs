using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Waypoint.Core;
using Waypoint.Core.Background;
using Waypoint.Core.Budget;
using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Email;
using Waypoint.Core.Files;
using Waypoint.Core.Remote;
using Waypoint.Core.Schedule;
using Waypoint.Core.Theme;
using Forms = System.Windows.Forms;

namespace Waypoint.App;

public partial class App : System.Windows.Application
{
    private const int MaxTrayTextLength = 127;
    private const int MaxBalloonTextLength = 200;
    private const string NextStepQuestion = "What do I do now?";
    private const string ErrorLogFile = "errors.log";
    private const string NotesFolderKey = "notes_files_root";
    private const long MaxErrorLogBytes = 1024 * 1024;
    private const int MaxStackedCards = 4;

    private static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromMinutes(1);

    private ThemePalette? _palette;
    private Mutex? _singleInstance;
    private EventWaitHandle _showList = null!;
    private EventWaitHandle? _exitRequest;
    private WaypointWorker _worker = null!;
    private ClassSchedule? _schedule;
    private DispatcherTimer? _calendarTimer;
    private DispatcherTimer? _statusTimer;
    private DispatcherTimer? _alertTimer;
    private BedtimeSchedule? _bedtimeSchedule;
    private int _openCards;

    // Items whose reminder card is up, so Ask doesn't show the same thing as held from earlier beside it.
    private readonly HashSet<string> _cardsUp = [];
    private bool _started;
    private DispatcherTimer? _discordTimer;
    private DispatcherTimer? _emailTimer;
    private NoteFileSync? _noteSync;

    // Set in server mode, when the server runs reminders, Discord, and email and this PC only shows and captures.
    private ServerConnection? _server;

    // Whenever a server address is set, in either mode, this PC tells the server how long since the last input
    // during the late window, since the server may be the one sending late-night check-ins.
    private IdleSender? _idleSender;

    // Keyboard and mouse, and game controllers, which Windows' last-input time doesn't see. Only read in the late window.
    private static readonly Waypoint.Core.Schedule.MinimumIdle PcIdle = new(WindowsIdle.Instance, new Waypoint.Core.Schedule.ControllerIdle(XInputPackets.Read));
    private DispatcherTimer? _idleTimer;

    // False in server mode with no address yet: nothing that sends is started, so nothing goes out beside the server.
    private bool _sends = true;
    private DispatcherTimer? _claimTimer;

    // Server mode with no address: typing waits here, in the file server mode sends from, not in this PC's database.
    private CaptureQueue? _heldCaptures;
    private DispatcherTimer? _serverTimer;

    // Work that can wait: checking email, the calendar, and Discord, syncing notes, and transcribing. At sign-in it
    // starts a minute later, so Waypoint doesn't compete with everything else starting then.
    private readonly List<Action> _later = [];
    private static readonly TimeSpan LoginSettle = TimeSpan.FromMinutes(1);
    private Forms.NotifyIcon? _tray;
    private WaypointDatabase _database = null!;
    private CaptureService _captures = null!;
    private Waypoint.Core.Transcription.TranscriptionQueue? _transcription;
    private LectureRecorder? _recorder;
    private Waypoint.Core.Recording.ClassStartOffer? _recordOffer;
    private Forms.ToolStripMenuItem? _recordItem;
    private CaptureWindow _captureWindow = null!;
    private ItemsWindow _itemsWindow = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandledException;

        // Problems are said plainly in the window, and the detail behind them goes to errors.log.
        PlainProblem.Log = LogError;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogError(args.Exception);
            args.SetObserved();
        };

        // A copy on its own data folder gets its own name, so it runs beside the installed copy without waking it or
        // being turned away by it.
        var instance = ChosenDataDirectory is { } chosen
            ? $@"Local\Waypoint-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(chosen).ToUpperInvariant())))[..16]}"
            : @"Local\Waypoint";
        _singleInstance = new Mutex(initiallyOwned: true, instance, out var isFirstInstance);
        _showList = new EventWaitHandle(false, EventResetMode.AutoReset, $"{instance}.ShowList");
        if (!isFirstInstance)
        {
            // Already running, so ask that copy to open its list.
            _showList.Set();
            Shutdown();
            return;
        }

        var dataDirectory = GetDataDirectory();
        // Before anything reads a statement or an alert, so purchases go under the account names the profiles give.
        Waypoint.Core.Budget.BankProfiles.Load(dataDirectory);
        _database = new WaypointDatabase(Path.Combine(dataDirectory, "waypoint.db"));
        var settings = WaypointSettings.Load(Path.Combine(dataDirectory, "settings.json"));
        // Server mode with no address doesn't fall back to running everything here, which would send beside the server.
        // It starts nothing that sends and says what's missing. Going back to Local after server mode starts Discord
        // from now, so captures the server already took aren't taken again from this PC's old place.
        var run = PcMode.Choose(settings.Server);
        _sends = PcMode.Sends(run);
        _heldCaptures = PcMode.HeldCaptures(run, dataDirectory);
        if (run == PcRun.Server)
        {
            _server = new ServerConnection(settings.Server!.Address!.Trim(), dataDirectory, _database.GetRecordingMarksForNote);
        }

        PcMode.Started(_database, run, DateTimeOffset.Now);

        // Whisper, set up once for lectures, also turns voice notes sent in Discord's capture channel into text.
        var whisper = new Waypoint.Core.Transcription.WhisperSetup(dataDirectory, new Waypoint.Core.Transcription.HttpFileDownloader(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }));
        var audio = new MediaFoundationAudio();
        var voice = new Waypoint.Core.Transcription.WhisperVoiceTranscriber(_database, whisper, audio, new Waypoint.Core.Transcription.ChildProcessRunner(), dataDirectory);

        // The reminders, alerts, Discord, calendar, and email live in Core, so a server can run the same logic.
        _worker = new WaypointWorker(
            _database,
            settings,
            dataDirectory,
            new WindowHost(this),
            LogError,
            new WorkerParts(path => new ProtectedTokenStore(path), TextRecognizer: ImageText.Recognizer, Runner: DiscordRunner.Pc, Idle: PcIdle, Voice: voice))
        {
            HoldsConnections = !_sends,
        };
        _schedule = _worker.Schedule;
        _bedtimeSchedule = _worker.BedtimeSchedule;
        var idleReports = new Waypoint.Core.Remote.IdleReporter(_worker.BedtimeSchedule, PcIdle);
        var fileBedtime = _worker.FileBedtime;
        _captures = _worker.Captures;
        _captureWindow = new CaptureWindow(OnCaptureSubmitted, OnFilesDropped);

        // Notes are written with Haiku where sorting runs: here in local mode, on the server in server mode.
        var noteMaker = new Waypoint.Core.Notes.NoteMaker(
            _database, new Waypoint.Core.Notes.NoteWriter(new Waypoint.Core.Notes.ClaudeCliRunner(dataDirectory)), _schedule);
        Waypoint.Core.Notes.INotesData notes = (Waypoint.Core.Notes.INotesData?)_server?.Notes
            ?? new Waypoint.Core.Notes.LocalNotesData(_database, noteMaker, new Waypoint.Core.Notes.ClaudeCliRunner(dataDirectory));

        // Files sent in Discord's notes channel make notes the same way, where Discord runs. In server mode that's the server.
        if (_server is null)
        {
            _worker.NoteFiles = notes;
        }

        _itemsWindow = new ItemsWindow(_database, OnCaptureSubmitted, ImportCsv, OnFilesDropped, _server?.Data, _server?.Budget, _server?.Sessions, _server?.Lists, _server?.History, notes, _server?.Feedback)
        {
            Classes = _schedule,
            CardUpFor = item => _cardsUp.Contains(ItemRef.Format(item)),
        };
        _itemsWindow.NotesPage.ConnectStudy(_server is { } connection
            ? new Waypoint.Core.Remote.ServerNoteAudio(connection.Notes, _database, dataDirectory)
            : new Waypoint.Core.Notes.NoteAudio(_database, dataDirectory));

        // Recordings are transcribed on this PC, in both modes. Nothing downloads until the user agrees on the Notes
        // page. Only the transcript goes to the server, as note text, with the audio only if it's kept.
        _transcription = new Waypoint.Core.Transcription.TranscriptionQueue(
            _database,
            noteMaker,
            whisper,
            audio,
            new Waypoint.Core.Transcription.ChildProcessRunner(),
            dataDirectory,
            notes: notes);
        _itemsWindow.NotesPage.ConnectTranscription(_transcription);

        // Voice notes kept from Discord while speech to text wasn't set up are read as soon as it is.
        var voiceReady = voice.IsReady;
        _transcription.Changed += change =>
        {
            if (!voiceReady && voice.IsReady)
            {
                voiceReady = true;
                _ = _worker.ReadHeldVoiceNotesAsync();
            }
        };
        _later.Add(_transcription.Start);

        // Lectures record only when the user asks, from + on Notes, the tray, or the optional class start card.
        _recorder = new LectureRecorder(
            new Waypoint.Core.Recording.Recorder(_database, dataDirectory, new KeepAwake(), _schedule),
            _database,
            _transcription,
            _itemsWindow,
            dataDirectory,
            (title, text) => _worker.NotifyAsync(title, text, CardTone.Attention));
        _recorder.StateChanged += ShowRecordingState;
        _recorder.TrayTextChanged += () =>
        {
            if (_tray is not null && _recorder.IsRecording)
            {
                var text = _recorder.TrayText;
                _tray.Text = Trial.TrayTip(text, MaxTrayTextLength);
            }
        };
        _itemsWindow.NotesPage.RecordRequested += _recorder.AskToStart;
        _recordOffer = new Waypoint.Core.Recording.ClassStartOffer(_schedule);

        // Every note is also a .md file, so other apps can read and edit them. In server mode the files follow the
        // server's notes, with the same rules, and wait while it's away or while typing from then is still being saved.
        _noteSync = _server is { } synced
            ? new NoteFileSync(
                synced.Notes.ForSync(),
                NotesFolder(dataDirectory),
                () => _itemsWindow.NotesPage.Refresh(),
                LogError,
                ServerConnection.SyncInterval,
                () => synced.Client.IsOnline && synced.Notes.WaitingToSave == 0)
            : new NoteFileSync(_database, NotesFolder(dataDirectory), () => _itemsWindow.NotesPage.Refresh(), LogError);
        // The folder is saved only once the move is made. In server mode it needs the server, and says so.
        _itemsWindow.ConnectNotesFolder(() => _noteSync.Root, root =>
        {
            try
            {
                _noteSync.MoveTo(root, moved => _database.SetPreference(NotesFolderKey, moved));
            }
            catch (Waypoint.Core.Remote.ServerUnavailableException away)
            {
                _itemsWindow.ShowReply($"The notes folder wasn't moved. {away.Message}");
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                _itemsWindow.ShowReply($"The notes folder wasn't moved. {PlainProblem.Report(exception)} Try again, or pick another folder.");
            }
        });
        _later.Add(_noteSync.Start);

        _itemsWindow.DataChanged += RefreshStatus;
        _captures.Changed += () => Dispatcher.BeginInvoke(RefreshStatus);
        _captures.Sorted += outcome => Dispatcher.BeginInvoke(() => ShowOutcome(outcome));

        // The window says only that sorting failed. Why goes here, once for each new reason, until a sort works again.
        _captures.Failed += LogSortFailure;
        _captures.Sorted += _ => _loggedSortFailure = null;
        _itemsWindow.RetrySorting = () =>
        {
            if (_server is null)
            {
                _ = _captures.ProcessPendingAsync();
            }
            else
            {
                _ = SyncWithServerAsync();
            }
        };

        // In server mode the server runs all of this, so nothing below is started here and nothing is sent twice.
        // A trial run starts none of it either, since its data folder is often a copy of the installed one's.
        // A feed can also be set up later on the Settings page, so the hourly sync runs either way and does nothing
        // until there is one. Once it runs, a newly saved link is read straight away.
        if (_server is null && _sends && Trial.StartsConnections)
        {
            _calendarTimer = new DispatcherTimer { Interval = WaypointWorker.CalendarSyncInterval };
            _calendarTimer.Tick += (_, _) => _ = _worker.SyncCalendarAsync();
            _later.Add(() =>
            {
                _worker.SyncsCalendar = true;
                _calendarTimer.Start();
                _ = _worker.SyncCalendarAsync();
            });
        }

        // Discord can also be set up later on the Settings page, so its poll timer runs either way and does
        // nothing until it's connected. In server mode the server runs it, so it isn't started here.
        _discordTimer = new DispatcherTimer { Interval = WaypointWorker.DiscordPollInterval };
        _discordTimer.Tick += (_, _) => _ = _worker.PollDiscordAsync();
        if (_server is null && _sends && Trial.StartsConnections)
        {
            // Only one copy uses the bot. If the server, or another PC, is already using it, this PC sends nothing
            // and says so, and takes over only if that copy stops.
            _claimTimer = new DispatcherTimer { Interval = DiscordClaim.CheckEvery };
            _claimTimer.Tick += (_, _) => _ = _worker.CheckDiscordClaimAsync();
            _later.Add(() =>
            {
                _worker.StartDiscord();
                _discordTimer.Start();
                _claimTimer.Start();
            });
        }

        // Alert email can also be set up later on the Settings page, so its timer runs either way and does nothing until then.
        if (_server is null && _sends && Trial.StartsConnections)
        {
            _emailTimer = new DispatcherTimer { Interval = WaypointWorker.EmailPollInterval };
            _emailTimer.Tick += (_, _) => _ = _worker.PollEmailAsync();
            _later.Add(() =>
            {
                _emailTimer.Start();
                _ = _worker.PollEmailAsync();
            });
        }

        if (_server is null && _sends && Trial.StartsConnections && _worker.Outlook is not null)
        {
            // "Check my email" in Ask reads it now instead of waiting for the timer.
            _captures.CheckEmail = _ => System.Threading.Tasks.TaskExtensions.Unwrap(Dispatcher.InvokeAsync(_worker.CheckEmailNowAsync).Task);
        }

        _itemsWindow.ConnectSettings(
            (ISettingsData?)_server?.Settings ?? new LocalSettingsData(_database, _worker),
            _bedtimeSchedule,
            fileBedtime,
            () => Trial.Describe(_sends
                ? _server?.Client.LastStatus?.Connections ?? _worker.DescribeConnections()
                : [new ConnectionStatus("Server", PcMode.NeedsAddressNotice)]));

        _statusTimer = new DispatcherTimer { Interval = StatusRefreshInterval };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        if (_server is null)
        {
            if (Trial.StartsAlerts && _sends)
            {
                _alertTimer = new DispatcherTimer { Interval = WaypointWorker.AlertCheckInterval };
                _alertTimer.Tick += (_, _) =>
                {
                    // Separately, so a problem in one can't stop the alerts after it.
                    _worker.CheckAlerts();
                    if (Trial.OffersRecording)
                    {
                        Guarded(CheckRecordOffer);
                    }
                };
                _alertTimer.Start();
            }

            // In Local mode a running server can still hold Discord, so with its address set it hears the idle time too.
            // An address that isn't http or https is skipped here rather than stopping the app.
            if (Trial.StartsConnections)
            {
                _idleSender = IdleSender.ForAddress(settings.Server?.Address, dataDirectory, idleReports);
            }
        }
        else
        {
            // Short polling, so changes made from Discord or the phone show up here, and waiting captures go out.
            _serverTimer = new DispatcherTimer { Interval = ServerConnection.SyncInterval };
            _serverTimer.Tick += (_, _) => _ = SyncWithServerAsync();
            _serverTimer.Start();
            _ = SyncWithServerAsync();
            _idleSender = new IdleSender(_server.Client, idleReports);
        }

        // On its own timer, so a slow or failing sync round can't hold up tonight's idle readings.
        if (_idleSender is { } sender)
        {
            _idleTimer = new DispatcherTimer { Interval = ServerConnection.SyncInterval };
            _idleTimer.Tick += (_, _) => _ = sender.TickAsync(DateTime.Now);
            _idleTimer.Start();
        }

        _tray = CreateTray();
        RefreshStatus();
        if (!_sends)
        {
            ShowToast("Add the server address", PcMode.NeedsAddressNotice);
        }

        var showListThread = new Thread(() =>
        {
            while (_showList.WaitOne())
            {
                Dispatcher.BeginInvoke(() => _itemsWindow.ShowLastPage());
            }
        })
        { IsBackground = true };
        showListThread.Start();

        // install.ps1 sets this to close the app the usual way, saving drafts and finishing a recording, before it
        // switches versions. Only the copy that holds this name's single-instance lock listens for it.
        _exitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, $"{instance}.Exit");
        ThreadPool.RegisterWaitForSingleObject(
            _exitRequest, (_, _) => Dispatcher.BeginInvoke(() => Shutdown()), null, Timeout.Infinite, executeOnlyOnce: true);

        if (_server is null)
        {
            _later.Add(() => _ = _captures.ProcessPendingAsync());
        }

        StartLater(e.Args.Contains(StartupSetting.AtLoginArgument));
        // Only the installed copy keeps the sign-in entry, so a copy on its own data folder, trial or not, never
        // points it at itself.
        if (ChosenDataDirectory is null)
        {
            StartupSetting.Refresh();
        }

        _started = true;

        // A recording cut off by a crash or power cut is fixed and offered once things have settled.
        Dispatcher.BeginInvoke(() => Guarded(() => _recorder?.OfferUnfinished()), DispatcherPriority.ApplicationIdle);
    }

    private void StartLater(bool atLogin)
    {
        void Run()
        {
            foreach (var start in _later)
            {
                Guarded(start);
            }

            _later.Clear();
        }

        if (!atLogin)
        {
            Run();
            return;
        }

        var wait = new DispatcherTimer { Interval = LoginSettle };
        wait.Tick += (_, _) =>
        {
            wait.Stop();
            Run();
        };
        wait.Start();
    }

    /// <summary>The optional card at class start. Off unless turned on in Settings, and it only asks.</summary>
    private void CheckRecordOffer()
    {
        var turnedOn = Waypoint.Core.Recording.ClassStartOffer.IsOn(_database.GetPreference(Waypoint.Core.Recording.ClassStartOffer.Preference));
        if (_recorder is not { } recorder || _recordOffer?.Check(DateTime.Now, turnedOn, recorder.IsRecording) is not { } current)
        {
            return;
        }

        new RecordOfferWindow(current.Meeting.Course, recorder.StartFromCard).Show();
    }

    private void ShowRecordingState()
    {
        if (_recordItem is not null)
        {
            _recordItem.Text = _recorder?.IsRecording == true ? "Stop recording" : "Start recording";
        }

        RefreshStatus();
    }

    private void OnRecordItemClicked()
    {
        if (_recorder?.IsRecording == true)
        {
            _recorder.Stop();
        }
        else
        {
            _recorder?.AskToStart();
        }
    }

    private void Guarded(Action check)
    {
        try
        {
            check();
        }
        catch (Exception exception)
        {
            LogError(exception);
        }
    }

    /// <summary>Once running, staying up matters most, since alerts stop if the app does. A failed start still ends it.</summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // In server mode a change made while the server is away says so in Ask, and nothing else is wrong. During
        // startup it's a failed start like any other: handled there, the tray and window would never be made, and the
        // copy would sit invisible, holding the single-instance lock.
        if (e.Exception is ServerUnavailableException away && _started)
        {
            _itemsWindow?.ShowReply(away.Message);
            e.Handled = true;
            return;
        }

        LogError(e.Exception);
        e.Handled = _started;
    }

    private string? _loggedSortFailure;

    private void LogSortFailure(string error)
    {
        if (error == _loggedSortFailure)
        {
            return;
        }

        _loggedSortFailure = error;
        LogText($"Sorting failed: {error}");
    }

    private readonly Dictionary<string, string?> _loggedProblems = [];

    /// <summary>The window says these plainly, so the detail is kept here, once each time it changes.</summary>
    private void LogOnce(string what, string? error)
    {
        if (_loggedProblems.TryGetValue(what, out var logged) && logged == error)
        {
            return;
        }

        _loggedProblems[what] = error;
        if (error is not null)
        {
            LogText($"{what} failed: {error}");
        }
    }

    private static void LogError(Exception exception) => LogText(exception.ToString());

    private static void LogText(string text)
    {
        try
        {
            var path = Path.Combine(GetDataDirectory(), ErrorLogFile);
            if (File.Exists(path) && new FileInfo(path).Length > MaxErrorLogBytes)
            {
                File.Delete(path);
            }

            File.AppendAllText(path, $"{DateTime.Now:O}\n{text}\n---\n");
        }
        catch (Exception logFailure) when (logFailure is IOException or UnauthorizedAccessException)
        {
            // Nothing else to do with it, and the app should keep running.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // A second copy exits before any of this is set up, so each part is checked before it's used.
        _itemsWindow?.SaveDrafts();
        _itemsWindow?.SavePlace();
        _noteSync?.Finish();

        // Finishes the file, so what was recorded is offered as notes next start.
        _recorder?.Dispose();

        // A capture or press read just before closing is still saved, within a few seconds. Off this thread, so
        // nothing it finishes can wait on the window thread being free.
        if (_worker is { } worker)
        {
            System.Threading.Tasks.Task.Run(() => worker.StopDiscordAsync(DiscordGateway.EventDrainLimit)).Wait();
        }

        // Outside a night's check-in window, nothing about the last check-in is left saved.
        _worker?.ForgetLateNight();
        _transcription?.Dispose();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// The folder chosen in Settings, or Documents\Waypoint Notes. A trial run always keeps its notes inside its data
    /// folder, even when its copied database names the real folder, so a rehearsal never touches the real notes. A data
    /// folder set without a trial run keeps its notes inside it unless a folder was chosen.
    /// </summary>
    private string NotesFolder(string dataDirectory) =>
        IsTrialRun ? Path.Combine(dataDirectory, "Notes")
        : _database.GetPreference(NotesFolderKey) is { Length: > 0 } chosen && Path.IsPathFullyQualified(chosen) ? chosen
        : ChosenDataDirectory is not null ? Path.Combine(dataDirectory, "Notes")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Waypoint Notes");

    /// <summary>The data folder set with WAYPOINT_DATA_DIR, or null for the usual one.</summary>
    private static string? ChosenDataDirectory =>
        Environment.GetEnvironmentVariable("WAYPOINT_DATA_DIR") is { Length: > 0 } directory ? directory : null;

    /// <summary>
    /// WAYPOINT_TRIAL=1 with a data folder of its own: a rehearsal beside the installed copy. It keeps its notes in its
    /// folder, leaves the sign-in entry alone, and starts no alerts or connections.
    /// </summary>
    private static readonly TrialRun Trial = TrialRun.From(ChosenDataDirectory, Environment.GetEnvironmentVariable);

    private static bool IsTrialRun => Trial.Active;

    /// <summary>The tray's tip when there's nothing else to say. A trial run's tip says so, from <see cref="TrialRun.TrayTip"/>.</summary>
    private const string TrayIdle = "Waypoint";

    private static string GetDataDirectory()
    {
        var directory = ChosenDataDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Waypoint");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private Forms.NotifyIcon CreateTray()
    {
        var startWithWindows = new Forms.ToolStripMenuItem("Start with Windows") { Checked = StartupSetting.IsEnabled, CheckOnClick = true };
        startWithWindows.Click += (_, _) => StartupSetting.Set(startWithWindows.Checked);

        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new Forms.ToolStripProfessionalRenderer(new TrayMenuColors(_palette ?? Themes.Default)),
            BackColor = Color.Black,
            ForeColor = Color.White,
        };
        menu.HandleCreated += (_, _) => WindowStyling.Apply(menu.Handle);
        menu.Items.Add("Ask Waypoint...", null, (_, _) => _captureWindow.ShowForCapture());
        menu.Items.Add("Open Waypoint", null, (_, _) => _itemsWindow.ShowList());
        menu.Items.Add(NextStepQuestion, null, (_, _) => OnCaptureSubmitted(NextStepQuestion));
        // The same form as To do's Save my spot, so it works without sorting. Opened once the menu has closed, like Pair.
        menu.Items.Add("Save my spot...", null, (_, _) => Dispatcher.BeginInvoke(_itemsWindow.OpenSaveSpot));

        // Lines group the menu: what's used every day, recording, connecting money and the server, backups, and
        // starting with Windows, then Exit on its own.
        menu.Items.Add(new Forms.ToolStripSeparator());

        // Recording stays on this PC in both modes. Its notes go to the server in server mode.
        _recordItem = new Forms.ToolStripMenuItem(_recorder?.IsRecording == true ? "Stop recording" : "Start recording");
        _recordItem.Click += (_, _) => OnRecordItemClicked();
        menu.Items.Add(_recordItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        if (_server is not null)
        {
            // The rest work on this PC's database, so in server mode they're left out until they go through the server.
            // Opened once the tray menu has closed, so the form isn't held inside the menu's own click and shows up
            // as a window of its own, which is how screen readers and UI Automation find it.
            menu.Items.Add("Pair with server...", null, (_, _) => Dispatcher.BeginInvoke(PairWithServer));
            menu.Items.Add("Import card or bank CSV...", null, (_, _) => ImportCsv());
            menu.Items.Add("Connect alert email", null, (_, _) => _ = ConnectServerEmailAsync(_server));
            menu.Items.Add(new Forms.ToolStripSeparator());
            AddServerBackupItems(menu, _server);
        }
        else
        {
            menu.Items.Add("Import card or bank CSV...", null, (_, _) => ImportCsv());
            menu.Items.Add("Connect alert email", null, (_, _) => _ = ConnectEmailAsync());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Back up now", null, (_, _) => BackUpNow());
            menu.Items.Add("Restore a backup...", null, (_, _) => RestoreBackup());
            menu.Items.Add("Export notes...", null, (_, _) => ExportNotes());
        }

        // The sign-in entry belongs to the installed copy. From a copy on its own data folder, unchecking it would
        // remove the real one, and checking it would point it at this copy, which starts without its data folder.
        if (ChosenDataDirectory is null)
        {
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(startWithWindows);
        }

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        using var iconStream = typeof(App).Assembly.GetManifestResourceStream("waypoint.ico")!;
        var icon = new Icon(iconStream, Forms.SystemInformation.SmallIconSize);

        var tray = new Forms.NotifyIcon { Icon = icon, Text = Trial.TrayTip(TrayIdle, MaxTrayTextLength), Visible = true, ContextMenuStrip = menu };
        tray.BalloonTipClicked += (_, _) => _itemsWindow.ShowList();
        tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                _itemsWindow.ShowList();
            }
        };
        return tray;
    }

    private void ShowReminderCard(DueReminder reminder, Action closed)
    {
        // Repeating items and reminders are finished here. An exam's alert is only an alert.
        var finishesItem = reminder.RepeatMinutes is not null || reminder.Item.Kind is ItemKind.Reminder or ItemKind.Task;
        var snoozeMinutes = reminder.RepeatMinutes ?? WaypointWorker.DefaultSnoozeMinutes;

        SystemSounds.Asterisk.Play();
        ReminderWindow? window = null;
        ReminderCardHold? holdLater = null;

        // A dose gets Took it, Not now (once), and Skip, each kept for the day the card is for, whichever card answers first.
        if (reminder.Item is { Dose: true, Recurs: not null, DueDate: { } dueOn })
        {
            string Answer(DoseAnswer answer)
            {
                var text = _database.AnswerDose(reminder.Item.Id, dueOn, answer, DateTime.Now, reminder);
                RefreshStatus();
                return text;
            }

            window = new ReminderWindow(
                reminder,
                CardLabels.TookIt,
                TransitionAssist.FollowUpMinutes,
                () => ShowToast(reminder.Item.Title, Answer(DoseAnswer.Took)),
                () => Answer(DoseAnswer.NotNow),
                dose: new ReminderDose(!reminder.IsFollowUp, () => Answer(DoseAnswer.Skip)))
            {
                Slot = _openCards % MaxStackedCards,
                FeedbackKept = () => _database.AddFeedback(Feedback.Reminder, ItemRef.Format(reminder.Item), null, DateTime.Now),
                FeedbackNoted = (id, note) => _database.NoteFeedback(id, note),
            };
            _openCards++;
            var doseRef = ItemRef.Format(reminder.Item);
            _cardsUp.Add(doseRef);
            window.Closed += (_, _) =>
            {
                _openCards--;
                _cardsUp.Remove(doseRef);
                closed();
            };
            window.Show();
            return;
        }

        // A waiting-on nudge gets Follow up, Still waiting, and Done, all answered here without the sorter.
        var waitingOn = _database.WaitingFor(reminder);
        var waiting = waitingOn is null
            ? null
            : new ReminderWaiting(
                () =>
                {
                    _database.DismissReminder(reminder);
                    _itemsWindow.ShowReply(WaitingOn.DraftReply(waitingOn));
                    _itemsWindow.ShowList();
                },
                () =>
                {
                    ShowToast("Still waiting", _database.StillWaiting(waitingOn.ItemId, null, DateTime.Now).Text);
                    RefreshStatus();
                });
        var transition = waiting is null && TransitionAssist.Applies(reminder)
            ? new ReminderTransition(
                finishesItem,
                () => _database.DismissReminder(reminder),
                () =>
                {
                    _database.DismissReminder(reminder);
                    _database.RecordReminderAnswer(reminder.Item, ReminderAnswer.NotNow, DateTime.Now);
                },
                TransitionAssist.OffersMoreTime(reminder) ? () => _database.FollowUpReminder(reminder, DateTime.Now) : null,
                TransitionAssist.OffersSaveSpot(_database.GetLastSpotSaved(), DateTime.Now) ? () => SaveSpotFromCard(window!) : null)
            : null;
        window = new ReminderWindow(
            reminder,
            finishesItem ? "Done" : "Got it",
            snoozeMinutes,
            () =>
            {
                if (finishesItem)
                {
                    _database.SetDone(reminder.Item, true);
                }
                else
                {
                    _database.DismissReminder(reminder);
                }

                RefreshStatus();
            },
            () => _database.SnoozeReminder(reminder, DateTime.Now.AddMinutes(snoozeMinutes)),
            reminder.Item.Source == ItemSource.Calendar ? () => _database.SkipNudges(reminder.Item.Id) : null,
            Activation.CanStart(reminder.Item)
                ? () =>
                {
                    _database.RecordReminderAnswer(reminder.Item, ReminderAnswer.Started, DateTime.Now);
                    _captures.StartOn(reminder.Item);
                }
                : null,
            transition,
            waiting)
        {
            Slot = _openCards % MaxStackedCards,
            FeedbackKept = () =>
            {
                // Saying it didn't help answers the card, so it isn't held for after this either.
                holdLater?.Answered();
                return _database.AddFeedback(Feedback.Reminder, ItemRef.Format(reminder.Item), null, DateTime.Now);
            },
            FeedbackNoted = (id, note) => _database.NoteFeedback(id, note),
        };
        _openCards++;

        // A card with Start left unanswered for a while means "after this", so the item is held once, to come back
        // when To do or Ask next opens. The card stays up, and answering it after all drops the hold.
        holdLater = ReminderCardHold.Applies(reminder.Item, waiting is not null) ? new ReminderCardHold(_database, reminder.Item, DateTime.Now) : null;
        holdLater?.Start();
        var cardRef = ItemRef.Format(reminder.Item);
        _cardsUp.Add(cardRef);

        window.Closed += (_, _) =>
        {
            _openCards--;
            _cardsUp.Remove(cardRef);
            holdLater?.Answered();
            closed();
        };
        window.Show();
    }

    /// <summary>The same form and save as the To do page's Save my spot, opened over the reminder card.</summary>
    private bool SaveSpotFromCard(Window card)
    {
        var form = new SaveSpotWindow(card, _database.GetActiveSession(DateTime.Now)?.TaskTitle, spot =>
            _database.SaveSpotByHand(spot.Project, spot.Stopped, spot.Next, spot.Links, spot.Thoughts, DateTime.Now));
        if (form.ShowDialog() != true || form.Reply is not { } reply)
        {
            return false;
        }

        _itemsWindow.ShowReply(reply);
        RefreshStatus();
        return true;
    }

    private void BackUpNow()
    {
        try
        {
            _itemsWindow.SaveDrafts();
            var path = _database.BackUpNow(DateTime.Now);
            ShowToast("Backed up", $"Saved a copy in {Path.GetDirectoryName(path)}");
        }
        catch (Exception exception)
        {
            ShowToast("Backup didn't work", $"{PlainProblem.Report(exception)} Try Back up now again in a moment.");
        }
    }

    /// <summary>Asks first, and saves how things are now before replacing anything.</summary>
    private void RestoreBackup()
    {
        Directory.CreateDirectory(_database.BackupFolder);
        using var dialog = new Forms.OpenFileDialog
        {
            Filter = "Waypoint backups (*.db)|*.db",
            Title = "Choose a backup to restore",
            InitialDirectory = _database.BackupFolder,
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        var when = File.GetLastWriteTime(dialog.FileName).ToString("dddd, MMM d 'at' h:mm tt", CultureInfo.CurrentCulture);
        if (!NoteConfirmWindow.Ask(
            _itemsWindow,
            RestoreTitle,
            $"Replace everything in Waypoint with the backup from {when}?",
            "A copy of how things are right now is saved in the backups folder first, so you can switch back.",
            "Restore",
            "Cancel"))
        {
            return;
        }

        try
        {
            _itemsWindow.SaveDrafts();
            _itemsWindow.Hide();
            // The notes folder sync waits, so it can't undo the restore with the files from before it.
            var path = dialog.FileName;
            var result = _noteSync is { } sync
                ? sync.WhileHeld(() => _database.RestoreBackup(path, DateTime.Now))
                : _database.RestoreBackup(path, DateTime.Now);
            if (!result.Restored)
            {
                NoteConfirmWindow.Tell(null, RestoreTitle, result.Problem ?? "Nothing was changed.");
                return;
            }

            // Drafts were saved above, into the copy taken before the restore. What the pages still hold is from
            // before it, so it must not be written into the restored data on the way out.
            _itemsWindow.DiscardDrafts();

            if (NoteConfirmWindow.Ask(
                null,
                RestoreTitle,
                "Restored. Close Waypoint now? Then open it again the way you usually do.",
                $"Waypoint needs to close and open again to show it everywhere. How things were before is saved as {Path.GetFileName(result.SafetyCopy)}.",
                "Close Waypoint",
                "Not now"))
            {
                Shutdown();
            }
        }
        catch (Exception exception)
        {
            NoteConfirmWindow.Tell(null, RestoreTitle, $"The restore didn't work, so nothing was changed. {PlainProblem.Report(exception)} Try again, or pick another backup.");
        }
    }

    /// <summary>Into a new dated folder, so an earlier export is never written over.</summary>
    private void ExportNotes()
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Choose where to save your notes", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        try
        {
            _itemsWindow.SaveDrafts();
            var name = $"Waypoint notes {DateTime.Now:yyyy-MM-dd HHmm}";
            var destination = Path.Combine(dialog.SelectedPath, name);
            var count = _database.ExportNotes(destination);
            ShowToast("Notes exported", $"Saved {count} {(count == 1 ? "note" : "notes")} in {name}");
        }
        catch (Exception exception)
        {
            ShowToast("Export didn't work", $"{PlainProblem.Report(exception)} Try again, or pick another folder.");
        }
    }

    private void ImportCsv()
    {
        using var dialog = new Forms.OpenFileDialog { Filter = "CSV files (*.csv)|*.csv", Title = "Import card or bank CSV", Multiselect = true };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        ImportStatements(dialog.FileNames.Select(File.ReadAllText));
    }

    /// <summary>
    /// In server mode the files are read here and imported there, so the server's budget is the one that changes.
    /// That can take the server a while, so the window isn't held up waiting.
    /// </summary>
    private async void ImportStatements(IEnumerable<string> statements)
    {
        if (_server is null)
        {
            ShowImported(_worker.ImportStatementsReply(statements));
            return;
        }

        var server = _server;
        var read = statements.ToList();
        try
        {
            ShowImported(await System.Threading.Tasks.Task.Run(() => server.Budget.ImportStatements(read)));
        }
        catch (ServerUnavailableException)
        {
            _itemsWindow.ShowReply("Can't reach the server, so nothing was imported. Import the file again once it's back.");
        }
        catch (ServerTimedOutException timedOut)
        {
            _itemsWindow.ShowReply(timedOut.Message);
        }
        catch (InvalidOperationException exception)
        {
            _itemsWindow.ShowReply($"The server couldn't import that. {PlainProblem.Report(exception)} Check that it's a card or checking export, then import it again.");
        }
    }

    /// <summary>What an import said, then Purchases, when it added any, so they're seen where they went.</summary>
    private void ShowImported(string reply)
    {
        _itemsWindow.ShowReply(reply);
        if (reply.StartsWith("Added ", StringComparison.Ordinal))
        {
            _itemsWindow.ShowPurchases();
        }
    }

    private async System.Threading.Tasks.Task ConnectEmailAsync()
    {
        if (_worker.Outlook is not { } outlook)
        {
            _itemsWindow.ShowReply(EmailNotSetUp);
            _itemsWindow.ShowList();
            return;
        }

        try
        {
            var code = await outlook.BeginSignInAsync();
            _itemsWindow.ShowReply($"Enter code {code.UserCode} on the Microsoft page that just opened, then choose the alerts inbox.");
            _itemsWindow.ShowList();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(code.VerificationUri) { UseShellExecute = true });

            var connected = await outlook.FinishSignInAsync(code);
            _itemsWindow.ShowReply(connected ? "Alert email connected." : "That sign-in didn't finish. Try Connect alert email again.");
            _ = _worker.PollEmailAsync();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or KeyNotFoundException or System.ComponentModel.Win32Exception)
        {
            _itemsWindow.ShowReply($"Couldn't connect the alert email. {PlainProblem.Report(exception)} Try Connect alert email again in a moment.");
        }
    }

    private const string EmailNotSetUp = "Alert email isn't set up yet. Add its app ID on the Settings page first.";

    /// <summary>
    /// In server mode email is read on the server, so it signs in there. The code comes back to show here, and the
    /// server finishes the sign-in once it's entered. The Settings page says when it's connected.
    /// </summary>
    private async System.Threading.Tasks.Task ConnectServerEmailAsync(ServerConnection server)
    {
        try
        {
            var signIn = await System.Threading.Tasks.Task.Run(server.Settings.ConnectAlertEmail);
            _itemsWindow.ShowList();
            if (signIn.Problem is not null || signIn.UserCode is null
                || !Uri.TryCreate(signIn.VerificationUri, UriKind.Absolute, out var link) || link.Scheme != Uri.UriSchemeHttps)
            {
                _itemsWindow.ShowReply(signIn.Problem ?? "The server couldn't start the alert email sign-in. Try Connect alert email again.");
                return;
            }

            _itemsWindow.ShowReply($"Enter code {signIn.UserCode} on the Microsoft page that just opened, then choose the alerts inbox. The Alert email line on the Settings page says once it's connected.");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
        }
        catch (ServerUnavailableException)
        {
            _itemsWindow.ShowReply("Can't reach the server, so alert email can't sign in yet. Try again once it's back.");
            _itemsWindow.ShowList();
        }
        catch (Exception exception) when (exception is ServerTimedOutException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception)
        {
            _itemsWindow.ShowReply($"Couldn't connect the alert email. {exception.Message}");
            _itemsWindow.ShowList();
        }
    }

    /// <summary>Replies where the capture was typed, so the same reply never shows up in both places.</summary>
    private void ShowOutcome(CaptureOutcome outcome)
    {
        var reply = string.Join("\n", new[] { ItemGroups.OutcomeSummary(outcome), outcome.Answer }.Where(text => !string.IsNullOrWhiteSpace(text)));

        if (outcome.Origin == CaptureOrigin.Discord)
        {
            _worker.ReplyInDiscord(outcome, reply);
            return;
        }

        var made = outcome.CaptureId is { } capture ? _database.GetItemsFromCapture(capture) : [];
        _itemsWindow.ShowReply(reply, made);
        if (_itemsWindow.IsVisible)
        {
            return;
        }

        // The buttons live on the session card, so a new session opens the window.
        if (outcome.Activation is not null)
        {
            _itemsWindow.ShowList();
            return;
        }

        // App prompts don't reply in Discord, so an answer opens the window instead of a toast.
        if (_worker.Discord is null)
        {
            ShowToast("Waypoint", reply);
        }
        else if (!string.IsNullOrWhiteSpace(outcome.Answer) || outcome.Changes.Any(change => change.Applied && change.Change.Action == ChangeAction.BreakDown))
        {
            _itemsWindow.ShowList();
        }
    }

    /// <summary>The worker's fallbacks shown on this PC: a tray toast, a reminder card, and the status refresh.</summary>
    private sealed class WindowHost(App app) : IWorkerHost
    {
        public void ShowNotice(string title, string text) => OnUiThread(() => app.ShowToast(title, text));

        public void ShowReminder(DueReminder reminder, Action closed) => OnUiThread(() => app.ShowReminderCard(reminder, closed));

        public void ShowWeeklyReset(string text) => OnUiThread(() => app._itemsWindow.ShowReply(text));

        public void Changed() => OnUiThread(app.RefreshStatus);

        public void Post(Action action) => app.Dispatcher.BeginInvoke(action);

        private void OnUiThread(Action action)
        {
            if (app.Dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                app.Dispatcher.BeginInvoke(action);
            }
        }
    }

    private void ShowToast(string title, string text) =>
        _tray?.ShowBalloonTip(10000, title, text.Length > MaxBalloonTextLength ? text[..MaxBalloonTextLength] : text, Forms.ToolTipIcon.None);

    /// <summary>Files are only ever read, one at a time in the background. A bank export goes to the import.</summary>
    private void OnFilesDropped(string[] paths)
    {
        // Recordings become notes, so they go to the Notes page. Everything else is read here as before.
        var recordings = paths.Where(Waypoint.Core.Transcription.TranscriptionQueue.IsAudioFile).ToList();
        if (recordings.Count > 0)
        {
            _itemsWindow.ShowNotes();
            _itemsWindow.NotesPage.ImportAudio(recordings);
            paths = paths.Except(recordings).ToArray();
            if (paths.Length == 0)
            {
                return;
            }
        }

        _itemsWindow.ShowList();
        foreach (var path in paths)
        {
            _itemsWindow.ShowPrompt(Path.GetFileName(path.TrimEnd('\\', '/')));
            _ = ReadDroppedAsync(path);
        }
    }

    private async System.Threading.Tasks.Task ReadDroppedAsync(string path)
    {
        var statement = await System.Threading.Tasks.Task.Run(() => WaypointWorker.ReadStatement(path));
        if (statement is not null)
        {
            ImportStatements([statement]);
            return;
        }

        var file = await System.Threading.Tasks.Task.Run(() => ImageText.ReadFile(path));
        if (file.Problem is { } problem)
        {
            _itemsWindow.ShowReply(problem);
            return;
        }

        // Read here, sorted there, the same as typing it.
        if (_server is not null)
        {
            if (file.Text is { } text)
            {
                _server.Capture(text, file.Name);
                _ = SyncWithServerAsync();
            }

            return;
        }

        _captures.CaptureFile(file);
        _ = _captures.ProcessPendingAsync();
    }

    private void OnCaptureSubmitted(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            _itemsWindow.ShowPrompt(text);
        }

        if (_server is not null)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                _server.Capture(text.Trim());
                _ = SyncWithServerAsync();
            }

            RefreshStatus();
            return;
        }

        if (_heldCaptures is { } held)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                held.Add(text.Trim(), DateTimeOffset.Now);
                _itemsWindow.ShowReply("Kept on this PC. It goes to the server once the server address is set in settings.json.");
            }

            RefreshStatus();
            return;
        }

        _captures.Capture(text);
        _ = _captures.ProcessPendingAsync();
    }

    /// <summary>One round with the server: check it, send what's waiting, show replies, and refresh the pages.</summary>
    private async System.Threading.Tasks.Task SyncWithServerAsync()
    {
        if (_server is null)
        {
            return;
        }

        try
        {
            foreach (var reply in await _server.SyncAsync())
            {
                ShowServerReply(reply);
            }

            // Discord settings saved while there was no address go to the server once it's reached.
            // While the server is away they wait for the next round.
            var server = _server;
            if (server.Client.IsOnline && _database.GetPreference(PcMode.HeldDiscordKey) == "1")
            {
                try
                {
                    var token = new ProtectedTokenStore(Path.Combine(GetDataDirectory(), DiscordSetup.TokenFile)).Load();
                    if (await System.Threading.Tasks.Task.Run(() => PcMode.SendHeldDiscord(_database, token, server.Settings.SaveDiscord)) is { } saved)
                    {
                        _itemsWindow.ShowReply($"Discord settings saved on this PC were sent to the server. {saved}");
                    }
                }
                catch (ServerUnavailableException)
                {
                }
            }

            // The server's classes and bedtime are the ones in use, read each round so a change saved from another
            // PC shows here without a restart. While it's away the last ones read are kept.
            var client = _server.Client;
            var (classes, bedtime) = await System.Threading.Tasks.Task.Run(() =>
                (Waypoint.Core.Remote.ServerSettingsFollow.FindClasses(client), Waypoint.Core.Remote.ServerSettingsFollow.FindBedtime(client)));
            if (Waypoint.Core.Remote.ServerSettingsFollow.Apply(classes, bedtime, _schedule, _bedtimeSchedule, _database))
            {
                _itemsWindow.ClassesChanged();
            }

            // Typing in notes while the server was away is saved now. A note changed there meanwhile keeps both.
            var sent = await _server.Notes.SendOfflineAsync();
            if (sent.Count > 0)
            {
                ShowNotesSent(sent);
            }

            // What the server couldn't get to Discord, such as "Time to leave", shows here the way it would locally.
            // A trial run leaves them for the installed copy, since each is shown only on the first PC to claim it.
            if (Trial.StartsAlerts)
            {
                foreach (var notice in await _server.Client.TakeNoticesAsync())
                {
                    ShowToast(notice.Title, notice.Text);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
        {
            LogError(exception);
        }

        RefreshStatus();
    }

    private void ShowNotesSent(IReadOnlyList<Waypoint.Core.Remote.OfflineNoteSent> sent)
    {
        var both = sent.Count(note => note.KeptBoth);
        var suffix = Waypoint.Core.Remote.OfflineNotes.KeptBothSuffix.Trim();
        if (both == 1)
        {
            _itemsWindow.ShowReply($"A note changed on the server while it was away, so both versions are kept. What was typed here is the note ending in {suffix}.");
        }
        else if (both > 1)
        {
            _itemsWindow.ShowReply($"{both} notes changed on the server while it was away, so both versions are kept. What was typed here is in the notes ending in {suffix}.");
        }

        // Turned down for good, such as a note too big for the server. The text is kept in a file on this PC.
        if (sent.FirstOrDefault(note => note.Problem is not null) is { } refused)
        {
            var count = sent.Count(note => note.Problem is not null);
            var what = count == 1 ? "A note typed while the server was away couldn't be saved there" : $"{count} notes typed while the server was away couldn't be saved there";
            _itemsWindow.ShowReply($"{what}. {refused.Problem} Nothing is lost: {(count == 1 ? "it's" : "they're")} kept on this PC.", "Open", () => OpenKept(() => _server?.Notes.WriteUnsavedCopy()));
        }

        _itemsWindow.NotesPage.FollowSent(sent);
        _itemsWindow.NotesPage.Refresh(force: true);
    }

    /// <summary>
    /// Opens a plain text copy of what the server couldn't save, in the PC's text editor, so it can be copied back
    /// in. The kept file stays as it is, so opening it never loses anything.
    /// </summary>
    private void OpenKept(Func<string?> write)
    {
        try
        {
            if (write() is { } copy)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(copy) { UseShellExecute = true });
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _itemsWindow.ShowReply($"Couldn't open what's kept on this PC. {exception.Message}");
            _itemsWindow.ShowList();
        }
    }

    /// <summary>The same as a local reply, with notifications assumed to go out from the server through Discord.</summary>
    private void ShowServerReply(ServerReply reply)
    {
        _itemsWindow.ShowReply(reply.Text, reply.Made);
        if (!_itemsWindow.IsVisible && (reply.StartedSession || reply.OpensWindow))
        {
            _itemsWindow.ShowList();
        }
    }

    private void PairWithServer()
    {
        _itemsWindow.ShowList();
        var form = RenameWindow.ForPairing(_itemsWindow);
        if (form.ShowDialog() != true || string.IsNullOrWhiteSpace(form.NewName))
        {
            return;
        }

        _server!.Pair(form.NewName);
        _itemsWindow.ShowReply("Paired. Checking the server now.");
        _ = SyncWithServerAsync();
    }

    /// <summary>Follows a typed theme change, and leaves the brushes alone otherwise.</summary>
    private void ApplyTheme()
    {
        var palette = _database.GetTheme();

        if (palette != _palette)
        {
            _palette = palette;
            ThemeBrushes.Apply(Resources, palette);

            // The tray menu is Windows Forms, so it can't use the brushes and gets new colors instead.
            if (_tray?.ContextMenuStrip is { } menu)
            {
                menu.Renderer = new Forms.ToolStripProfessionalRenderer(new TrayMenuColors(palette));
            }
        }
    }

    private void RefreshStatus()
    {
        if (_tray is null)
        {
            return;
        }

        if (_server is not null)
        {
            RefreshFromServer(_server);
            return;
        }

        var pending = _database.GetPendingCaptures().Count + (_heldCaptures?.Count ?? 0);
        var status = SortStatus.TrayTip(pending, _captures.LastError is not null) ?? TrayIdle;
        var notice = !_sends ? PcMode.NeedsAddressNotice
            : _worker.DiscordRunsOn is { } elsewhere ? $"Discord is running on {elsewhere}, so this PC sends no reminders or alerts. Switch this PC to server mode to use that copy's data here."
            : null;
        if (!_sends)
        {
            status = PcMode.NeedsAddressTray;
        }
        else if (_worker.DiscordRunsOn is { } host && status == TrayIdle)
        {
            status = $"Waypoint: Discord is running on {host}";
        }

        if (_recorder is { IsRecording: true } recorder)
        {
            status = recorder.TrayText;
        }

        _tray.Text = Trial.TrayTip(status, MaxTrayTextLength);
        ApplyTheme();
        LogOnce("Calendar", _worker.Calendar?.LastError);
        LogOnce("Assignment reminders", _worker.Nudges?.LastError);
        LogOnce("Discord or email", _worker.Discord?.LastError ?? _worker.Outlook?.LastError);
        _itemsWindow.Refresh(
            pending,
            _captures.LastError,
            _worker.Calendar?.LastError,
            _worker.Nudges?.LastError,
            _worker.Discord?.LastError ?? _worker.Outlook?.LastError,
            _schedule?.GetNextClass(DateTime.Now),
            _bedtimeSchedule is { } bedtime ? BedtimeRoutine.GetStatus(_database, bedtime, DateTime.Now) : null,
            notice);
    }

    /// <summary>
    /// The tray and the window in server mode. Tonight's card is decided on the server, from its bedtime settings.
    /// While the server is away the pages keep what they last showed, and the status line says so.
    /// </summary>
    private void RefreshFromServer(ServerConnection server)
    {
        var pending = server.Pending;
        var status = server.Client.LastStatus;
        var problem = server.Client.Problem;
        var text = problem is not null ? $"Waypoint: {problem}"
            : SortStatus.TrayTip(pending, status?.SortingError is not null) ?? TrayIdle;

        _tray!.Text = Trial.TrayTip(text, MaxTrayTextLength);
        ApplyTheme();
        try
        {
            _itemsWindow.Refresh(
                pending,
                status?.SortingError,
                status?.CalendarError,
                status?.NudgeError,
                status?.ConnectionError,
                _schedule?.GetNextClass(DateTime.Now),
                server.Sessions.GetTonight(DateTime.Now),
                string.Join("\n", new[] { problem, server.Queue.Refused }.OfType<string>()) is { Length: > 0 } notice ? notice : null,
                server.Queue.RefusedCount > 0 ? () => OpenKept(server.Queue.WriteRefusedCopy) : null);
        }
        catch (ServerUnavailableException)
        {
            // Nothing was read yet, so there's nothing to show. The status line already says why.
        }
    }
}

