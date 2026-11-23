using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Waypoint.Core.Budget;

/// <summary>Turns a card statement's merchant text into a readable name, so one store groups as one.</summary>
public static partial class MerchantName
{
    /// <summary>Big national chains whose statement text varies by store. A bank profile's merchantNames come first.</summary>
    private static readonly (string Match, string Name)[] Known =
    [
        ("DOMINO'S", "Domino's"), ("DOMINOS", "Domino's"), ("TACO BELL", "Taco Bell"), ("BURGER KING", "Burger King"),
        ("POPEYES", "Popeyes"), ("FIVE GUYS", "Five Guys"), ("SHAKE SHACK", "Shake Shack"), ("DUNKIN", "Dunkin'"),
        ("JIMMY JOHN", "Jimmy John's"), ("JERSEY MIKE", "Jersey Mike's"), ("ARBY'S", "Arby's"), ("POSTMATES", "Postmates"),
        ("INSTACART", "Instacart"), ("KROGER", "Kroger"), ("SAFEWAY", "Safeway"), ("PUBLIX", "Publix"),
        ("WHOLEFDS", "Whole Foods"), ("WHOLE FOODS", "Whole Foods"), ("H-E-B", "H-E-B"), ("MEIJER", "Meijer"), ("WEGMANS", "Wegmans"),
    ];

    public static string Clean(string raw)
    {
        foreach (var pair in BankProfiles.Current.MerchantNames)
        {
            if (raw.Contains(pair[0], StringComparison.OrdinalIgnoreCase))
            {
                return pair[1];
            }
        }

        foreach (var (match, name) in Known)
        {
            if (raw.Contains(match, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        var text = Prefix().Replace(Tail().Replace(ColumnGap().Split(raw.Trim())[0], ""), "").Trim();
        if (text.Length == 0)
        {
            return raw.Trim();
        }

        return text.Any(char.IsLower) ? text : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex ColumnGap();

    [GeneratedRegex(@"\s*(?:#\s?\d.*|\d{3}-\d{3}-\d{4}.*|\s\d{5,}.*|\s[A-Z][A-Za-z]+\s[A-Z]{2}$|\s[A-Z]{2}$)")]
    private static partial Regex Tail();

    [GeneratedRegex(@"^(?:TST\*|SQ\s?\*|PRC\*|PAYPAL\s?\*|PP\*|SP\s)\s*")]
    private static partial Regex Prefix();
}

/// <summary>
/// Reads card exports by their column names. A file with the rewards card's type column (DEBIT or CREDIT) shows
/// purchases as negative, and other files show them as positive. Card payments are left out.
/// </summary>
public static class CsvImport
{
    /// <summary>Paying a card, or its rewards, isn't spending or a refund. The card profiles can add their own wording.</summary>
    private static readonly string[] NotSpendingWords = ["PAYMENT", "AUTOPAY", "STATEMENT CREDIT"];

    private static bool IsNotSpending(string text) =>
        NotSpendingWords.Concat(BankProfiles.Current.Cards.SelectMany(card => card.ExportPaymentDescriptors.Concat(card.CreditDescriptors)))
            .Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>The purchase date column's possible names, lower case: the card profiles' own, then the usual ones.</summary>
    private static string[] DateHeaders() =>
        BankProfiles.Current.Cards.SelectMany(card => card.CsvDateHeaders).Select(header => header.Trim().ToLowerInvariant())
            .Where(header => header.Length > 0).Append("transaction date").Distinct().ToArray();

    // Card issuers name the same categories differently, so each group is the names cards use, then the names it
    // may have here, best first. "Meals out" is the default name for eating out and is often renamed.
    private static readonly (string[] Bank, string[] Here)[] CategoryGroups =
    [
        (["Groceries", "Grocery", "Grocery Stores", "Supermarkets", "Supermarket"], ["Groceries", "Grocery"]),
        (RestaurantPurchases.BankNames, ["Meals out", "Food", "Restaurants", "Eating out", "Dining"]),
        (["Gas", "Gas Stations", "Gasoline", "Fuel"], ["Gas", "Fuel"]),
        (["Travel", "Airlines", "Lodging", "Hotels"], ["Travel"]),
        (["Merchandise", "Shopping", "Department Stores", "General Merchandise"], ["Shopping"]),
        (["Entertainment"], ["Entertainment"]),
    ];

    private static readonly Dictionary<string, string[]> BankCategories = CategoryGroups
        .SelectMany(group => group.Bank.Select(bank => (bank, group.Here)))
        .ToDictionary(pair => pair.bank, pair => pair.Here, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The rewards card profile's type column is how that card's exports differ from the other card's.
    /// </summary>
    public static bool IsTypeColumnExport(string csv) => HasTypeColumn(Headers(csv.Split('\n')[0]));

    private static bool HasTypeColumn(List<string> headers) =>
        headers.Contains(BankProfiles.Current.RewardsCard.TypeColumn.Trim().ToLowerInvariant());

    private static List<string> Headers(string line) =>
        SplitLine(line.TrimEnd('\r')).Select(header => header.Trim().Trim('﻿').ToLowerInvariant()).ToList();

    /// <summary>A bank or card export, which the import reads, rather than a CSV that's just a file.</summary>
    public static bool IsStatement(string csv) => CheckingImport.IsChecking(csv) || IsCardExport(csv);

    /// <summary>Only a real card export is imported. Any other CSV with a date and an amount is just a file.</summary>
    public static bool IsCardExport(string csv)
    {
        var headers = Headers(csv.Split('\n')[0]);
        return Find(headers, "description", "name", "merchant") >= 0
            && Find(headers, "amount") >= 0
            && (HasTypeColumn(headers) || Find(headers, DateHeaders()) >= 0);
    }

    public static IReadOnlyList<ImportedTransaction> Parse(string csv) => Read(csv).Purchases;

    /// <summary>Payments and rewards credits, as positive amounts. These lower a card's balance.</summary>
    public static IReadOnlyList<ImportedTransaction> ParsePayments(string csv) => Read(csv).Payments;

    private static (List<ImportedTransaction> Purchases, List<ImportedTransaction> Payments) Read(string csv)
    {
        var lines = csv.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0).ToList();
        if (lines.Count == 0)
        {
            // Said outright, so an empty export isn't mistaken for one whose purchases were all counted already.
            throw new FormatException("That file is empty.");
        }

        var headers = Headers(lines[0]);
        var date = Find(headers, [.. DateHeaders(), "date"]);
        var description = Find(headers, "description", "name", "merchant");
        var amount = Find(headers, "amount");
        var category = Find(headers, "category");
        if (date < 0 || description < 0 || amount < 0)
        {
            throw new FormatException("That CSV doesn't have date, description, and amount columns.");
        }

        var negativeIsSpending = HasTypeColumn(headers);
        var transactions = new List<ImportedTransaction>();
        var payments = new List<ImportedTransaction>();

        foreach (var line in lines.Skip(1))
        {
            var cells = SplitLine(line);
            if (cells.Count <= Math.Max(date, Math.Max(description, amount))
                || !DateOnly.TryParse(cells[date], CultureInfo.InvariantCulture, out var day)
                || !decimal.TryParse(cells[amount].Replace("$", "").Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            var text = cells[description].Trim();
            var cents = (long)Math.Round(value * 100);

            // Payment wording only counts on money coming in, in the file's own sign convention: a type column file
            // shows purchases as negative and the others show them as positive. A purchase whose name happens to say
            // PAYMENT is still a purchase.
            var isCredit = negativeIsSpending ? cents > 0 : cents < 0;
            if (isCredit && IsNotSpending(text))
            {
                payments.Add(new ImportedTransaction(day, text, Math.Abs(cents)));
                continue;
            }

            var bank = category >= 0 && category < cells.Count ? cells[category].Trim() : null;
            var suggested = bank is null ? null : BankCategories.GetValueOrDefault(bank);
            transactions.Add(new ImportedTransaction(day, text, negativeIsSpending ? -cents : cents, null, suggested, RestaurantPurchases.IsBankRestaurant(bank)));
        }

        return (transactions, payments);
    }

    /// <summary>A file's data rows split into cells, without its header.</summary>
    public static IEnumerable<List<string>> Rows(string csv) =>
        csv.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0).Skip(1).Select(SplitLine);

    private static int Find(List<string> headers, params string[] names) =>
        names.Select(name => headers.IndexOf(name)).FirstOrDefault(index => index >= 0, -1);

    private static List<string> SplitLine(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;

        foreach (var character in line)
        {
            if (character == '"')
            {
                quoted = !quoted;
            }
            else if (character == ',' && !quoted)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(character);
            }
        }

        cells.Add(cell.ToString());
        return cells;
    }
}
