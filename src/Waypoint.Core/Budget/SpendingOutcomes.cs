using System.Globalization;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Budget;

/// <summary>A purchase taken off the wait list, with what was decided: bought, skipped, or just decided.</summary>
public sealed record SettledWait(string Description, long PriceCents, DateTime AddedAt, string Decision);

/// <summary>
/// Which spending helps have made a difference, from what's already kept. It only counts what can be checked: the
/// wait list's decisions, and whether a category came down the month after it went over its cap, which is what the
/// spending review points out. Each says plainly when there's too little to tell, and nothing here is a score.
/// </summary>
public static class SpendingOutcomes
{
    public const string Title = "WHAT'S HELPING";

    /// <summary>Fewer than this many decisions, or months over a cap, isn't enough to say anything.</summary>
    public const int MinCases = 3;

    /// <summary>The same bar the spending review uses for a month to count.</summary>
    public const int MinPurchasesForMonth = 10;

    /// <summary>How far back the cap months are looked at.</summary>
    public const int MonthsBack = 12;

    public const string Bought = "bought";
    public const string Skipped = "skipped";

    /// <summary>
    /// The lines for the Spending review page and for "what's helping my spending?". StartedOn is when Waypoint was
    /// first used, since a month before it couldn't have been helped by anything here.
    /// </summary>
    public static ReportSection Build(DateOnly today, IReadOnlyList<SettledWait> settled, IReadOnlyList<BudgetCategory> categories, IReadOnlyList<SpendingRow> spending, DateOnly? startedOn)
    {
        var lines = new List<string> { WaitLine(settled) };
        lines.AddRange(CapLines(today, categories, spending, startedOn));
        lines.Add("Can I afford this? checks and purchase alerts aren't kept, so there's nothing to measure for them.");
        return new ReportSection(Title, lines.Select(line => new ReportRow(line, "")).ToList());
    }

    /// <summary>What the page shows while nothing can be told yet.</summary>
    public const string NotYet = "This fills in after a few months.";

    /// <summary>
    /// The section as the Spending review page shows it: only lines with something to report, or one short line
    /// while there's nothing yet. Asking still gets every line, with why.
    /// </summary>
    public static ReportSection ForPage(ReportSection full)
    {
        var results = full.Rows.Where(row => !IsWaiting(row.Label)).ToList();
        return results.Count > 0 ? full with { Rows = results } : new ReportSection(full.Title, [new ReportRow(NotYet, "")]);
    }

    private static bool IsWaiting(string line) =>
        line.Contains("not enough yet", StringComparison.Ordinal)
        || line.Contains("nothing decided yet", StringComparison.Ordinal)
        || line.StartsWith("Can I afford this? checks", StringComparison.Ordinal);

    /// <summary>The same lines as one reply, for the assistant.</summary>
    public static string Describe(ReportSection section) => string.Join("\n", section.Rows.Select(row => $"- {row.Label}"));

    private static string WaitLine(IReadOnlyList<SettledWait> settled)
    {
        var bought = settled.Count(wait => Is(wait, Bought));
        var skipped = settled.Where(wait => Is(wait, Skipped)).ToList();
        var decided = bought + skipped.Count;
        var unsaid = settled.Count - decided;
        var more = unsaid == 0 ? "" : unsaid == 1 ? " 1 more came off without saying which." : $" {unsaid} more came off without saying which.";

        if (decided < MinCases)
        {
            return decided == 0 && unsaid == 0
                ? "Wait list: nothing decided yet, so there's no way to tell if waiting helps."
                : $"Wait list: not enough yet. {Count(decided, "decision")} so far, and it takes {MinCases} to say anything.{more}";
        }

        if (skipped.Count == 0)
        {
            return $"Wait list: you bought all {decided} things you parked, so waiting hasn't changed what you buy yet.{more}";
        }

        var saved = skipped.Sum(wait => wait.PriceCents);
        return $"Wait list: you let go of {skipped.Count} of the {decided} things you parked, about {Briefing.Money(saved)} not spent.{more}";
    }

    private static IEnumerable<string> CapLines(DateOnly today, IReadOnlyList<BudgetCategory> categories, IReadOnlyList<SpendingRow> spending, DateOnly? startedOn)
    {
        var counted = spending.Where(row => !row.OneOff).ToList();
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var started = startedOn is { } on ? new DateOnly(on.Year, on.Month, 1) : thisMonth;
        var cases = new List<(string Category, long Over, long After)>();

        // A month over its cap shows in the review all through the next month, so the next month is the one to compare.
        for (var back = 2; back <= MonthsBack + 1; back++)
        {
            var month = thisMonth.AddMonths(-back);
            var after = month.AddMonths(1);
            if (after < started || InMonth(counted, month).Count < MinPurchasesForMonth || InMonth(counted, after).Count < MinPurchasesForMonth)
            {
                continue;
            }

            foreach (var category in categories.Where(category => category.MonthlyCents > 0))
            {
                var spent = Spent(counted, month, category.Name);
                if (spent > category.MonthlyCents)
                {
                    cases.Add((category.Name, spent, Spent(counted, after, category.Name)));
                }
            }
        }

        if (cases.Count < MinCases)
        {
            yield return $"Spending review: not enough yet. It takes {MinCases} months over budget, each followed by a full month, and there {(cases.Count == 1 ? "is 1" : $"are {cases.Count}")} so far.";
            yield break;
        }

        var lower = cases.Count(entry => entry.After < entry.Over);
        var change = cases.Sum(entry => entry.Over - entry.After);
        var names = string.Join(", ", cases.Select(entry => entry.Category).Distinct(StringComparer.OrdinalIgnoreCase));
        var total = change > 0 ? $", about {Briefing.Money(change)} less in all" : change < 0 ? $", though about {Briefing.Money(-change)} more in all" : "";
        yield return $"Spending review: after a month over budget, the next month was lower {lower} of {cases.Count} times{total} ({names}).";
        yield return "That compares against today's budgets, and it can't show the review is what made the difference.";
    }

    private static bool Is(SettledWait wait, string decision) => wait.Decision.Trim().Equals(decision, StringComparison.OrdinalIgnoreCase);

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : string.Create(CultureInfo.InvariantCulture, $"{count} {noun}s");

    private static List<SpendingRow> InMonth(IEnumerable<SpendingRow> rows, DateOnly month) =>
        rows.Where(row => row.Date.Year == month.Year && row.Date.Month == month.Month).ToList();

    private static long Spent(IEnumerable<SpendingRow> rows, DateOnly month, string category) =>
        InMonth(rows, month).Where(row => row.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).Sum(row => row.NetCents);
}
