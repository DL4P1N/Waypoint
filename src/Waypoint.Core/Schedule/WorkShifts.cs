namespace Waypoint.Core.Schedule;

/// <summary>One stretch of work on a day, in local time.</summary>
public sealed record WorkShift(DateOnly Date, TimeOnly Start, TimeOnly End, int CommuteMinutes = WorkShift.DefaultCommuteMinutes)
{
    public const int DefaultCommuteMinutes = 15;

    private static readonly TimeOnly Noon = new(12, 0);

    public DateTime Starts => Date.ToDateTime(Start);

    public DateTime LeaveAt => Starts.AddMinutes(-CommuteMinutes);

    /// <summary>Working from home: no commute, so there is nothing to leave for.</summary>
    public bool IsRemote => CommuteMinutes == 0;

    public bool IsMorning => Start < Noon;
}
