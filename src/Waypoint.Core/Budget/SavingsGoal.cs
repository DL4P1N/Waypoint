using System.Globalization;
using System.Text.RegularExpressions;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Budget;

/// <summary>
/// A savings goal the user set. Saved money is what's in savings, cushion, and invested accounts, and
/// StartCents is what they held when the goal was set, so only money added since counts toward it.
/// </summary>
public sealed record SavingsGoal(long TargetCents, DateOnly? By, DateOnly SetOn, long StartCents);

/// <summary>
/// How a goal stands today. SetAsideCents is what should go to savings before NextPayday, or this month when
/// there's no paycheck to go on, to reach it in time. It's zero with no date, or once the goal is met.
/// </summary>
public sealed record SavingsGoalStatus(
    SavingsGoal Goal,
    long SavedCents,
    long SetAsideCents,
    long? PerPaycheckCents,
    long? PerMonthCents,
    DateOnly? NextPayday,
    bool HasSavingsAccount)
{
    public long LeftCents => Math.Max(Goal.TargetCents - SavedCents, 0);

    public bool IsMet => SavedCents >= Goal.TargetCents;

    public double Fill => Goal.TargetCents > 0 ? Math.Clamp((double)Math.Max(SavedCents, 0) / Goal.TargetCents, 0, 1) : 1;
}

/// <summary>
/// Works out a savings goal's progress and pace from balances the user keeps and the pay schedule. It never
/// says where to put the money, only how much keeps the goal on track.
/// </summary>
public static partial class SavingsPlan
{
    private const int LongestPlanDays = 800;

    /// <summary>Money set aside: savings, cushion, and invested accounts. Checking and cards don't count.</summary>
    public static long SavedBalance(IReadOnlyList<BudgetAccount> accounts) =>
        accounts.Where(IsSavings).Sum(account => account.BalanceCents);

    public static bool IsSavings(BudgetAccount account) =>
        account.Kind is AccountKind.Savings or AccountKind.Cushion or AccountKind.Invested;

    /// <summary>A scheduled item that moves money into savings or investing rather than paying a bill, by its name.</summary>
    public static bool IsTransfer(string label) => BankProfiles.Current.SavingWords().IsMatch(label);

    /// <summary>A transfer that buys investments, as opposed to one that only moves money into savings.</summary>
    public static bool IsInvesting(string label) => InvestingWords().IsMatch(label);

    public static SavingsGoalStatus Status(SavingsGoal goal, IReadOnlyList<BudgetAccount> accounts, DateOnly today, IReadOnlyList<ScheduledItem> schedule, IReadOnlyList<PayDeposit>? received = null)
    {
        var saved = SavedBalance(accounts) - goal.StartCents;
        var left = Math.Max(goal.TargetCents - saved, 0);
        var hasAccount = accounts.Any(IsSavings);
        if (goal.By is not { } by || left == 0)
        {
            return new SavingsGoalStatus(goal, saved, 0, null, null, null, hasAccount);
        }

        var days = Math.Clamp(by.DayNumber - today.DayNumber, 0, LongestPlanDays);
        var flows = days > 0 ? BudgetMath.Flows(today, days, schedule, [], received) : [];
        var paydays = flows.Where(flow => flow.Cents > 0).Select(flow => flow.Date).Distinct().ToList();

        if (days == 0)
        {
            return new SavingsGoalStatus(goal, saved, left, null, null, null, hasAccount);
        }

        if (paydays.Count > 0)
        {
            // The money from this paycheck covers the share due before the next one, less any transfer already scheduled then.
            var next = paydays[0];
            var perPaycheck = CeilingDiv(left, paydays.Count);
            var scheduled = flows.Where(flow => flow.Date < next && flow.Cents < 0 && IsTransfer(flow.Label)).Sum(flow => -flow.Cents);
            return new SavingsGoalStatus(goal, saved, Math.Max(perPaycheck - scheduled, 0), perPaycheck, null, next, hasAccount);
        }

        var months = Math.Max(((by.Year - today.Year) * 12) + by.Month - today.Month, 1);
        var perMonth = CeilingDiv(left, months);
        var thisMonth = flows.Where(flow => flow.Date.Month == today.Month && flow.Date.Year == today.Year && flow.Cents < 0 && IsTransfer(flow.Label)).Sum(flow => -flow.Cents);
        return new SavingsGoalStatus(goal, saved, Math.Max(perMonth - thisMonth, 0), null, perMonth, null, hasAccount);
    }

    /// <summary>One or two plain lines about where the goal stands.</summary>
    public static string Describe(SavingsGoalStatus status, DateOnly today)
    {
        var goal = status.Goal;
        var by = goal.By is { } date ? $" by {Short(date)}" : "";
        var headline = $"Savings goal: {Briefing.Money(Math.Max(status.SavedCents, 0))} of {Briefing.Money(goal.TargetCents)}{by}, counting from {Short(goal.SetOn)}.";
        return $"{headline} {Pace(status, today)}";
    }

    /// <summary>The pace that keeps the goal on track, or that it's met, plus a note when nothing measures it yet.</summary>
    public static string Pace(SavingsGoalStatus status, DateOnly today)
    {
        var lines = new List<string>();
        if (status.IsMet)
        {
            lines.Add("You've reached it.");
        }
        else if (status.Goal.By is { } end && end <= today)
        {
            lines.Add($"The date has come, with {Briefing.Money(status.LeftCents)} to go. Pick a new date whenever you like.");
        }
        else if (status.PerPaycheckCents is { } perPaycheck)
        {
            lines.Add($"About {Briefing.Money(perPaycheck)} a paycheck keeps it on track.");
        }
        else if (status.PerMonthCents is { } perMonth)
        {
            lines.Add($"About {Briefing.Money(perMonth)} a month keeps it on track.");
        }
        else
        {
            lines.Add($"{Briefing.Money(status.LeftCents)} to go.");
        }

        if (!status.HasSavingsAccount)
        {
            lines.Add("I don't have a savings or cushion balance to measure it by yet. Set one, such as \"cushion is at $200\".");
        }

        return string.Join(" ", lines);
    }

    private static long CeilingDiv(long cents, int parts) => (cents + parts - 1) / parts;

    private static string Short(DateOnly date) => date.ToString("MMM d", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\b(invest\w*|brokerage|roth|ira|etf|index fund|stocks?|shares)\b", RegexOptions.IgnoreCase)]
    private static partial Regex InvestingWords();
}
