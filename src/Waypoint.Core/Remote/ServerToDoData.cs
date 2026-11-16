using System.Globalization;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Remote;

/// <summary>
/// The To do page's calls, sent to the server. Reads show the last answer while it's away, and changes say they
/// can't be made yet. The server uses its own clock for "now", since it decides
/// reminders and history, so the times passed in here are only used by the local version.
/// </summary>
public sealed class ServerToDoData(ServerClient server) : IToDoData
{
    private static readonly HttpMethod Post = HttpMethod.Post;
    private static readonly HttpMethod Put = HttpMethod.Put;

    public string? GetPreference(string key) => server.GetOr($"api/preferences/{Uri.EscapeDataString(key)}", new PreferenceValue(null)).Value;

    public void SetPreference(string key, string value) => server.Send(Put, $"api/preferences/{Uri.EscapeDataString(key)}", new PreferenceValue(value));

    public IReadOnlyList<StoredItem> GetOpenItems() => server.GetOr<List<StoredItem>>("api/items/open", []);

    public IReadOnlyList<StoredItem> GetFinishedForList(DateTime now) => server.GetOr<List<StoredItem>>("api/items/finished", []);

    public IReadOnlyList<ToDoFilter> GetToDoFilters() => server.GetOr<List<ToDoFilter>>("api/filters", []);

    public IReadOnlyDictionary<long, ItemPlacement> GetItemPlacements() =>
        server.GetOr<List<PlacementEntry>>("api/placements", []).ToDictionary(entry => entry.ItemId, entry => entry.Placement);

    public void SetToDoFilterOrder(IReadOnlyList<long> ids) => server.Send(Put, "api/filters/order", new OrderRequest(ids));

    public bool RemoveToDoFilter(long id, DateTime now) => server.Send<FlagValue>(HttpMethod.Delete, $"api/filters/{id}").Value;

    public void RestoreToDoFilter(long id) => server.Send(Post, $"api/filters/{id}/restore");

    public FilterEdit RenameToDoFilter(long id, string name) => server.Send<FilterEdit>(Put, $"api/filters/{id}/name", new NameRequest(name));

    public FilterEdit CreateToDoFilter(string name) => server.Send<FilterEdit>(Post, "api/filters", new NameRequest(name));

    public HandAddResult AddByHand(HandAddedItem input, DateTime now) => server.Send<HandAddResult>(Post, "api/items", input);

    public SpendSummary GetSpendSummary(DateOnly today) => server.GetOr("api/money", new SpendSummary(null, null));

    public IReadOnlyList<WorkShift> GetShifts(DateOnly date) =>
        server.GetOr<List<WorkShift>>($"api/shifts/{date.ToString("O", CultureInfo.InvariantCulture)}", []);

    public string? GetActiveTaskTitle(DateTime now) => server.GetOr("api/activation/session", new TextValue(null)).Value;

    public SuggestionHabits GetSuggestionHabits(DateTime now) => server.GetOr("api/suggestion-habits", SuggestionHabits.Defaults);

    public OpReply SaveSpotByHand(string? project, string? stopped, string? next, IReadOnlyList<string>? links, IReadOnlyList<string>? thoughts, DateTime now) =>
        server.Send<OpReply>(Post, "api/spot", new SpotRequest(project, stopped, next, links, thoughts));

    public OpReply StartActivation(string? taskRef, string method, DateTime now, string? firstAction = null) =>
        server.Send<OpReply>(Post, "api/activation/start", new StartRequest(taskRef, method, firstAction));

    public OpReply StartSmaller(StoredItem item, string method, DateTime now) => server.Send<OpReply>(Post, "api/activation/smaller", new SmallerRequest(item, method));

    public void SetDone(StoredItem item, bool done) => server.Send(Post, "api/items/done", new DoneRequest(item, done));

    public bool IsMovedFromFeed(StoredItem item) => server.Send<FlagValue>(Post, "api/items/moved", new ItemRequest(item)).Value;

    public DeletedItem? DeleteItem(StoredItem item, DateTime now) => server.Send<DeletedItem?>(Post, "api/items/delete", new ItemRequest(item));

    public void UndoDelete(DeletedItem deleted, DateTime now) => server.Send(Post, "api/items/undelete", deleted);

    public bool AssignToFilter(StoredItem item, long? filterId) => server.Send<FlagValue>(Post, "api/items/filter", new AssignRequest(item, filterId)).Value;

    public AppliedChange RescheduleItem(StoredItem item, DateOnly date, TimeOnly? time, DateTime now) =>
        server.Send<AppliedChange>(Post, "api/items/reschedule", new RescheduleRequest(item, date, time));

    public AppliedChange ChangeItem(ItemChange change, DateTime now) => server.Send<AppliedChange>(Post, "api/items/change", change);

    public IReadOnlyList<WaitingItem> GetWaiting() => server.GetOr<List<WaitingItem>>("api/waiting", []);

    /// <summary>The same rule as the database's, worked out here from the waits, so it needs no call of its own.</summary>
    public IReadOnlyList<StoredItem> WithoutQuietWaiting(IEnumerable<StoredItem> items, DateOnly today)
    {
        var quiet = GetWaiting().Where(waiting => !waiting.IsDue(today)).Select(waiting => waiting.ItemId).ToHashSet();
        return items.Where(item => item.Source != ItemSource.Capture || !quiet.Contains(item.Id)).ToList();
    }

    public HandAddResult AddWaitingByHand(string who, string? topic, DateOnly? followUp, string? email, DateTime now) =>
        server.Send<HandAddResult>(Post, "api/waiting", new WaitingRequest(who, topic, followUp, email));

    public string? MarkWaitingByHand(StoredItem item, string who, DateOnly? followUp, string? email, DateTime now) =>
        server.Send<TextValue>(Post, "api/waiting/mark", new MarkWaitingRequest(item, who, followUp, email)).Value;

    public OpReply StillWaiting(long? itemId, DateOnly? until, DateTime now) =>
        server.Send<OpReply>(Post, "api/waiting/still", new StillWaitingRequest(itemId, until));

    public bool StopWaiting(long itemId) => server.Send<FlagValue>(HttpMethod.Delete, $"api/waiting/{itemId}").Value;
}
