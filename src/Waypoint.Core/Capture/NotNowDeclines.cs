using System.Globalization;
using Waypoint.Core.Background;

namespace Waypoint.Core.Capture;

/// <summary>
/// Items just turned down with Not now, on the session card or a reminder. For one snooze window afterwards the
/// suggestion and the energy buttons pass over them, so the next thing offered isn't the one just declined.
/// They're kept as a preference so the PC sees the same list in server mode.
/// </summary>
public static class NotNowDeclines
{
    public const string Preference = "not_now_declined";

    /// <summary>The same as a reminder's snooze.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(WaypointWorker.DefaultSnoozeMinutes);

    private const string Format = "yyyy-MM-ddTHH:mm:ss";

    /// <summary>The refs declined less than <see cref="Window"/> ago.</summary>
    public static IReadOnlySet<string> Read(string? stored, DateTime now) =>
        Entries(stored, now).Select(entry => entry.Ref).ToHashSet(StringComparer.Ordinal);

    /// <summary>The stored text with one more ref declined now. Anything past its window is dropped.</summary>
    public static string Add(string? stored, string itemRef, DateTime now)
    {
        var kept = Entries(stored, now).Where(entry => entry.Ref != itemRef).Append((Ref: itemRef, Until: now + Window));
        return string.Join('\n', kept.Select(entry => $"{entry.Until.ToString(Format, CultureInfo.InvariantCulture)}|{entry.Ref}"));
    }

    /// <summary>
    /// The declined refs with the parent of any declined step added, since a session started from a step is
    /// declined for its whole task. A step under a declined parent is passed over through its parent's ref.
    /// </summary>
    public static IReadOnlySet<string> WithParents(IReadOnlySet<string> declined, IEnumerable<StoredItem> open)
    {
        if (declined.Count == 0)
        {
            return declined;
        }

        var all = declined.ToHashSet(StringComparer.Ordinal);
        foreach (var item in open)
        {
            if (item.ParentRef is { } parent && declined.Contains(ItemRef.Format(item)))
            {
                all.Add(parent);
            }
        }

        return all;
    }

    private static IEnumerable<(string Ref, DateTime Until)> Entries(string? stored, DateTime now)
    {
        foreach (var line in (stored ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|', 2);
            if (parts.Length == 2
                && DateTime.TryParseExact(parts[0], Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var until)
                && until > now
                && parts[1].Length > 0)
            {
                yield return (parts[1], until);
            }
        }
    }
}
