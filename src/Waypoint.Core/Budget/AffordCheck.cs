using System.Globalization;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Budget;

/// <summary>Everything needed to work out a purchase. CheckingSeenOn is the last day the balance was known for sure.</summary>
public sealed record AffordRequest(
    long PriceCents,
    DateOnly Today,
    BudgetAccount? Checking,
    DateOnly? CheckingSeenOn,
    IReadOnlyList<ScheduledItem> Schedule,
    IReadOnlyList<BudgetAccount> Cards,
    IReadOnlyList<PayDeposit>? Received = null,
    string? CardName = null,
    BudgetAccount? Cushion = null,
    long? SafeToSpendCents = null,
    SavingsGoalStatus? Goal = null);

/// <summary>
/// LowestCents is the low point if it's bought, or null without a checking balance. OffGoal is true when what's left
/// after the savings goal's share would be under the checking minimum.
/// </summary>
public sealed record AffordAnswer(long? LowestCents, DateOnly? Payday, bool UnderFloor, string Text, bool OffGoal = false);

/// <summary>
/// Answers yes or no in its first line, then shows what a purchase would do to checking. Assumptions come last,
/// after a blank line, so they can be shown smaller. It sticks to the user's own
/// balances, minimums, and the 30% card share, and never touches investing, debt, or credit decisions.
/// </summary>
public static class AffordCheck
{
    private const int MaxAssumptions = 4;
    private const int StaleDays = 3;

    public static AffordAnswer Check(AffordRequest request)
    {
        if (request.Checking is not { } checking)
        {
            return new AffordAnswer(null, null, false, "I don't have a checking balance yet. Type one, such as \"checking is at $400\", and I can work it out.");
        }

        var card = request.CardName is { } name
            ? request.Cards.FirstOrDefault(candidate => candidate.Name.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase))
            : null;
        var outlook = BudgetMath.UntilPayday(checking.BalanceCents, request.Today, request.Schedule, request.Cards, request.Received);

        var (lowest, headline) = card is null
            ? InCash(request, outlook)
            : OnCard(request, checking, card);

        var minimum = Briefing.Money(BudgetMath.CheckingMinimumCents);
        var overSafe = request.SafeToSpendCents is { } safe && request.PriceCents > safe;
        var safeLine = $"It's more than today's {Briefing.Money(Math.Max(request.SafeToSpendCents ?? 0, 0))} safe to spend.";
        var goalLine = request.Goal is { } goal ? GoalLine(goal, lowest, request.Today) : null;
        var offGoal = request.Goal is { } pace && goalLine is not null
            && lowest >= BudgetMath.CheckingMinimumCents && lowest - pace.SetAsideCents < BudgetMath.CheckingMinimumCents;

        // The answer comes first, in one line, then a few short facts, then what it rests on.
        string verdict;
        var facts = new List<string> { headline };
        if (card is not null && CardShare(card, request.PriceCents, request.Today) is { } share)
        {
            facts.Add(share);
        }

        if (lowest < BudgetMath.CheckingMinimumCents)
        {
            verdict = $"No, not comfortably. Checking would go {Briefing.Money(BudgetMath.CheckingMinimumCents - lowest)} under your {minimum} checking minimum.";
            if (overSafe)
            {
                facts.Add(safeLine);
            }
        }
        else if (overSafe)
        {
            verdict = $"Yes, but it's tight. {safeLine}";
        }
        else if (offGoal)
        {
            verdict = "Yes, but it's tight. It leaves less than the savings goal needs.";
        }
        else
        {
            verdict = $"Yes. Checking stays above your {minimum} checking minimum.";
        }

        if (request.Cushion is { } cushion && cushion.BalanceCents < BudgetMath.CushionFloorCents)
        {
            facts.Add($"Your cushion is {Briefing.Money(BudgetMath.CushionFloorCents - cushion.BalanceCents)} short of {Briefing.Money(BudgetMath.CushionFloorCents)}.");
        }

        if (goalLine is not null)
        {
            facts.Add(goalLine);
        }

        var details = $"{Assumptions(outlook)} {Incomplete(request)}";
        var text = $"{verdict}\n{string.Join("\n", facts.Select(fact => $"• {fact}"))}\n\n{details}";
        return new AffordAnswer(lowest, outlook.Payday?.Date, lowest < BudgetMath.CheckingMinimumCents, text, offGoal);
    }

    /// <summary>
    /// What the savings goal's share does to the low point. Nothing is said without a date to pace it by, or once
    /// it's met, and the minimum line above already covers a purchase that's under the checking minimum by itself.
    /// </summary>
    private static string? GoalLine(SavingsGoalStatus goal, long lowest, DateOnly today)
    {
        if (goal.IsMet || goal.SetAsideCents <= 0 || goal.Goal.By is not { } by)
        {
            return null;
        }

        var when = by <= today ? "now"
            : goal.NextPayday is { } payday ? $"before payday on {Short(payday)}"
            : "this month";
        var after = lowest - goal.SetAsideCents;
        var line = $"To stay on track for {Briefing.Money(goal.Goal.TargetCents)} saved by {Short(by)}, about {Briefing.Money(goal.SetAsideCents)} would go to savings {when}, leaving checking at about {Briefing.Money(after)}.";
        return lowest >= BudgetMath.CheckingMinimumCents && after < BudgetMath.CheckingMinimumCents
            ? $"{line} That's under your checking minimum, so buying it would push the savings goal back."
            : line;
    }

    private static (long Lowest, string Headline) InCash(AffordRequest request, PaydayOutlook outlook)
    {
        var lowest = outlook.LowestCents - request.PriceCents;
        return outlook.Payday is { } payday
            ? (lowest, $"If you buy it, checking would be about {Briefing.Money(lowest)} before payday on {Short(payday.Date)}.")
            : (lowest, $"If you buy it, checking would get as low as about {Briefing.Money(lowest)}. I don't have a paycheck in the next {BudgetMath.ForecastDays} days.");
    }

    /// <summary>A card charge reaches checking at the autopay for the statement it lands on.</summary>
    private static (long Lowest, string Headline) OnCard(AffordRequest request, BudgetAccount checking, BudgetAccount card)
    {
        var before = BudgetMath.Forecast(checking.BalanceCents, request.Today, BudgetMath.ForecastDays, request.Schedule, request.Cards, request.Received);
        if (card.AutopayDay is not { } autopayDay)
        {
            return (before.LowestCents, $"On {card.Name} it won't touch checking until you pay it. Checking stays at about {Briefing.Money(before.LowestCents)} at its lowest.");
        }

        var autopay = BudgetMath.NextMonthDay(request.Today.AddDays(1), autopayDay);
        if (card.CloseDay is { } closeDay && BudgetMath.NextMonthDay(request.Today, closeDay) > autopay)
        {
            return (before.LowestCents, $"On {card.Name} it lands on the next statement, after the {Short(autopay)} autopay, so checking stays at about {Briefing.Money(before.LowestCents)} at its lowest until then.");
        }

        var charged = request.Cards.Select(other => other.Name == card.Name ? other with { BalanceCents = other.BalanceCents + request.PriceCents } : other).ToList();
        var after = BudgetMath.Forecast(checking.BalanceCents, request.Today, BudgetMath.ForecastDays, request.Schedule, charged, request.Received);
        return (after.LowestCents, $"On {card.Name} it comes out with the {Short(autopay)} autopay, and checking would get as low as about {Briefing.Money(after.LowestCents)} on {Short(after.LowestOn)}.");
    }

    /// <summary>
    /// What the charge does to the card's share of its limit by the statement close, since that balance is the one
    /// reported. Over 30%, it says what paying before the close takes to stay at 30%. Null without a limit.
    /// </summary>
    private static string? CardShare(BudgetAccount card, long priceCents, DateOnly today)
    {
        if (card.LimitCents is not { } limit || limit <= 0)
        {
            return null;
        }

        var after = card.BalanceCents + priceCents;
        var when = card.CloseDay is { } closeDay ? $" when it closes {Short(BudgetMath.NextMonthDay(today, closeDay))}" : "";
        var line = $"On {card.Name} that's {Percent(after, limit)}% of its limit{when}, up from {Percent(card.BalanceCents, limit)}%.";
        var pay = BudgetMath.PayDownTo(after, limit, 0.30m);
        return pay > 0
            ? $"{line} Paying {Briefing.Money(pay)} {(when.Length > 0 ? "before then" : "first")} keeps it at 30%."
            : line;
    }

    private static int Percent(long cents, long limit) => (int)Math.Round(cents * 100m / limit);

    private static string Assumptions(PaydayOutlook outlook)
    {
        var flows = (outlook.Payday is { } payday ? new[] { payday } : []).Concat(outlook.Bills).OrderBy(flow => flow.Date).ToList();
        if (flows.Count == 0)
        {
            return "Nothing else is scheduled before then.";
        }

        var shown = flows.Take(MaxAssumptions).Select(flow => flow.Cents > 0
            ? $"{flow.Label} {Briefing.Money(flow.Cents)} expected {Short(flow.Date)}"
            : $"{flow.Label} {Briefing.Money(-flow.Cents)} on {Short(flow.Date)}");
        var more = flows.Count > MaxAssumptions ? $", and {flows.Count - MaxAssumptions} more" : "";
        return $"That assumes these happen: {string.Join(", ", shown)}{more}.";
    }

    private static string Incomplete(AffordRequest request) =>
        request.CheckingSeenOn is { } seen && request.Today.DayNumber - seen.DayNumber > StaleDays
            ? $"Checking was last confirmed {Short(seen)}, and I only know the bills and purchases that reached me, so treat this as rough."
            : "I only know the bills and purchases that reached me.";

    private static string Short(DateOnly date) => date.ToString("MMM d", CultureInfo.InvariantCulture);
}
