using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Remote;

/// <summary>Saved choices such as which filter is open. Pages that only need these take this, not a database.</summary>
public interface IPreferences
{
    string? GetPreference(string key);

    void SetPreference(string key, string value);
}

/// <summary>
/// Everything the To do page reads and changes. Local mode passes straight to this PC's database. Server mode
/// sends the same calls to the server, so the page works the same either way.
/// </summary>
public interface IToDoData : IPreferences
{
    IReadOnlyList<StoredItem> GetOpenItems();

    IReadOnlyList<StoredItem> GetFinishedForList(DateTime now);

    IReadOnlyList<ToDoFilter> GetToDoFilters();

    IReadOnlyDictionary<long, ItemPlacement> GetItemPlacements();

    void SetToDoFilterOrder(IReadOnlyList<long> ids);

    bool RemoveToDoFilter(long id, DateTime now);

    void RestoreToDoFilter(long id);

    FilterEdit RenameToDoFilter(long id, string name);

    FilterEdit CreateToDoFilter(string name);

    HandAddResult AddByHand(HandAddedItem input, DateTime now);

    SpendSummary GetSpendSummary(DateOnly today);

    IReadOnlyList<WorkShift> GetShifts(DateOnly date);

    string? GetActiveTaskTitle(DateTime now);

    /// <summary>What the suggestion has learned from history, or the starting guesses when there's too little.</summary>
    SuggestionHabits GetSuggestionHabits(DateTime now);

    OpReply SaveSpotByHand(string? project, string? stopped, string? next, IReadOnlyList<string>? links, IReadOnlyList<string>? thoughts, DateTime now);

    OpReply StartActivation(string? taskRef, string method, DateTime now, string? firstAction = null);

    OpReply StartSmaller(StoredItem item, string method, DateTime now);

    void SetDone(StoredItem item, bool done);

    bool IsMovedFromFeed(StoredItem item);

    DeletedItem? DeleteItem(StoredItem item, DateTime now);

    void UndoDelete(DeletedItem deleted, DateTime now);

    bool AssignToFilter(StoredItem item, long? filterId);

    AppliedChange RescheduleItem(StoredItem item, DateOnly date, TimeOnly? time, DateTime now);

    AppliedChange ChangeItem(ItemChange change, DateTime now);

    IReadOnlyList<WaitingItem> GetWaiting();

    /// <summary>Takes out waits whose follow-up day hasn't come, for anything that picks what to do next.</summary>
    IReadOnlyList<StoredItem> WithoutQuietWaiting(IEnumerable<StoredItem> items, DateOnly today);

    HandAddResult AddWaitingByHand(string who, string? topic, DateOnly? followUp, string? email, DateTime now);

    string? MarkWaitingByHand(StoredItem item, string who, DateOnly? followUp, string? email, DateTime now);

    OpReply StillWaiting(long? itemId, DateOnly? until, DateTime now);

    bool StopWaiting(long itemId);
}

/// <summary>Today's behavior: every call goes to the database on this PC.</summary>
public sealed class LocalToDoData(WaypointDatabase database) : IToDoData
{
    public string? GetPreference(string key) => database.GetPreference(key);

    public void SetPreference(string key, string value) => database.SetPreference(key, value);

    public IReadOnlyList<StoredItem> GetOpenItems() => database.GetOpenItems();

    public IReadOnlyList<StoredItem> GetFinishedForList(DateTime now) => database.GetFinishedForList(now);

    public IReadOnlyList<ToDoFilter> GetToDoFilters() => database.GetToDoFilters();

    public IReadOnlyDictionary<long, ItemPlacement> GetItemPlacements() => database.GetItemPlacements();

    public void SetToDoFilterOrder(IReadOnlyList<long> ids) => database.SetToDoFilterOrder(ids);

    public bool RemoveToDoFilter(long id, DateTime now) => database.RemoveToDoFilter(id, now);

    public void RestoreToDoFilter(long id) => database.RestoreToDoFilter(id);

    public FilterEdit RenameToDoFilter(long id, string name) => database.RenameToDoFilter(id, name);

    public FilterEdit CreateToDoFilter(string name) => database.CreateToDoFilter(name);

    public HandAddResult AddByHand(HandAddedItem input, DateTime now) => database.AddByHand(input, now);

    public SpendSummary GetSpendSummary(DateOnly today) => ReadSpendSummary(database, today);

    public IReadOnlyList<WorkShift> GetShifts(DateOnly date) => database.GetShifts(date);

    public string? GetActiveTaskTitle(DateTime now) => database.GetActiveSession(now)?.TaskTitle;

    public SuggestionHabits GetSuggestionHabits(DateTime now) => database.GetSuggestionHabits(now);

    public OpReply SaveSpotByHand(string? project, string? stopped, string? next, IReadOnlyList<string>? links, IReadOnlyList<string>? thoughts, DateTime now) =>
        database.SaveSpotByHand(project, stopped, next, links, thoughts, now);

    public OpReply StartActivation(string? taskRef, string method, DateTime now, string? firstAction = null) =>
        database.StartActivation(taskRef, firstAction, null, method, now);

    public OpReply StartSmaller(StoredItem item, string method, DateTime now) => database.StartSmaller(item, method, now);

    public void SetDone(StoredItem item, bool done) => database.SetDone(item, done);

    public bool IsMovedFromFeed(StoredItem item) => database.IsMovedFromFeed(item);

    public DeletedItem? DeleteItem(StoredItem item, DateTime now) => database.DeleteItem(item, now);

    public void UndoDelete(DeletedItem deleted, DateTime now) => database.UndoDelete(deleted, now);

    public bool AssignToFilter(StoredItem item, long? filterId) => database.AssignToFilter(item, filterId);

    public AppliedChange RescheduleItem(StoredItem item, DateOnly date, TimeOnly? time, DateTime now) => database.RescheduleItem(item, date, time, now);

    public AppliedChange ChangeItem(ItemChange change, DateTime now) => database.ChangeItem(change, now);

    public IReadOnlyList<WaitingItem> GetWaiting() => database.GetWaiting();

    public IReadOnlyList<StoredItem> WithoutQuietWaiting(IEnumerable<StoredItem> items, DateOnly today) => database.WithoutQuietWaiting(items, today);

    public HandAddResult AddWaitingByHand(string who, string? topic, DateOnly? followUp, string? email, DateTime now) =>
        database.AddWaitingByHand(who, topic, followUp, email, now);

    public string? MarkWaitingByHand(StoredItem item, string who, DateOnly? followUp, string? email, DateTime now) =>
        database.MarkWaitingByHand(item, who, followUp, email, now);

    public OpReply StillWaiting(long? itemId, DateOnly? until, DateTime now) => database.StillWaiting(itemId, until, now);

    public bool StopWaiting(long itemId) => database.StopWaiting(itemId);

    /// <summary>Safe to spend and the first budget line that needs a look, the same on both ends.</summary>
    public static SpendSummary ReadSpendSummary(WaypointDatabase database, DateOnly today) =>
        new(
            database.GetSafeToSpend(today),
            BudgetReport.Attention(database.GetBudgetReport(today)).Select(BudgetReport.ShortAttention).FirstOrDefault(),
            database.GetSafeToSpendReason(today));
}
