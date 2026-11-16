using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Remote;

/// <summary>
/// "This didn't help" clicks, kept where the rest of the data is: this PC's database in local mode, and the server's
/// in server mode, so the ones pressed on Discord and on the PC end up in one list.
/// </summary>
public interface IFeedbackData
{
    /// <summary>Keeps the click straight away and returns its ID, so a note can be added after.</summary>
    long AddFeedback(string feature, string? itemRef, DateTime now);

    /// <summary>Adds the optional one-line note to a kept click.</summary>
    void NoteFeedback(long id, string? note);

    /// <summary>Newest first.</summary>
    IReadOnlyList<FeedbackEntry> GetFeedback();

    void ClearFeedback();

    /// <summary>Puts back what Clear all took, for its Undo.</summary>
    void RestoreFeedback(IReadOnlyList<FeedbackEntry> entries);
}

public sealed class LocalFeedbackData(WaypointDatabase database) : IFeedbackData
{
    public long AddFeedback(string feature, string? itemRef, DateTime now) => database.AddFeedback(feature, itemRef, null, now);

    public void NoteFeedback(long id, string? note) => database.NoteFeedback(id, note);

    public IReadOnlyList<FeedbackEntry> GetFeedback() => database.GetFeedback();

    public void ClearFeedback() => database.ClearFeedback();

    public void RestoreFeedback(IReadOnlyList<FeedbackEntry> entries) => database.RestoreFeedback(entries);
}

public sealed record FeedbackRequest(string Feature, string? ItemRef, string? Note);

/// <summary>The kept click's ID, or how many were cleared.</summary>
public sealed record FeedbackKept(long Value);

/// <summary>The server's list. A click while it's away isn't kept, the same as any other change.</summary>
public sealed class ServerFeedbackData(ServerClient server) : IFeedbackData
{
    public const string Path = "api/feedback";

    public long AddFeedback(string feature, string? itemRef, DateTime now) =>
        server.Send<FeedbackKept>(HttpMethod.Post, Path, new FeedbackRequest(feature, itemRef, null)).Value;

    public void NoteFeedback(long id, string? note) =>
        server.Send(HttpMethod.Put, $"{Path}/{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}/note", new TextValue(note));

    public IReadOnlyList<FeedbackEntry> GetFeedback() => server.GetOr<List<FeedbackEntry>>(Path, []);

    public void ClearFeedback() => server.Send(HttpMethod.Delete, Path);

    public void RestoreFeedback(IReadOnlyList<FeedbackEntry> entries) => server.Send(HttpMethod.Post, $"{Path}/restore", entries);
}
