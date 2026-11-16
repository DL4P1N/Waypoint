using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>Eating out from an import: a food category if there is one, and otherwise uncategorized, never Buffer.</summary>
public sealed class RestaurantPurchasesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public RestaurantPurchasesTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private static string Csv(string description, string category) => $"""
        Posted On,Purchase Date,Category,Description,Amount
        {Today:MM/dd/yyyy},{Today:MM/dd/yyyy},{category},{description},20.00
        """;

    private string CategoryOf(string title) => _database.GetSpending(Today, Today).Single(row => row.Title == title).Category;

    private void RemoveAllCategories()
    {
        // A new database starts with the usual categories, Meals out among them.
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM budget_categories";
        command.ExecuteNonQuery();
    }

    [Fact]
    public void A_restaurant_from_a_card_export_is_left_uncategorized_when_there_is_no_food_category()
    {
        RemoveAllCategories();
        _database.SaveCategory(new BudgetCategory("Groceries", 22500));
        _database.SaveCategory(new BudgetCategory(BudgetMath.DefaultCategory, 12500));
        var before = _database.GetCategories().Select(category => category.Name).Order().ToList();

        _database.ImportTransactions("Contoso Card", CsvImport.Parse(Csv("CORNER BISTRO", "Restaurants")));
        _database.ImportTransactions("Contoso Card", CsvImport.Parse(Csv("HARDWARE STORE", "Home Improvement")));

        Assert.Equal(BudgetMath.UncategorizedLabel, CategoryOf("CORNER BISTRO"));
        Assert.Equal(BudgetMath.DefaultCategory, CategoryOf("HARDWARE STORE"));

        // Nothing was made up for it.
        Assert.Equal(before, _database.GetCategories().Select(category => category.Name).Order().ToList());
    }

    [Fact]
    public void A_restaurant_goes_in_the_food_category_when_there_is_one()
    {
        RemoveAllCategories();
        _database.SaveCategory(new BudgetCategory("Groceries", 22500));
        _database.SaveCategory(new BudgetCategory("Eating out with friends", 10000));

        _database.ImportTransactions("Contoso Card", CsvImport.Parse(Csv("CORNER BISTRO", "Restaurants")));

        Assert.Equal("Eating out with friends", CategoryOf("CORNER BISTRO"));
    }

    [Fact]
    public void A_restaurant_from_an_alert_email_is_left_uncategorized_too()
    {
        RemoveAllCategories();
        _database.SaveCategory(new BudgetCategory(BudgetMath.DefaultCategory, 12500));

        // An alert has no bank category, only the merchant.
        _database.ImportTransactions("Contoso Card", [new ImportedTransaction(Today, "TST* JOES PIZZA", 1800, "email-1")]);
        _database.ImportTransactions("Contoso Card", [new ImportedTransaction(Today, "SHELL OIL", 4000, "email-2")]);

        Assert.Equal(BudgetMath.UncategorizedLabel, CategoryOf("TST* JOES PIZZA"));
        Assert.Equal(BudgetMath.DefaultCategory, CategoryOf("SHELL OIL"));
    }

    [Fact]
    public void Restaurants_are_told_apart_by_whole_words()
    {
        Assert.True(RestaurantPurchases.LooksLikeRestaurant("TST* Anything"));
        Assert.True(RestaurantPurchases.LooksLikeRestaurant("Main St Deli"));
        Assert.False(RestaurantPurchases.LooksLikeRestaurant("Flower Delivery Co"));
        Assert.Null(RestaurantPurchases.FoodCategory(["Groceries", "Buffer", "Wants"]));
        Assert.Equal("Food", RestaurantPurchases.FoodCategory(["Groceries", "Food"]));
    }
}
