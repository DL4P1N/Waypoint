using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>Net worth kept once a day, its trend, and the monthly summary.</summary>
public sealed class NetWorthTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 11, 9);
    private static readonly DateTime Now = Today.ToDateTime(new TimeOnly(12, 0));
    private static readonly DateOnly October = new(2026, 10, 1);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-{Guid.NewGuid():N}.db");
    private readonly WaypointDatabase _database;

    public NetWorthTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static NetWorthSnapshot Snapshot(DateOnly day, long net) => new(day, net, 0, new Dictionary<string, long>());

    /// <summary>Sets checking and a card as of a day, then keeps that day's snapshot.</summary>
    private void Balances(DateOnly day, long checking, long card)
    {
        _database.SaveBudgetAccount(new BudgetAccount("Northwind Bank checking", AccountKind.Checking, checking, day));
        _database.SaveBudgetAccount(new BudgetAccount("Contoso Card", AccountKind.Card, card, day, 250000));
        _database.RecordNetWorthSnapshot(day);
    }

    [Fact]
    public void A_snapshot_keeps_net_worth_and_each_part_once_a_day()
    {
        Balances(Today, 50000, 12000);
        Balances(Today, 60000, 12000);

        var snapshot = Assert.Single(_database.GetNetWorthSnapshots(Today.AddDays(-30), Today));
        Assert.Equal((60000L, 12000L, 48000L), (snapshot.AssetsCents, snapshot.OwedCents, snapshot.NetCents));
        Assert.Equal(60000, snapshot.Parts["Northwind Bank checking"]);
        Assert.Equal(12000, snapshot.Parts["Contoso Card"]);
    }

    [Fact]
    public void Nothing_is_kept_without_any_accounts()
    {
        _database.RecordNetWorthSnapshot(Today);

        Assert.Empty(_database.GetNetWorthSnapshots(Today.AddDays(-30), Today));
    }

    [Fact]
    public void Working_out_todays_budget_keeps_a_snapshot_and_another_day_does_not()
    {
        var realToday = DateOnly.FromDateTime(DateTime.Now);
        _database.SaveBudgetAccount(new BudgetAccount("Northwind Bank checking", AccountKind.Checking, 50000, realToday.AddDays(-5)));

        _database.GetBudgetReport(realToday.AddDays(-2));
        Assert.Empty(_database.GetNetWorthSnapshots(realToday.AddDays(-30), realToday));

        _database.GetBudgetReport(realToday);
        Assert.Equal(realToday, Assert.Single(_database.GetNetWorthSnapshots(realToday.AddDays(-30), realToday)).Day);
    }

    [Fact]
    public void By_month_shows_each_months_last_figure_newest_first_with_the_change()
    {
        var section = NetWorthTrend.ByMonth([
            Snapshot(new DateOnly(2026, 9, 12), 90000),
            Snapshot(new DateOnly(2026, 9, 30), 100000),
            Snapshot(new DateOnly(2026, 10, 15), 110000),
            Snapshot(new DateOnly(2026, 10, 31), 125000),
            Snapshot(new DateOnly(2026, 11, 9), 120000),
        ]);

        Assert.Equal("NET WORTH BY MONTH", section.Title);
        Assert.Equal(
            [
                ("November 2026, as of Nov 9", "$1,200.00  (-$50.00)"),
                ("October 2026, as of Oct 31", "$1,250.00  (+$250.00)"),
                ("September 2026, as of Sep 30", "$1,000.00"),
            ],
            section.Rows.Select(row => (row.Label, row.Value)));
    }

    [Fact]
    public void The_line_needs_two_snapshots()
    {
        Assert.Empty(NetWorthTrend.Line([Snapshot(Today, 100)]));
        Assert.Equal([100L, 300L], NetWorthTrend.Line([Snapshot(Today, 300), Snapshot(Today.AddDays(-1), 100)]));
    }

    [Fact]
    public void The_monthly_summary_compares_spending_and_counts_income_and_net_worth()
    {
        IReadOnlyList<SpendingRow> spending =
        [
            new(new DateOnly(2026, 9, 8), "Kroger", 10000, "Groceries", false, 0),
            new(new DateOnly(2026, 10, 5), "Kroger", 8000, "Groceries", false, 0),
            new(new DateOnly(2026, 10, 20), "Aldi", 4000, "Groceries", false, 0),
            new(new DateOnly(2026, 10, 10), "Movies", 3000, "Fun", false, 1000),
            new(new DateOnly(2026, 10, 12), "Gift", 50000, "Fun", true, 0),
        ];
        var deposits = new[]
        {
            new PayDeposit("Paycheck", new DateOnly(2026, 10, 13), new DateOnly(2026, 10, 13), 85000, false),
            new PayDeposit("Paycheck", new DateOnly(2026, 10, 27), new DateOnly(2026, 10, 27), 61000, true),
            new PayDeposit("Paycheck", new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 4), 85000, false),
        };

        var sections = MonthlySummary.Build(
            October,
            Today,
            [new BudgetCategory("Groceries", 22500), new BudgetCategory("Fun", 5000), new BudgetCategory("Gas", 4000)],
            spending,
            deposits,
            [new OtherIncome(new DateOnly(2026, 10, 3), "Tutoring", 12000)],
            [Snapshot(new DateOnly(2026, 9, 30), 100000), Snapshot(new DateOnly(2026, 10, 15), 110000), Snapshot(new DateOnly(2026, 10, 31), 125000), Snapshot(Today, 1)]);

        Assert.Equal(["SPENT IN OCTOBER", "INCOME", "NET WORTH"], sections.Select(section => section.Title));
        Assert.Equal(
            [
                ("Groceries", "$120.00  (September $100.00)"),
                ("Fun", "$20.00  (September $0.00)"),
                ("Total", "$140.00  (September $100.00)"),
            ],
            sections[0].Rows.Select(row => (row.Label, row.Value)));
        Assert.Equal(
            [("2 paychecks", "$1,460.00"), ("Other income", "$120.00"), ("Total", "$1,580.00")],
            sections[1].Rows.Select(row => (row.Label, row.Value)));
        Assert.Equal(
            [("Sep 30", "$1,000.00"), ("Oct 31", "$1,250.00"), ("Change", "+$250.00")],
            sections[2].Rows.Select(row => (row.Label, row.Value)));

        var text = MonthlySummary.Text(October, sections);
        Assert.StartsWith("October:", text);
        Assert.Contains("Spent: Groceries $120.00  (September $100.00)", text);
        Assert.Contains("Net worth: Sep 30 $1,000.00, Oct 31 $1,250.00, Change +$250.00.", text);
    }

    [Fact]
    public void The_current_month_is_so_far_and_tracking_that_started_partway_counts_from_its_first_day()
    {
        var sections = MonthlySummary.Build(
            new DateOnly(2026, 11, 1),
            Today,
            [],
            [],
            [],
            [],
            [Snapshot(new DateOnly(2026, 11, 3), 50000), Snapshot(Today, 52500)]);

        Assert.Equal("SPENT IN NOVEMBER SO FAR", sections[0].Title);
        Assert.Equal(("No paychecks or other income recorded", ""), (sections[1].Rows.Single().Label, sections[1].Rows.Single().Value));
        Assert.Equal(("Change", "+$25.00"), (sections[2].Rows[^1].Label, sections[2].Rows[^1].Value));
    }

    [Fact]
    public void A_month_without_snapshots_says_it_was_not_tracked()
    {
        var sections = MonthlySummary.Build(October, Today, [], [], [], [], [Snapshot(Today, 1)]);

        Assert.Equal("Not tracked for this month", sections[2].Rows.Single().Label);
    }

    [Fact]
    public void How_did_i_do_last_month_reads_the_month_before_from_the_database()
    {
        _database.SaveCategory(new BudgetCategory("Groceries", 22500));
        _database.AddExpense("Kroger", 4500, "Groceries", new DateOnly(2026, 10, 14), Now);
        _database.AddExpense("Kroger", 2000, "Groceries", new DateOnly(2026, 9, 14), Now);
        _database.AddExpense("Kroger", 9900, "Groceries", Today, Now);
        _database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.AddIncome, "Tutoring", 8000, null), new DateOnly(2026, 10, 2));
        Balances(new DateOnly(2026, 9, 30), 40000, 10000);
        Balances(new DateOnly(2026, 10, 31), 45000, 5000);

        var reply = AssistantOps.Apply(new AssistantOp(AssistantOpKind.MonthlySummary), _database, 0, Now);

        Assert.True(reply.IsAnswer);
        Assert.StartsWith("October:", reply.Text);
        Assert.Contains("Groceries $45.00  (September $20.00)", reply.Text);
        Assert.Contains("Other income $80.00", reply.Text);
        Assert.Contains("Change +$100.00", reply.Text);
    }

    [Fact]
    public void A_named_month_is_summarized_and_a_future_one_is_not()
    {
        var september = AssistantOps.Apply(new AssistantOp(AssistantOpKind.MonthlySummary, At: new DateTime(2026, 9, 20)), _database, 0, Now);
        var future = AssistantOps.Apply(new AssistantOp(AssistantOpKind.MonthlySummary, At: new DateTime(2026, 12, 20)), _database, 0, Now);

        Assert.StartsWith("September:", september.Text);
        Assert.Contains("hasn't happened", future.Text);
        Assert.Contains("monthly_summary", ClaudeCliSorter.Schema);
    }
}
