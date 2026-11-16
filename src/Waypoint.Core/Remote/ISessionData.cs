using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Remote;

/// <summary>
/// The cards above Ask: the session that's going, pay dates waiting for a yes, and tonight's checklist. Most of the
/// session card's buttons are typed words, so they go wherever typing goes. The rest call here, through the same
/// methods the assistant's ops use, so a button never needs Claude.
/// </summary>
public interface ISessionData
{
    ActivationSession? GetActiveSession(DateTime now);

    OpReply PauseSession(DateTime until, DateTime now);

    /// <summary>The session that was put off, if any, so To do can offer to pick it back up.</summary>
    ActivationSession? GetPausedSession();

    OpReply ResumeSession();

    /// <summary>Ends the session that's going, or the paused one, without marking anything done.</summary>
    OpReply CancelSession(DateTime now);

    /// <summary>Checks or unchecks a step on tonight's checklist, and says what's left.</summary>
    OpReply MarkRoutineStep(string step, bool done, DateTime now);

    /// <summary>Brings tonight's bedtime reminder back later, the same as Not now on its card.</summary>
    OpReply SnoozeBedtime(DateTime now);

    /// <summary>No more bedtime reminders tonight.</summary>
    OpReply SkipBedtime(DateTime now);

    /// <summary>What the pay card asks, or null when nothing is waiting for a yes.</summary>
    string? DescribePayProposal(DateTime now);

    BedtimeStatus? GetTonight(DateTime now);

    /// <summary>
    /// The one item held from earlier for "after this", taken so it shows only once. Null when there's none, or when
    /// the server can't be reached, since it's only ever a reminder riding on something already showing.
    /// </summary>
    StoredItem? TakeAfterThis(DateTime now);
}

/// <summary>Today's behavior: every call goes to the database on this PC.</summary>
public sealed class LocalSessionData(WaypointDatabase database, BedtimeSchedule? bedtime = null) : ISessionData
{
    public ActivationSession? GetActiveSession(DateTime now) => database.GetActiveSession(now);

    public OpReply PauseSession(DateTime until, DateTime now) => database.PauseSession(null, until, now);

    public ActivationSession? GetPausedSession() => database.GetPausedSession();

    public OpReply ResumeSession() => database.ResumeSession();

    public OpReply CancelSession(DateTime now) => database.CancelSession(now);

    public OpReply MarkRoutineStep(string step, bool done, DateTime now) => BedtimeRoutine.Mark(database, bedtime, step, done, now);

    public OpReply SnoozeBedtime(DateTime now) => BedtimeRoutine.Snooze(database, bedtime, now);

    public OpReply SkipBedtime(DateTime now) => BedtimeRoutine.Dismiss(database, bedtime, now);

    public string? DescribePayProposal(DateTime now) => ReadPayProposal(database, now);

    public BedtimeStatus? GetTonight(DateTime now) => bedtime is null ? null : BedtimeRoutine.GetStatus(database, bedtime, now);

    public StoredItem? TakeAfterThis(DateTime now) => database.TakeAfterThis(now);

    public static string? ReadPayProposal(WaypointDatabase database, DateTime now) =>
        database.GetPayProposal(now) is { } proposal ? database.DescribeProposal(proposal, now) : null;
}

public sealed record RoutineStepRequest(string Step, bool Done);

/// <summary>The cards' reads, from the server. Its clock decides, so "now" here only matters locally.</summary>
public sealed class ServerSessionData(ServerClient server) : ISessionData
{
    public ActivationSession? GetActiveSession(DateTime now) => server.GetOr("api/session", new SessionValue(null)).Session;

    public OpReply PauseSession(DateTime until, DateTime now) => server.Send<OpReply>(HttpMethod.Post, "api/session/pause", new PauseRequest(null, until));

    public ActivationSession? GetPausedSession() => server.GetOr("api/session/paused", new SessionValue(null)).Session;

    public OpReply ResumeSession() => server.Send<OpReply>(HttpMethod.Post, "api/session/resume");

    public OpReply CancelSession(DateTime now) => server.Send<OpReply>(HttpMethod.Post, "api/session/cancel");

    public OpReply MarkRoutineStep(string step, bool done, DateTime now) => server.Send<OpReply>(HttpMethod.Post, "api/tonight/mark", new RoutineStepRequest(step, done));

    public OpReply SnoozeBedtime(DateTime now) => server.Send<OpReply>(HttpMethod.Post, "api/tonight/snooze");

    public OpReply SkipBedtime(DateTime now) => server.Send<OpReply>(HttpMethod.Post, "api/tonight/skip");

    public string? DescribePayProposal(DateTime now) => server.GetOr("api/pay-proposal", new TextValue(null)).Value;

    public BedtimeStatus? GetTonight(DateTime now) => server.GetOr("api/tonight", new TonightValue(null)).Tonight;

    public StoredItem? TakeAfterThis(DateTime now)
    {
        try
        {
            return server.Send<HeldItemValue>(HttpMethod.Post, AfterThisPath).Item;
        }
        catch (Exception exception) when (exception is ServerUnavailableException or ServerTimedOutException or InvalidOperationException)
        {
            return null;
        }
    }

    public const string AfterThisPath = "api/after-this/take";
}

/// <summary>The item held from earlier, or none.</summary>
public sealed record HeldItemValue(StoredItem? Item);
