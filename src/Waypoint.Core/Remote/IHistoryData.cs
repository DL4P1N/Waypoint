using System.Globalization;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Remote;

/// <summary>A History row and whether it can be put back, read together so the page needs one call, not one per row.</summary>
public sealed record HistoryRow(HistoryEntry Entry, bool CanReopen);

/// <summary>
/// Everything the History page reads and changes. Local mode passes straight to this PC's database, and server
/// mode sends the same calls to the server.
/// </summary>
public interface IHistoryData : IPreferences
{
    IReadOnlyList<HistoryRow> SearchHistory(string? words, int limit, HistoryFilter filter);

    DeletedItem? DeleteHistoryItem(HistoryEntry entry, DateTime now);

    void UndoDelete(DeletedItem deleted, DateTime now);

    bool ReopenEntry(HistoryEntry entry);
}

/// <summary>Today's behavior: every call goes to the database on this PC. The server answers with this too.</summary>
public sealed class LocalHistoryData(WaypointDatabase database) : IHistoryData
{
    public string? GetPreference(string key) => database.GetPreference(key);

    public void SetPreference(string key, string value) => database.SetPreference(key, value);

    public IReadOnlyList<HistoryRow> SearchHistory(string? words, int limit, HistoryFilter filter) =>
        database.SearchHistory(words, limit, filter).Select(entry => new HistoryRow(entry, database.CanReopen(entry))).ToList();

    public DeletedItem? DeleteHistoryItem(HistoryEntry entry, DateTime now) => database.DeleteHistoryItem(entry, now);

    public void UndoDelete(DeletedItem deleted, DateTime now) => database.UndoDelete(deleted, now);

    public bool ReopenEntry(HistoryEntry entry) => database.ReopenEntry(entry);
}

/// <summary>
/// The History page's calls, sent to the server. Each search is kept, so while the server is away the page
/// shows what that search last found, and changes say they can't be made yet.
/// </summary>
public sealed class ServerHistoryData(ServerClient server) : IHistoryData
{
    private static readonly HttpMethod Post = HttpMethod.Post;

    public string? GetPreference(string key) => server.GetOr($"api/preferences/{Uri.EscapeDataString(key)}", new PreferenceValue(null)).Value;

    public void SetPreference(string key, string value) => server.Send(HttpMethod.Put, $"api/preferences/{Uri.EscapeDataString(key)}", new PreferenceValue(value));

    public IReadOnlyList<HistoryRow> SearchHistory(string? words, int limit, HistoryFilter filter) =>
        server.GetOr<List<HistoryRow>>(
            $"api/history?filter={filter}&limit={limit.ToString(CultureInfo.InvariantCulture)}&words={Uri.EscapeDataString(words?.Trim() ?? "")}",
            []);

    public DeletedItem? DeleteHistoryItem(HistoryEntry entry, DateTime now) => server.Send<DeletedItem?>(Post, "api/history/delete", new HistoryEntryRequest(entry));

    public void UndoDelete(DeletedItem deleted, DateTime now) => server.Send(Post, "api/items/undelete", deleted);

    public bool ReopenEntry(HistoryEntry entry) => server.Send<FlagValue>(Post, "api/history/reopen", new HistoryEntryRequest(entry)).Value;
}

public sealed record HistoryEntryRequest(HistoryEntry Entry);
