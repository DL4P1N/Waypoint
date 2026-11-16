using Waypoint.Core.Schedule;

namespace Waypoint.Core.Capture;

public interface ICaptureSorter
{
    /// <summary>
    /// Reads a capture into new items, changes to existing items, and any answer.
    /// <paramref name="writtenAt"/> is when the capture was written, so "friday" still
    /// means the right day if sorting happens later.
    /// Returns null if the sorter declined, which leaves the capture pending.
    /// </summary>
    Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default);
}

public interface INudgePlanner
{
    /// <summary>
    /// Plans reminders for upcoming calendar events. Returns null if the planner declined,
    /// which leaves the events to be tried again later.
    /// </summary>
    Task<IReadOnlyList<EventNudges>?> PlanAsync(IReadOnlyList<StoredItem> events, DateTime now, ClassSchedule? schedule, CancellationToken cancellationToken = default);
}
