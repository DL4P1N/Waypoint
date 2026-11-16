using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>The optional savings goal, its pace, and how "Can I afford this?" keeps it on track.</summary>
public sealed class SavingsGoalTests : IDisposable
{
    // A Monday. Paydays on the 10th and 25th land Nov 10, Nov 25, Dec 10, Dec 24, Jan 8, Jan 25, Feb 10, and Feb 25
    // before Mar 1, so a $500 goal is $62.50 a paycheck.
    private static readonly DateOnly Today = new(2026, 11, 2);
    private static readonly DateTime Now = Today.ToDateTime(new TimeOnly(12, 0));
    private static readonly DateOnly March = new(2027, 3, 1);
    private static readonly ScheduledItem Paycheck = new("Paycheck", 85000, [10, 25], true);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-{Guid.NewGuid():N}.db");
    private readonly WaypointDatabase _database;

    public SavingsGoalTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static BudgetAccount Cushion(long cents) => new("Fabrikam cushion", AccountKind.Cushion, cents, Today);

    private static BudgetAccount Checking(long cents) => new("Northwind Bank checking", AccountKind.Checking, cents, Today);

    private static SavingsGoal Goal(DateOnly? by = null) => new(50000, by ?? March, Today, 20000);

    private OpReply Say(AssistantOpKind kind, long? cents = null, DateTime? at = null) =>
        AssistantOps.Apply(new AssistantOp(kind, AmountCents: cents, At: at), _database, 0, Now);

    [Fact]
    public void A_dated_goal_is_split_across_the_paychecks_before_it()
    {
        var status = SavingsPlan.Status(Goal(), [Cushion(20000)], Today, [Paycheck], []);

        Assert.Equal((0L, 6250L, 6250L), (status.SavedCents, status.PerPaycheckCents!.Value, status.SetAsideCents));
        Assert.Equal(new DateOnly(2026, 11, 10), status.NextPayday);
        Assert.Contains("$62.50 a paycheck", SavingsPlan.Describe(status, Today));
    }

    [Fact]
    public void Money_added_to_savings_since_the_goal_was_set_counts_toward_it()
    {
        var status = SavingsPlan.Status(Goal(), [Cushion(30000), new BudgetAccount("Roth IRA", AccountKind.Invested, 5000, Today), Checking(90000)], Today, [Paycheck], []);

        Assert.Equal((15000L, 35000L), (status.SavedCents, status.LeftCents));
        Assert.Equal(0.3, status.Fill, 3);
    }

    [Fact]
    public void A_savings_transfer_already_scheduled_before_payday_counts_toward_the_share()
    {
        var transfer = new ScheduledItem("Fabrikam transfer", -4000, [4]);

        var status = SavingsPlan.Status(Goal(), [Cushion(20000)], Today, [Paycheck, transfer], []);

        Assert.Equal(2250, status.SetAsideCents);
    }

    [Fact]
    public void Without_a_pay_schedule_the_pace_is_by_the_month()
    {
        var status = SavingsPlan.Status(Goal(), [Cushion(20000)], Today, [], []);

        Assert.Equal((12500L, 12500L), (status.PerMonthCents!.Value, status.SetAsideCents));
        Assert.Null(status.NextPayday);
    }

    [Fact]
    public void A_goal_with_no_date_has_no_pace_and_a_met_goal_needs_nothing()
    {
        var undated = SavingsPlan.Status(new SavingsGoal(50000, null, Today, 0), [Cushion(10000)], Today, [Paycheck], []);
        var met = SavingsPlan.Status(Goal(), [Cushion(80000)], Today, [Paycheck], []);

        Assert.Equal((0L, 40000L), (undated.SetAsideCents, undated.LeftCents));
        Assert.Contains("$400.00 to go", SavingsPlan.Describe(undated, Today));
        Assert.True(met.IsMet);
        Assert.Equal(0, met.SetAsideCents);
        Assert.Contains("reached it", SavingsPlan.Describe(met, Today));
    }

    [Fact]
    public void Afford_check_says_what_the_goal_share_leaves_in_checking()
    {
        var goal = SavingsPlan.Status(Goal(), [Cushion(20000)], Today, [Paycheck], []);

        var answer = AffordCheck.Check(new AffordRequest(25000, Today, Checking(50000), Today, [Paycheck], [], Goal: goal));

        Assert.False(answer.OffGoal);
        Assert.Contains("To stay on track for $500.00 saved by Mar 1, about $62.50 would go to savings before payday on Nov 10, leaving checking at about $187.50.", answer.Text);
        Assert.DoesNotContain("behind", answer.Text);
    }

    [Fact]
    public void A_purchase_that_leaves_too_little_for_the_goal_says_it_would_fall_behind()
    {
        var goal = SavingsPlan.Status(Goal(), [Cushion(20000)], Today, [Paycheck], []);

        var answer = AffordCheck.Check(new AffordRequest(30000, Today, Checking(50000), Today, [Paycheck], [], Goal: goal));

        Assert.True(answer.OffGoal);
        Assert.False(answer.UnderFloor);
        Assert.Contains("leaving checking at about $137.50. That's under your checking minimum, so buying it would push the savings goal back.", answer.Text);
    }

    [Fact]
    public void An_undated_goal_adds_nothing_to_the_afford_answer()
    {
        var goal = SavingsPlan.Status(new SavingsGoal(50000, null, Today, 0), [Cushion(0)], Today, [Paycheck], []);

        var answer = AffordCheck.Check(new AffordRequest(30000, Today, Checking(50000), Today, [Paycheck], [], Goal: goal));

        Assert.DoesNotContain("savings", answer.Text);
        Assert.False(answer.OffGoal);
    }

    [Fact]
    public void Saying_a_goal_saves_it_from_todays_savings_and_changing_it_keeps_its_start()
    {
        _database.SaveBudgetAccount(Cushion(20000));

        var reply = Say(AssistantOpKind.SavingsGoalSet, 50000, March.ToDateTime(TimeOnly.MinValue));
        _database.SaveBudgetAccount(Cushion(30000));
        Say(AssistantOpKind.SavingsGoalSet, 60000, March.ToDateTime(TimeOnly.MinValue));

        Assert.Contains("$500.00 by Mar 1", reply.Text);
        var goal = _database.GetSavingsGoal()!;
        Assert.Equal(new SavingsGoal(60000, March, Today, 20000), goal);
        Assert.Equal(10000, _database.GetSavingsGoalStatus(Today)!.SavedCents);
    }

    [Fact]
    public void A_goal_needs_an_amount_and_a_date_after_today()
    {
        Assert.True(Say(AssistantOpKind.SavingsGoalSet).IsAnswer);
        Assert.Contains("after today", Say(AssistantOpKind.SavingsGoalSet, 50000, Now.AddDays(-1)).Text);
        Assert.Null(_database.GetSavingsGoal());
    }

    [Fact]
    public void Clearing_the_goal_removes_it()
    {
        Say(AssistantOpKind.SavingsGoalSet, 50000);

        Assert.Equal("Removed your savings goal", Say(AssistantOpKind.SavingsGoalClear).Text);
        Assert.Null(_database.GetSavingsGoal());
        Assert.Contains("no savings goal yet", Say(AssistantOpKind.SavingsGoalShow).Text);
    }

    [Fact]
    public void Without_a_savings_balance_the_goal_says_nothing_measures_it_yet()
    {
        Say(AssistantOpKind.SavingsGoalSet, 50000);

        Assert.Contains("don't have a savings or cushion balance", Say(AssistantOpKind.SavingsGoalShow).Text);
    }

    [Fact]
    public void Can_i_afford_this_counts_the_saved_goal()
    {
        _database.AddScheduledItem(Paycheck);
        _database.SaveBudgetAccount(Checking(50000));
        _database.SaveBudgetAccount(Cushion(20000));
        _database.SetSavingsGoal(50000, March.ToDateTime(TimeOnly.MinValue), Today);

        var reply = _database.CheckAffordable(25000, null, Today);

        Assert.Contains("about $62.50 would go to savings before payday on Nov 10", reply.Text);
    }

    [Fact]
    public void Transfer_names_are_told_apart_from_bills()
    {
        Assert.True(SavingsPlan.IsTransfer("Fabrikam transfer"));
        Assert.True(SavingsPlan.IsTransfer("Savings"));
        Assert.True(SavingsPlan.IsTransfer("Invest in index fund"));
        Assert.False(SavingsPlan.IsTransfer("Phone bill"));
        Assert.False(SavingsPlan.IsTransfer("Streamco"));
        Assert.True(SavingsPlan.IsInvesting("Roth IRA buy"));
        Assert.False(SavingsPlan.IsInvesting("Cushion transfer"));
    }

    [Fact]
    public void The_sorter_knows_the_goal_ops()
    {
        Assert.Contains("savings_goal_set", ClaudeCliSorter.Schema);
        Assert.True(AssistantOps.TryParseKind("savings_goal_show", out var kind));
        Assert.Equal(AssistantOpKind.SavingsGoalShow, kind);
    }
}
