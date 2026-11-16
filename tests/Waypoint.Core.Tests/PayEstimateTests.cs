using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Tests;

/// <summary>Paychecks estimated from the shifts in each pay period at the hourly rate, with the flat amount as the fallback.</summary>
public sealed class PayEstimateTests : IDisposable
{
    // A Monday. Paydays are the 10th and 25th, earlier on a weekend, so Nov 10 and Nov 25.
    private static readonly DateOnly Today = new(2026, 11, 2);
    private static readonly ScheduledItem Paycheck = new("Paycheck", 85000, [10, 25], true);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-{Guid.NewGuid():N}.db");
    private readonly WaypointDatabase _database;

    public PayEstimateTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    /// <summary>Mondays and Wednesdays, 9 to 5.</summary>
    private static IReadOnlyList<WorkShift> MondaysAndWednesdays(DateOnly day) =>
        day.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Wednesday ? [new WorkShift(day, new TimeOnly(9, 0), new TimeOnly(17, 0))] : [];

    private void Seed(bool withHours = true)
    {
        _database.AddScheduledItem(Paycheck);
        _database.SaveBudgetAccount(new BudgetAccount("Northwind Bank checking", AccountKind.Checking, 50000, Today));
        if (withHours)
        {
            _database.SetWeeklyHours(["Monday", "Wednesday"], "09:00-17:00");
        }
    }

    private ScheduledItem ForecastPaycheck() => _database.GetForecastSchedule(Today).Single(item => item.Label == "Paycheck");

    [Fact]
    public void A_shift_past_midnight_still_counts_its_hours()
    {
        Assert.Equal(8m, PayEstimate.Hours(new WorkShift(Today, new TimeOnly(9, 0), new TimeOnly(17, 0))));
        Assert.Equal(6.5m, PayEstimate.Hours(new WorkShift(Today, new TimeOnly(20, 0), new TimeOnly(2, 30))));
    }

    [Fact]
    public void Take_home_pay_is_hours_times_the_rate_times_the_share()
    {
        // 40 hours at $20.00 is $800.00 before withholding, and 86% of that is $688.00.
        Assert.Equal(68800, PayEstimate.Cents(40m, 20.00m));
        Assert.Equal((long)Math.Round(12.5m * PayEstimate.DefaultRate * PayEstimate.TakeHomeShare * 100m), PayEstimate.Cents(12.5m, PayEstimate.DefaultRate));
        Assert.Null(PayEstimate.Cents(0, PayEstimate.DefaultRate));
    }

    [Fact]
    public void An_estimate_says_how_it_was_worked_out()
    {
        var how = PayEstimate.Explain(Paycheck, new DateOnly(2026, 11, 25), 20.00m, MondaysAndWednesdays);

        // Nov 4 to Nov 18 holds five Mondays and Wednesdays of 8 hours.
        Assert.Equal(
            "It pays for Nov 4 to Nov 18: 40 hours on your schedule × $20.00 × 86% take-home, which is a rough guess at withholding. If a pay stub says otherwise, the hours or the share are off.",
            how);
        Assert.Null(PayEstimate.Explain(Paycheck, new DateOnly(2026, 11, 25), 20.00m, _ => []));
    }

    [Fact]
    public void A_pay_period_closes_a_week_before_payday_and_starts_after_the_last_one_closed()
    {
        Assert.Equal((new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 18)), PayEstimate.Period(Paycheck, new DateOnly(2026, 11, 25)));
        Assert.Null(PayEstimate.Period(Paycheck with { Pattern = PayPattern.Once, Anchor = Today }, Today));
    }

    [Fact]
    public void Each_payday_is_estimated_from_the_shifts_in_its_period()
    {
        // Nov 4 to Nov 18 has two Mondays and three Wednesdays, so 40 hours, and $20.00 times 86% of that.
        var estimated = PayEstimate.WithEstimates(Paycheck, Today, BudgetMath.ForecastDays, 20.00m, MondaysAndWednesdays);

        Assert.Equal(68800, estimated.AmountOn(new DateOnly(2026, 11, 25)));
        Assert.Equal(85000, estimated.AmountOn(new DateOnly(2026, 11, 24)));
    }

    [Fact]
    public void A_period_with_no_shifts_keeps_the_flat_amount()
    {
        var estimated = PayEstimate.WithEstimates(Paycheck, Today, BudgetMath.ForecastDays, 20.00m, _ => []);

        Assert.Null(estimated.Amounts);
        Assert.Equal(85000, estimated.AmountOn(new DateOnly(2026, 11, 25)));
    }

    [Fact]
    public void The_forecast_uses_saved_work_hours_and_the_rate()
    {
        Seed();

        Assert.Equal(68800, ForecastPaycheck().AmountOn(new DateOnly(2026, 11, 25)));

        _database.SetHourlyRate(22m);
        Assert.Equal(75680, ForecastPaycheck().AmountOn(new DateOnly(2026, 11, 25)));
    }

    [Fact]
    public void A_day_off_lowers_that_paycheck()
    {
        Seed();
        _database.SetDayOff(new DateTime(2026, 11, 11));

        // 32 hours instead of 40.
        Assert.Equal(55040, ForecastPaycheck().AmountOn(new DateOnly(2026, 11, 25)));
    }

    [Fact]
    public void Without_work_hours_the_flat_paycheck_is_used()
    {
        Seed(withHours: false);

        var paycheck = ForecastPaycheck();
        Assert.Null(paycheck.Amounts);
        Assert.Contains("Paycheck $850.00 expected Nov 10", _database.CheckAffordable(1000, null, Today).Text);
    }

    [Fact]
    public void Can_I_afford_this_and_until_payday_use_the_estimate()
    {
        Seed();

        Assert.Contains("Paycheck $688.00 expected Nov 10", _database.CheckAffordable(1000, null, Today).Text);

        var section = _database.GetBudgetReport(Today).Single(report => report.Title == "UNTIL PAYDAY");
        Assert.Contains(section.Rows, row => row.Label == "Paycheck, expected Nov 10" && row.Value == "$688.00");
        Assert.Contains("from your shifts at $20.00 an hour", _database.DescribePay(Today));
    }

    [Fact]
    public void The_rate_starts_at_twenty_and_only_takes_a_real_amount()
    {
        Assert.Equal(20.00m, _database.GetHourlyRate());

        Assert.True(_database.SetHourlyRate(null).IsAnswer);
        Assert.True(_database.SetHourlyRate(0).IsAnswer);
        Assert.True(_database.SetHourlyRate(5000).IsAnswer);
        Assert.Equal(20.00m, _database.GetHourlyRate());
    }

    [Fact]
    public void I_make_twenty_two_an_hour_now_sets_the_rate()
    {
        var reply = AssistantOps.Apply(new AssistantOp(AssistantOpKind.PayRateSet, AmountCents: 2200), _database, 0, Today.ToDateTime(TimeOnly.MinValue));

        Assert.Equal(22m, _database.GetHourlyRate());
        Assert.Contains("$22.00", reply.Text);
        Assert.Contains("pay_rate_set", ClaudeCliSorter.Schema);
    }

    [Fact]
    public void A_deposit_still_matches_the_flat_amount_it_was_scheduled_with()
    {
        // Matching real deposits to paydays goes by the flat amount, so an estimate can't stop one from counting.
        Seed();

        Assert.Equal(new DateOnly(2026, 11, 10), BudgetMath.ExpectedNear(ForecastPaycheck(), new DateOnly(2026, 11, 10), 85000));
    }

    [Fact]
    public void A_short_paycheck_matches_its_estimate_and_isnt_still_expected()
    {
        // Five 8-hour shifts in the period make the Nov 10 check about $688.00, far under the flat $850.
        Seed();

        _database.ImportChecking([new CheckingRow(new DateOnly(2026, 11, 10), "ACME PAYROLL", 57200, CheckingKind.Other)]);

        var deposit = Assert.Single(_database.GetPayDeposits());
        Assert.Equal(new DateOnly(2026, 11, 10), deposit.DueOn);
        Assert.Equal(new DateOnly(2026, 11, 25), BudgetMath.NextOccurrence(ForecastPaycheck(), new DateOnly(2026, 11, 10), _database.GetPayDeposits()));
    }

    [Fact]
    public void I_got_paid_with_no_amount_counts_the_estimate()
    {
        Seed();

        _database.ConfirmPayTyped(null, new DateOnly(2026, 11, 10));

        Assert.Equal(68800, Assert.Single(_database.GetPayDeposits()).Cents);
    }

    [Fact]
    public void The_pay_line_leads_with_the_next_paycheck_and_where_its_amount_comes_from()
    {
        Seed();

        Assert.StartsWith("Next pay: about $688 on Tue, Nov 10.", _database.DescribePay(Today));
        Assert.Contains("How this is worked out: from your shifts at $20.00 an hour.", _database.DescribePay(Today));
        Assert.Contains("Paid on the 10th and 25th of each month.", _database.DescribePay(Today));
    }

    [Fact]
    public void Without_shifts_the_pay_line_gives_the_flat_amount()
    {
        Seed(withHours: false);

        Assert.StartsWith("Next pay: about $850.00 on Tue, Nov 10.", _database.DescribePay(Today));
        Assert.DoesNotContain("How this is worked out", _database.DescribePay(Today));
    }

    [Fact]
    public void The_pay_schedule_form_starts_on_the_saved_schedule()
    {
        Assert.Null(_database.GetPayScheduleForm(Today));

        Seed();
        var form = _database.GetPayScheduleForm(Today)!;

        Assert.Equal("twice_monthly", form.Pattern);
        Assert.Equal([10, 25], form.Days);
        Assert.Equal(new DateOnly(2026, 11, 10), form.Next);
        Assert.Equal(85000, form.Cents);
        Assert.Equal(20.00m, form.HourlyRate);
        Assert.Null(form.CalendarThrough);
    }

    [Fact]
    public void A_monthly_payday_moved_for_a_weekend_still_starts_the_form_on_its_usual_day()
    {
        // Nov 8, 2026 is a Sunday, so it's paid Friday the 6th, but the schedule is still the 8th.
        _database.AddScheduledItem(Paycheck with { Days = [8] });

        var form = _database.GetPayScheduleForm(Today)!;
        Assert.Equal("monthly", form.Pattern);
        Assert.Equal(new DateOnly(2026, 11, 8), form.Next);
        Assert.Null(form.HourlyRate);
    }

    [Fact]
    public void A_payday_on_the_1st_moved_into_the_month_before_starts_the_form_on_the_next_month()
    {
        // Nov 1, 2026 is a Sunday, so it's paid Friday Oct 30, but the usual date is Nov 1, not Oct 1.
        _database.AddScheduledItem(Paycheck with { Days = [1] });

        var form = _database.GetPayScheduleForm(new DateOnly(2026, 10, 26))!;
        Assert.Equal("monthly", form.Pattern);
        Assert.Equal(new DateOnly(2026, 11, 1), form.Next);
    }

    [Fact]
    public void With_no_schedule_it_says_where_to_add_one()
    {
        var text = _database.DescribePay(Today);

        Assert.Equal(WaypointDatabase.NoPaySchedule, text);
        Assert.Contains("Pay schedule on Budget", text);
        Assert.DoesNotContain("I don't", text);
    }
}
