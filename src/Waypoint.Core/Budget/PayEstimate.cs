using Waypoint.Core.Schedule;

namespace Waypoint.Core.Budget;

/// <summary>
/// Estimates each paycheck from the work shifts in its pay period and the hourly rate, so a short or long
/// stretch of work shows up in the forecast instead of the same flat amount every time.
/// </summary>
/// <remarks>
/// About 14% of gross pay is assumed to be withheld, which fits federal and state withholding plus 7.65% for
/// Social Security and Medicare on hourly pay. That share is an assumption, not a tax calculation, and
/// a pay stub is the way to check it.
/// </remarks>
public static class PayEstimate
{
    public const decimal DefaultRate = 20.00m;

    /// <summary>What's left of gross pay after withholding, as a rough share.</summary>
    public const decimal TakeHomeShare = 0.86m;

    /// <summary>
    /// A pay period closes about a week before its payday, so a shift doesn't count toward a check that's already
    /// been cut.
    /// </summary>
    public const int PeriodLagDays = 7;

    private const int LongestGapDays = 62;

    /// <summary>Hours in a shift. One that ends before it starts runs past midnight.</summary>
    public static decimal Hours(WorkShift shift)
    {
        var length = shift.End.ToTimeSpan() - shift.Start.ToTimeSpan();
        return (decimal)(length < TimeSpan.Zero ? length + TimeSpan.FromDays(1) : length).TotalHours;
    }

    /// <summary>Take-home pay for these hours, in cents, or null with no hours to go on.</summary>
    public static long? Cents(decimal hours, decimal rate) =>
        hours > 0 && rate > 0 ? (long)Math.Round(hours * rate * TakeHomeShare * 100m, MidpointRounding.AwayFromZero) : null;

    /// <summary>
    /// The work days a payday pays for: from the day after the previous payday's period closed through this one's
    /// close. Null if there's no earlier payday to measure from.
    /// </summary>
    public static (DateOnly From, DateOnly Through)? Period(ScheduledItem pay, DateOnly payday)
    {
        for (var day = payday.AddDays(-1); day >= payday.AddDays(-LongestGapDays); day = day.AddDays(-1))
        {
            if (BudgetMath.Falls(pay, day))
            {
                return (day.AddDays(1 - PeriodLagDays), payday.AddDays(-PeriodLagDays));
            }
        }

        return null;
    }

    /// <summary>
    /// How a payday's estimate was worked out, to check against a pay stub: the days it pays for, the hours on the
    /// schedule then, the rate, and the take-home share, which is a guess. Null if there's no period or no shifts.
    /// </summary>
    public static string? Explain(ScheduledItem pay, DateOnly payday, decimal rate, Func<DateOnly, IReadOnlyList<WorkShift>> shiftsOn)
    {
        if (Period(pay, payday) is not { } period)
        {
            return null;
        }

        var hours = 0m;
        for (var day = period.From; day <= period.Through; day = day.AddDays(1))
        {
            hours += shiftsOn(day).Sum(Hours);
        }

        if (hours <= 0)
        {
            return null;
        }

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return string.Format(
            culture,
            "It pays for {0:MMM d} to {1:MMM d}: {2:0.##} hours on your schedule × ${3:0.00} × {4:0%} take-home, which is a rough guess at withholding. If a pay stub says otherwise, the hours or the share are off.",
            period.From,
            period.Through,
            hours,
            rate,
            TakeHomeShare);
    }

    /// <summary>
    /// The paycheck with an estimate for each payday in the next <paramref name="days"/> days whose period has
    /// shifts. A payday with none keeps the flat amount, since nothing better is known.
    /// </summary>
    public static ScheduledItem WithEstimates(ScheduledItem pay, DateOnly today, int days, decimal rate, Func<DateOnly, IReadOnlyList<WorkShift>> shiftsOn)
    {
        var amounts = new Dictionary<DateOnly, long>();
        for (var offset = 0; offset <= days; offset++)
        {
            var payday = today.AddDays(offset);
            if (!BudgetMath.Falls(pay, payday) || Period(pay, payday) is not { } period)
            {
                continue;
            }

            var hours = 0m;
            for (var day = period.From; day <= period.Through; day = day.AddDays(1))
            {
                hours += shiftsOn(day).Sum(Hours);
            }

            if (Cents(hours, rate) is { } cents)
            {
                amounts[payday] = cents;
            }
        }

        return amounts.Count > 0 ? pay with { Amounts = amounts } : pay;
    }
}
