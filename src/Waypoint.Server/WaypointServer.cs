using Microsoft.Extensions.Options;
using Waypoint.Core;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Platform;
using Waypoint.Core.Remote;

namespace Waypoint.Server;

/// <summary>The one database and the background work, made once and shared by the API and the loops.</summary>
public sealed class WaypointServer
{
    private readonly ILogger<WaypointServer> _logger;
    private int _pairingChecked;

    public WaypointServer(IOptions<ServerOptions> options, ServerHost host, ReplyLog replies, WorkerParts parts, ILogger<WaypointServer> logger, ReportedIdle? idle = null)
    {
        _logger = logger;
        var settings = options.Value;

        // What the PC is told is said plainly, so the detail behind it is kept in the journal.
        PlainProblem.Log = exception => logger.LogWarning(exception, "A problem was shown to the user");
        DataDirectory = settings.ResolveDataDirectory();
        // Before anything reads a statement or an alert, so purchases go under the account names the profiles give.
        Waypoint.Core.Budget.BankProfiles.Load(DataDirectory);
        Database = new WaypointDatabase(Path.Combine(DataDirectory, "waypoint.db"));

        // Once, before anything reads the notes: typed headings and lists that were saved as text get their marks back.
        Waypoint.Core.Notes.TypedMarkRepair.RunOnce(Database, (message, problem) =>
        {
            if (problem is null)
            {
                logger.LogInformation("{Message}", message);
            }
            else
            {
                logger.LogWarning(problem, "{Message}", message);
            }
        });
        var file = WaypointSettings.Load(Path.Combine(DataDirectory, "settings.json"));
        Host = host;
        host.Database = Database;
        Worker = new WaypointWorker(
            Database,
            file,
            DataDirectory,
            host,
            exception => logger.LogError(exception, "A background check failed"),
            parts with { Idle = parts.Idle ?? idle ?? new ReportedIdle() });

        Token = ServerToken.LoadOrCreate(DataDirectory, out var created);
        if (created is not null)
        {
            // Only its hash is kept. The token itself goes to a file only the server's user can read, never the
            // journal, which more people and backups can see.
            var pairing = Path.Combine(DataDirectory, ServerToken.PairingFileName);
            FileTokenStore.WriteOwnerOnly(pairing, created + Environment.NewLine);
            logger.LogWarning("Made a new pairing token for the PC app. It's in {Path}, and is removed once a PC has paired.", pairing);
        }

        TimeZoneWarning = TimeZones.Warning(TimeZoneInfo.Local, settings.TimeZone, OperatingSystem.IsWindows() ? null : TimeZones.SystemZone());
        if (TimeZoneWarning is not null)
        {
            logger.LogWarning("{Warning}", TimeZoneWarning);
        }

        // The PC's status line says when sorting fails. The journal says so too, once per distinct error.
        var sortingFailures = new SortingFailureLog(logger);
        Worker.Captures.Failed += sortingFailures.Failed;
        Worker.Captures.Sorted += outcome =>
        {
            sortingFailures.Worked();
            var reply = string.Join("\n", new[] { ItemGroups.OutcomeSummary(outcome), outcome.Answer }.Where(text => !string.IsNullOrWhiteSpace(text)));
            if (outcome.Origin == CaptureOrigin.Discord)
            {
                Worker.ReplyInDiscord(outcome, reply);
                return;
            }

            var made = outcome.CaptureId is { } capture ? Database.GetItemsFromCapture(capture) : [];
            var opensWindow = !string.IsNullOrWhiteSpace(outcome.Answer) || outcome.Changes.Any(change => change.Applied && change.Change.Action == ChangeAction.BreakDown);
            replies.Add(reply, made, outcome.Activation is not null, opensWindow);
        };
    }

    public string DataDirectory { get; }

    public WaypointDatabase Database { get; }

    public WaypointWorker Worker { get; }

    public ServerHost Host { get; }

    public ServerToken Token { get; }

    /// <summary>
    /// Called when a PC has presented the right token. The token is in the PC now, so its pairing file, which only
    /// the first PC needed, is deleted, and the journal says so. Does nothing after the first call.
    /// </summary>
    public void Paired()
    {
        if (Interlocked.Exchange(ref _pairingChecked, 1) != 0)
        {
            return;
        }

        var pairing = Path.Combine(DataDirectory, ServerToken.PairingFileName);
        try
        {
            if (File.Exists(pairing))
            {
                File.Delete(pairing);
                _logger.LogWarning("A PC has paired, so the pairing token file {Path} was removed.", pairing);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "A PC has paired, but the pairing token file {Path} couldn't be removed. Delete it by hand.", pairing);
            Interlocked.Exchange(ref _pairingChecked, 0);
        }
    }

    public string? TimeZoneWarning { get; }

    /// <summary>When the alert checks last ran, so the health check and the heartbeat can tell if they've stalled.</summary>
    public DateTime? LastCheck
    {
        get => Interlocked.Read(ref _lastCheckTicks) is var ticks and > 0 ? new DateTime(ticks) : null;
        set => Interlocked.Exchange(ref _lastCheckTicks, value?.Ticks ?? 0);
    }

    private long _lastCheckTicks;

    public bool IsHealthy(DateTime now, bool runsBackground) => Problem(now, runsBackground) is null;

    /// <summary>
    /// Why the server isn't doing its job, or null when it is: the alert checks have stalled, or Discord is set up
    /// but sends or the live connection have failed for longer than <see cref="WaypointWorker.DiscordTroubleAfter"/>.
    /// </summary>
    public string? Problem(DateTime now, bool runsBackground) =>
        !runsBackground ? null
        : LastCheck is not { } last || now - last >= TimeSpan.FromMinutes(2) ? "Reminders on the server have stopped running."
        : Worker.DiscordTrouble(now);
}
