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

}

/// <summary>
/// Where a Discord capture's answer goes, and whether it was already told sorting is waiting. Heard is a voice
/// note's words, shown with the answer.
/// </summary>
internal sealed record DiscordReplyTo(string MessageId, bool ToldWaiting = false, string? Heard = null);
