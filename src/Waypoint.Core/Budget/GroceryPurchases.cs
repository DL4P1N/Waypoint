using System.Text.RegularExpressions;

namespace Waypoint.Core.Budget;

/// <summary>
/// Grocery shopping from a card export or a card's alert email, by the store's name. It goes in a groceries category
/// when there is one, so a grocery run isn't left in Buffer just because the bank gave no category.
/// </summary>
public static class GroceryPurchases
{
    /// <summary>Grocery stores and warehouse clubs, matched as whole words.</summary>
    private static readonly string[] StoreNames =
    [
        "costco", "sam's club", "kroger", "safeway", "albertsons", "trader joe's",
        "trader joe", "whole foods", "wholefds", "aldi", "fred meyer", "winco", "sprouts", "fry's food", "ralphs",
        "publix", "h-e-b", "heb", "wegmans", "food lion", "giant eagle", "meijer", "hy-vee", "lidl",
        "fresh market", "natural grocers", "grocery outlet", "save mart", "vons", "king soopers", "food 4 less",
        "instacart", "stop & shop", "shoprite", "harris teeter", "piggly wiggly", "market basket", "smart & final",
        "winn-dixie", "jewel-osco", "food city", "stater bros", "hannaford", "raley's", "schnucks", "weis markets",
        "grocery", "groceries", "supermarket",
    ];

    /// <summary>Category names that mean groceries, best first, then a word a name for it might have.</summary>
    private static readonly string[] CategoryNames = ["Groceries", "Grocery", "Food at home"];

    public static bool LooksLikeGrocery(string description) =>
        StoreNames.Any(name => Regex.IsMatch(
            description,
            $@"(?<![\p{{L}}]){Regex.Escape(name)}(?![\p{{L}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    /// <summary>The category here that means groceries, or null when there's none.</summary>
    public static string? GroceryCategory(IEnumerable<string> categories)
    {
        var names = categories.ToList();
        foreach (var preferred in CategoryNames)
        {
            if (names.FirstOrDefault(name => name.Equals(preferred, StringComparison.OrdinalIgnoreCase)) is { } exact)
            {
                return exact;
            }
        }

        return names.FirstOrDefault(name => name.Contains("grocer", StringComparison.OrdinalIgnoreCase));
    }
}
