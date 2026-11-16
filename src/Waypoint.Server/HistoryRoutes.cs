using Waypoint.Core.Data;
using Waypoint.Core.Remote;

namespace Waypoint.Server;

/// <summary>The History page: searching what's been closed, reopening, and deleting. Undo of a delete is POST /api/items/undelete.</summary>
public static class HistoryRoutes
{
    public const int MaxRows = 500;

    public static void Map(RouteGroupBuilder api)
    {
        static LocalHistoryData History(WaypointServer server) => new(server.Database);

        api.MapGet("/history", (string? words, int? limit, HistoryFilter? filter, WaypointServer server) =>
            History(server).SearchHistory(words, Math.Clamp(limit ?? 100, 1, MaxRows), filter ?? HistoryFilter.All));
        api.MapPost("/history/delete", (HistoryEntryRequest request, WaypointServer server) => History(server).DeleteHistoryItem(request.Entry, DateTime.Now));
        api.MapPost("/history/reopen", (HistoryEntryRequest request, WaypointServer server) => new FlagValue(History(server).ReopenEntry(request.Entry)));
    }
}
