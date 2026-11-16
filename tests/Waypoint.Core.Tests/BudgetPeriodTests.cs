using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

public sealed class BudgetPeriodTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 11, 4);

    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    private string Path => System.IO.Path.Combine(_directory, "waypoint.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    [Fact]
    public void A_monthly_category_is_judged_only_on_its_month()
    {
        var details = BudgetReport.Categories(new DateOnly(2026, 11, 1), Today,
            [new BudgetCategory("Wants", 5000)],
            [new SpendingRow(Today, "Megamart", 4000, "Wants", false, 0)]);

        var wants = details.Single();
        Assert.Null(wants.WeekCapCents);
        Assert.False(wants.IsOver);
        Assert.Equal(1000, BudgetReport.LeftThisMonth(details));
        Assert.Equal(0, BudgetReport.LeftThisWeek(details));
    }

    [Fact]
    public void Week_and_month_money_left_never_overlap()
    {
        var details = BudgetReport.Categories(new DateOnly(2026, 11, 1), Today,
            [new BudgetCategory("Groceries", 22500, BudgetPeriod.Week), new BudgetCategory("Buffer", 12500)],
            [new SpendingRow(Today, "Trader Joe's", 2000, "Groceries", false, 0), new SpendingRow(Today, "Target", 2500, "Buffer", false, 0)]);

        Assert.Equal(BudgetMath.WeeklyCap(22500) - 2000, BudgetReport.LeftThisWeek(details));
        Assert.Equal(10000, BudgetReport.LeftThisMonth(details));
    }

    [Fact]
    public void An_over_monthly_cap_needs_attention_and_a_weekly_one_only_on_its_week()
    {
        var report = BudgetReport.Build(Today,
            [new BudgetCategory("Groceries", 22500, BudgetPeriod.Week), new BudgetCategory("Wants", 5000)],
            [
                new SpendingRow(new DateOnly(2026, 11, 1), "Trader Joe's", 40000, "Groceries", false, 0),
                new SpendingRow(new DateOnly(2026, 11, 2), "Trader Joe's", 1000, "Groceries", false, 0),
                new SpendingRow(Today, "Megamart", 6000, "Wants", false, 0),
            ],
            [],
            []);

        Assert.Equal(["Groceries"], report.Single(section => section.Title == "THIS WEEK").Rows.Select(row => row.Label));
        Assert.Equal(["Wants"], report.Single(section => section.Title == "THIS MONTH").Rows.Select(row => row.Label));
        Assert.Equal(["Wants is $10.00 over this month's $50.00 budget."], BudgetReport.Attention(report));
    }

    [Fact]
    public void Last_month_spending_in_this_week_does_not_count_against_a_month_cap()
    {
        var report = BudgetReport.Build(new DateOnly(2026, 11, 2),
            [new BudgetCategory("Wants", 5000)],
            [new SpendingRow(new DateOnly(2026, 10, 31), "Megamart", 9000, "Wants", false, 0)],
            [],
            []);

        Assert.Empty(BudgetReport.Attention(report));
    }

    [Fact]
    public void Upgrade_sets_each_period()
    {
        Legacy();

        var database = new WaypointDatabase(Path);

        var categories = database.GetCategories();
        Assert.Equal(new BudgetCategory("Wants", 6500), categories.Single(category => category.Name == "Wants"));
        Assert.Equal(new BudgetCategory("Buffer", 12500), categories.Single(category => category.Name == "Buffer"));
        Assert.Equal(BudgetPeriod.Week, categories.Single(category => category.Name == "Groceries").Period);
        Assert.Equal(BudgetPeriod.Week, categories.Single(category => category.Name == "Meals out").Period);
        Assert.Equal(BudgetPeriod.Month, categories.Single(category => category.Name == "Wants").Period);
        Assert.Equal(BudgetPeriod.Month, categories.Single(category => category.Name == "Buffer").Period);

        var spending = database.GetSpending(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)).Single();
        Assert.Equal("Wants", spending.Category);
        Assert.Equal("MEGAMART ONLINE", spending.Title);

        database.ImportTransactions("Contoso Card", [new ImportedTransaction(new DateOnly(2026, 10, 20), "MEGAMART ONLINE", 1234)]);
        Assert.Contains(database.GetSpending(new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 20)), row => row.Category == "Wants" && row.AmountCents == 1234);
    }

    [Fact]
    public void Upgrade_runs_once_so_a_later_period_stays()
    {
        Legacy();
        var database = new WaypointDatabase(Path);
        database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.SetPeriod, "Groceries", null, "month"), Today);

        var reopened = new WaypointDatabase(Path);

        Assert.Equal(BudgetPeriod.Month, reopened.GetCategories().Single(category => category.Name == "Groceries").Period);
    }

    [Theory]
    [InlineData("monthly", BudgetPeriod.Month)]
    [InlineData("week", BudgetPeriod.Week)]
    public void Typing_sets_a_period_and_keeps_the_cap(string text, BudgetPeriod expected)
    {
        var database = new WaypointDatabase(Path);
        database.SaveCategory(new BudgetCategory("Groceries", 22500));

        database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.SetPeriod, "groceries", null, text), Today);

        Assert.Equal(new BudgetCategory("Groceries", 22500, expected), database.GetCategories().Single());
    }

    [Fact]
    public void Raising_a_cap_keeps_the_period()
    {
        var database = new WaypointDatabase(Path);
        database.SaveCategory(new BudgetCategory("Groceries", 22500, BudgetPeriod.Week));

        database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.SetCap, "groceries", 32000, null), Today);

        Assert.Equal(BudgetPeriod.Week, database.GetCategories().Single().Period);
    }

    [Fact]
    public void Typing_a_rename_moves_purchases_and_merchant_rules()
    {
        var database = new WaypointDatabase(Path);
        database.SaveCategory(new BudgetCategory("Wants", 5000));
        database.SaveMerchantRule("megamart", "Wants");
        database.ImportTransactions("Contoso Card", [new ImportedTransaction(new DateOnly(2026, 10, 20), "MEGAMART ONLINE", 1234)]);

        Assert.Equal("Wants is now Fun", database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.RenameCategory, "wants", null, "Fun"), Today));

        Assert.Equal("Fun", database.GetCategories().Single().Name);
        Assert.Equal("Fun", database.GetSpending(new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 20)).Single().Category);
        database.ImportTransactions("Contoso Card", [new ImportedTransaction(new DateOnly(2026, 10, 25), "MEGAMART OTHER", 999)]);
        Assert.Equal("Fun", database.GetSpending(new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 25)).Single().Category);
    }

    [Fact]
    public void The_sorter_understands_the_new_ops()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            {"items": [], "budget_ops": [
                {"op": "set_period", "name": "groceries", "amount_cents": null, "category": "month"},
                {"op": "rename_category", "name": "wants", "amount_cents": null, "category": "fun"}]}
            """);

        var ops = SortedItemParser.ParseResult(document.RootElement).BudgetOps!;

        Assert.Equal([BudgetOpKind.SetPeriod, BudgetOpKind.RenameCategory], ops.Select(op => op.Kind));
    }

    /// <summary>Builds the shape a database had before periods, so the upgrade runs on it.</summary>
    private void Legacy()
    {
        new WaypointDatabase(Path).ImportTransactions("Contoso Card", [new ImportedTransaction(new DateOnly(2026, 10, 10), "MEGAMART ONLINE", 2500)]);

        using var connection = new SqliteConnection($"Data Source={Path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            DELETE FROM preferences WHERE key = 'budget_periods_separated';
            ALTER TABLE budget_categories DROP COLUMN period;
            INSERT INTO budget_categories (name, monthly_cents, position) VALUES
                ('Groceries', 22500, 1), ('Meals out', 8000, 2), ('Wants', 6500, 3), ('Buffer', 12500, 4);
            UPDATE items SET category = 'Wants';
            INSERT INTO merchant_rules (match, category) VALUES ('megamart', 'Wants');
            """;
        command.ExecuteNonQuery();
    }
}
