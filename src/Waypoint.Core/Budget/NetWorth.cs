using System.Globalization;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Budget;

/// <summary>Net worth on one day, with each account's balance. Cards are what's owed, as a positive amount.</summary>
public sealed record NetWorthSnapshot(DateOnly Day, long AssetsCents, long OwedCents, IReadOnlyDictionary<string, long> Parts)
{
    public long NetCents => AssetsCents - OwedCents;

    /// <summary>The same figures the Budget page shows: everything but cards is an asset, and cards are owed.</summary>
    public static NetWorthSnapshot From(DateOnly day, IReadOnlyList<BudgetAccount> accounts) => new(
        day,
        accounts.Where(account => account.Kind != AccountKind.Card).Sum(account => account.BalanceCents),
        accounts.Where(account => account.Kind == AccountKind.Card).Sum(account => account.BalanceCents),
        accounts.GroupBy(account => account.Name).ToDictionary(group => group.Key, group => group.Sum(account => account.BalanceCents)));
}

/// <summary>
/// Net worth over time, from the snapshots kept each day the budget is looked at. Days with no snapshot are just
/// gaps, so nothing has to be kept up for it to work.
/// </summary>
public static class NetWorthTrend
{
    public const int MonthsShown = 6;

    /// <summary>The last snapshot of each month, newest first, with the change from the month before when there is one.</summary>
    public static ReportSection ByMonth(IReadOnlyList<NetWorthSnapshot> snapshots, int months = MonthsShown)
    {
        var ends = snapshots
            .GroupBy(snapshot => (snapshot.Day.Year, snapshot.Day.Month))
            .Select(group => group.MaxBy(snapshot => snapshot.Day)!)
            .OrderBy(snapshot => snapshot.Day)
            .ToList();

        var rows = new List<ReportRow>();
        for (var index = ends.Count - 1; index >= 0 && rows.Count < months; index--)
        {
            var end = ends[index];
            var change = index > 0 ? $"  ({Signed(end.NetCents - ends[index - 1].NetCents)})" : "";
            rows.Add(new ReportRow($"{end.Day.ToString("MMMM yyyy", CultureInfo.InvariantCulture)}, as of {Short(end.Day)}", $"{Briefing.Money(end.NetCents)}{change}"));
        }

        return new ReportSection("NET WORTH BY MONTH", rows);
    }

    /// <summary>Snapshot values in date order, for a small line. Empty with fewer than two, since one point isn't a trend.</summary>
    public static IReadOnlyList<long> Line(IReadOnlyList<NetWorthSnapshot> snapshots) =>
        snapshots.Count < 2 ? [] : snapshots.OrderBy(snapshot => snapshot.Day).Select(snapshot => snapshot.NetCents).ToList();

    /// <summary>A change with its sign, such as +$40.00 or -$12.50.</summary>
    public static string Signed(long cents) => cents < 0 ? $"-{Briefing.Money(-cents)}" : $"+{Briefing.Money(cents)}";

    internal static string Short(DateOnly date) => date.ToString("MMM d", CultureInfo.InvariantCulture);
}

/// <summary>
/// One month at a glance: spending by category against the month before, income, and how net worth moved.
/// Only figures Waypoint already has are used, and anything it can't know is said plainly.
/// </summary>
public static class MonthlySummary
{
    public static IReadOnlyList<ReportSection> Build(
        DateOnly month,
        DateOnly today,
        IReadOnlyList<BudgetCategory> categories,
        IReadOnlyList<SpendingRow> spending,
        IReadOnlyList<PayDeposit> deposits,
        IReadOnlyList<OtherIncome> otherIncome,
        IReadOnlyList<NetWorthSnapshot> snapshots)
    {
        var start = new DateOnly(month.Year, month.Month, 1);
        var last = start.AddMonths(1).AddDays(-1);
        var through = today < last ? today : last;
        var before = start.AddMonths(-1);
        var monthName = Name(start);
        var beforeName = Name(before);

        var now = Totals(spending, start, last);
        var then = Totals(spending, before, start.AddDays(-1));
        var names = categories.Select(category => category.Name)
            .Concat(now.Keys).Concat(then.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => now.ContainsKey(name) || then.ContainsKey(name))
            .ToList();

        var spent = names.Select(name =>
        {
            var cents = now.GetValueOrDefault(name);
            var earlier = then.GetValueOrDefault(name);
            return new ReportRow(name, $"{Briefing.Money(cents)}  ({beforeName} {Briefing.Money(earlier)})");
        }).ToList();
        var total = now.Values.Sum();
        var totalBefore = then.Values.Sum();
        spent.Add(new ReportRow("Total", $"{Briefing.Money(total)}  ({beforeName} {Briefing.Money(totalBefore)})"));

        var pay = deposits.Where(deposit => deposit.ReceivedOn >= start && deposit.ReceivedOn <= last).ToList();
        var other = otherIncome.Where(entry => entry.Date >= start && entry.Date <= last).ToList();
        var income = new List<ReportRow>();
        if (pay.Count > 0)
        {
            income.Add(new ReportRow(pay.Count == 1 ? "1 paycheck" : $"{pay.Count} paychecks", Briefing.Money(pay.Sum(deposit => deposit.Cents))));
        }

        if (other.Count > 0)
        {
            income.Add(new ReportRow("Other income", Briefing.Money(other.Sum(entry => entry.AmountCents))));
        }

        income.Add(pay.Count + other.Count > 0
            ? new ReportRow("Total", Briefing.Money(pay.Sum(deposit => deposit.Cents) + other.Sum(entry => entry.AmountCents)))
            : new ReportRow("No paychecks or other income recorded", ""));

        return
        [
            new ReportSection(through < last ? $"SPENT IN {monthName.ToUpperInvariant()} SO FAR" : $"SPENT IN {monthName.ToUpperInvariant()}", spent),
            new ReportSection("INCOME", income),
            new ReportSection("NET WORTH", NetWorthRows(snapshots, start, through)),
        ];
    }

    /// <summary>The summary as a few lines for the chat.</summary>
    public static string Text(DateOnly month, IReadOnlyList<ReportSection> sections)
    {
        var lines = new List<string> { $"{Name(month)}:" };
        foreach (var section in sections)
        {
            var heading = section.Title.StartsWith("SPENT", StringComparison.Ordinal) ? "Spent" : char.ToUpperInvariant(section.Title[0]) + section.Title[1..].ToLowerInvariant();
            lines.Add($"{heading}: {string.Join(", ", section.Rows.Select(row => row.Value.Length > 0 ? $"{row.Label} {row.Value}" : row.Label))}.");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// The change from the last snapshot before the month, or the month's first one when tracking started partway
    /// through, to the latest one in it.
    /// </summary>
    private static IReadOnlyList<ReportRow> NetWorthRows(IReadOnlyList<NetWorthSnapshot> snapshots, DateOnly start, DateOnly through)
    {
        var ordered = snapshots.Where(snapshot => snapshot.Day <= through).OrderBy(snapshot => snapshot.Day).ToList();
        var end = ordered.LastOrDefault(snapshot => snapshot.Day >= start);
        var from = ordered.LastOrDefault(snapshot => snapshot.Day < start) ?? ordered.FirstOrDefault(snapshot => snapshot.Day >= start);
        if (end is null || from is null || from.Day == end.Day)
        {
            return [new ReportRow(end is null ? "Not tracked for this month" : $"Only one day tracked so far, {NetWorthTrend.Short(end.Day)}", end is null ? "" : Briefing.Money(end.NetCents))];
        }

        return
        [
            new ReportRow($"{NetWorthTrend.Short(from.Day)}", Briefing.Money(from.NetCents)),
            new ReportRow($"{NetWorthTrend.Short(end.Day)}", Briefing.Money(end.NetCents)),
            new ReportRow("Change", NetWorthTrend.Signed(end.NetCents - from.NetCents)),
        ];
    }

    /// <summary>Spending by category in a span, as the caps count it: one-offs left out and repayments taken off.</summary>
    private static Dictionary<string, long> Totals(IReadOnlyList<SpendingRow> spending, DateOnly from, DateOnly through) =>
        spending
            .Where(row => !row.OneOff && row.Date >= from && row.Date <= through)
            .GroupBy(row => row.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.NetCents), StringComparer.OrdinalIgnoreCase);

    private static string Name(DateOnly month) => month.ToString("MMMM", CultureInfo.InvariantCulture);
}
