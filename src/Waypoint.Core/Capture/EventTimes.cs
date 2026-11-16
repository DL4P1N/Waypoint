namespace Waypoint.Core.Capture;

/// <summary>When an event ends. Typed and hand-added events share this, so they get the same default.</summary>
public static class EventTimes
{
    public static readonly TimeSpan DefaultLength = TimeSpan.FromHours(1);

    /// <summary>An event with a start and no end runs an hour. Late starts wrap past midnight. Other kinds have no end unless one is given.</summary>
    public static TimeOnly? EndOf(SortedItem item) =>
        item.DueTime is not { } start ? null
        : item.EndTime is { } end ? end
        : item.Kind == ItemKind.Event ? start.Add(DefaultLength)
        : null;
}
