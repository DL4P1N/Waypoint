using Waypoint.Core.Data;
using Waypoint.Core.Remote;

namespace Waypoint.Server;

/// <summary>
/// Changes the PC sends with a key of its own, in <see cref="ServerClient.OnceHeader"/>, so the same one sent again
/// after its answer was lost is answered as before instead of done twice. One without a key, from a PC from before,
/// is done as it always was.
/// </summary>
public static class SentOnce
{
    /// <summary>Longer than any key a PC makes, which is its own id and a new one.</summary>
    private const int MaxKey = 200;

    /// <summary>The key the change was sent with, or null for none.</summary>
    public static string? Key(HttpContext context) =>
        context.Request.Headers[ServerClient.OnceHeader].ToString().Trim() is { Length: > 0 and <= MaxKey } key ? key : null;

    /// <summary>Does <paramref name="work"/> once per key, on the database it's handed, which is the write keeping the key.</summary>
    public static T Run<T>(HttpContext context, WaypointDatabase database, Func<WaypointDatabase, T> work) =>
        Key(context) is { } key ? database.Once(key, DateTime.Now, work) : work(database);
}
