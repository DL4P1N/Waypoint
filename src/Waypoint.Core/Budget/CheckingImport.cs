using System.Globalization;

namespace Waypoint.Core.Budget;

public enum CheckingKind
{
    /// <summary>Changes the balance only: pay, transfers, fees, and cash.</summary>
    Other,
    CardPayment,
    Spending,
}

/// <summary>A row from a checking export. Cents is signed, as the bank shows it. Card is set for card payments.</summary>
public sealed record CheckingRow(DateOnly Date, string Description, long Cents, CheckingKind Kind, string? Card = null);

/// <summary>
/// Reads checking exports, as the checking profile describes them. Only purchases and payment app payments count as
/// spending, so fees or a transfer doesn't land on a weekly cap.
/// </summary>
public static class CheckingImport
{
    /// <summary>The account checking rows are kept under, from the checking profile.</summary>
    public static string CheckingAccount => BankProfiles.Current.Checking.Name;

    /// <summary>Whether a checking row is money moved to the brokerage.</summary>
    public static bool IsBrokerageDeposit(string description) =>
        BankProfiles.Current.Brokerage.CheckingDescriptors.Any(text => description.Contains(text, StringComparison.OrdinalIgnoreCase));

    public static bool IsChecking(string csv)
    {
        var first = csv.Split('\n')[0];
        return BankProfiles.Current.Checking.CsvHeaders.All(header => first.Contains(header, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<CheckingRow> Parse(string csv) => Read(csv).Rows;

    /// <summary>A checking export's rows, and how many couldn't be read (no date or amount), so those are counted.</summary>
    public static (IReadOnlyList<CheckingRow> Rows, int Unreadable) Read(string csv)
    {
        var rows = new List<CheckingRow>();
        var unreadable = 0;
        foreach (var cells in CsvImport.Rows(csv))
        {
            if (cells.Count >= 3
                && DateOnly.TryParse(cells[0], CultureInfo.InvariantCulture, out var day)
                && CsvImport.TryReadAmount(cells[2], out var value))
            {
                rows.Add(Classify(day, cells[1].Trim(), (long)Math.Round(value * 100)));
            }
            else
            {
                unreadable++;
            }
        }

        return (rows, unreadable);
    }

    /// <summary>A purchase's description without the purchase wording and reference number around the merchant.</summary>
    public static string PurchaseTitle(string description)
    {
        var app = BankProfiles.Current.PaymentApp;
        if (app.Mentions(description))
        {
            return app.Name;
        }

        var checking = BankProfiles.Current.Checking;
        var prefix = checking.PurchasePrefixPattern().Match(description);
        var rest = prefix is { Success: true, Index: 0 } ? description[prefix.Length..] : description;
        return checking.ReferenceSuffixPattern().Replace(rest, "").Trim();
    }

    /// <summary>Whether a checking row starts with the checking profile's purchase wording.</summary>
    public static bool IsPurchase(string description) =>
        BankProfiles.Current.Checking.PurchasePrefixPattern().Match(description) is { Success: true, Index: 0, Length: > 0 };

    private static CheckingRow Classify(DateOnly day, string text, long cents)
    {
        var profiles = BankProfiles.Current;
        if (cents < 0 && profiles.CardPaidBy(text, atStart: true) is { } card)
        {
            return new CheckingRow(day, text, cents, CheckingKind.CardPayment, card.Name);
        }

        var isAppPayment = profiles.PaymentApp.Starts(text) && text.Contains("PAYMENT", StringComparison.OrdinalIgnoreCase);
        return IsPurchase(text) || isAppPayment
            ? new CheckingRow(day, text, cents, CheckingKind.Spending)
            : new CheckingRow(day, text, cents, CheckingKind.Other);
    }
}
