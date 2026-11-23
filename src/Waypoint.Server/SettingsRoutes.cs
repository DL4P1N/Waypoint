using System.Globalization;
using Waypoint.Core.Remote;
using Waypoint.Core.Schedule;

namespace Waypoint.Server;

/// <summary>
/// The Settings page's server half: work hours, pay rate, bedtime, classes, Discord, alert email, and the calendar feed. These go through the same class
/// the PC uses in local mode, so the rules are the same on both ends. A Discord token sent here is saved with the
/// server's other sign-ins, readable only by the service user, and is never sent back.
/// </summary>
public static class SettingsRoutes
{
    public static void Map(RouteGroupBuilder api)
    {
        static LocalSettingsData Settings(WaypointServer server) => new(server.Database, server.Worker);

        api.MapGet("/settings/weekly", (WaypointServer server) => Settings(server).GetWeeklyHours());
        api.MapPost("/settings/weekly", (WeeklyHoursRequest request, WaypointServer server) => Settings(server).SetWeeklyHours(request.Days, request.Hours));
        api.MapPost("/settings/weekly/remove", (WeeklyHoursRequest request, WaypointServer server) => Settings(server).RemoveWeeklyHours(request.Days));

        api.MapGet("/settings/dates", (string? from, WaypointServer server) =>
            Day(from) is { } day ? Results.Ok(Settings(server).GetWorkDates(day)) : Results.BadRequest());
        api.MapDelete("/settings/dates/{date}", (string date, WaypointServer server) =>
            Day(date) is { } day ? Results.Ok(Settings(server).RemoveWorkDate(day)) : Results.BadRequest());
        api.MapPost("/settings/shifts", (ShiftRequest request, WaypointServer server) => Settings(server).AddWorkShift(request.Start, request.End));
        api.MapPost("/settings/dayoff", (ShiftRequest request, WaypointServer server) => Settings(server).SetDayOff(request.Start));

        api.MapGet("/settings/commute", (WaypointServer server) => new NumberValue(Settings(server).GetCommuteMinutes()));
        api.MapPut("/settings/commute", (TextValue minutes, WaypointServer server) => Settings(server).SetCommute(minutes.Value));
        api.MapGet("/settings/rate", (WaypointServer server) => new NumberValue(Settings(server).GetHourlyRate()));
        api.MapPut("/settings/rate", (RateRequest request, WaypointServer server) => Settings(server).SetHourlyRate(request.Dollars));

        api.MapGet("/settings/bedtime", (WaypointServer server) => Settings(server).GetBedtime());
        api.MapPut("/settings/bedtime", (BedtimeChoices choices, WaypointServer server) => new TextValue(Settings(server).SetBedtimeChoices(choices)));
        api.MapGet("/settings/classes", (WaypointServer server) => Settings(server).GetClasses());
        api.MapPut("/settings/classes", (ClassesForm form, WaypointServer server) => new TextValue(Settings(server).SaveClasses(form)));
        api.MapGet("/settings/tonight", (WaypointServer server) => new TextValue(Settings(server).DescribeTonight(DateTime.Now)));

        api.MapGet("/settings/discord", (WaypointServer server) => Settings(server).GetDiscord());
        api.MapPut("/settings/discord", (DiscordSaveRequest request, WaypointServer server) =>
            new TextValue(Settings(server).SaveDiscord(
                new DiscordForm(request.OwnerUserId, request.CaptureChannelId, request.AlertsChannelId, request.BudgetChannelId, false, request.NotesForumChannelId, request.ListsForumChannelId),
                request.Token)));
        api.MapPost("/settings/discord/test", async (WaypointServer server) => new TextValue(await Settings(server).TestDiscordAsync()));

        // The feed link comes in here and is kept on the server, but is never read back out.
        api.MapGet("/settings/calendar", (WaypointServer server) => Settings(server).GetCalendarFeed());
        api.MapPut("/settings/calendar", (TextValue link, WaypointServer server) => new TextValue(Settings(server).SaveCalendarFeed(link.Value)));
        api.MapDelete("/settings/calendar", (WaypointServer server) => new TextValue(Settings(server).RemoveCalendarFeed()));
        api.MapPost("/settings/calendar/put-back", (WaypointServer server) => new TextValue(Settings(server).PutBackCalendarFeed()));

        api.MapGet("/settings/email", (WaypointServer server) => Settings(server).GetAlertEmail());
        api.MapPut("/settings/email", (AlertEmailForm form, WaypointServer server) => new TextValue(Settings(server).SaveAlertEmail(form)));
    }

    private static DateOnly? Day(string? text) =>
        DateOnly.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
}
