namespace Waypoint.Core.Schedule;

public enum ClassAlertStage
{
    Warning,
    LeaveNow,
}

public sealed record ClassAlert(ClassAlertStage Stage, ClassOccurrence Class, DateTime LeaveAt);

/// <summary>
/// Decides when to alert about the next class: one warning before it's time to leave,
/// then repeated interrupts until the alert is acknowledged or the class starts.
/// </summary>
public sealed class ClassAlertTracker(ClassSchedule schedule)
{
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromMinutes(2);

    private readonly HashSet<ClassOccurrence> _warned = [];
    private readonly HashSet<ClassOccurrence> _acknowledged = [];
    private readonly Dictionary<ClassOccurrence, DateTime> _lastInterrupt = [];

    /// <summary>Returns the alert to show right now, if any.</summary>
    public ClassAlert? Check(DateTime now)
    {
        if (schedule.GetNextClass(now) is not { } next || _acknowledged.Contains(next))
        {
            return null;
        }

        var leaveAt = next.Start.AddMinutes(-next.Meeting.TravelMinutes);

        if (now >= leaveAt)
        {
            if (_lastInterrupt.TryGetValue(next, out var last) && now - last < RepeatInterval)
            {
                return null;
            }

            _lastInterrupt[next] = now;
            return new ClassAlert(ClassAlertStage.LeaveNow, next, leaveAt);
        }

        // A warning during another class is just noise, since that class ending is the cue.
        if (now >= leaveAt.AddMinutes(-schedule.FirstWarningMinutes) && !schedule.IsInClass(now) && _warned.Add(next))
        {
            return new ClassAlert(ClassAlertStage.Warning, next, leaveAt);
        }

        return null;
    }

    public void Acknowledge(ClassOccurrence occurrence) => _acknowledged.Add(occurrence);
}
