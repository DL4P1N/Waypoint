using System.Globalization;
using Waypoint.Core.Budget;
using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Email;
using Waypoint.Core.Files;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Background;

/// <summary>
/// What the worker is built from. Tests swap the clock, the web client, and the sorter. TextRecognizer reads
/// pictures and scanned pages in files sent from Discord, and ReadFile, when given, replaces the reading altogether.
/// Runner, when given, turns on the check that only one copy uses the bot at a time (see <see cref="DiscordClaim"/>),
/// with HostName as this machine's name in it. Voice turns voice notes sent from the phone into text; without one,
/// as on the server, they're kept unread.
/// </summary>
public sealed record WorkerParts(
    Func<string, ITokenStore> TokenStore,
    Func<DateTime>? Clock = null,
    Func<HttpClient>? Http = null,
    ICaptureSorter? CaptureSorter = null,
    INudgePlanner? NudgePlanner = null,
    Func<string, FileText>? ReadFile = null,
    bool ConnectGateway = true,
    ITextRecognizer? TextRecognizer = null,
    DiscordRunner? Runner = null,
    string? HostName = null,
    IUserIdle? Idle = null,
    Transcription.IVoiceTranscriber? Voice = null);

/// <summary>
/// Reminders, class alerts, the wind-down, briefings, Discord, the calendar feed, alert email, and backups.
/// It knows nothing about windows or timers, so the PC app and the server run the same logic: each calls the
/// Check and Poll methods on its own schedule and shows what <see cref="IWorkerHost"/> is asked to.
/// </summary>
public sealed partial class WaypointWorker
{
    public const int DefaultSnoozeMinutes = 15;

    public static readonly TimeSpan CalendarSyncInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan AlertCheckInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DiscordPollInterval = TimeSpan.FromSeconds(30);

    // While the live connection carries messages, polling is only a catch-up, so it can be rare.
    public static readonly TimeSpan DiscordLivePollInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan EmailPollInterval = TimeSpan.FromMinutes(5);

    private const string LastBriefingKey = "last_briefing";
    private const int ReviewLinesInReset = 2;
    private const string LastWeeklyResetKey = "last_weekly_reset";
    private const string UnreadAlertsFile = "unread-alerts.txt";

    private static readonly TimeOnly DefaultBriefingTime = new(7, 30);
    private static readonly TimeOnly BriefingCutoff = new(14, 0);
    private static readonly TimeOnly DefaultWeeklyResetTime = new(18, 0);
    private static readonly TimeOnly WeeklyResetCutoff = new(12, 0);

    private readonly WaypointDatabase _database;
    private readonly WaypointSettings _settings;
    private readonly string _dataDirectory;
    private readonly IWorkerHost _host;
    private readonly Action<Exception> _logError;
    private readonly Func<DateTime> _clock;
    private readonly ClassAlertTracker? _classAlerts;
    private readonly BedtimeReminders _bedtime;
    private readonly LateCheckIns _lateCheckIns;
    private readonly TimeOnly _briefingTime;
    private readonly TimeOnly _weeklyResetTime;
    private readonly Func<HttpClient> _http;
    private readonly INudgePlanner _nudgePlanner;

    // Attachments are downloaded with a client that doesn't follow redirects, so each hop is checked first.
    private readonly Func<HttpClient?> _downloads;
    private readonly Func<string, FileText> _readFile;
    private readonly DiscordSettings? _fileDiscord;
    private readonly ITokenStore _discordToken;
    private readonly OutlookSettings? _fileOutlook;
    private readonly ITokenStore _outlookToken;
    private readonly Func<HttpClient> _outlookHttp;
    private readonly bool _connectGateway;
    private DiscordGateway? _gateway;
    private CancellationTokenSource? _discordStop;
    private Task _gatewayRun = Task.CompletedTask;

    // Only one copy uses the bot at a time. The host asked for Discord, and the claim says whether it may connect.
    private readonly DiscordRunner? _runner;
    private readonly string _hostName;
    private DiscordClaim? _claim;
    private volatile bool _discordWanted;
    private volatile bool _claimKnown;

    // Discord captures waiting for their answer, so it can reply under the message they came from.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, DiscordReplyTo> _discordReplies = new();

    // Shared with Discord's button presses and with sends finishing in the background, so both sets are locked.
    private readonly HashSet<(ItemSource Source, long Id)> _activeReminders = [];
    private readonly HashSet<ClassOccurrence> _leaveAlertsSent = [];
    private DateOnly? _backupCheckedOn;

    public WaypointWorker(WaypointDatabase database, WaypointSettings settings, string dataDirectory, IWorkerHost host, Action<Exception> logError, WorkerParts parts)
    {
        _database = database;
        _settings = settings;
        _dataDirectory = dataDirectory;
        _host = host;
        _logError = logError;
        _clock = parts.Clock ?? (() => DateTime.Now);
        var http = parts.Http ?? (() => new HttpClient());
        _http = http;
        _downloads = parts.Http is null ? () => new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) } : () => null;
        _readFile = parts.ReadFile ?? (path => TextRecognition.ReadFile(path, parts.TextRecognizer));
        _connectGateway = parts.ConnectGateway;
        _runner = parts.Runner;
        _hostName = parts.HostName ?? Environment.MachineName;
        _voice = parts.Voice;

        // Classes saved on the Settings page sit on top of settings.json's, and saving there changes this one in place.
        FileSchedule = settings.Schedule;
        Schedule = ClassSetup.Combine(FileSchedule, database);
        _briefingTime = settings.BriefingTime ?? DefaultBriefingTime;
        _weeklyResetTime = settings.WeeklyResetTime ?? DefaultWeeklyResetTime;
        database.DefaultCommuteMinutes = settings.WorkCommuteMinutes ?? WorkShift.DefaultCommuteMinutes;
        database.ScheduleCourses = Schedule.Meetings.Select(meeting => meeting.Course).ToList();

        // Always on, since typed work hours can set a morning with no class schedule at all.
        // The Settings page's bedtime choices live in preferences and sit on top of settings.json, which is never rewritten.
        FileBedtime = settings.Bedtime ?? new BedtimeSettings();
        database.UpgradeWindDownSteps(FileBedtime);
        BedtimeSchedule = new BedtimeSchedule(database.GetBedtimeChoices().ApplyTo(FileBedtime), Schedule, database.GetFirstShift);

        var sorter = new ClaudeCliSorter(dataDirectory);
        Captures = new CaptureService(database, parts.CaptureSorter ?? sorter, Schedule, BedtimeSchedule, _clock);
        Views = new PinnedViews(database);
        Captures.Changed += () => _viewsDirty = true;
        Captures.Sorted += OnSortedForCard;
        _bedtime = new BedtimeReminders(BedtimeSchedule, database);
        UserIdle = parts.Idle;
        _lateCheckIns = new LateCheckIns(BedtimeSchedule, database, parts.Idle);

        // The Settings page's link is used over settings.json's, and saving one there starts it without a restart.
        _nudgePlanner = parts.NudgePlanner ?? sorter;
        StartCalendar();

        // Discord comes from settings.json and the Settings page together. It connects when the host calls StartDiscord.
        _fileDiscord = settings.Discord;
        _discordToken = parts.TokenStore(Path.Combine(dataDirectory, DiscordSetup.TokenFile));
        DiscordSettings = DiscordSetup.Combine(_fileDiscord, _discordToken.Load(), database);

        // Alert email comes from settings.json and the Settings page together, so it can be set up later too.
        _fileOutlook = settings.Outlook;
        _outlookToken = parts.TokenStore(Path.Combine(dataDirectory, "outlook.token"));
        _outlookHttp = http;
        if (OutlookSetup.Combine(_fileOutlook, database) is { } outlook)
        {
            Outlook = new OutlookMail(http(), database, outlook, _outlookToken);

            // "Check my email" in Ask reads it now instead of waiting for the timer. A host with a UI thread may replace this.
            Captures.CheckEmail = _ => CheckEmailNowAsync();
        }

        _classAlerts = new ClassAlertTracker(Schedule);
    }

    /// <summary>The classes in use, changed in place when the Settings page saves them, so nothing needs a restart.</summary>
    public ClassSchedule Schedule { get; }

    /// <summary>The class schedule in settings.json, which the Settings page's classes sit on top of.</summary>
    public ClassSchedule? FileSchedule { get; }

    public ClassesForm GetClasses() => ClassSetup.Form(Schedule, _database);

    /// <summary>Saves the Settings page's classes and starts using them straight away, or says why they weren't saved.</summary>
    public string SaveClasses(ClassesForm form)
    {
        if (ClassSetup.Save(_database, form) is { } problem)
        {
            return problem;
        }

        Schedule.Use(ClassSetup.Combine(FileSchedule, _database));
        _database.ScheduleCourses = Schedule.Meetings.Select(meeting => meeting.Course).ToList();
        _host.Changed();
        return ClassesSaved(Schedule.Meetings.Length);
    }

    /// <summary>The reply once classes are saved, the same on this PC and from the server.</summary>
    public static string ClassesSaved(int count) => count switch
    {
        0 => "Saved. There are no classes now.",
        1 => "Saved. Waypoint uses this class straight away.",
        _ => $"Saved. Waypoint uses these {count} classes straight away.",
    };

    public BedtimeSettings FileBedtime { get; }

    public BedtimeSchedule BedtimeSchedule { get; }

    public CaptureService Captures { get; }

    /// <summary>Null while no feed is set up. Replaced when a link is saved on the Settings page.</summary>
    public CalendarFeedSync? Calendar { get; private set; }

    public AssignmentNudges? Nudges { get; private set; }

    /// <summary>Set once Discord has been started, and cleared while it reconnects with new settings.</summary>
    public DiscordBridge? Discord { get; private set; }

    /// <summary>What Discord connects with, or null when it isn't set up.</summary>
    public DiscordSettings? DiscordSettings { get; private set; }

    /// <summary>Alert email, or null until it has an app ID. Saving one on the Settings page sets it up straight away.</summary>
    public OutlookMail? Outlook { get; private set; }

    /// <summary>What alert email is set up with, for the Settings page. Neither value is a secret.</summary>
    public OutlookSettings? OutlookSettings => OutlookSetup.Combine(_fileOutlook, _database);

    /// <summary>
    /// Saves alert email's app ID and forwarding address from the Settings page and uses them straight away, so
    /// reading email starts without a restart once it's signed in. A blank one falls back to settings.json.
    /// </summary>
    public string SaveAlertEmail(string? clientId, string? ownAddress)
    {
        if (OutlookSetup.Save(_database, clientId, ownAddress) is { } problem)
        {
            return problem;
        }

        if (OutlookSetup.Combine(_fileOutlook, _database) is not { } outlook)
        {
            Outlook = null;
            return "Saved. Alert email needs an app ID before it can sign in.";
        }

        Outlook = new OutlookMail(_outlookHttp(), _database, outlook, _outlookToken);
        Captures.CheckEmail ??= _ => CheckEmailNowAsync();
        return Outlook.IsConnected
            ? "Saved. Purchase alerts are read every few minutes."
            : "Saved. Choose Connect alert email in the tray menu to sign in.";
    }

    /// <summary>
    /// Runs every check. Each is separate, so a problem in one can't stop the alerts after it.
    /// </summary>
    public void CheckAlerts()
    {
        // Another copy sends the alerts, or Discord is still connecting here, so this one only keeps its own backups.
        // Anything due stays due and goes to Discord on a later round.
        if (DiscordRunsOn is not null || WaitingForDiscord)
        {
            Guarded(CheckBackup);
            return;
        }

        Guarded(CheckClassAlert);
        Guarded(() => _database.MoveOnPastDoses(_clock()));
        Guarded(CheckReminders);
        Guarded(CheckBedtime);
        Guarded(CheckLateNight);
        Guarded(CheckWaitList);
        Guarded(CheckBriefing);
        Guarded(CheckPayCalendar);
        Guarded(CheckWeeklyReset);
        Guarded(CheckSessionPrompt);
        Guarded(CheckUnanswered);
        Guarded(CheckBackup);
    }

    /// <summary>
    /// The live connection carries messages, button presses, and slash commands. Polling every 30 seconds is
    /// only for when it's down or can't carry messages, plus a catch-up whenever it comes back.
    /// Does nothing when Discord isn't set up. With the one-copy check on, it connects only once that check says
    /// no other copy is using the bot.
    /// </summary>
    public void StartDiscord()
    {
        _discordWanted = true;
        _discordWantedAt ??= _clock();
        if (HoldsConnections)
        {
            return;
        }

        if (_runner is not null && (!_claimKnown || DiscordRunsOn is not null))
        {
            _ = CheckDiscordClaimAsync();
            return;
        }

        ConnectDiscord();
    }

    /// <summary>
    /// Set by a PC in server mode that has no address yet: Discord never connects here, and Discord settings saved on
    /// the Settings page are kept for the server instead.
    /// </summary>
    public bool HoldsConnections { get; init; }

    /// <summary>The machine that uses the bot instead of this one, or null when this one does or may.</summary>
    public string? DiscordRunsOn { get; private set; }

    /// <summary>How long alerts wait for Discord to connect before they show on this PC instead.</summary>
    public static readonly TimeSpan DiscordConnectGrace = TimeSpan.FromMinutes(2);

    private DateTime? _discordWantedAt;

    /// <summary>
    /// Discord is set up here but hasn't connected yet, as in the first moments after starting. Alerts wait for it
    /// rather than showing on this PC, unless it takes much longer than it should.
    /// </summary>
    private bool WaitingForDiscord =>
        _discordWanted && !HoldsConnections && DiscordSettings is not null && Discord is null && DiscordRunsOn is null
        && _discordWantedAt is { } since && _clock() - since < DiscordConnectGrace;

    /// <summary>Why the one-copy check couldn't be made, until it can.</summary>
    public string? ClaimProblem { get; private set; }

    /// <summary>Whether Discord is working, for the server's heartbeat.</summary>
    public DiscordHealth DiscordHealth { get; } = new();

    /// <summary>How long Discord can fail before the server stops saying it's healthy.</summary>
    public static readonly TimeSpan DiscordTroubleAfter = TimeSpan.FromMinutes(10);

    /// <summary>What's wrong with Discord, once it has failed for longer than <see cref="DiscordTroubleAfter"/>, or null.</summary>
    public string? DiscordTrouble(DateTime now) =>
        DiscordSettings is null || !_discordWanted || DiscordRunsOn is not null ? null : DiscordHealth.Problem(now, DiscordTroubleAfter);

    /// <summary>
    /// Reads and updates the pinned note that says which copy uses the bot, then connects or steps aside to match.
    /// The host runs it every <see cref="DiscordClaim.CheckEvery"/>. While another copy has it, this one sends no
    /// alerts and reads nothing from Discord. If Discord can't be asked the first time, this copy connects anyway,
    /// so a missing permission never silences everything, and says why in Settings.
    /// </summary>
    public async Task CheckDiscordClaimAsync(CancellationToken cancellationToken = default)
    {
        if (HoldsConnections)
        {
            return;
        }

        if (_runner is not { } runner || DiscordSettings is not { } settings)
        {
            DiscordRunsOn = null;
            return;
        }

        var claim = _claim ??= new DiscordClaim(ClaimClient(), settings, _hostName, runner, () => new DateTimeOffset(_clock()));
        var check = await claim.CheckAsync(cancellationToken);
        if (!ReferenceEquals(claim, _claim))
        {
            // Discord was set up again meanwhile, and that starts its own check.
            return;
        }

        ClaimProblem = check.Problem;
        switch (check.State)
        {
            case ClaimState.Elsewhere:
                _claimKnown = true;
                DiscordRunsOn = check.RunningOn;
                if (Discord is not null)
                {
                    Disconnect();
                }

                _host.Changed();
                return;
            case ClaimState.Here:
                _claimKnown = true;
                DiscordRunsOn = null;
                break;
            case ClaimState.Unknown when _claimKnown:
                return;
        }

        if (_discordWanted && Discord is null)
        {
            ConnectDiscord();
            _host.Changed();
        }
    }

    private HttpClient ClaimClient()
    {
        var client = _http();
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    private void ConnectDiscord()
    {
        if (DiscordSettings is not { } settings)
        {
            return;
        }

        var client = _http();
        client.Timeout = TimeSpan.FromSeconds(30);
        var bridge = new DiscordBridge(client, _database, settings)
        {
            Clock = _clock,
            StillWorking = IsDiscordWorking,
            Downloads = _downloads(),
            Captured = message => _host.Post(() => OnDiscordMessage(message)),
            AlertActed = action =>
            {
                var reply = _database.ApplyAlertAction(action, _clock(), DefaultSnoozeMinutes);
                _host.Changed();
                return reply;
            },
            Pressed = OnButtonPressed,
            Acted = OnButtonActedAsync,
            Command = OnDiscordCommand,
            Suggest = OnDiscordSuggest,
            Picked = OnViewPicked,
            PressWillReply = HowPressWillReply,
            SpotSaved = (project, stopped, next) =>
            {
                var saved = SaveSpotFromDiscord(project, stopped, next);
                _host.Changed();
                ViewsChanged();
                return saved;
            },
            SpotProject = () => _database.GetActiveSession(_clock())?.TaskTitle,
            OpensSpotForm = OpensSpotFormForLateNight,
            FeedbackGiven = (feature, itemRef) =>
            {
                // A reminder that didn't help was answered, so it isn't held for after this.
                if (feature == Feedback.Reminder)
                {
                    _database.DropAfterThis(itemRef);
                }

                return _database.AddFeedback(feature, itemRef, null, _clock());
            },
            FeedbackNoted = (id, note) => _database.NoteFeedback(id, note),
            Submitted = OnFormSent,
            NotesSent = message => _host.Post(() => OnNotesMessage(message)),
            ThreadStarted = OnThreadStartedAsync,
            ThreadMessage = OnThreadMessageAsync,
            ThreadGone = OnThreadGone,
        };
        bridge.Connected += () =>
        {
            // Posts typed in while the connection was down are read again on the next round.
            _caughtUpAt = null;
            _host.Post(() => _ = PollDiscordAsync(force: true));
        };

        var stop = new CancellationTokenSource();
        var gateway = new DiscordGateway(settings.BotToken, bridge.HandleEventAsync, _logError);
        Discord = bridge;
        _gateway = gateway;
        _discordStop = stop;
        if (_connectGateway)
        {
            _gatewayRun = Task.Run(() => gateway.RunAsync(stop.Token));
        }

        _ = PollDiscordAsync(force: true);

        // Voice notes kept from before, such as while speech to text wasn't set up, are tried again now that replies can go out.
        _ = ReadHeldVoiceNotesAsync();
    }

    public void StopDiscord()
    {
        _discordWanted = false;
        _discordWantedAt = null;
        Disconnect();
    }

    /// <summary>
    /// Stops Discord and waits, up to <paramref name="limit"/>, for events already read to be finished, so a capture
    /// or press that arrived just before closing is still saved. Anything left is logged by the gateway.
    /// </summary>
    public async Task StopDiscordAsync(TimeSpan limit)
    {
        var run = _gatewayRun;
        StopDiscord();
        try
        {
            await run.WaitAsync(limit).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logError(exception);
        }
    }

    private void Disconnect()
    {
        _discordStop?.Cancel();
        StopVoiceNotes();
        Discord = null;
        _gateway = null;
        _discordStop = null;
        DiscordHealth.Reset();
    }

    /// <summary>Saves Discord values from the Settings page and reconnects with them straight away. A blank token keeps the one there is.</summary>
    public string SaveDiscord(string? ownerUserId, string? captureChannelId, string? alertsChannelId, string? budgetChannelId, string? token, string? notesForumId = null, string? listsForumId = null)
    {
        if (DiscordSetup.SaveIds(_database, ownerUserId, captureChannelId, alertsChannelId, budgetChannelId, notesForumId, listsForumId) is { } problem)
        {
            return problem;
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            _discordToken.Save(token.Trim());
        }

        if (HoldsConnections)
        {
            DiscordSettings = DiscordSetup.Combine(_fileDiscord, _discordToken.Load(), _database);
            _database.SetPreference(Remote.PcMode.HeldDiscordKey, "1");
            return Remote.PcMode.HeldDiscordReply;
        }

        StopDiscord();
        _claim = null;
        _claimKnown = false;
        DiscordRunsOn = null;
        DiscordSettings = DiscordSetup.Combine(_fileDiscord, _discordToken.Load(), _database);
        if (DiscordSettings is null)
        {
            return "Saved. Discord needs both a bot token and your user ID before it can connect.";
        }

        StartDiscord();
        return "Saved. Connecting to Discord now.";
    }

    public async Task<string> TestDiscordAsync() =>
        Discord is not { } discord
            ? "Discord isn't set up yet."
            : await discord.SendTestAsync() is { } problem ? $"The test didn't send: {problem}" : "Sent. Check Discord.";

    /// <summary>Does nothing while no feed is set up, so the hourly timer can run either way.</summary>
    public Task SyncCalendarAsync(CancellationToken cancellationToken = default) =>
        Calendar?.SyncAsync(cancellationToken) ?? Task.CompletedTask;

    /// <summary>What a connection's status says before the error's own text, which the phone leaves out.</summary>
    private const string LastTryFailed = " The last try didn't work:";

    /// <summary>Status only, for the Settings page. Tokens and feed links are never shown.</summary>
    public IReadOnlyList<ConnectionStatus> DescribeConnections()
    {
        static string Problem(string? error) => error is null ? "" : $"{LastTryFailed} {error}";

        var discord = DescribeDiscord(Problem);
        var calendar = Calendar is null
            ? "Not set up. Paste its link below."
            : $"Set up. It updates every hour.{Problem(Calendar.LastError)}";
        var email = Outlook is not { } outlook
            ? "Not set up. Add its app ID below."
            : outlook.IsConnected
                ? $"Connected. Purchase alerts are read every few minutes.{Problem(outlook.LastError)}{(outlook.SkippedSender is { } skipped ? $" {skipped}" : "")}"
                : "Set up, but not signed in yet. Use Connect alert email in the tray menu.";

        return
        [
            new ConnectionStatus("Discord", discord),
            new ConnectionStatus("Calendar feed", calendar),
            new ConnectionStatus("Alert email", email),
        ];
    }

    private string DescribeDiscord(Func<string?, string> problem)
    {
        if (DiscordRunsOn is { } other)
        {
            return $"Running on {other}, so this copy sends no alerts and reads nothing from Discord. If that one stops for a few minutes, this one takes over.";
        }

        if (Discord is not { } bridge)
        {
            return "Not set up. Fill it in below.";
        }

        var problems = problem(_gateway?.LastError ?? bridge.LastError) + (bridge.PinProblem is { } pin ? $" {pin}" : "")
            + (bridge.ViewProblem is { } view ? $" {view}" : "") + (bridge.ThreadProblem is { } thread ? $" {thread}" : "")
            + (ForumNotice is { } forum ? $" {forum}" : "") + (ClaimProblem is { } claim ? $" {claim}" : "");
        return _gateway switch
        {
            { IsConnected: true, HasMessages: true } => $"Connected. Alerts go to Discord, and messages sent there arrive right away.{problems}",
            { IsConnected: true } => "Connected, but Message Content Intent is off for the bot in the Discord developer portal, so captures can't be read. Buttons and commands still work.",
            _ => $"Not connected right now. Alerts still try Discord first, then show on this PC.{problems}",
        };
    }

    private void Guarded(Action check)
    {
        try
        {
            check();
        }
        catch (Exception exception)
        {
            _logError(exception);
        }
    }

    private void CheckClassAlert()
    {
        if (_classAlerts?.Check(_clock()) is not { } alert)
        {
            return;
        }

        if (alert.Stage == ClassAlertStage.Warning)
        {
            var meeting = alert.Class.Meeting;
            var leaveAt = alert.LeaveAt.ToString("h:mm tt", CultureInfo.CurrentCulture);
            _ = NotifyAsync($"{meeting.Course} coming up", $"Leave by: {leaveAt}\nRoom: {meeting.Room}", CardTone.Attention);
            return;
        }

        // One message per class, so acknowledging here stops the tracker's repeats.
        _classAlerts.Acknowledge(alert.Class);
        bool first;
        lock (_leaveAlertsSent)
        {
            first = _leaveAlertsSent.Add(alert.Class);
        }

        if (first)
        {
            _ = NotifyAsync("Time to leave", $"Class: {alert.Class.Meeting.Course}\nRoom: {alert.Class.Meeting.Room}", CardTone.Urgent);
        }
    }

    private void CheckReminders()
    {
        // Assignment nudges wait out the quiet part of the night. Anything asked for, and exams, still come through.
        var now = _clock();
        var quiet = BedtimeSchedule.IsQuiet(now);
        DueReminder? reminder;
        lock (_activeReminders)
        {
            var dueNow = _database.GetDueReminders(now);
            DropStaleReminderSends(dueNow);
            reminder = dueNow.FirstOrDefault(due => (!quiet || due.Item.Source != ItemSource.Calendar) && !_activeReminders.Contains(ActiveKey(due)));
            if (reminder is null)
            {
                return;
            }

            // Tracked one by one, so a slow send or an unanswered card never holds up the next reminder.
            _activeReminders.Add(ActiveKey(reminder));
        }

        if (Discord is { } discord)
        {
            _ = SendReminderAsync(discord, reminder);
        }
        else
        {
            ShowReminder(reminder);
        }
    }

    private static (ItemSource, long) ActiveKey(DueReminder reminder) => (reminder.Item.Source, reminder.Id);

    private void Release(DueReminder reminder)
    {
        lock (_activeReminders)
        {
            _activeReminders.Remove(ActiveKey(reminder));
        }
    }

    private void ShowReminder(DueReminder reminder) => _host.ShowReminder(reminder, () => Release(reminder));

    /// <summary>Reminders go to Discord alone. The host's card is only for when Discord still can't be reached after retries.</summary>
    private async Task SendReminderAsync(DiscordBridge discord, DueReminder reminder)
    {
        // Only things that can be finished get the prompt. A nudge about an assignment or an exam alert doesn't finish it.
        // A dose is answered with Took it, so its card has buttons even though it repeats.
        var dose = reminder.Item is { Dose: true, Recurs: not null };
        var canFinish = dose || (reminder.Item.Recurs is null && (reminder.RepeatMinutes is not null || reminder.Item.Kind is ItemKind.Reminder or ItemKind.Task));
        var canStart = Activation.CanStart(reminder.Item);
        var waiting = _database.WaitingFor(reminder);
        var card = waiting is not null
            ? WaitingNudgeCard(reminder, waiting, DateOnly.FromDateTime(_clock()))
            : DiscordText.Reminder(reminder, DateOnly.FromDateTime(_clock()), canFinish, canStart);

        // The same offer the PC card makes: only on a reminder that can be started, and not right after a spot was saved.
        card = card with { SaveSpot = card.Transition && TransitionAssist.OffersSaveSpot(_database.GetLastSpotSaved(), _clock()), FeedbackFeature = Feedback.Reminder, FeedbackRef = ItemRef.Format(reminder.Item) };

        // The card is noted before it goes, so a crash before it's saved finds the card it sent instead of sending
        // another. The note is kept with Discord's nonce, so a try that went through unheard isn't made twice.
        var key = ReminderSendKey(reminder);
        var channel = discord.AlertsChannelFor();
        var send = _database.BeginDiscordSend(key, channel, _clock());
        var messageId = send.MessageId;
        if (messageId is null && !send.Fresh && channel is not null)
        {
            messageId = (await discord.FindSentAsync(channel, send.StartedAt, send.Nonce, card)).MessageId;
        }

        var sent = messageId is not null;
        if (!sent)
        {
            sent = await AlertRetry.RunAsync(async () =>
            {
                var delivery = await discord.DeliverOnceAsync(channel, card, send.Nonce);
                messageId = delivery.MessageId;
                return delivery.Sent;
            }, RetryWait);
            DiscordHealth.Sent(sent, _clock());
        }

        if (!sent)
        {
            // Shown here instead, and answered here, so a later card for the same reminder starts afresh.
            _database.FinishDiscordSend(key);
            ShowReminder(reminder);
            return;
        }

        try
        {
            if (messageId is not null)
            {
                _database.SetDiscordSendMessage(key, messageId);
            }

            // The card's record, the reminder moving on, and the note going are one write, so a crash leaves the note
            // with the card in it and the next try saves that card instead of sending another.
            _database.InOneWrite(() =>
            {
                // A waiting nudge's buttons carry its item, so there's nothing to look up later.
                if (waiting is null && (canFinish || canStart) && messageId is not null)
                {
                    _database.TrackSentReminder(messageId, reminder.Item.Id, reminder.Item.Source, startOnly: !canFinish, sentAt: _clock(), dueOn: dose ? reminder.Item.DueDate : null);
                }

                _database.SettleSentReminder(reminder, _clock());
                _database.FinishDiscordSend(key);
                return 0;
            });
        }
        finally
        {
            Release(reminder);
        }

        _host.Changed();
    }

    /// <summary>
    /// One reminder's card, the same each time it's tried, until the reminder moves on. What it's for and when it's
    /// due are in it too, so a later reminder given the row id of one that's gone never takes that one's card.
    /// </summary>
    private static string ReminderSendKey(DueReminder reminder) =>
        $"{ReminderSendPrefix}{(reminder.Item.Source == ItemSource.Calendar ? "event" : "item")}:{reminder.Id}:{reminder.Item.Id}:{reminder.RemindAt}";

    private const string ReminderSendPrefix = "reminder:";

    /// <summary>
    /// A card's note whose reminder is no longer due (finished, put off, or gone elsewhere) is let go, so it can't
    /// stay forever. A reminder still on its way is still due, so its note is kept.
    /// </summary>
    private void DropStaleReminderSends(IReadOnlyList<DueReminder> due)
    {
        var keys = due.Select(ReminderSendKey).ToHashSet(StringComparer.Ordinal);
        foreach (var send in _database.GetDiscordSends(ReminderSendPrefix))
        {
            if (!keys.Contains(send.Key))
            {
                _database.FinishDiscordSend(send.Key);
            }
        }
    }

    /// <summary>The pause between Discord retries. Tests set it to skip the waiting.</summary>
    public Func<TimeSpan, Task>? RetryWait { get; set; }

    private void CheckBedtime()
    {
        var now = _clock();
        if (!BedtimeSchedule.IsQuiet(now))
        {
            foreach (var notice in TakeHeldToSend())
            {
                _ = SendHeldNoticeAsync(notice);
            }
        }

        if (_bedtime.Check(now) is not { } reminder)
        {
            return;
        }

        // A spot saved during tonight's wind-down means the save-your-spot stage has nothing left to ask.
        var spotSaved = BedtimeRoutine.GetStatus(_database, BedtimeSchedule, now)?.Steps.Any(step => step.Label == BedtimeRoutine.SaveProject && step.Done) == true;
        if (BedtimeRoutine.Skips(reminder, spotSaved))
        {
            _host.Changed();
            return;
        }

        var (headline, details) = BedtimeRoutine.Describe(reminder, spotSaved, _database.GetActiveSession(now)?.TaskTitle, BedtimeSchedule.Settings);
        var button = reminder.Task switch
        {
            BedtimeTask.EveningStep => AlertActionKind.EveningStepDone,
            BedtimeTask.WindDownStep => AlertActionKind.WindDownStepDone,
            BedtimeTask.Bed => AlertActionKind.SnoozeBedtime,
            _ => (AlertActionKind?)null,
        };
        // The heads-up is a natural point to bring back something put off earlier, on the card already going out.
        var held = reminder.Task == BedtimeTask.HeadsUp ? TakeAfterThisLine() : default;
        var stepName = BedtimeRoutine.StepName(reminder.Task, BedtimeSchedule.Settings);
        _ = NotifyAsync(headline, held.Line is { } line ? $"{details}\n{line}" : details, bedtimeButton: button, actions: held.Actions, saveSpot: BedtimeRoutine.OffersSaveSpot(reminder, spotSaved), bedtimeLabel: stepName is null ? null : DoneLabel(stepName));
        _host.Changed();
    }

    /// <summary>Held notices going out now, so a slow send isn't started again on the next round.</summary>
    private readonly HashSet<long> _heldSending = [];

    /// <summary>
    /// The held notices not already going out. They're read inside the guard, so one that has just gone out and
    /// been let go is never read before its send is over and started again.
    /// </summary>
    private List<HeldNotice> TakeHeldToSend()
    {
        lock (_heldSending)
        {
            return _database.GetHeldNotices().Where(notice => _heldSending.Add(notice.Id)).ToList();
        }
    }

    /// <summary>
    /// A held notice is kept until it's gone out, and noted before it's sent, so a crash partway sends it once:
    /// never lost, and never twice. It's let go in the same write that notes it's done.
    /// </summary>
    private async Task SendHeldNoticeAsync(HeldNotice notice)
    {
        var key = $"held:{notice.Id}";
        try
        {
            await SendNoticeAsync(notice.Title, notice.Text, notice.Tone, notice.ToBudget, sendKey: key);
            _database.InOneWrite(() =>
            {
                _database.DropHeldNotice(notice.Id);
                _database.FinishDiscordSend(key);
                return 0;
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logError(exception);
        }
        finally
        {
            lock (_heldSending)
            {
                _heldSending.Remove(notice.Id);
            }
        }
    }

    private void CheckSessionPrompt()
    {
        if (_database.TakeDuePrompt(_clock()) is { } prompt)
        {
            _ = NotifyAsync(prompt.Headline, prompt.Text);
            _host.Changed();
        }
    }

    /// <summary>
    /// Each one is marked as it goes out, so a wait only checks in once. Bought and Skip are on the card, since
    /// typing only works in the capture channel. They wait out the quiet part of the night, buttons and all.
    /// </summary>
    private void CheckWaitList()
    {
        var now = _clock();
        if (BedtimeSchedule.IsQuiet(now))
        {
            return;
        }

        foreach (var item in _database.TakeWaitCheckIns(now))
        {
            var why = item.Reason is { } reason ? $"You wanted it: {reason}\n" : "";
            var id = item.Id.ToString(CultureInfo.InvariantCulture);
            _ = NotifyAsync($"Wait's over: {item.Description}", $"{why}Price: {Briefing.Money(item.PriceCents)}\nStill want it?", CardTone.Money, toBudgetChannel: true,
                actions: [new CardAction("Bought", WaitBought + id), new CardAction(CardLabels.Skip, WaitSkip + id, ButtonRole.Skip)]);
        }
    }

    private void CheckPayCalendar()
    {
        if (_database.TakePayCalendarNotice(DateOnly.FromDateTime(_clock())) is { } notice)
        {
            _ = NotifyAsync("Pay calendar", notice, CardTone.Money, toBudgetChannel: true, canWait: true);
        }
    }

    private void CheckBriefing()
    {
        var now = _clock();
        var time = TimeOnly.FromDateTime(now);
        var today = DateOnly.FromDateTime(now).ToString("O", CultureInfo.InvariantCulture);
        if (BedtimeSchedule.IsQuiet(now) || time < _briefingTime || time >= BriefingCutoff || _database.GetPreference(LastBriefingKey) == today)
        {
            return;
        }

        _database.SetPreference(LastBriefingKey, today);
        var safeToSpend = _database.GetSafeToSpend(DateOnly.FromDateTime(now));
        var attention = BudgetReport.Attention(_database.GetBudgetReport(DateOnly.FromDateTime(now)));
        var open = _database.GetOpenItems();
        var study = StudyWindows.ForToday(now, open, Schedule, _database.GetShifts(DateOnly.FromDateTime(now)), BedtimeSchedule)?.Line;
        var held = TakeAfterThisLine();
        var briefing = string.Join("\n", new[] { Briefing.Build(now, open, Schedule, safeToSpend, _database.GetStaleBalances(DateOnly.FromDateTime(now))), study, held.Line }.OfType<string>().Concat(attention));

        // One ping a morning. Safe to spend is already a line in it, and the Budget view keeps the number too.
        _ = NotifyAsync("Today", briefing, actions: held.Actions);
    }

    /// <summary>Sunday evening, or Monday morning if the PC was off for it.</summary>
    private void CheckWeeklyReset()
    {
        var now = _clock();
        var time = TimeOnly.FromDateTime(now);
        var today = DateOnly.FromDateTime(now);
        var isDue = (today.DayOfWeek == DayOfWeek.Sunday && time >= _weeklyResetTime)
            || (today.DayOfWeek == DayOfWeek.Monday && time < WeeklyResetCutoff);
        var week = (today.DayOfWeek == DayOfWeek.Monday ? today.AddDays(-1) : today).ToString("O", CultureInfo.InvariantCulture);
        if (BedtimeSchedule.IsQuiet(now) || !isDue || _database.GetPreference(LastWeeklyResetKey) == week)
        {
            return;
        }

        _database.SetPreference(LastWeeklyResetKey, week);
        var attention = BudgetReport.Attention(_database.GetBudgetReport(today));
        var review = _database.GetSpendingReview(today).Rows.Take(ReviewLinesInReset).Select(row => row.Label);
        var reset = string.Join("\n", new[] { Briefing.BuildWeek(now, _database.GetOpenItems()) }.Concat(attention).Concat(review));
        _host.ShowWeeklyReset(reset);
        _ = NotifyAsync("Your week", reset);
    }

    /// <summary>Once a day. A failure is logged and tried again the next day or the next start.</summary>
    private void CheckBackup()
    {
        var now = _clock();
        var today = DateOnly.FromDateTime(now);
        if (_backupCheckedOn == today)
        {
            return;
        }

        _backupCheckedOn = today;
        _database.BackUpIfDue(now);
    }

    /// <summary>Does nothing until alert email is set up, so the timer can run either way.</summary>
    public async Task PollEmailAsync()
    {
        if (Outlook is not { } outlook)
        {
            return;
        }

        await outlook.PollAsync(OnPurchase, OnUnreadAlert, cents => _ = NotifyAsync($"{Briefing.Money(cents)} deposit", "Added to your cushion", CardTone.Money, canWait: true), OnPaymentApp);
        _host.Changed();
    }

    /// <summary>The same read as the timer's, saying what came in, for when it's asked for in Ask.</summary>
    public async Task<string> CheckEmailNowAsync()
    {
        if (Outlook is not { } outlook)
        {
            return "Alert email isn't set up yet. Add its app ID on the Settings page.";
        }

        var purchases = 0;
        var deposits = 0;
        await outlook.PollAsync(
            purchase =>
            {
                purchases++;
                OnPurchase(purchase);
            },
            OnUnreadAlert,
            cents =>
            {
                deposits++;
                _ = NotifyAsync($"{Briefing.Money(cents)} deposit", "Added to your cushion", CardTone.Money, canWait: true);
            },
            OnPaymentApp);
        _host.Changed();
        if (outlook.LastError is { } error)
        {
            return $"Couldn't read email: {error}";
        }

        var found = new[] { Count(purchases, "new purchase"), Count(deposits, "deposit") }.Where(part => part.Length > 0).ToList();
        var skipped = outlook.SkippedSender is { } note ? $" {note}" : "";
        return (found.Count == 0 ? "Checked email. Nothing new came in." : $"Checked email: {string.Join(" and ", found)}.") + skipped;

        static string Count(int count, string what) => count switch
        {
            0 => "",
            1 => $"1 {what}",
            _ => $"{count} {what}s",
        };
    }

    private void OnPurchase(EmailPurchase purchase)
    {
        var left = _database.GetSafeToSpend(DateOnly.FromDateTime(_clock())) is { } cents ? $"\nSafe to spend today: {Briefing.Money(cents)}" : "";
        var headline = $"{Briefing.Money(purchase.Transaction.AmountCents)} at {MerchantName.Clean(purchase.Transaction.Description)}";
        _ = NotifyAsync(headline, $"Card: {purchase.Account}{left}", CardTone.Money, toBudgetChannel: true, canWait: true);
    }

    private void OnPaymentApp(PaymentAppOutcome outcome) => _ = PostPaymentAppAsync(outcome);

    /// <summary>
    /// Says a payment was matched, or asks which purchase it was for where typing is read. The card's ID is kept
    /// with the payment, so a reply to it settles that payment without going through sorting.
    /// </summary>
    public async Task PostPaymentAppAsync(PaymentAppOutcome outcome)
    {
        if (outcome.Matched)
        {
            await NotifyAsync("Repaid", outcome.Text, CardTone.Money, toBudgetChannel: true, canWait: true);
            return;
        }

        var waiting = _database.GetWaitingRepayments().FirstOrDefault(repayment => repayment.MessageId == outcome.MessageId);
        IReadOnlyList<RepaymentCandidate> candidates = waiting is null ? [] : _database.GetRepaymentCandidates(waiting, WaypointDatabase.MaxCardCandidates);
        var ask = candidates.Count > 0 ? "Pick the purchase below, or reply with its name." : "Reply with the purchase's name.";
        var card = await SendNoticeAsync(BankProfiles.Current.PaymentApp.Name, $"{outcome.Text}\n{ask}", CardTone.Attention, toCaptureChannel: true, actions: PaymentAppButtons(candidates, outcome.MessageId));
        if (card is not null && outcome.MessageId is { } payment)
        {
            _database.SetRepaymentCard(payment, card);
        }
    }

    /// <summary>Saved beside the database, so the parser can learn their format. Only the newest are kept.</summary>
    private void OnUnreadAlert(UnreadAlert alert) =>
        UnreadAlertLog.Append(Path.Combine(_dataDirectory, UnreadAlertsFile), alert);

    public async Task PollDiscordAsync(bool force = false)
    {
        if (Discord is not { } discord)
        {
            return;
        }

        if (_connectGateway)
        {
            DiscordHealth.Connection(_gateway?.IsConnected == true, _clock());
        }

        // Views keep their own pace, so they're checked even when polling isn't needed.
        _ = RefreshDiscordViewsAsync();
        if (!force && _gateway is { IsConnected: true, HasMessages: true } && discord.LastPolled is { } polled && DateTimeOffset.Now - polled < DiscordLivePollInterval)
        {
            return;
        }

        await discord.PollAsync();
        foreach (var action in await discord.ReadAlertActionsAsync())
        {
            await discord.ActOnAlertAsync(action);
        }

        _host.Changed();
    }

    /// <summary>
    /// One of the owner's messages in the capture channel. Its text and each file are captured separately, each
    /// under the message's ID, so a message handed over again after a crash or a close saves nothing twice. The
    /// message is marked finished once its text is saved and every file is dealt with. Until then it's handed over
    /// again later, such as when Waypoint closed before its files could be read.
    /// </summary>
    public void OnDiscordMessage(DiscordMessage message)
    {
        // A reply to a payment app card answers that card. It's never sorted, so it can't become a note or a task.
        // Answering it again only says it's already settled.
        if (message.ReplyTo is { } card && message.Text.Trim().Length > 0 && _database.FindRepaymentForCard(card, message.ReplyToText) is { } repayment)
        {
            AnswerPaymentAppReply(message, card, repayment);
        }
        else if (Captures.CaptureOnce(DiscordClientId(message.Id), message.Text, message.SentAt, CaptureOrigin.Discord) is { } id)
        {
            _discordReplies[id] = new DiscordReplyTo(message.Id);
        }

        if (message.Attachments.Count == 0)
        {
            _database.FinishDiscordMessage(message.Id, _clock());
        }

        foreach (var attachment in message.Attachments)
        {
            _ = ReadDiscordFileOnceAsync(message, attachment);
        }

        _ = ProcessDiscordAsync();
    }

    /// <summary>What a capture from a Discord message, or one of its files, is saved under.</summary>
    internal static string DiscordClientId(string messageId, string? attachmentId = null) =>
        attachmentId is null ? $"discord:{messageId}" : $"discord:{messageId}:{attachmentId}";

    /// <summary>The message a capture came from, by what it was saved under, for one whose reply was waiting when Waypoint closed.</summary>
    private string? DiscordMessageOf(long captureId) =>
        _database.GetCaptureClientId(captureId) is { } clientId && clientId.Split(':') is ["discord", { Length: > 0 } messageId, ..] ? messageId : null;

    /// <summary>Marks one file on a message as dealt with, finishing the message once all of them are.</summary>
    private void FinishDiscordFile(DiscordMessage message, DiscordAttachment attachment) =>
        _database.FinishDiscordPart(message.Id, attachment.Id, message.Attachments.Select(file => file.Id).ToList(), _clock());

    /// <summary>A file not already dealt with the last time the message was handed over.</summary>
    private async Task ReadDiscordFileOnceAsync(DiscordMessage message, DiscordAttachment attachment)
    {
        if (!StartDiscordFile(message, attachment))
        {
            return;
        }

        try
        {
            if (_database.IsDiscordPartDone(message.Id, attachment.Id))
            {
                return;
            }

            await ReadDiscordFileAsync(message, attachment, () => FinishDiscordFile(message, attachment));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Not marked as dealt with, so the message is handed over again later.
            _logError(exception);
        }
        finally
        {
            _discordFilesWorking.TryRemove((message.Id, attachment.Id), out _);
        }
    }

    // Files on Discord messages being read right now, so one handed over again meanwhile isn't read a second time.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Message, string File), bool> _discordFilesWorking = new();

    /// <summary>False when the file is already being read here.</summary>
    private bool StartDiscordFile(DiscordMessage message, DiscordAttachment attachment) =>
        _discordFilesWorking.TryAdd((message.Id, attachment.Id), true);

    private bool IsDiscordWorking(string messageId) => _discordFilesWorking.Keys.Any(working => working.Message == messageId);

    /// <summary>
    /// A file sent from the phone goes through the same reading as a dropped one, so it can add things but never
    /// change or finish what's already here. The downloaded copy is deleted once it's read. Once its words are
    /// saved, or the owner is told why it wasn't read, <paramref name="dealtWith"/> is called. Without Discord,
    /// such as while Waypoint is closing, it's left for the message to be handed over again.
    /// </summary>
    private async Task ReadDiscordFileAsync(DiscordMessage message, DiscordAttachment attachment, Action dealtWith)
    {
        if (Discord is not { } discord)
        {
            return;
        }

        // A voice note is captured as its words. A video stays a recording, which goes in on the PC.
        if (Transcription.VoiceNotes.IsVoiceNote(attachment.FileName, attachment.ContentType))
        {
            await ReadVoiceNoteAsync(discord, message, attachment, dealtWith);
            return;
        }

        if (Transcription.TranscriptionQueue.IsAudioFile(attachment.FileName))
        {
            await discord.ReplyAsync(message.Id, RecordingNotRead(attachment.FileName));
            dealtWith();
            return;
        }

        // Saved before Waypoint stopped, so there's nothing left to read.
        var clientId = DiscordClientId(message.Id, attachment.Id);
        if (_database.HasCaptureClientId(clientId))
        {
            dealtWith();
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), "Waypoint", "discord");
        var name = string.Concat(Path.GetFileName(attachment.FileName).Select(letter => Path.GetInvalidFileNameChars().Contains(letter) ? '_' : letter));
        var path = Path.Combine(folder, $"{attachment.Id}-{name}");
        try
        {
            Directory.CreateDirectory(folder);
            if (await discord.DownloadAsync(attachment, path, FileReader.MaxBytes) is { } problem)
            {
                await discord.ReplyAsync(message.Id, DiscordText.Notice("File not read", problem, CardTone.Attention));
                dealtWith();
                return;
            }

            // An import counts each row once, so a bank file read again only says it was already counted.
            if (await Task.Run(() => ReadStatement(path)) is { } statement)
            {
                await discord.ReplyAsync(message.Id, DiscordText.Notice("Bank file", ImportStatementsReply([statement]), CardTone.Money));
                dealtWith();
                return;
            }

            var file = await Task.Run(() => _readFile(path));
            file = file with { Name = Redaction.ApplyToName(attachment.FileName) };
            if (file.Problem is { } unreadable)
            {
                await discord.ReplyAsync(message.Id, DiscordText.Notice("File not read", unreadable, CardTone.Attention));
                dealtWith();
                return;
            }

            if (Captures.CaptureFile(file, CaptureOrigin.Discord, clientId) is { } id)
            {
                _discordReplies[id] = new DiscordReplyTo(message.Id);
            }

            dealtWith();
            await ProcessDiscordAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await discord.ReplyAsync(message.Id, DiscordText.Notice("File not read", $"Couldn't read {attachment.FileName}. Try sending it again.", CardTone.Attention));
            dealtWith();
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The system clears its temp folder in the end.
            }
        }
    }

    /// <summary>
    /// Sorts, then says so once for anything from Discord that couldn't be sorted yet, so a capture is never
    /// silently stuck. Its real answer still comes once sorting works again.
    /// </summary>
    private async Task ProcessDiscordAsync()
    {
        await Captures.ProcessPendingAsync();
        var pending = _database.GetPendingCaptures().Select(capture => capture.Id).ToHashSet();
        foreach (var (id, target) in _discordReplies)
        {
            if (!pending.Contains(id) || target.ToldWaiting || Discord is not { } discord)
            {
                continue;
            }

            _discordReplies[id] = target with { ToldWaiting = true };
            // The error itself is for the log and the PC's status line, and can be long or hold paths.
            var notice = DiscordText.Notice("Saved", "Sorting isn't working right now, so this is kept and will be sorted once it is.", CardTone.Attention);
            await discord.ReplyAsync(target.MessageId, target.Heard is { } heard ? notice with { Fields = [.. notice.Fields, DiscordText.HeardField(heard)] } : notice);
        }
    }

    /// <summary>A bank export as text, or null if the file isn't one. Small files only, the same limit as any file.</summary>
    public static string? ReadStatement(string path)
    {
        if (!path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || !File.Exists(path) || new FileInfo(path).Length > FileReader.MaxBytes)
        {
            return null;
        }

        try
        {
            var csv = File.ReadAllText(path);
            return CsvImport.IsStatement(csv) ? csv : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Imports bank exports and says what came of it.</summary>
    public string ImportStatementsReply(IEnumerable<string> statements)
    {
        // Each file is its own import, so one that can't be read doesn't hide the ones that were.
        var results = new List<ImportResult>();
        var problems = new List<string>();
        // The PC reads each file lazily as it's imported, so moving to the next one is inside the try too.
        using var files = statements.GetEnumerator();
        while (true)
        {
            try
            {
                if (!files.MoveNext())
                {
                    break;
                }

                results.Add(_database.ImportStatement(files.Current));
            }
            catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException)
            {
                problems.Add(exception.Message);
            }
        }

        if (results.Count == 0)
        {
            return $"Couldn't import that file. {string.Join(" ", problems.Distinct())}".TrimEnd();
        }

        var waitsResolved = results.Sum(result => result.WaitsResolved);
        _host.Post(_host.Changed);
        var added = results.Sum(result => result.Added);
        var skipped = results.Sum(result => result.Skipped);
        // Where they went comes first. Ones already counted are only mentioned when there are some.
        var counted = skipped == 1 ? "1 was already counted." : $"{skipped} were already counted.";
        var reply = added > 0
            ? $"Added {added} {(added == 1 ? "purchase" : "purchases")} to Budget.{(skipped > 0 ? $" {counted}" : "")}"
            : skipped > 0
                ? $"Nothing new. {(skipped == 1 ? "That one was" : $"All {skipped} were")} already counted."
                : "There was nothing to add from that file.";
        reply += waitsResolved > 0 ? $" {waitsResolved} from your wait list marked bought." : "";
        return problems.Count == 0
            ? reply
            : $"{reply} {(problems.Count == 1 ? "1 file" : $"{problems.Count} files")} couldn't be imported. {string.Join(" ", problems.Distinct())}";
    }

    /// <summary>Slash commands. They only read or do small things, so they answer straight away without the sorter.</summary>
    public CommandReply OnDiscordCommand(SlashCommand command)
    {
        var now = _clock();
        var today = DateOnly.FromDateTime(now);
        CommandReply reply;
        switch (command.Name)
        {
            case "help":
                reply = new CommandReply(DiscordViews.HelpCard(), true);
                break;
            case "now":
                reply = DescribeNow(now);
                break;
            case "today":
                var safe = _database.GetSafeToSpend(today);
                var attention = BudgetReport.Attention(_database.GetBudgetReport(today));
                var briefing = string.Join("\n", new[] { Briefing.Build(now, _database.GetOpenItems(), Schedule, safe, _database.GetStaleBalances(today)) }.Concat(attention));
                reply = new CommandReply(DiscordText.Notice("Today", briefing));
                break;
            case "budget":
                reply = new CommandReply(BudgetWithPurchases(today));
                break;
            case "afford":
                reply = decimal.TryParse(command.Options.GetValueOrDefault("amount"), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) && amount > 0
                    ? new CommandReply(DiscordText.Notice("Can I afford it?", _database.CheckAffordable((long)Math.Round(amount * 100, MidpointRounding.AwayFromZero), null, today).Text, CardTone.Money))
                    : new CommandReply(DiscordText.Notice("Can I afford it?", "Try an amount like 65 or 12.50."), true);
                break;
            case "snooze":
                var minutes = int.TryParse(command.Options.GetValueOrDefault("minutes"), out var asked) && asked > 0 ? asked : DefaultSnoozeMinutes;
                var latest = _database.LatestSentReminderMessage();
                var snoozed = _database.ApplyAlertAction(new AlertAction(latest, AlertActionKind.Snooze), now, minutes);

                // The card it was about turns gray too, so its buttons can't act on it a second time.
                if (snoozed is not null && latest is not null && Discord is { } bridge)
                {
                    _ = bridge.MarkHandledAsync(latest, snoozed);
                }

                reply = new CommandReply(DiscordText.Notice(snoozed ?? "No reminder is waiting to be snoozed.", "", CardTone.Finished), true);
                break;
            case "save-spot" or "savespot":
                reply = new CommandReply(DiscordText.Notice(SaveSpotFromDiscord(null, command.Options.GetValueOrDefault("stopped"), command.Options.GetValueOrDefault("next")), "", CardTone.Finished), true);
                break;
            default:
                reply = OnPhoneCommand(command, now) ?? new CommandReply(DiscordViews.HelpCard("That one's gone, so here's what there is"), true);
                break;
        }

        _host.Changed();
        ViewsChanged();
        return reply;
    }

    /// <summary>One next step: the session that's going, or else the most pressing thing, with a Start button.</summary>
    private CommandReply DescribeNow(DateTime now)
    {
        if (_database.GetActiveSession(now) is { Status: SessionStatus.Active } session)
        {
            return new CommandReply(DiscordText.Notice(session.TaskTitle, Activation.JobReply(session.Action)) with { HasSessionButtons = true, CanTrial = session.CanTrial });
        }

        if (Activation.PickTask(_database.WithoutQuietWaiting(_database.GetOpenItems(), DateOnly.FromDateTime(now)), null) is not { } item)
        {
            return new CommandReply(DiscordText.Notice("Nothing's waiting", "There's nothing due to work on right now."));
        }

        var card = DiscordText.Reminder(new DueReminder(item.Id, item, null, null), DateOnly.FromDateTime(now), false);
        return new CommandReply(card with { Title = $"Next: {card.Title}", HasStartNext = true, FeedbackFeature = Feedback.Suggestion, FeedbackRef = ItemRef.Format(item) });
    }

    /// <summary>Replies in Discord to something typed there, so the same reply never shows up in both places.</summary>
    public void ReplyInDiscord(CaptureOutcome outcome, string reply)
    {
        // A button's answer already replaced the card that was pressed.
        if (outcome.CaptureId is { } answered && _answeredOnCard.TryRemove(answered, out _))
        {
            return;
        }

        var card = DiscordText.Reply(reply.Length > 0 ? reply : "Saved.");
        card = outcome.Activation is { } running ? card with { HasSessionButtons = true, CanTrial = running.CanTrial } : card;
        var target = outcome.CaptureId is { } captureId && _discordReplies.TryRemove(captureId, out var found) ? found : null;

        // One saved before a restart still answers under the message it came from.
        target ??= outcome.CaptureId is { } saved && DiscordMessageOf(saved) is { } from ? new DiscordReplyTo(from) : null;

        // A voice note's reply shows what was heard, so a mis-hearing can be spotted.
        if (target?.Heard is { } heard)
        {
            card = card with { Fields = [.. card.Fields, DiscordText.HeardField(heard)] };
        }

        _ = Discord?.ReplyAsync(target?.MessageId, card);
    }

    /// <summary>
    /// /save-spot and the Save my spot form on a card. The spot is for what's named, else the session going now. It
    /// saves locally with the same form save as the PC.
    /// </summary>
    private string SaveSpotFromDiscord(string? project, string? stopped, string? next)
    {
        var now = _clock();
        var name = string.IsNullOrWhiteSpace(project) ? _database.GetActiveSession(now)?.TaskTitle : project;
        return _database.SaveSpotByHand(name, stopped, next, null, null, now).Text;
    }

    /// <summary>Notifications go to Discord alone. The host shows it instead if Discord isn't set up, or still can't be reached after retries.</summary>
    /// <param name="feedback">Adds a small This didn't help kept under this feature (see <see cref="Feedback"/>), with no item.</param>
    /// <param name="bedtimeLabel">The words on <paramref name="bedtimeButton"/>, for a wind-down step's Done.</param>
    public Task NotifyAsync(string title, string text, CardTone tone = CardTone.Reminder, bool toBudgetChannel = false, bool canWait = false, AlertActionKind? bedtimeButton = null, IReadOnlyList<CardAction>? actions = null, bool toCaptureChannel = false, bool saveSpot = false, string? feedback = null, string? bedtimeLabel = null) =>
        SendNoticeAsync(title, text, tone, toBudgetChannel, canWait, bedtimeButton, actions, toCaptureChannel, saveSpot, feedback, bedtimeLabel);

    /// <summary>The same, handing back the Discord message's ID when it went to Discord, so the card can be found again.</summary>
    /// <param name="sendKey">
    /// For a notice that must go out once, such as a held one: it's noted under this key before it's sent and sent with
    /// Discord's nonce, so a try before that went through is found instead of sent again. The caller lets the note go.
    /// </param>
    private async Task<string?> SendNoticeAsync(string title, string text, CardTone tone = CardTone.Reminder, bool toBudgetChannel = false, bool canWait = false, AlertActionKind? bedtimeButton = null, IReadOnlyList<CardAction>? actions = null, bool toCaptureChannel = false, bool saveSpot = false, string? feedback = null, string? bedtimeLabel = null, string? sendKey = null)
    {
        if (canWait && BedtimeSchedule.IsQuiet(_clock()))
        {
            _database.HoldNotice(title, text, tone, toBudgetChannel);
            return null;
        }

        // Right after starting, Discord may still be connecting. The notice waits for it instead of showing here.
        var step = TimeSpan.FromSeconds(1);
        for (var waited = TimeSpan.Zero; WaitingForDiscord && waited < DiscordConnectGrace; waited += step)
        {
            await (RetryWait ?? (span => Task.Delay(span)))(step);
        }

        if (Discord is not { } discord)
        {
            _host.ShowNotice(title, text);
            return null;
        }

        var card = DiscordText.Notice(title, text, tone) with { BedtimeButton = bedtimeButton, BedtimeLabel = bedtimeLabel, Actions = actions ?? [], SaveSpot = saveSpot, FeedbackFeature = feedback };
        string? id = null;
        var send = sendKey is null ? null : _database.BeginDiscordSend(sendKey, discord.AlertsChannelFor(toBudgetChannel), _clock());
        if (send is { MessageId: { } before })
        {
            id = before;
        }
        else if (send is { Fresh: false, Channel: { } channel })
        {
            id = (await discord.FindSentAsync(channel, send.StartedAt, send.Nonce, card)).MessageId;
        }

        var sent = id is not null || await AlertRetry.RunAsync(
            async () =>
            {
                var delivery = send is not null ? await discord.DeliverOnceAsync(send.Channel, card, send.Nonce)
                    : toCaptureChannel ? await discord.DeliverToCaptureAsync(card)
                    : await discord.DeliverAsync(card, toBudgetChannel);
                id = delivery.MessageId;
                return delivery.Sent;
            },
            RetryWait);
        DiscordHealth.Sent(sent, _clock());
        if (sent && send is not null && id is not null)
        {
            _database.SetDiscordSendMessage(send.Key, id);
        }

        if (!sent)
        {
            _host.ShowNotice(title, text);
        }

        return sent ? id : null;
    }

    /// <summary>A button pressed in Discord. Runs on the gateway's thread.</summary>
    public string? OnButtonPressed(string messageId, AlertActionKind kind)
    {
        _viewsDirty = true;

        // A session's buttons send the same words as typing, so both take the same path.
        var word = kind switch
        {
            AlertActionKind.DidIt => "did it",
            AlertActionKind.Smaller => "smaller",
            AlertActionKind.NotNow => "not now",
            AlertActionKind.Trial => "2 minutes",
            AlertActionKind.EveningStepDone => BedtimeRoutine.DoneText(BedtimeSchedule.Settings.EveningStepLabel()),
            AlertActionKind.WindDownStepDone => BedtimeRoutine.DoneText(BedtimeSchedule.Settings.WindDownStepLabel()),
            AlertActionKind.SnoozeBedtime => BedtimeRoutine.SnoozeWord,
            _ => null,
        };

        if (word is not null)
        {
            Captures.Capture(word, null, CaptureOrigin.Discord);
            _ = Captures.ProcessPendingAsync();
            return "Got it";
        }

        if (kind == AlertActionKind.StartNext)
        {
            if (Activation.PickTask(_database.WithoutQuietWaiting(_database.GetOpenItems(), DateOnly.FromDateTime(_clock())), null) is not { } next)
            {
                return "Nothing to start";
            }

            Captures.StartOn(next, CaptureOrigin.Discord);
            return $"Started: {next.Title}";
        }

        if (kind == AlertActionKind.Start)
        {
            // The card, its answer, and the session are one write, so a failure partway leaves the card to press again.
            if (_database.StartFromReminder(messageId, _clock()) is not { } started)
            {
                return null;
            }

            Captures.Started(started.Reply, CaptureOrigin.Discord);
            return "Started";
        }

        var reply = kind is AlertActionKind.MoreTime or AlertActionKind.NotNowReminder
            ? _database.ApplyTransitionAction(messageId, kind, _clock())
            : _database.ApplyAlertAction(new AlertAction(messageId, kind), _clock(), DefaultSnoozeMinutes);
        _host.Changed();
        return reply;
    }
}

/// <summary>
/// Where a Discord capture's answer goes, and whether it was already told sorting is waiting. Heard is a voice
/// note's words, shown with the answer.
/// </summary>
internal sealed record DiscordReplyTo(string MessageId, bool ToldWaiting = false, string? Heard = null);
