using Waypoint.Core.Data;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Capture;

/// <summary>
/// Plans reminders for feed assignments as they come into view. Events that can't be
/// planned yet stay unplanned and are tried again after the next sync.
/// </summary>
public sealed class AssignmentNudges(WaypointDatabase database, INudgePlanner planner, ClassSchedule? schedule = null)
{
    private const int DaysAhead = 14;
    private const int MaxPerRun = 10;

    private static readonly TimeOnly EndOfDay = new(23, 59);

    private readonly SemaphoreSlim _planning = new(1, 1);

    public string? LastError { get; private set; }

    /// <summary>Raised after a planning run finishes, possibly on a background thread.</summary>
    public event Action? Changed;

    public async Task PlanAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        if (!await _planning.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var today = DateOnly.FromDateTime(now);
            var events = database.GetEventsNeedingNudges(today, today.AddDays(DaysAhead), MaxPerRun);
            if (events.Count > 0 && await planner.PlanAsync(events, now, schedule, cancellationToken) is { } plans)
            {
                database.SaveNudges(events.Select(item => Trim(item, plans, now)).ToList());
            }

            LastError = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastError = exception.Message;
        }
        finally
        {
            _planning.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>Goes through the events rather than the reply, so one the planner missed still gets a nudge.</summary>
    private static EventNudges Trim(StoredItem item, IReadOnlyList<EventNudges> plans, DateTime now)
    {
        var due = item.DueAt?.LocalDateTime ?? item.DueDate!.Value.ToDateTime(EndOfDay);
        var planned = plans.FirstOrDefault(plan => plan.EventId == item.Id)?.Reminders;
        return new EventNudges(item.Id, ReminderPlan.ForAssignment(due, planned, now));
    }
}
