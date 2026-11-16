using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>Grocery runs from an import land in the groceries category, and the review doesn't repeat empty advice.</summary>
public sealed class GroceryPurchasesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public GroceryPurchasesTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private string CategoryOf(string title) => _database.GetSpending(Today, Today).Single(row => row.Title == title).Category;

    [Fact]
    public void Grocery_stores_go_in_groceries_even_without_a_bank_category()
    {
        _database.SaveCategory(new BudgetCategory("Groceries", 32000));

        _database.ImportTransactions("Lucerne Card", [
            new ImportedTransaction(Today, "TRADER JOE'S #12", 4000, "a"),
            new ImportedTransaction(Today, "ALDI #12", 3000, "b"),
            new ImportedTransaction(Today, "PUBLIX #12", 9000, "c"),
            new ImportedTransaction(Today, "SHELL OIL 1234", 4000, "d"),
        ]);

        Assert.Equal("Groceries", CategoryOf("TRADER JOE'S #12"));
        Assert.Equal("Groceries", CategoryOf("ALDI #12"));
        Assert.Equal("Groceries", CategoryOf("PUBLIX #12"));
        Assert.Equal(BudgetMath.DefaultCategory, CategoryOf("SHELL OIL 1234"));
    }

    [Fact]
    public void Store_names_match_whole_words_and_the_category_is_found_by_name()
    {
        Assert.True(GroceryPurchases.LooksLikeGrocery("Trader Joe's #12"));
        Assert.False(GroceryPurchases.LooksLikeGrocery("Aldine Hardware"));
        Assert.Equal("Grocery run", GroceryPurchases.GroceryCategory(["Fun", "Grocery run"]));
        Assert.Null(GroceryPurchases.GroceryCategory(["Fun", "Buffer"]));
    }

    [Fact]
    public void The_review_says_nothing_about_categories_with_no_spending_and_folds_the_rest_into_one_line()
    {
        var month = new DateOnly(Today.Year, Today.Month, 1).AddMonths(-1);
        var categories = new[]
        {
            new BudgetCategory("Groceries", 32000),
            new BudgetCategory("Fun", 10000),
            new BudgetCategory("Gas", 10000),
            new BudgetCategory("Gifts", 10000),
        };
        var spending = Enumerable.Range(1, 10)
            .Select(day => new SpendingRow(month.AddDays(day), "Store", 100, day % 2 == 0 ? "Fun" : "Gas", false, 0))
            .ToList();

        var lines = BudgetCoach.Review(Today, categories, spending).Rows.Select(row => row.Label).ToList();

        Assert.DoesNotContain(lines, line => line.Contains("$0.00"));
        var folded = Assert.Single(lines, line => line.Contains("less than half"));
        Assert.StartsWith("Fun and Gas each used less than half", folded);
        Assert.DoesNotContain("room to move", folded);
    }
}
