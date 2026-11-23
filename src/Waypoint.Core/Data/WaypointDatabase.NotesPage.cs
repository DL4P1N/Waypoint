using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

/// <summary>What the Notes page reads besides notes and folders: saved spots and the classes a folder can be made for.</summary>
public sealed partial class WaypointDatabase
{
    /// <summary>
    /// Saved spots, newest first, for the Notes page's read-only Saved spots entry. Every word of
    /// <paramref name="words"/> has to appear somewhere in the spot: project, where it stopped, next step, links, or thoughts.
    /// </summary>
    public IReadOnlyList<Handoff> GetSavedSpots(string? words = null)
    {
        var spots = GetHandoffs("1 = 1 ORDER BY id DESC");
        var terms = (words ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return spots;
        }

        return spots
            .Where(spot =>
            {
                var text = string.Join('\n', new[] { spot.Project, spot.Stopped ?? "", spot.Next ?? "" }.Concat(spot.Links).Concat(spot.Thoughts));
                return terms.All(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();
    }

    /// <summary>Classes a notes folder can be made for: the schedule in settings, plus courses from the feed and items. Sorted by name.</summary>
    public IReadOnlyList<string> GetClassNames()
    {
        using var lease = Connect(out var connection);
        return ActiveCourses(connection).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
