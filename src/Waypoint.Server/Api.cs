using System.Globalization;
using Microsoft.Extensions.Options;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Email;
using Waypoint.Core.Remote;

namespace Waypoint.Server;

/// <summary>
/// The private API the PC app uses. Every route under /api needs the pairing token. Only /health answers without
/// it, and it says nothing about the data. "Now" is always the server's own clock.
/// </summary>
public static class Api
{
    public static void Map(WebApplication app)
    {
        // Plain HTTP allowed outside this machine and Tailscale is said here too, so a check of /health shows it.
        app.MapGet("/health", (WaypointServer server, IOptions<ServerOptions> options) =>
            (server.IsHealthy(DateTime.Now, options.Value.RunBackground), options.Value.PublicHttpWarning()) switch
            {
                (true, null) => Results.Ok(new { status = "ok" }),
                (true, var warning) => Results.Ok(new { status = "ok", warning }),
                (false, null) => Results.Json(new { status = "stalled" }, statusCode: StatusCodes.Status503ServiceUnavailable),
                (false, var warning) => Results.Json(new { status = "stalled", warning }, statusCode: StatusCodes.Status503ServiceUnavailable),
            });

        var api = app.MapGroup("/api");

        api.MapGet("/status", (WaypointServer server, ReplyLog replies) => Status(server, replies));

        // The PC shows a notice that couldn't reach Discord, and says so here. Only the first to say so gets true.
        api.MapPost("/notices/{id:long}/delivered", (long id, WaypointServer server) => new FlagValue(server.Database.MarkNoticeDelivered(id)));

        MapCaptures(api);
        MapItems(api);
        MapFilters(api);
        MapReads(api);
        MapActions(api);
        MapWaiting(api);
        BudgetRoutes.Map(api);
        SessionRoutes.Map(api);
        NotesRoutes.Map(api);
    }

    /// <summary>Another group under /api, for a page's routes kept in a file of their own. The token is checked before routing, in <see cref="UseToken"/>.</summary>
    public static RouteGroupBuilder Protected(WebApplication app) => app.MapGroup("/api");

    /// <summary>Marks the one route allowed a body over the usual limit. It sets its own limit and streams the body to disk.</summary>
    public sealed class LargeBody
    {
        public static readonly LargeBody Instance = new();

        private LargeBody()
        {
        }
    }

    /// <summary>Marks the restore itself, the one change let through while a restore runs.</summary>
    public sealed class RestoreRoute
    {
        public static readonly RestoreRoute Instance = new();

        private RestoreRoute()
        {
        }
    }

    /// <summary>
    /// Turns away any request without the pairing token, other than /health, before routing, so nothing in its body is
    /// read first. Then holds every body to a small limit, except a route marked <see cref="LargeBody"/>. During a
    /// restore every change is turned away with a 503, and a change under way is counted so the restore can wait for it.
    /// </summary>
    public static void UseToken(WebApplication app)
    {
        var server = app.Services.GetRequiredService<WaypointServer>();
        var limit = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.MaxRequestBytes;
        var maintenance = app.Services.GetRequiredService<ServerMaintenance>();

        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
            {
                var header = context.Request.Headers.Authorization.ToString();
                var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..] : null;
                if (!server.Token.Matches(token))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }

            await next(context);
        });

        app.UseRouting();

        app.Use(async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<LargeBody>() is null)
            {
                if (context.Request.ContentLength > limit)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return;
                }

                if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
                {
                    size.MaxRequestBodySize = limit;
                }
            }

            if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)
                || context.GetEndpoint()?.Metadata.GetMetadata<RestoreRoute>() is not null)
            {
                await next(context);
                return;
            }

            using var change = maintenance.TryStartChange();
            if (change is null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.RetryAfter = "10";
                await context.Response.WriteAsJsonAsync(new { status = "restoring", problem = ServerMaintenance.Restoring });
                return;
            }

            await next(context);
        });
    }

    /// <summary>Waiting on someone: the To do page's Waiting on filter and its forms.</summary>
    private static void MapWaiting(RouteGroupBuilder api)
    {
        api.MapGet("/waiting", (WaypointServer server) => server.Database.GetWaiting());
        api.MapPost("/waiting", (WaitingRequest request, WaypointServer server) =>
            server.Database.AddWaitingByHand(request.Who, request.Topic, request.FollowUp, request.Email, DateTime.Now));
        api.MapPost("/waiting/mark", (MarkWaitingRequest request, WaypointServer server) =>
            new TextValue(server.Database.MarkWaitingByHand(request.Item, request.Who, request.FollowUp, request.Email, DateTime.Now)));
        api.MapPost("/waiting/still", (StillWaitingRequest request, WaypointServer server) =>
            server.Database.StillWaiting(request.ItemId, request.Until, DateTime.Now));
        api.MapDelete("/waiting/{id:long}", (long id, WaypointServer server) => new FlagValue(server.Database.StopWaiting(id)));
    }

    private static ServerStatus Status(WaypointServer server, ReplyLog replies)
    {
        var worker = server.Worker;
        var warnings = new[]
        {
            server.TimeZoneWarning,
            server.Host.Undelivered,
            server.Problem(DateTime.Now, runsBackground: server.LastCheck is not null),
        };

        return new ServerStatus(
            DateTime.Now,
            TimeZoneInfo.Local.Id,
            server.Database.GetPendingCaptures().Count,
            worker.Captures.LastError,
            worker.Calendar?.LastError,
            worker.Nudges?.LastError,
            worker.Discord?.LastError ?? worker.Outlook?.LastError,
            worker.DescribeConnections(),
            warnings.OfType<string>().ToList(),
            replies.Last,
            replies.Boot,
            server.Database.GetUndeliveredNotices());
    }

    private static void MapCaptures(RouteGroupBuilder api)
    {
        api.MapPost("/captures", (CaptureRequest capture, WaypointServer server) =>
        {
            if (string.IsNullOrWhiteSpace(capture.Text) || string.IsNullOrWhiteSpace(capture.Id))
            {
                return Results.BadRequest();
            }

            // The PC's ID is saved with the capture, so a send that failed can be tried again, and one whose answer
            // was lost, even across a restart, isn't saved twice.
            var captures = server.Worker.Captures;
            if (captures.CaptureSent(capture.Id, capture.Text, capture.WrittenAt, capture.FileName) is null)
            {
                return Results.Ok(new CaptureAccepted(Duplicate: true));
            }

            _ = captures.ProcessPendingAsync();
            return Results.Ok(new CaptureAccepted(Duplicate: false));
        });

        api.MapGet("/captures/pending", (WaypointServer server) => server.Database.GetPendingCaptures());

        api.MapGet("/replies", (long? after, ReplyLog replies) => replies.After(after ?? 0));
    }

    private static void MapItems(RouteGroupBuilder api)
    {
        api.MapGet("/items/open", (WaypointServer server) => server.Database.GetOpenItems());
        api.MapGet("/items/finished", (WaypointServer server) => server.Database.GetFinishedForList(DateTime.Now));
        // One sent with the PC's own id is kept once, however often it's sent. One without, from a PC from before, adds as it always has.
        api.MapPost("/items", (HandAddedItem input, string? client, WaypointServer server) => client is { Length: > 0 } id
            ? server.Database.AddByHandOnce(id, input, DateTime.Now)
            : server.Database.AddByHand(input, DateTime.Now));
        api.MapPost("/items/done", (DoneRequest request, WaypointServer server) =>
        {
            server.Database.SetDone(request.Item, request.Done);
            return Results.NoContent();
        });
        api.MapPost("/items/took", (ItemRequest request, WaypointServer server) => server.Database.TakeDose(request.Item, DateTime.Now));
        api.MapPost("/items/dose", (DoseRequest request, WaypointServer server) => new FlagValue(server.Database.SetDose(request.Item, request.Dose)));
        api.MapPost("/items/moved", (ItemRequest request, WaypointServer server) => new FlagValue(server.Database.IsMovedFromFeed(request.Item)));
        api.MapPost("/items/delete", (ItemRequest request, WaypointServer server) => server.Database.DeleteItem(request.Item, DateTime.Now));
        api.MapPost("/items/undelete", (DeletedItem deleted, WaypointServer server) =>
        {
            server.Database.UndoDelete(deleted, DateTime.Now);
            return Results.NoContent();
        });
        api.MapPost("/items/filter", (AssignRequest request, WaypointServer server) => new FlagValue(server.Database.AssignToFilter(request.Item, request.FilterId)));
        api.MapPost("/items/reschedule", (RescheduleRequest request, WaypointServer server) =>
            server.Database.RescheduleItem(request.Item, request.Date, request.Time, DateTime.Now));
        api.MapPost("/items/change", (ItemChange change, WaypointServer server) => server.Database.ChangeItem(change, DateTime.Now));
    }

    private static void MapFilters(RouteGroupBuilder api)
    {
        api.MapGet("/filters", (WaypointServer server) => server.Database.GetToDoFilters());
        api.MapGet("/placements", (WaypointServer server) =>
            server.Database.GetItemPlacements().Select(entry => new PlacementEntry(entry.Key, entry.Value)).ToList());
        api.MapPost("/filters", (NameRequest request, WaypointServer server) => server.Database.CreateToDoFilter(request.Name));
        api.MapPut("/filters/order", (OrderRequest request, WaypointServer server) =>
        {
            server.Database.SetToDoFilterOrder(request.Ids);
            return Results.NoContent();
        });
        api.MapPut("/filters/{id:long}/name", (long id, NameRequest request, WaypointServer server) => server.Database.RenameToDoFilter(id, request.Name));
        api.MapDelete("/filters/{id:long}", (long id, WaypointServer server) => new FlagValue(server.Database.RemoveToDoFilter(id, DateTime.Now)));
        api.MapPost("/filters/{id:long}/restore", (long id, WaypointServer server) =>
        {
            server.Database.RestoreToDoFilter(id);
            return Results.NoContent();
        });
    }

    /// <summary>Reads for the pages and for Discord-style summaries: preferences, money, shifts, lists, notes, and the budget.</summary>
    private static void MapReads(RouteGroupBuilder api)
    {
        api.MapGet("/preferences/{key}", (string key, WaypointServer server) => new PreferenceValue(server.Database.GetPreference(key)));
        api.MapPut("/preferences/{key}", (string key, PreferenceValue value, WaypointServer server) =>
        {
            server.Database.SetPreference(key, value.Value ?? "");
            return Results.NoContent();
        });
        api.MapGet("/money", (WaypointServer server) => LocalToDoData.ReadSpendSummary(server.Database, DateOnly.FromDateTime(DateTime.Now)));
        api.MapGet("/shifts/{date}", (string date, WaypointServer server) =>
            DateOnly.TryParseExact(date, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? Results.Ok(server.Database.GetShifts(day))
                : Results.BadRequest());
        api.MapGet("/lists", (WaypointServer server) => server.Database.GetListOverview(includeEmptyShopping: true));
        api.MapGet("/notes", (WaypointServer server) => server.Database.GetNotes());
        api.MapGet("/budget", (WaypointServer server) => server.Database.GetBudgetReport(DateOnly.FromDateTime(DateTime.Now)));
    }

    private static void MapActions(RouteGroupBuilder api)
    {
        api.MapGet("/activation/session", (WaypointServer server) => new TextValue(server.Database.GetActiveSession(DateTime.Now)?.TaskTitle));
        api.MapGet("/suggestion-habits", (WaypointServer server) => server.Database.GetSuggestionHabits(DateTime.Now));
        api.MapPost("/activation/start", (StartRequest request, WaypointServer server) =>
            server.Database.StartActivation(request.TaskRef, request.FirstAction, null, request.Method, DateTime.Now));
        api.MapPost("/activation/smaller", (SmallerRequest request, WaypointServer server) =>
            server.Database.StartSmaller(request.Item, request.Method, DateTime.Now));
        api.MapPost("/spot", (SpotRequest request, WaypointServer server) =>
            server.Database.SaveSpotByHand(request.Project, request.Stopped, request.Next, request.Links, request.Thoughts, DateTime.Now));
        api.MapPost("/routine", (RoutineMarkRequest request, WaypointServer server) =>
        {
            // Kept for a PC on an older version. Newer ones use /tonight/mark, which answers. Both check the step the same way.
            Waypoint.Core.Schedule.BedtimeRoutine.Mark(server.Database, server.Worker.BedtimeSchedule, request.Step, request.Done, DateTime.Now);
            return Results.NoContent();
        });

        // Alert email signs in on the server: this gives the code to enter, and the sign-in finishes in the background.
        api.MapPost("/email/connect", async (WaypointServer server, ILoggerFactory logs) =>
        {
            if (server.Worker.Outlook is not { } outlook)
            {
                return Results.Ok(new EmailSignIn(null, null, "Alert email isn't set up yet. Add its app ID on the Settings page first."));
            }

            DeviceCode code = await outlook.BeginSignInAsync();
            _ = Task.Run(async () =>
            {
                var logger = logs.CreateLogger("Waypoint.Email");
                try
                {
                    logger.LogInformation(await outlook.FinishSignInAsync(code) ? "Alert email connected" : "Alert email sign-in didn't finish");
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Alert email sign-in failed");
                }
            });
            return Results.Ok(new EmailSignIn(code.UserCode, code.VerificationUri));
        });
    }
}
