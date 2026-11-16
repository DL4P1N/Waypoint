using Waypoint.Core.Schedule;

namespace Waypoint.Core.Remote;

/// <summary>What the PC tells the server during the late window: seconds since its last keyboard or mouse input, and nothing else.</summary>
public sealed record IdleReport(int IdleSeconds);

/// <summary>
/// On the PC whenever a server address is set. A reading goes out about once a minute, only from a few minutes before
/// bedtime until morning on nights the server could send check-ins, and only with check-ins turned on.
/// </summary>
public sealed class IdleReporter(BedtimeSchedule schedule, IUserIdle idle)
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    private DateTime? _last;

    /// <summary>The reading to send now, or null. The idle time isn't read outside the window.</summary>
    public IdleReport? Due(DateTime now)
    {
        if (LateCheckIns.Window(schedule, now, LateCheckIns.ReportLead) is null || (_last is { } last && now >= last && now - last < Every))
        {
            return null;
        }

        if (idle.IdleFor(now) is not { } idleFor)
        {
            return null;
        }

        _last = now;
        return new IdleReport((int)Math.Min(idleFor.TotalSeconds, int.MaxValue));
    }
}

/// <summary>
/// On the server, the PC's last reading, kept in memory only. Without one in the last few minutes the PC counts as
/// idle, so a PC that's off or asleep gets no check-ins.
/// </summary>
public sealed class ReportedIdle : IUserIdle
{
    public static readonly TimeSpan GoneAfter = TimeSpan.FromMinutes(3);

    private readonly object _lock = new();
    private (TimeSpan Idle, DateTime At)? _last;

    public void Report(IdleReport report, DateTime at)
    {
        lock (_lock)
        {
            _last = (TimeSpan.FromSeconds(Math.Max(0, report.IdleSeconds)), at);
        }
    }

    public TimeSpan? IdleFor(DateTime now)
    {
        lock (_lock)
        {
            return _last is { } last && now >= last.At && now - last.At < GoneAfter ? last.Idle + (now - last.At) : null;
        }
    }
}
