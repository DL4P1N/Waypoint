using Waypoint.Core.Remote;

namespace Waypoint.Server;

/// <summary>The Lists page's changes. Reading every list is GET /api/lists, which Discord-style summaries share.</summary>
public static class ListsRoutes
{
    public static void Map(RouteGroupBuilder api)
    {
        static LocalListsData Lists(WaypointServer server) => new(server.Database);

        api.MapPost("/lists", (NameRequest request, WaypointServer server) => Lists(server).CreateList(request.Name, DateTime.Now));
        api.MapPost("/lists/rename", (ListRenameRequest request, WaypointServer server) =>
            Lists(server).RenameList(request.Name, request.NewName, DateTime.Now));
        api.MapPost("/lists/order", (ListOrderRequest request, WaypointServer server) =>
        {
            Lists(server).OrderLists(request.Names);
            return Results.NoContent();
        });
        api.MapPost("/lists/start", (NameRequest request, WaypointServer server) => new TextValue(Lists(server).StartList(request.Name)));
        api.MapPost("/lists/delete", (NameRequest request, WaypointServer server) => new TextValue(Lists(server).DeleteList(request.Name, DateTime.Now)));
        api.MapPost("/lists/entries", (ListEntryRequest request, WaypointServer server) =>
        {
            Lists(server).AddListEntry(request.List, request.Text, DateTime.Now);
            return Results.NoContent();
        });
        api.MapPost("/lists/entries/done", (EntryDoneRequest request, WaypointServer server) =>
        {
            Lists(server).SetEntryDone(request.Entry, request.Done, DateTime.Now);
            return Results.NoContent();
        });
        api.MapPost("/lists/entries/edit", (EntryEditRequest request, WaypointServer server) =>
        {
            Lists(server).EditEntry(request.Entry, request.Text);
            return Results.NoContent();
        });
        api.MapPost("/lists/entries/delete", (EntryRequest request, WaypointServer server) =>
        {
            Lists(server).DeleteEntry(request.Entry, DateTime.Now);
            return Results.NoContent();
        });
        api.MapPost("/lists/entries/restore", (EntryRequest request, WaypointServer server) =>
        {
            Lists(server).RestoreEntry(request.Entry, DateTime.Now);
            return Results.NoContent();
        });
    }
}
