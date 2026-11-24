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
public static partial class CsvImport
{
    /// <summary>Paying a card, or its rewards, isn't spending or a refund. The card profiles can add their own wording.</summary>
    private static readonly string[] NotSpendingWords = ["PAYMENT", "AUTOPAY", "STATEMENT CREDIT"];

    private static bool IsNotSpending(string text) =>
        NotSpendingWords.Concat(BankProfiles.Current.Cards.SelectMany(card => card.ExportPaymentDescriptors.Concat(card.CreditDescriptors)))
            .Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>Wording for a payment sent back, such as one returned unpaid or reversed.</summary>
    private static bool IsReversal(string text) => Reversal().IsMatch(text);

    [GeneratedRegex(@"\b(?:RETURN(?:ED)?|REVERS(?:AL|ED|E))\b", RegexOptions.IgnoreCase)]
    private static partial Regex Reversal();

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
    public static bool IsTypeColumnExport(string csv) => HasTypeColumn(Headers(csv));

    private static bool HasTypeColumn(List<string> headers) =>
        headers.Contains(BankProfiles.Current.RewardsCard.TypeColumn.Trim().ToLowerInvariant());

    /// <summary>The first record's cells, trimmed and lower case.</summary>
    private static List<string> Headers(string csv) => HeaderNames(Records(csv).FirstOrDefault()?.Cells ?? []);

    private static List<string> HeaderNames(List<string> cells) =>
        cells.Select(header => header.Trim().Trim('\uFEFF').ToLowerInvariant()).ToList();

    /// <summary>A bank or card export, which the import reads, rather than a CSV that's just a file.</summary>
    public static bool IsStatement(string csv) => CheckingImport.IsChecking(csv) || IsCardExport(csv);

    /// <summary>Only a real card export is imported. Any other CSV with a date and an amount is just a file.</summary>
    public static bool IsCardExport(string csv)
    {
        var headers = Headers(csv);
        return Find(headers, "description", "name", "merchant") >= 0
            && Find(headers, "amount") >= 0
            && (HasTypeColumn(headers) || Find(headers, DateHeaders()) >= 0);
    }

    public static IReadOnlyList<ImportedTransaction> Parse(string csv) => Read(csv).Purchases;

    /// <summary>
    /// Payments and rewards credits, as positive amounts, which lower a card's balance. A payment sent back is negative,
    /// so it puts the balance back up.
    /// </summary>
    public static IReadOnlyList<ImportedTransaction> ParsePayments(string csv) => Read(csv).Payments;

    /// <summary>
    /// A card export's purchases, its payments and rewards credits, and how many rows couldn't be read (no date or
    /// amount where the columns say), so those are counted rather than quietly left out.
    /// </summary>
    public static CardExport Read(string csv)
    {
        var records = Records(csv).ToList();
        if (records.Count == 0)
        {
            // Said outright, so an empty export isn't mistaken for one whose purchases were all counted already.
            throw new FormatException("That file is empty.");
        }

        var headers = HeaderNames(records[0].Cells);
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
        var unreadable = 0;

        foreach (var record in records.Skip(1))
        {
            switch (ReadRow(record, date, amount, description, out var day, out var value))
            {
                case RowRead.Skip:
                    continue;
                case RowRead.Unreadable:
                    unreadable++;
                    continue;
            }

            var cells = record.Cells;
            var text = cells[description].Trim();
            var cents = (long)Math.Round(value * 100);

            // Payment wording only counts on money coming in, in the file's own sign convention: a type column file
            // shows purchases as negative and the others show them as positive. A purchase whose name happens to say
            // PAYMENT is still a purchase. A payment sent back, such as one returned unpaid, puts the balance back up
            // without being spending.
            var isCredit = negativeIsSpending ? cents > 0 : cents < 0;
            if (IsNotSpending(text) && (isCredit || IsReversal(text)))
            {
                payments.Add(new ImportedTransaction(day, text, isCredit ? Math.Abs(cents) : -Math.Abs(cents)));
                continue;
            }

            var bank = category >= 0 && category < cells.Count ? cells[category].Trim() : null;
            var suggested = bank is null ? null : BankCategories.GetValueOrDefault(bank);
            transactions.Add(new ImportedTransaction(day, text, negativeIsSpending ? -cents : cents, null, suggested, RestaurantPurchases.IsBankRestaurant(bank)));
        }

        return new CardExport(transactions, payments, unreadable);
    }

    /// <summary>What a data row turned out to be.</summary>
    internal enum RowRead
    {
        Transaction,

        /// <summary>Not a transaction at all, such as a total, a summary line, or a line with no amount.</summary>
        Skip,

        /// <summary>Looks like a transaction, but its date or amount can't be read, or the line itself was broken.</summary>
        Unreadable,
    }

    /// <summary>
    /// Reads a row's date and amount. Totals, summary blocks and lines with no amount are passed over without counting
    /// as unreadable: a row only counts as unreadable when it has an amount and something date-like that can't be read,
    /// or when its line was broken, such as by a stray quote.
    /// </summary>
    internal static RowRead ReadRow(CsvRecord record, int dateColumn, int amountColumn, int textColumn, out DateOnly day, out decimal value)
    {
        day = default;
        value = 0;
        if (record.Broken)
        {
            return RowRead.Unreadable;
        }

        var cells = record.Cells;
        var dateCell = dateColumn < cells.Count ? cells[dateColumn].Trim() : "";
        var amountCell = amountColumn < cells.Count ? cells[amountColumn].Trim() : "";
        if (amountCell.Length == 0 || !dateCell.Any(char.IsAsciiDigit))
        {
            return RowRead.Skip;
        }

        return textColumn < cells.Count && DateOnly.TryParse(dateCell, CultureInfo.InvariantCulture, out day) && TryReadAmount(amountCell, out value)
            ? RowRead.Transaction
            : RowRead.Unreadable;
    }

    /// <summary>An amount as exports write it, with or without a dollar sign and thousands commas.</summary>
    internal static bool TryReadAmount(string cell, out decimal value) =>
        decimal.TryParse(cell.Replace("$", "").Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out value);

    /// <summary>A file's data rows split into cells, without its header.</summary>
    public static IEnumerable<List<string>> Rows(string csv) => DataRecords(csv).Select(record => record.Cells);

    /// <summary>A file's data rows, without its header, each marked if its line was broken.</summary>
    internal static IEnumerable<CsvRecord> DataRecords(string csv) => Records(csv).Skip(1);

    private static int Find(List<string> headers, params string[] names) =>
        names.Select(name => headers.IndexOf(name)).FirstOrDefault(index => index >= 0, -1);

    /// <summary>The most line breaks a quoted cell can hold before its opening quote is taken as a stray one.</summary>
    private const int MaxQuotedLines = 5;

    /// <summary>The longest a quoted cell can run over several lines before its opening quote is taken as a stray one.</summary>
    private const int MaxQuotedLength = 2000;

    /// <summary>
    /// The file's records, each split into cells. A quote opens a quoted cell only as the cell's first character, and
    /// anywhere else it's just a character, as in 12" SUB. A line break inside a quoted cell stays in it, and two
    /// quotes inside one are one quote. A quoted cell that runs over more lines or text than a cell plausibly holds,
    /// a record over several lines without the header's number of cells, or one whose later lines would each make a
    /// whole row on their own, is taken as a stray quote: that line is read on its own and marked broken, and reading
    /// carries on from the next line. Blank records are left out.
    /// </summary>
    private static IEnumerable<CsvRecord> Records(string csv)
    {
        var lines = csv.Split('\n');
        int? columns = null;
        var start = 0;
        while (start < lines.Length)
        {
            var (cells, used, closed) = ParseRecord(lines, start, MaxQuotedLines + 1);
            var broken = false;
            if (used > 1 && (!closed
                || (columns is { } count && (cells.Count != count || lines.Skip(start + 1).Take(used - 1).Any(line => line.Count(c => c == ',') >= count - 1)))))
            {
                (cells, _, _) = ParseRecord(lines, start, 1);
                used = 1;
                broken = true;
            }

            start += used;
            if (!cells.Any(text => text.Trim().Length > 0))
            {
                continue;
            }

            columns ??= cells.Count;
            yield return new CsvRecord(cells, broken);
        }
    }

    /// <summary>
    /// One record from <paramref name="start"/>, over at most <paramref name="maxLines"/> lines. Closed is false when
    /// a quoted cell was still open where it had to stop.
    /// </summary>
    private static (List<string> Cells, int Used, bool Closed) ParseRecord(string[] lines, int start, int maxLines)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var justClosed = false;
        var cellWasQuoted = false;
        var quotedLength = 0;
        var used = 0;
        while (start + used < lines.Length && used < maxLines)
        {
            var line = lines[start + used];
            used++;
            foreach (var character in line)
            {
                if (quoted)
                {
                    quotedLength++;
                    if (character == '"')
                    {
                        quoted = false;
                        justClosed = true;
                    }
                    else
                    {
                        cell.Append(character);
                    }

                    continue;
                }

                if (character == '"' && justClosed)
                {
                    // Two quotes inside a quoted cell are one quote, and the cell carries on quoted.
                    cell.Append('"');
                    quoted = true;
                    justClosed = false;
                    continue;
                }

                justClosed = false;
                switch (character)
                {
                    case '"' when !cellWasQuoted && cell.ToString().Trim().Length == 0:
                        cell.Clear();
                        quoted = true;
                        cellWasQuoted = true;
                        break;
                    case ',':
                        cells.Add(cell.ToString());
                        cell.Clear();
                        cellWasQuoted = false;
                        quotedLength = 0;
                        break;
                    case '\r':
                        break;
                    default:
                        cell.Append(character);
                        break;
                }
            }

            if (!quoted)
            {
                cells.Add(cell.ToString());
                return (cells, used, true);
            }

            if (quotedLength > MaxQuotedLength)
            {
                break;
            }

            cell.Append('\n');
        }

        cells.Add(cell.ToString());
        return (cells, used, !quoted);
    }
}

/// <summary>A record's cells. Broken is set when its line had to be read on its own, such as after a stray quote.</summary>
public sealed record CsvRecord(List<string> Cells, bool Broken);

/// <summary>What a card export holds. Unreadable counts the rows with no date or amount that could be read.</summary>
public sealed record CardExport(IReadOnlyList<ImportedTransaction> Purchases, IReadOnlyList<ImportedTransaction> Payments, int Unreadable);
