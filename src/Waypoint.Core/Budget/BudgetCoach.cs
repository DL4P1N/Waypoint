using System.Globalization;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Budget;

/// <summary>
/// Plain feedback on spending against the user's own caps. It's worked out locally from their
/// purchases, and sticks to the budget: it never touches investing, debt, or credit decisions.
/// </summary>
public static class BudgetCoach
{
    private const int MonthsCompared = 3;
    private const int MinPurchasesForMonth = 10;
    private const int MinDaysForPace = 7;
    private const decimal PaceMargin = 1.1m;
    private const decimal UnderusedShare = 0.5m;
    private const int PatternDays = 30;
    private const int MinLatePurchases = 3;
    private const int MinSmallPurchases = 8;
    private const long SmallPurchaseCents = 1000;

    private static readonly TimeOnly LateStarts = new(22, 0);
    private static readonly TimeOnly LateEnds = new(5, 0);

    public static ReportSection Review(DateOnly today, IReadOnlyList<BudgetCategory> categories, IReadOnlyList<SpendingRow> spending)
    {
        var counted = spending.Where(row => !row.OneOff).ToList();
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var months = Enumerable.Range(1, MonthsCompared)
            .Select(back => thisMonth.AddMonths(-back))
            .Where(month => InMonth(counted, month).Count >= MinPurchasesForMonth)
            .ToList();

        var lines = new List<string>();
        lines.AddRange(Pace(today, thisMonth, categories, counted));

        if (months.Count > 0)
        {
            var month = months[0];
            var rows = InMonth(counted, month);
            var planned = categories.Sum(category => category.MonthlyCents ?? 0);
            var total = rows.Sum(row => row.NetCents);
            if (planned > 0)
            {
                lines.Add($"{Name(month)} total: {Briefing.Money(total)} against your {Briefing.Money(planned)} budget.");
            }

            var over = OverCaps(month, months, categories, counted).ToList();
            lines.AddRange(over);
            if (Underused(months, categories, counted, over.Count > 0) is { } underused)
            {
                lines.Add(underused);
            }
        }

        return new ReportSection("SPENDING REVIEW", lines.Select(line => new ReportRow(line, "")).ToList());
    }

    /// <summary>The single most useful change, in one short line. Null if nothing's over.</summary>
    public static string? Tip(DateOnly today, IReadOnlyList<BudgetCategory> categories, IReadOnlyList<SpendingRow> spending)
    {
        var counted = spending.Where(row => !row.OneOff).ToList();
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var month = Enumerable.Range(1, MonthsCompared)
            .Select(back => thisMonth.AddMonths(-back))
            .Where(candidate => InMonth(counted, candidate).Count >= MinPurchasesForMonth)
            .Cast<DateOnly?>()
            .FirstOrDefault();
        if (month is not { } reviewed)
        {
            return null;
        }

        var rows = InMonth(counted, reviewed);
        var worst = categories
            .Where(category => category.MonthlyCents > 0)
            .Select(category => (Category: category, Over: Spent(rows, category.Name) - category.MonthlyCents!.Value))
            .Where(entry => entry.Over > 0)
            .OrderByDescending(entry => entry.Over)
            .FirstOrDefault();
        if (worst.Category is null)
        {
            return null;
        }

        var purchases = rows.Where(row => Is(row, worst.Category.Name) && row.NetCents > 0).ToList();
        var average = Math.Max(Spent(rows, worst.Category.Name) / Math.Max(purchases.Count, 1), 1);
        var fewer = (int)Math.Ceiling((decimal)worst.Over / average);
        var word = fewer == 1 ? "purchase" : "purchases";
        return $"{worst.Category.Name} was {Briefing.Money(worst.Over)} over in {Name(reviewed)}. {fewer} fewer {word} would have kept it within budget.";
    }

    /// <summary>
    /// Only shown when asked for. Times exist for typed purchases alone, since card and bank exports
    /// carry dates, so late-night figures say what they're based on.
    /// </summary>
    public static IReadOnlyList<string> Patterns(DateOnly today, IReadOnlyList<BudgetCategory> categories, IReadOnlyList<SpendingRow> spending)
    {
        var counted = spending.Where(row => !row.OneOff && row.NetCents > 0).ToList();
        var recent = counted.Where(row => row.Date > today.AddDays(-PatternDays) && row.Date <= today).ToList();
        var lines = new List<string>();

        var timed = recent.Where(row => row.At is not null).ToList();
        var late = timed.Where(row => row.At >= LateStarts || row.At < LateEnds).ToList();
        if (late.Count >= MinLatePurchases)
        {
            lines.Add($"{late.Count} of your {timed.Count} typed purchases in the last {PatternDays} days came after 10 PM, {Briefing.Money(late.Sum(row => row.NetCents))} in all.");
        }

        var small = recent.Where(row => row.NetCents < SmallPurchaseCents).ToList();
        if (small.Count >= MinSmallPurchases)
        {
            lines.Add($"{small.Count} purchases under {Briefing.Money(SmallPurchaseCents)} in the last {PatternDays} days added up to {Briefing.Money(small.Sum(row => row.NetCents))}.");
        }

        var thisMonth = InMonth(counted, new DateOnly(today.Year, today.Month, 1));
        lines.AddRange(categories
            .Where(category => category.MonthlyCents is not null && Spent(thisMonth, category.Name) > category.MonthlyCents)
            .Select(category => $"{category.Name} is at {Briefing.Money(Spent(thisMonth, category.Name))} of {Briefing.Money(category.MonthlyCents!.Value)} this month."));

        if (lines.Count == 0)
        {
            lines.Add($"Nothing stands out in the last {PatternDays} days.");
        }

        lines.Add("Card and bank imports only carry dates, so time of day comes from typed purchases alone.");
        return lines;
    }

    private static IEnumerable<string> Pace(DateOnly today, DateOnly month, IReadOnlyList<BudgetCategory> categories, IReadOnlyList<SpendingRow> counted)
    {
        if (today.Day < MinDaysForPace)
        {
            yield break;
        }

        var days = DateTime.DaysInMonth(today.Year, today.Month);
        foreach (var category in categories.Where(category => category.MonthlyCents > 0))
        {
            var spent = Spent(InMonth(counted, month), category.Name);
            var projected = spent * days / today.Day;
            if (projected > category.MonthlyCents * PaceMargin)
            {
                yield return $"{category.Name} is on pace for about {Briefing.Money(projected)} this month, against {Briefing.Money(category.MonthlyCents!.Value)}.";
            }
        }
    }

    private static IEnumerable<string> OverCaps(DateOnly month, List<DateOnly> months, IReadOnlyList<BudgetCategory> categories, IReadOnlyList<SpendingRow> counted)
    {
        var over = categories
            .Where(category => category.MonthlyCents is not null)
            .Select(category => (Category: category, Spent: Spent(InMonth(counted, month), category.Name)))
            .Where(entry => entry.Spent > entry.Category.MonthlyCents)
            .OrderByDescending(entry => entry.Spent - entry.Category.MonthlyCents);

        foreach (var (category, spent) in over)
        {
            var cap = category.MonthlyCents!.Value;
            var purchases = InMonth(counted, month).Where(row => Is(row, category.Name) && row.NetCents > 0).ToList();
            var line = $"{category.Name}: {Briefing.Money(spent)} against {Briefing.Money(cap)} in {Name(month)}.";

            if (cap > 0 && purchases.Count > 0)
            {
                var average = spent / purchases.Count;
                var fewer = (int)Math.Ceiling((decimal)(spent - cap) / Math.Max(average, 1));
                line += $" {fewer} fewer of your {purchases.Count} purchases, at about {Briefing.Money(average)} each, would close the gap.";
            }

            var top = purchases.GroupBy(row => MerchantName.Clean(row.Title)).OrderByDescending(group => group.Sum(row => row.NetCents)).FirstOrDefault();
            if (top is not null && top.Count() > 1 && !top.Key.Equals(category.Name, StringComparison.OrdinalIgnoreCase))
            {
                line += $" The biggest share was {top.Key}, {Briefing.Money(top.Sum(row => row.NetCents))} across {top.Count()}.";
            }

            if (cap > 0 && months.Count == MonthsCompared && months.All(past => Spent(InMonth(counted, past), category.Name) > cap))
            {
                var average = months.Sum(past => Spent(InMonth(counted, past), category.Name)) / months.Count;
                line += $" It's been over all {months.Count} months, averaging {Briefing.Money(average)}. Want to set a budget for this?";
            }

            yield return line;
        }
    }

    /// <summary>
    /// Categories that used well under their cap, in one line. One with nothing spent at all says nothing, since
    /// its purchases may simply be filed somewhere else. Moving money only comes up when something ran over.
    /// </summary>
    private static string? Underused(List<DateOnly> months, IReadOnlyList<BudgetCategory> categories, IReadOnlyList<SpendingRow> counted, bool anyOver)
    {
        var under = categories
            .Where(category => category.MonthlyCents > 0)
            .Select(category => (Category: category, Average: months.Sum(month => Spent(InMonth(counted, month), category.Name)) / months.Count))
            .Where(entry => entry.Average > 0 && entry.Average < entry.Category.MonthlyCents * UnderusedShare)
            .ToList();
        if (under.Count == 0)
        {
            return null;
        }

        var room = anyOver ? ", so there's room to move some of that to a category that runs over" : "";
        if (under.Count == 1)
        {
            var (category, average) = under[0];
            return $"{category.Name} averaged {Briefing.Money(average)} of {Briefing.Money(category.MonthlyCents!.Value)}{room}.";
        }

        var names = under.Select(entry => entry.Category.Name).ToList();
        var list = $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
        return $"{list} each used less than half of their budget{room}.";
    }

    private static List<SpendingRow> InMonth(IEnumerable<SpendingRow> rows, DateOnly month) =>
        rows.Where(row => row.Date.Year == month.Year && row.Date.Month == month.Month).ToList();

    private static long Spent(IEnumerable<SpendingRow> rows, string category) =>
        rows.Where(row => Is(row, category)).Sum(row => row.NetCents);

    private static bool Is(SpendingRow row, string category) =>
        row.Category.Equals(category, StringComparison.OrdinalIgnoreCase);

    private static string Name(DateOnly month) => month.ToString("MMMM", CultureInfo.InvariantCulture);
}
