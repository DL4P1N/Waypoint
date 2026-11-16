using System.Globalization;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

/// <summary>
/// Bills with amounts, kept on the budget schedule with a negative amount, so the forecast, Until payday, and
/// "Can I afford this?" all see them the same way they see the paycheck.
/// </summary>
public sealed partial class WaypointDatabase
{
    public const string BillMonthly = "monthly";
    public const string BillWeekly = "weekly";
    public const string BillBiweekly = "biweekly";
    public const string BillOnce = "once";

    public IReadOnlyList<ScheduledItem> GetBills() => GetSchedule().Where(item => item.IsBill).ToList();

    /// <summary>
    /// Adds a bill, or changes the one with this name. How often is monthly, weekly, biweekly, or once, and monthly
    /// is assumed when days of the month are given. Account is a card, or null, "checking", or "debit" for checking.
    /// Anything missing is asked for, and nothing is saved until it's all there.
    /// </summary>
    public OpReply SetBill(string? name, long? cents, string? howOften, IReadOnlyList<string> days, DateTime? at, string? account, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new OpReply("What's the bill called?", true);
        }

        var label = name.Trim();
        if (cents is not > 0)
        {
            return new OpReply($"About how much is {label}?", true);
        }

        var anchor = at is { } date ? DateOnly.FromDateTime(date) : (DateOnly?)null;
        var dayNumbers = days.Select(day => int.TryParse(day, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0).Where(day => day is >= 1 and <= 31).Distinct().ToArray();
        var often = howOften?.Trim().ToLowerInvariant() is { Length: > 0 } given ? given : dayNumbers.Length > 0 || anchor is not null ? BillMonthly : null;

        PayPattern pattern;
        switch (often)
        {
            case BillMonthly:
                pattern = PayPattern.MonthDays;
                dayNumbers = dayNumbers.Length > 0 ? dayNumbers : anchor is { } first ? [first.Day] : [];
                if (dayNumbers.Length == 0)
                {
                    return new OpReply($"What day of the month is {label} due?", true);
                }

                anchor = null;
                break;
            case BillWeekly:
            case BillBiweekly:
            case BillOnce:
                pattern = often switch { BillWeekly => PayPattern.Weekly, BillBiweekly => PayPattern.Biweekly, _ => PayPattern.Once };
                if (anchor is null)
                {
                    return new OpReply($"What date is {label} next due?", true);
                }

                dayNumbers = [];
                break;
            default:
                return new OpReply($"How often is {label} due: monthly, weekly, every other week, or once?", true);
        }

        string? card = null;
        if (account?.Trim() is { Length: > 0 } named && !named.Equals("checking", StringComparison.OrdinalIgnoreCase) && !named.Equals("debit", StringComparison.OrdinalIgnoreCase))
        {
            var found = FindAccount(named);
            if (found is null)
            {
                var cards = GetBudgetAccounts().Where(candidate => candidate.Kind == AccountKind.Card).Select(candidate => candidate.Name).ToList();
                return new OpReply(cards.Count > 0
                    ? $"Which card is {label} charged to: {string.Join(", ", cards)}, or checking?"
                    : $"I don't have a card called {named}. Is {label} paid from checking?", true);
            }

            card = found.Kind == AccountKind.Card ? found.Name : null;
        }

        var existing = GetBills().FirstOrDefault(bill => Same(bill.Label, label));
        var item = new ScheduledItem(existing?.Label ?? label, -cents.Value, dayNumbers, false, pattern, anchor, existing?.Id ?? 0, Account: card);
        if (existing is null)
        {
            AddScheduledItem(item, today);
        }
        else
        {
            UpdateBill(item);
        }

        var next = BudgetMath.NextOccurrence(item, today) is { } due ? $", next due {ShortDate(due)}" : "";
        var on = card is null ? "" : $" on {card}";
        return new OpReply($"{item.Label} is {Briefing.Money(cents.Value)} {Cadence(item)}{on}{next}");
    }

    public OpReply RemoveBill(string? name)
    {
        var bills = GetBills();
        var match = string.IsNullOrWhiteSpace(name)
            ? null
            : bills.FirstOrDefault(bill => Same(bill.Label, name)) ?? Single(bills.Where(bill => bill.Label.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase)));
        if (match is null)
        {
            return new OpReply(bills.Count > 0 ? $"Which bill: {string.Join(", ", bills.Select(bill => bill.Label))}?" : "There are no bills to remove.", true);
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM budget_schedule WHERE id = $id AND amount_cents < 0";
        command.Parameters.AddWithValue("$id", match.Id);
        command.ExecuteNonQuery();
        return new OpReply($"Removed {match.Label} from your bills");
    }

    public string DescribeBills(DateOnly today)
    {
        var bills = GetBills();
        if (bills.Count == 0)
        {
            return "There are no bills yet. Tell me one, such as \"my internet bill is $55 on the 8th\".";
        }

        return string.Join("\n", bills.Select(bill =>
        {
            var next = BudgetMath.NextOccurrence(bill, today) is { } due ? $", next {ShortDate(due)}" : "";
            var on = bill.Account is { } card ? $" on {card}" : "";
            return $"{bill.Label}: {Briefing.Money(-bill.AmountCents)} {Cadence(bill)}{on}{next}";
        }));
    }

    private static ScheduledItem? Single(IEnumerable<ScheduledItem> bills)
    {
        var list = bills.Take(2).ToList();
        return list.Count == 1 ? list[0] : null;
    }

    private void UpdateBill(ScheduledItem item)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE budget_schedule SET amount_cents = $amount, days = $days, pattern = $pattern, anchor = $anchor, account = $account WHERE id = $id";
        command.Parameters.AddWithValue("$amount", item.AmountCents);
        command.Parameters.AddWithValue("$days", string.Join(',', item.Days));
        command.Parameters.AddWithValue("$pattern", item.Pattern.ToString());
        command.Parameters.AddWithValue("$anchor", (object?)item.Anchor?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$account", (object?)item.Account ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", item.Id);
        command.ExecuteNonQuery();
    }
}
