using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Remote;

/// <summary>
/// Everything the Lists page reads and changes, and its saved choices such as which list is open. Local mode passes
/// straight to this PC's database, and server mode sends the same calls to the server, so the page works the same
/// either way.
/// </summary>
public interface IListsData : IPreferences
{
    /// <summary>Every list, with Shopping always first even when it's empty.</summary>
    IReadOnlyList<ListOverview> GetListOverview();

    ListEdit CreateList(string name, DateTime now);

    ListEdit RenameList(string name, string newName, DateTime now);

    /// <summary>Saves the lists' order from the Lists page. Shopping stays first whatever is sent.</summary>
    void OrderLists(IReadOnlyList<string> names);

    string DeleteList(string name, DateTime now);

    void AddListEntry(string list, string text, DateTime now);

    void SetEntryDone(ListEntry entry, bool done, DateTime now);

    void EditEntry(ListEntry entry, string text);

    void DeleteEntry(ListEntry entry, DateTime now);

    void RestoreEntry(ListEntry entry, DateTime now);

    /// <summary>Unchecks every entry so a routine or checklist can be run again, and says what to start with.</summary>
    string StartList(string name);
}

/// <summary>Today's behavior: every call goes to the database on this PC. The server answers with this too.</summary>
public sealed class LocalListsData(WaypointDatabase database) : IListsData
{
    public string? GetPreference(string key) => database.GetPreference(key);

    public void SetPreference(string key, string value) => database.SetPreference(key, value);

    public IReadOnlyList<ListOverview> GetListOverview() => database.GetListOverview(includeEmptyShopping: true);

    public ListEdit CreateList(string name, DateTime now) => database.CreateList(name, now);

    public ListEdit RenameList(string name, string newName, DateTime now) => database.RenameList(name, newName, now);

    public void OrderLists(IReadOnlyList<string> names) => database.OrderLists(names);

    public string DeleteList(string name, DateTime now) => database.DeleteList(name, now);

    public void AddListEntry(string list, string text, DateTime now) => database.AddListEntry(list, text, now);

    public void SetEntryDone(ListEntry entry, bool done, DateTime now) => database.SetEntryDone(entry, done, now);

    public void EditEntry(ListEntry entry, string text) => database.EditEntry(entry, text);

    public void DeleteEntry(ListEntry entry, DateTime now) => database.DeleteEntry(entry, now);

    public void RestoreEntry(ListEntry entry, DateTime now) => database.RestoreEntry(entry, now);

    public string StartList(string name) => database.StartList(name);
}

/// <summary>
/// The Lists page's calls, sent to the server. Reads show the last answer while it's away, and changes say they
/// can't be made yet. The server uses its own clock, so the times passed in here are only used locally.
/// </summary>
public sealed class ServerListsData(ServerClient server) : IListsData
{
    private static readonly HttpMethod Post = HttpMethod.Post;

    public string? GetPreference(string key) => server.GetOr($"api/preferences/{Uri.EscapeDataString(key)}", new PreferenceValue(null)).Value;

    public void SetPreference(string key, string value) => server.Send(HttpMethod.Put, $"api/preferences/{Uri.EscapeDataString(key)}", new PreferenceValue(value));

    /// <summary>With nothing read yet and no server, Shopping is still there, empty, so the page has a list to show.</summary>
    public IReadOnlyList<ListOverview> GetListOverview() =>
        server.GetOr<List<ListOverview>>("api/lists", [new ListOverview(WaypointDatabase.ShoppingList, [])]);

    public ListEdit CreateList(string name, DateTime now) => server.Send<ListEdit>(Post, "api/lists", new NameRequest(name));

    public ListEdit RenameList(string name, string newName, DateTime now) => server.Send<ListEdit>(Post, "api/lists/rename", new ListRenameRequest(name, newName));

    public void OrderLists(IReadOnlyList<string> names) => server.Send(Post, "api/lists/order", new ListOrderRequest(names.ToList()));

    public string DeleteList(string name, DateTime now) => server.Send<TextValue>(Post, "api/lists/delete", new NameRequest(name)).Value ?? "";

    public void AddListEntry(string list, string text, DateTime now) => server.Send(Post, "api/lists/entries", new ListEntryRequest(list, text));

    public void SetEntryDone(ListEntry entry, bool done, DateTime now) => server.Send(Post, "api/lists/entries/done", new EntryDoneRequest(entry, done));

    public void EditEntry(ListEntry entry, string text) => server.Send(Post, "api/lists/entries/edit", new EntryEditRequest(entry, text));

    public void DeleteEntry(ListEntry entry, DateTime now) => server.Send(Post, "api/lists/entries/delete", new EntryRequest(entry));

    public void RestoreEntry(ListEntry entry, DateTime now) => server.Send(Post, "api/lists/entries/restore", new EntryRequest(entry));

    public string StartList(string name) => server.Send<TextValue>(Post, "api/lists/start", new NameRequest(name)).Value ?? "";
}

public sealed record ListRenameRequest(string Name, string NewName);

public sealed record ListOrderRequest(List<string> Names);

public sealed record ListEntryRequest(string List, string Text);

public sealed record EntryRequest(ListEntry Entry);

public sealed record EntryDoneRequest(ListEntry Entry, bool Done);

public sealed record EntryEditRequest(ListEntry Entry, string Text);
