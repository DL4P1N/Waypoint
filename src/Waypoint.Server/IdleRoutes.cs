using Waypoint.Core.Remote;

namespace Waypoint.Server;

/// <summary>
/// The PC's idle time for late-night check-ins: seconds since its last keyboard or mouse input, posted about once
/// a minute from bedtime until morning. Kept in memory only, never logged or saved.
/// </summary>
public static class IdleRoutes
{
    public static void Map(RouteGroupBuilder api) =>
        api.MapPost("/idle", (IdleReport report, ReportedIdle idle) =>
        {
            idle.Report(report, DateTime.Now);
            return Results.NoContent();
        });
}
