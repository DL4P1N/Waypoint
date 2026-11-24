using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Waypoint.Core;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
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

    private static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromMinutes(1);

    private ThemePalette? _palette;
    private Mutex? _singleInstance;
    private EventWaitHandle _showList = null!;
    private EventWaitHandle? _exitRequest;
    private EventWaitHandle? _closing;
    private WaypointWorker _worker = null!;
    private PostedWork? _posted;
    private ClassSchedule? _schedule;
    private DispatcherTimer? _calendarTimer;
    private DispatcherTimer? _statusTimer;
    private DispatcherTimer? _alertTimer;
    private BedtimeSchedule? _bedtimeSchedule;

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

    /// <summary>Ctrl+Alt+Space opens Ask Waypoint from anywhere, when no other app has those keys.</summary>
    private GlobalHotkey? _askKey;
    private ItemsWindow _itemsWindow = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandledException;

        // Watched from the start, so a card that comes up before startup has finished still gets its shortcut.
        CornerCards.Changed += OnCornerCardsChanged;

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
        var afterRestore = e.Args.Length > 1 && e.Args[0] == AfterRestoreArgument ? e.Args[1] : null;
        if (!isFirstInstance && afterRestore is not null)
        {
            isFirstInstance = WaitForEarlierCopy(_singleInstance);
        }

        _holdsInstance = isFirstInstance;

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

        // Once, before the notes are read: typed headings and lists that were saved as text get their marks back. In
        // server mode the server does it to its own notes. A problem is only logged, so Waypoint still opens.
        if (run == PcRun.Local)
        {
            Waypoint.Core.Notes.TypedMarkRepair.RunOnce(_database, (message, problem) => LogText(problem is null ? message : $"{message}{Environment.NewLine}{problem}"));
        }

        // Whisper, set up once for lectures, also turns voice notes sent in Discord's capture channel into text.
        var whisper = new Waypoint.Core.Transcription.WhisperSetup(dataDirectory, new Waypoint.Core.Transcription.HttpFileDownloader(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }));
        var audio = new MediaFoundationAudio();
        var voice = new Waypoint.Core.Transcription.WhisperVoiceTranscriber(_database, whisper, audio, new Waypoint.Core.Transcription.ChildProcessRunner(), dataDirectory);

        // The reminders, alerts, Discord, calendar, and email live in Core, so a server can run the same logic.
        _posted = new PostedWork(Dispatcher, LogError);
        _worker = new WaypointWorker(
            _database,
            settings,
            dataDirectory,
            new WindowHost(this, _posted),
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

        _itemsWindow = new ItemsWindow(_database, OnCaptureSubmitted, ImportCsv, OnFilesDropped, _server?.Data, _server?.Budget, _server?.Sessions, _server?.Lists, _server?.History, notes, _server?.Feedback, _server?.Client)
        {
            Classes = _schedule,
            CardUpFor = item => _cardsUp.Contains(ItemRef.Format(item)),
        };
        // Notes whose typed headings and lists were saved as text are repaired when the database opens. Ask says so once.
        _later.Add(SayTypedMarksRepair);
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

        // In server mode what a change touched is read again off the window's thread before the status is shown.
        _itemsWindow.DataChanged += () =>
        {
            if (_server is null)
            {
                RefreshStatus();
            }
            else
            {
                _ = RefreshStatusAheadAsync();
            }
        };
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
        // Off the window thread, since the pinned views and the forum posts it brings up to date read the database.
        _discordTimer.Tick += (_, _) => _ = System.Threading.Tasks.Task.Run(() => _worker.PollDiscordAsync());
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
        _statusTimer.Tick += async (_, _) => await RefreshStatusAheadAsync();
        _statusTimer.Start();

        if (_server is null)
        {
            if (Trial.StartsAlerts && _sends)
            {
                _alertTimer = new DispatcherTimer { Interval = WaypointWorker.AlertCheckInterval };
                _alertTimer.Tick += (_, _) =>
                {
                    // Separately, so a problem in one can't stop the alerts after it. The checks and the daily backup
                    // run off the window thread, and the cards they show come back to it. The recording offer opens a
                    // window, so it stays here.
                    _ = _worker.StartAlertRound();
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
        RegisterAskKey();
        RefreshStatus();
        if (!_sends)
        {
            ShowToast("Add the server address", ServerAddress.Problem(settings.Server?.Address) is { } refused && !string.IsNullOrWhiteSpace(settings.Server?.Address) ? refused : PcMode.NeedsAddressNotice);
        }

        var showListThread = new Thread(() =>
        {
            while (_showList.WaitOne())
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (!_restoring)
                    {
                        _itemsWindow.ShowLastPage();
                    }
                });
            }
        })
        { IsBackground = true };
        showListThread.Start();

        // install.ps1 sets this to close the app the usual way, saving drafts and finishing a recording, before it
        // switches versions. Only the copy that holds this name's single-instance lock listens for it.
        _exitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, $"{instance}.Exit");

        // Set once closing starts, and left set until this copy has gone, so install.ps1 knows a slow close is still
        // saving and waits for it instead of stopping it. Cleared here in case a copy before this one left it set.
        _closing = new EventWaitHandle(false, EventResetMode.ManualReset, $"{instance}.Closing");
        _closing.Reset();
        ThreadPool.RegisterWaitForSingleObject(
            _exitRequest, (_, _) => Dispatcher.BeginInvoke(RequestExit), null, Timeout.Infinite, executeOnlyOnce: true);

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

        // Started again after a restore, it says how that went.
        if (afterRestore is not null)
        {
            Dispatcher.BeginInvoke(() => NoteConfirmWindow.Tell(null, RestoreTitle, afterRestore), DispatcherPriority.ApplicationIdle);
        }
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

        // Anything typed here that the server turned down, or that was found damaged, is told with a way to open it.
        var refused = server.Queue.Refused;
        var unreadable = server.Notes.Unreadable;
        var setAside = unreadable is not null || server.Queue.SetAsideFiles.Count > 0;

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
                string.Join("\n", new[] { problem, refused, unreadable }.OfType<string>()) is { Length: > 0 } notice ? notice : null,
                refused is null && unreadable is null ? null : () =>
                {
                    var opened = refused is null || OpenKept(server.Queue.WriteRefusedCopy);
                    if (unreadable is not null)
                    {
                        opened &= OpenKept(server.Notes.WriteUnsavedCopy);
                    }

                    if (opened && setAside)
                    {
                        SetAsideSeen(server);
                    }
                },
                setAside ? () => SetAsideSeen(server) : null);
        }
        catch (ServerUnavailableException)
        {
            // Nothing was read yet, so there's nothing to show. The status line already says why.
        }
    }
}
