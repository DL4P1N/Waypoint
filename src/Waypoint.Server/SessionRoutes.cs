using Waypoint.Core.Remote;
using Waypoint.Core.Schedule;
using Waypoint.Core.Capture;

namespace Waypoint.Server;

/// <summary>
/// The cards above Ask in the PC app: the session that's going, pay dates waiting for a yes, and tonight's checklist.
/// Tonight is worked out here, from the server's bedtime settings, since it decides the wind-down.
/// </summary>
public static class SessionRoutes
{
    /// <summary>The same calls the PC makes in local mode, with the server's bedtime settings.</summary>
    private static LocalSessionData Sessions(WaypointServer server) => new(server.Database, server.Worker.BedtimeSchedule);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/session", (WaypointServer server) => new SessionValue(server.Database.GetActiveSession(DateTime.Now)));
        api.MapPost("/session/pause", (PauseRequest request, WaypointServer server) =>
            server.Database.PauseSession(request.Note, request.Until, DateTime.Now));
        api.MapGet("/session/paused", (WaypointServer server) => new SessionValue(Sessions(server).GetPausedSession()));
        api.MapPost("/session/resume", (WaypointServer server) => Sessions(server).ResumeSession());
        api.MapPost("/session/cancel", (WaypointServer server) => Sessions(server).CancelSession(DateTime.Now));
        api.MapPost("/tonight/mark", (RoutineStepRequest request, WaypointServer server) => Sessions(server).MarkRoutineStep(request.Step, request.Done, DateTime.Now));
        api.MapPost("/tonight/snooze", (WaypointServer server) => Sessions(server).SnoozeBedtime(DateTime.Now));
        api.MapPost("/tonight/skip", (WaypointServer server) => Sessions(server).SkipBedtime(DateTime.Now));
        api.MapGet("/pay-proposal", (WaypointServer server) => new TextValue(LocalSessionData.ReadPayProposal(server.Database, DateTime.Now)));
        api.MapPost("/after-this/take", (WaypointServer server) => new HeldItemValue(server.Database.TakeAfterThis(DateTime.Now)));
        api.MapGet("/feedback", (WaypointServer server) => server.Database.GetFeedback());
        api.MapPost("/feedback", (FeedbackRequest request, WaypointServer server) =>
            new FeedbackKept(server.Database.AddFeedback(request.Feature, request.ItemRef, request.Note, DateTime.Now)));
        api.MapPut("/feedback/{id:long}/note", (long id, TextValue note, WaypointServer server) => new FlagValue(server.Database.NoteFeedback(id, note.Value)));
        api.MapDelete("/feedback", (WaypointServer server) => new FeedbackKept(server.Database.ClearFeedback()));
        api.MapPost("/feedback/restore", (List<FeedbackEntry> entries, WaypointServer server) =>
        {
            server.Database.RestoreFeedback(entries);
            return new FeedbackKept(entries.Count);
        });
        api.MapGet("/tonight", (WaypointServer server) =>
            new TonightValue(BedtimeRoutine.GetStatus(server.Database, server.Worker.BedtimeSchedule, DateTime.Now)));
    }
}
