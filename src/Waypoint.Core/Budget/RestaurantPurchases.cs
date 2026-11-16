namespace Waypoint.Core.Budget;

/// <summary>
/// Eating out from a card export or a card's alert email. It goes in a food or eating-out category if there is one.
/// With none, it's left uncategorized, rather than put in Buffer or in a category made up for it, so it waits to be
/// filed by hand.
/// </summary>
public static class RestaurantPurchases
{
    /// <summary>The names card issuers give eating out.</summary>
    internal static readonly string[] BankNames = ["Restaurants", "Restaurant", "Dining", "Food & Drink", "Food and Drink"];

    /// <summary>Category names that mean eating out, best first, then words a name for it might have.</summary>
    private static readonly string[] CategoryNames = ["Meals out", "Eating out", "Restaurants", "Dining", "Food"];

    private static readonly string[] CategoryWords = ["eating out", "restaurant", "dining", "takeout", "take out", "food", "meal"];

    /// <summary>Grocery shopping is food too, but it isn't eating out.</summary>
    private static readonly string[] NotEatingOut = ["grocer", "supermarket"];

    /// <summary>Card point-of-sale prefixes only restaurants use, such as Toast's TST*.</summary>
    private static readonly string[] RestaurantPrefixes = ["TST*", "TST *"];

    private static readonly string[] MerchantWords =
    [
        "restaurant", "pizza", "pizzeria", "grill", "diner", "cafe", "café", "coffee", "taco", "taqueria", "burger",
        "sushi", "ramen", "bbq", "barbecue", "bistro", "cantina", "deli", "bakery", "doordash", "uber eats",
        "ubereats", "grubhub", "postmates", "chipotle", "mcdonald's", "mcdonalds", "starbucks", "wendy's", "chick-fil-a",
        "panera", "domino's", "dominos", "subway", "taco bell", "kfc", "popeyes", "burger king", "five guys", "shake shack",
        "dunkin", "sonic drive", "arby's", "jimmy john's", "jersey mike's", "panda express", "little caesars",
        "papa john's", "pizza hut", "dairy queen", "jack in the box", "whataburger", "in-n-out", "culver's", "qdoba",
        "raising cane's", "zaxby's", "olive garden", "applebee's", "ihop", "denny's", "cracker barrel", "tim hortons",
    ];

    public static bool IsBankRestaurant(string? bankCategory) =>
        bankCategory is { } name && BankNames.Any(bank => bank.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>A merchant name that reads as somewhere to eat, for an alert email, which has no bank category.</summary>
    public static bool LooksLikeRestaurant(string description)
    {
        var text = description.Trim();
        if (RestaurantPrefixes.Any(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Whole words, so a deli is one and a delivery isn't.
        return MerchantWords.Any(word => System.Text.RegularExpressions.Regex.IsMatch(
            text, $@"(?<![\p{{L}}]){System.Text.RegularExpressions.Regex.Escape(word)}(?![\p{{L}}])", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant));
    }

    /// <summary>The category here that means eating out, or null when there's none, which leaves the purchase uncategorized.</summary>
    public static string? FoodCategory(IEnumerable<string> categories)
    {
        var names = categories.ToList();
        foreach (var preferred in CategoryNames)
        {
            if (names.FirstOrDefault(name => name.Equals(preferred, StringComparison.OrdinalIgnoreCase)) is { } exact)
            {
                return exact;
            }
        }

        return names.FirstOrDefault(name =>
            CategoryWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase))
            && !NotEatingOut.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }
}
