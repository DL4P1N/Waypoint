using Waypoint.Core.Capture;

namespace Waypoint.Core.Budget;

public enum AccountKind
{
    Checking,
    Savings,
    Card,
    Cushion,
    Invested,
}

/// <summary>Whether a category is watched week by week or only over the whole month.</summary>
public enum BudgetPeriod
{
    Month,
    Week,
}

/// <summary>A spending category. MonthlyCents is null if it's tracked but has no cap. A weekly one gets its week cap from it.</summary>
public sealed record BudgetCategory(string Name, long? MonthlyCents, BudgetPeriod Period = BudgetPeriod.Month);

/// <summary>
/// A balance the user keeps up to date. Limit, close day, and autopay day are only for cards. SetAt is the moment a
/// balance was typed, when it was typed for that day, and null for one from before it was kept or from an alert.
/// </summary>
public sealed record BudgetAccount(
    string Name,
    AccountKind Kind,
    long BalanceCents,
    DateOnly UpdatedOn,
    long? LimitCents = null,
    int? CloseDay = null,
    int? AutopayDay = null,
    long? AlertCents = null,
    DateTimeOffset? SetAt = null);

/// <summary>How a scheduled item comes around. MonthDays covers both monthly and twice a month.</summary>
public enum PayPattern
{
    MonthDays,
    Weekly,
    Biweekly,
    Once,
}

/// <summary>
/// Money that moves on a schedule. Days are for MonthDays, and Anchor is any one date for Weekly and
/// Biweekly, or the date for Once. Paydays land earlier when the day is on a weekend or bank holiday.
/// Dates are exact ones from a pay calendar, which win over the pattern for the span they cover.
/// A bill has a negative amount. Account is the card it's charged to, or null when it comes out of checking.
/// Amounts are estimates for particular dates, such as a paycheck worked out from the shifts in its period,
/// and AmountCents covers every other date.
/// </summary>
public sealed record ScheduledItem(
    string Label,
    long AmountCents,
    int[] Days,
    bool EarlierOnWeekend = false,
    PayPattern Pattern = PayPattern.MonthDays,
    DateOnly? Anchor = null,
    long Id = 0,
    IReadOnlyList<DateOnly>? Dates = null,
    string? Account = null,
    IReadOnlyDictionary<DateOnly, long>? Amounts = null)
{
    public bool IsBill => AmountCents < 0;

    public long AmountOn(DateOnly day) => Amounts is not null && Amounts.TryGetValue(day, out var cents) ? cents : AmountCents;
}

/// <summary>
/// The main paycheck as the pay schedule form starts out. Pattern is the form's choice, or null when none fits.
/// HourlyRate is set when paychecks are estimated from shifts, and CalendarThrough when a pay calendar sets the dates.
/// </summary>
public sealed record PayScheduleForm(string? Pattern, int[] Days, DateOnly? Next, long Cents, decimal? HourlyRate, DateOnly? CalendarThrough);

/// <summary>An expected deposit that really arrived. DueOn is the date it was expected, as it lands.</summary>
public sealed record PayDeposit(string Label, DateOnly DueOn, DateOnly ReceivedOn, long Cents, bool Typed);

/// <summary>One movement of money the forecast expects.</summary>
public sealed record CashFlow(DateOnly Date, string Label, long Cents, bool IsCard = false);

/// <summary>What happens between now and the next expected paycheck. Payday is null if none is scheduled.</summary>
public sealed record PaydayOutlook(CashFlow? Payday, long LowestCents, DateOnly LowestOn, IReadOnlyList<CashFlow> Bills);

/// <summary>One expense as it counts against the budget.</summary>
public sealed record SpendingRow(DateOnly Date, string Title, long AmountCents, string Category, bool OneOff, long ReimbursedCents, string Source = SpendingRow.TypedSource, TimeOnly? At = null, long Id = 0)
{
    public const string TypedSource = "Typed";

    /// <summary>Starts the capture text of a purchase read from a bank or card, followed by the account.</summary>
    public const string ImportedPrefix = "Imported: ";

    /// <summary>Where a purchase came from, in a short name, from the text of its capture.</summary>
    public static string SourceOf(string captureText)
    {
        if (!captureText.StartsWith(ImportedPrefix, StringComparison.Ordinal))
        {
            return TypedSource;
        }

        return BankProfiles.Current.ShortNameOf(captureText[ImportedPrefix.Length..]);
    }

    public long NetCents => AmountCents - ReimbursedCents;
}

/// <summary>A purchase that didn't come from typing. Key keeps it from counting twice.</summary>
/// <summary>
/// One purchase from an export. SuggestedCategories are the bank's category as names it might go by here, best first.
/// Only one that exists is used, since categories can be renamed.
/// </summary>
/// Restaurant is set when the bank files it as eating out (<see cref="RestaurantPurchases"/>).
public sealed record ImportedTransaction(DateOnly Date, string Description, long AmountCents, string? Key = null, IReadOnlyList<string>? SuggestedCategories = null, bool Restaurant = false);

/// <summary>A non-essential purchase waiting out its cooling-off time.</summary>
public sealed record WaitItem(string Description, long PriceCents, DateTime AddedAt, int Hours = BudgetMath.WaitHours, string? Reason = null, long Id = 0)
{
    public DateTime ReadyAt => AddedAt.AddHours(Hours);

    /// <summary>
    /// The one parked purchase this imported row clearly is: a word from its name in the merchant, at about
    /// its price, soon after it was parked. Anything less sure, or two that fit, returns null.
    /// </summary>
    public static WaitItem? MatchImport(IReadOnlyList<WaitItem> waiting, ImportedTransaction row)
    {
        var matches = waiting.Where(item =>
            row.AmountCents >= item.PriceCents * 0.9m
            && row.AmountCents <= item.PriceCents * 1.15m
            && row.Date >= DateOnly.FromDateTime(item.AddedAt)
            && row.Date <= DateOnly.FromDateTime(item.AddedAt).AddDays(BudgetMath.WaitMatchDays)
            && SearchIndex.Words(item.Description).Any(word => row.Description.Contains(word, StringComparison.OrdinalIgnoreCase))).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}

/// <summary>Money from something other than the main paycheck.</summary>
public sealed record OtherIncome(DateOnly Date, string Source, long AmountCents);

public sealed record ImportResult(int Added, int Skipped, int WaitsResolved = 0);

public enum BudgetOpKind
{
    SetCap,
    SetBalance,
    SetLimit,
    Reimburse,
    Categorize,
    AddWait,
    ResolveWait,
    AddIncome,
    MarkOneOff,
    SetPeriod,
    RenameCategory,

    /// <summary>A new account with its balance, from the Budget page. Category is the account kind. Never sorted from typing.</summary>
    AddAccount,
}

/// <summary>A typed update to the budget.</summary>
public sealed record BudgetOp(BudgetOpKind Kind, string Name, long? AmountCents, string? Category, int? Hours = null, string? Reason = null);

public sealed record ForecastResult(long LowestCents, DateOnly LowestOn, long EndCents, DateOnly EndOn);

/// <summary>One category's month, with its purchases. The week cap is only there for weekly categories, in the current month.</summary>
public sealed record CategoryDetail(string Name, long? CapCents, long MonthCents, long? WeekCapCents, long? WeekCents, IReadOnlyList<SpendingRow> Rows, BudgetPeriod Period = BudgetPeriod.Month)
{
    public bool IsOver => (CapCents is { } cap && MonthCents > cap) || (WeekCapCents is { } weekCap && WeekCents > weekCap);
}

/// <summary>One line of a report. Note is how a flagged row reads in a short list of what's worth a look: what it is and what would help.</summary>
public sealed record ReportRow(string Label, string Value, bool Flagged = false, string? Note = null);

public sealed record ReportSection(string Title, IReadOnlyList<ReportRow> Rows);

public static class BudgetMath
{
    public const long CheckingMinimumCents = 15000;
    public const long CushionFloorCents = 50000;
    public const long CushionTopCents = 60000;
    public const string DefaultCategory = "Buffer";

    /// <summary>Stored for a purchase left uncategorized on purpose, such as eating out with no category for it. Never a category itself.</summary>
    public const string Uncategorized = "";

    /// <summary>What an uncategorized purchase is listed under.</summary>
    public const string UncategorizedLabel = "Uncategorized";
    public const int WaitHours = 48;
    public const int WaitMatchDays = 30;
    public const int PayMatchDays = 3;

    public const int TypedPayRatio = 2;
    public const int ForecastDays = 35;
    public const decimal PayMatchShare = 0.2m;
    public const decimal FreelanceTaxShare = 0.25m;

    private const decimal WeeksPerMonth = 52m / 12m;
    private const int LongestMove = 4;
    private const int LookAheadDays = 400;

    /// <summary>A monthly cap as a weekly one, rounded up to a whole dollar.</summary>
    public static long WeeklyCap(long monthlyCents) => (long)Math.Ceiling(monthlyCents / WeeksPerMonth / 100m) * 100;

    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>The next date this day of the month comes around, today included.</summary>
    public static DateOnly NextMonthDay(DateOnly today, int day)
    {
        var date = OnDay(today.Year, today.Month, day);
        if (date < today)
        {
            var next = today.AddMonths(1);
            date = OnDay(next.Year, next.Month, day);
        }

        return date;
    }

    /// <summary>What to pay so the balance lands at this share of the limit. Zero if it's already there.</summary>
    public static long PayDownTo(long balanceCents, long limitCents, decimal share) =>
        Math.Max(balanceCents - (long)(limitCents * share), 0);

    /// <summary>The next date a scheduled item lands, today included, skipping any that already arrived. Null if it's done.</summary>
    public static DateOnly? NextOccurrence(ScheduledItem item, DateOnly today, IReadOnlyList<PayDeposit>? received = null)
    {
        for (var day = today; day <= today.AddDays(LookAheadDays); day = day.AddDays(1))
        {
            if (Falls(item, day) && !Arrived(item, day, received))
            {
                return day;
            }
        }

        return null;
    }

    /// <summary>
    /// What's expected after today, in date order. Anything already received is left out, so it can't count twice.
    /// A bill charged to a card doesn't touch checking itself. It's added to that card's next autopay when it lands
    /// on the statement the autopay pays, and later ones wait for a statement that hasn't happened yet.
    /// </summary>
    public static IReadOnlyList<CashFlow> Flows(DateOnly today, int days, IReadOnlyList<ScheduledItem> items, IReadOnlyList<BudgetAccount> cards, IReadOnlyList<PayDeposit>? received = null)
    {
        var flows = new List<CashFlow>();
        var unpaid = cards.Where(card => card.AutopayDay is not null).ToList();
        var charged = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        for (var offset = 1; offset <= days; offset++)
        {
            var day = today.AddDays(offset);
            foreach (var item in items.Where(item => Falls(item, day) && !Arrived(item, day, received)))
            {
                if (ChargedTo(item, cards) is not { } card)
                {
                    flows.Add(new CashFlow(day, item.Label, item.AmountOn(day)));
                }
                else if (unpaid.Contains(card) && day <= StatementCutoff(card, today))
                {
                    charged[card.Name] = charged.GetValueOrDefault(card.Name) - item.AmountOn(day);
                }
            }

            // Only the next autopay is known, since later statements haven't happened yet.
            foreach (var card in unpaid.Where(card => OnDay(day.Year, day.Month, card.AutopayDay!.Value) == day).ToList())
            {
                flows.Add(new CashFlow(day, $"{card.Name} autopay", -(card.BalanceCents + charged.GetValueOrDefault(card.Name)), true));
                unpaid.Remove(card);
            }
        }

        return flows;
    }

    /// <summary>The card a bill is charged to, or null when it comes out of checking or its card is gone.</summary>
    private static BudgetAccount? ChargedTo(ScheduledItem item, IReadOnlyList<BudgetAccount> cards) =>
        item.Account is { } name ? cards.FirstOrDefault(card => card.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) : null;

    /// <summary>
    /// The last day a charge still makes the card's next autopay: the statement close before it, or the day before
    /// the autopay when the close day isn't known.
    /// </summary>
    private static DateOnly StatementCutoff(BudgetAccount card, DateOnly today)
    {
        var autopay = NextMonthDay(today.AddDays(1), card.AutopayDay!.Value);
        if (card.CloseDay is not { } closeDay)
        {
            return autopay.AddDays(-1);
        }

        var close = OnDay(autopay.Year, autopay.Month, closeDay);
        if (close >= autopay)
        {
            var before = autopay.AddMonths(-1);
            close = OnDay(before.Year, before.Month, closeDay);
        }

        return close;
    }

    public static ForecastResult Forecast(long startCents, DateOnly today, int days, IReadOnlyList<ScheduledItem> items, IReadOnlyList<BudgetAccount> cards, IReadOnlyList<PayDeposit>? received = null)
    {
        var balance = startCents;
        var lowest = startCents;
        var lowestOn = today;
        var flows = Flows(today, days, items, cards, received).ToLookup(flow => flow.Date);

        for (var offset = 1; offset <= days; offset++)
        {
            var day = today.AddDays(offset);
            balance += flows[day].Sum(flow => flow.Cents);

            if (balance < lowest)
            {
                lowest = balance;
                lowestOn = day;
            }
        }

        return new ForecastResult(lowest, lowestOn, balance, today.AddDays(days));
    }

    /// <summary>
    /// The lowest the balance gets on a day still to come, and when. Today isn't counted, so a balance that only
    /// rises shows the dip that's actually ahead rather than repeating today's number. Null with nothing ahead.
    /// </summary>
    public static (long Cents, DateOnly On)? LowestAhead(long startCents, DateOnly today, int days, IReadOnlyList<ScheduledItem> items, IReadOnlyList<BudgetAccount> cards, IReadOnlyList<PayDeposit>? received = null)
    {
        var flows = Flows(today, days, items, cards, received).ToLookup(flow => flow.Date);
        var balance = startCents;
        (long Cents, DateOnly On)? lowest = null;
        for (var offset = 1; offset <= days; offset++)
        {
            var day = today.AddDays(offset);
            if (!flows[day].Any())
            {
                continue;
            }

            balance += flows[day].Sum(flow => flow.Cents);
            if (lowest is null || balance < lowest.Value.Cents)
            {
                lowest = (balance, day);
            }
        }

        return lowest;
    }

    /// <summary>The bills and low point before the next expected paycheck lands.</summary>
    public static PaydayOutlook UntilPayday(long startCents, DateOnly today, IReadOnlyList<ScheduledItem> items, IReadOnlyList<BudgetAccount> cards, IReadOnlyList<PayDeposit>? received = null)
    {
        var flows = Flows(today, ForecastDays, items, cards, received);
        var payday = flows.FirstOrDefault(flow => flow.Cents > 0);
        var before = flows.Where(flow => payday is null || flow.Date < payday.Date).ToList();

        var balance = startCents;
        var lowest = startCents;
        var lowestOn = today;
        foreach (var day in before.GroupBy(flow => flow.Date))
        {
            balance += day.Sum(flow => flow.Cents);
            if (balance < lowest)
            {
                lowest = balance;
                lowestOn = day.Key;
            }
        }

        return new PaydayOutlook(payday, lowest, lowestOn, before.Where(flow => flow.Cents < 0).ToList());
    }

    /// <summary>
    /// The expected date a real deposit belongs to, nearest first. Null if it's too far off in date or amount. The
    /// amount may be near the estimate for that payday or the flat amount, since either could be the closer guess.
    /// </summary>
    public static DateOnly? ExpectedNear(ScheduledItem item, DateOnly date, long? cents) =>
        Enumerable.Range(-PayMatchDays, (PayMatchDays * 2) + 1)
            .OrderBy(Math.Abs)
            .Select(offset => date.AddDays(offset))
            .Where(day => Falls(item, day))
            .Where(day => cents is not { } amount || Near(amount, item.AmountCents) || Near(amount, item.AmountOn(day)))
            .Cast<DateOnly?>()
            .FirstOrDefault();

    private static bool Near(long amount, long expected) => Math.Abs(amount - expected) <= expected * PayMatchShare;

    public static bool Falls(ScheduledItem item, DateOnly day)
    {
        if (item.Dates is { Count: > 0 } dates && day >= dates[0] && day <= dates[^1])
        {
            return dates.Contains(day);
        }

        switch (item.Pattern)
        {
            case PayPattern.Once:
                return item.Anchor == day;
            case PayPattern.Weekly:
            case PayPattern.Biweekly:
                var cycle = item.Pattern == PayPattern.Weekly ? 7 : 14;
                return item.Anchor is { } anchor && Enumerable.Range(0, LongestMove + 1)
                    .Select(offset => day.AddDays(offset))
                    .Any(date => ((date.DayNumber - anchor.DayNumber) % cycle + cycle) % cycle == 0 && Moved(date, item) == day);
            default:
                var next = day.AddMonths(1);
                return item.Days.Any(target => Moved(OnDay(day.Year, day.Month, target), item) == day || Moved(OnDay(next.Year, next.Month, target), item) == day);
        }
    }

    private static bool Arrived(ScheduledItem item, DateOnly day, IReadOnlyList<PayDeposit>? received) =>
        received?.Any(deposit => deposit.Label == item.Label && (deposit.DueOn == day || CalendarMoved(item, deposit, day))) == true;

    /// <summary>A deposit matched before a calendar came in may sit a few days off the calendar's date for it.</summary>
    private static bool CalendarMoved(ScheduledItem item, PayDeposit deposit, DateOnly day) =>
        item.Dates is { Count: > 0 } dates && !dates.Contains(deposit.DueOn) && Math.Abs(deposit.DueOn.DayNumber - day.DayNumber) <= PayMatchDays;

    private static DateOnly Moved(DateOnly date, ScheduledItem item)
    {
        if (!item.EarlierOnWeekend)
        {
            return date;
        }

        while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || BankHolidays.IsHoliday(date))
        {
            date = date.AddDays(-1);
        }

        return date;
    }

    private static DateOnly OnDay(int year, int month, int day) =>
        new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));
}

public static class BudgetReport
{
    /// <summary>
    /// Each account as it stands, for the chat and for its reply after checking email. Pass accounts brought up to
    /// date, so a card already counts the purchases emailed since its balance was confirmed.
    /// </summary>
    public static IReadOnlyList<string> BalanceLines(IReadOnlyList<BudgetAccount> accounts) =>
        accounts.Select(account => account.Kind switch
        {
            AccountKind.Card => $"{account.Name}: {Briefing.Money(account.BalanceCents)} owed"
                + (account.LimitCents is { } limit ? $" of {Briefing.Money(limit)}" : "")
                + $", confirmed {Short(account.UpdatedOn)} plus purchases since",
            _ => $"{account.Name}: {Briefing.Money(account.BalanceCents)} as of {Short(account.UpdatedOn)}",
        }).ToList();

    public static IReadOnlyList<ReportSection> Build(
        DateOnly today,
        IReadOnlyList<BudgetCategory> categories,
        IReadOnlyList<SpendingRow> monthSpending,
        IReadOnlyList<BudgetAccount> accounts,
        IReadOnlyList<ScheduledItem> schedule,
        IReadOnlyList<PayDeposit>? received = null)
    {
        var sections = new List<ReportSection>();
        var counted = monthSpending.Where(row => !row.OneOff).ToList();
        var weekStart = BudgetMath.WeekStart(today);
        var monthStart = new DateOnly(today.Year, today.Month, 1);

        sections.Add(new ReportSection("THIS WEEK", categories.Where(category => category.Period == BudgetPeriod.Week).Select(category =>
        {
            var spent = counted.Where(row => row.Category == category.Name && row.Date >= weekStart).Sum(row => row.NetCents);
            return CapRow(category, spent, category.MonthlyCents is { } monthly ? BudgetMath.WeeklyCap(monthly) : null);
        }).ToList()));

        sections.Add(new ReportSection("THIS MONTH", categories.Where(category => category.Period == BudgetPeriod.Month).Select(category =>
            CapRow(category, counted.Where(row => row.Category == category.Name && row.Date >= monthStart).Sum(row => row.NetCents), category.MonthlyCents)).ToList()));

        var cards = accounts.Where(account => account.Kind == AccountKind.Card).ToList();
        if (cards.Count > 0)
        {
            sections.Add(new ReportSection("CARDS", cards.SelectMany(card => CardRows(card, today)).Concat(AllCards(cards)).ToList()));
        }

        if (accounts.FirstOrDefault(account => account.Kind == AccountKind.Checking) is { } checking)
        {
            var forecast = BudgetMath.Forecast(checking.BalanceCents, today, BudgetMath.ForecastDays, schedule, cards, received);
            var ahead = BudgetMath.LowestAhead(checking.BalanceCents, today, BudgetMath.ForecastDays, schedule, cards, received);
            sections.Add(new ReportSection("CHECKING", [
                new ReportRow($"{checking.Name} (as of {Short(checking.UpdatedOn)})", Briefing.Money(checking.BalanceCents), checking.BalanceCents < BudgetMath.CheckingMinimumCents,
                    $"{checking.Name} is at {Briefing.Money(checking.BalanceCents)}, under your {Briefing.Money(BudgetMath.CheckingMinimumCents)} checking minimum."),
                ahead is { } low
                    ? new ReportRow($"Lowest ahead, {Short(low.On)}", Briefing.Money(low.Cents), low.Cents < BudgetMath.CheckingMinimumCents,
                        $"Checking dips to {Briefing.Money(low.Cents)} on {Short(low.On)}, under your {Briefing.Money(BudgetMath.CheckingMinimumCents)} checking minimum.")
                    : new ReportRow("Lowest ahead", "Nothing scheduled"),
                new ReportRow($"Projected {Short(forecast.EndOn)}", Briefing.Money(forecast.EndCents)),
            ]));

            if (schedule.Any(item => item.AmountCents > 0))
            {
                sections.Add(UntilPayday(BudgetMath.UntilPayday(checking.BalanceCents, today, schedule, cards, received), received));
            }
        }

        if (accounts.FirstOrDefault(account => account.Kind == AccountKind.Cushion) is { } cushion)
        {
            var short_ = BudgetMath.CushionFloorCents - cushion.BalanceCents;
            var over = cushion.BalanceCents - BudgetMath.CushionTopCents;
            var rows = new List<ReportRow>
            {
                new($"{cushion.Name} (as of {Short(cushion.UpdatedOn)})", Briefing.Money(cushion.BalanceCents), short_ > 0,
                    $"Cushion is {Briefing.Money(short_)} short of {Briefing.Money(BudgetMath.CushionFloorCents)}."),
                new($"{Briefing.Money(BudgetMath.CushionFloorCents)} cushion goal", short_ > 0 ? $"{Briefing.Money(short_)} to go" : "Met"),
            };

            if (over > 0)
            {
                rows.Add(new ReportRow($"Past {Briefing.Money(BudgetMath.CushionTopCents)}, ready to invest", Briefing.Money(over), true,
                    $"Cushion has {Briefing.Money(over)} more than {Briefing.Money(BudgetMath.CushionTopCents)}, ready to invest when you're ready."));
            }

            sections.Add(new ReportSection("CUSHION", rows));
        }

        var assets = accounts.Where(account => account.Kind != AccountKind.Card).Sum(account => account.BalanceCents);
        var owed = cards.Sum(card => card.BalanceCents);
        // With no accounts yet, net worth is only a row of zeros, so it waits until there's a balance.
        if (accounts.Count == 0)
        {
            return sections;
        }

        sections.Add(new ReportSection("NET WORTH", [
            new ReportRow("Assets", Briefing.Money(assets)),
            new ReportRow("Owed", Briefing.Money(owed)),
            new ReportRow("Net worth", Briefing.Money(assets - owed)),
        ]));

        return sections;
    }

    /// <summary>Estimates are labeled as expected, and only a real deposit reads as confirmed.</summary>
    private static ReportSection UntilPayday(PaydayOutlook outlook, IReadOnlyList<PayDeposit>? received)
    {
        var rows = new List<ReportRow>();
        if (outlook.Payday is { } payday)
        {
            rows.Add(new ReportRow($"{payday.Label}, expected {Short(payday.Date)}", Briefing.Money(payday.Cents)));
            rows.Add(new ReportRow($"Lowest before then, {Short(outlook.LowestOn)}", Briefing.Money(outlook.LowestCents)));
        }

        rows.AddRange(outlook.Bills.Select(bill => new ReportRow($"{bill.Label}, {Short(bill.Date)}", Briefing.Money(bill.Cents))));

        if (received?.MaxBy(deposit => deposit.ReceivedOn) is { } last)
        {
            rows.Add(new ReportRow($"Last {last.Label.ToLowerInvariant()}, {(last.Typed ? "typed" : "confirmed")} {Short(last.ReceivedOn)}", Briefing.Money(last.Cents)));
        }

        return new ReportSection("UNTIL PAYDAY", rows);
    }

    /// <summary>
    /// One row per category for a month, in budget order. Anything filed under a category that's
    /// since been removed still shows, so no spending goes missing.
    /// </summary>
    public static IReadOnlyList<CategoryDetail> Categories(
        DateOnly month, DateOnly today, IReadOnlyList<BudgetCategory> categories, IReadOnlyList<SpendingRow> spending)
    {
        var last = month.AddMonths(1).AddDays(-1);
        var isCurrent = today >= month && today <= last;
        var weekStart = BudgetMath.WeekStart(today);
        var names = categories.Select(category => category.Name)
            .Concat(spending.Select(row => row.Category))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return names.Select(name =>
        {
            var category = categories.FirstOrDefault(category => category.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var cap = category?.MonthlyCents;
            var period = category?.Period ?? BudgetPeriod.Month;
            var rows = spending.Where(row => row.Category.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            var monthRows = rows.Where(row => row.Date >= month && row.Date <= last).ToList();
            var week = rows.Where(row => !row.OneOff && row.Date >= weekStart && row.Date <= today).Sum(row => row.NetCents);

            return new CategoryDetail(
                name,
                cap,
                monthRows.Where(row => !row.OneOff).Sum(row => row.NetCents),
                isCurrent && period == BudgetPeriod.Week && cap is { } monthly ? BudgetMath.WeeklyCap(monthly) : null,
                isCurrent ? week : null,
                monthRows,
                period);
        }).ToList();
    }

    /// <summary>What's left this week across the weekly categories.</summary>
    public static long LeftThisWeek(IReadOnlyList<CategoryDetail> details) =>
        details.Sum(detail => Math.Max((detail.WeekCapCents ?? 0) - (detail.WeekCents ?? 0), 0));

    /// <summary>What's left this month across the monthly categories, which weekly ones never add to.</summary>
    public static long LeftThisMonth(IReadOnlyList<CategoryDetail> details) =>
        details.Where(detail => detail.Period == BudgetPeriod.Month).Sum(detail => Math.Max((detail.CapCents ?? 0) - detail.MonthCents, 0));

    /// <summary>Where the money went this month, biggest first.</summary>
    public static ReportSection TopMerchants(IReadOnlyList<CategoryDetail> details)
    {
        const int shown = 6;
        var rows = details
            .SelectMany(detail => detail.Rows)
            .Where(row => !row.OneOff)
            .GroupBy(row => MerchantName.Clean(row.Title), StringComparer.OrdinalIgnoreCase)
            .Select(group => (Name: group.Key, Cents: group.Sum(row => row.NetCents), Count: group.Count()))
            .Where(merchant => merchant.Cents > 0)
            .OrderByDescending(merchant => merchant.Cents)
            .Take(shown)
            .Select(merchant => new ReportRow(merchant.Count > 1 ? $"{merchant.Name} ({merchant.Count})" : merchant.Name, Briefing.Money(merchant.Cents)))
            .ToList();

        return new ReportSection("TOP MERCHANTS", rows);
    }

    /// <summary>
    /// The wait list, what's coming up, and any set-aside for other income. A transfer coming up says whether the last
    /// one was seen happening.
    /// </summary>
    public static IReadOnlyList<ReportSection> Extras(DateTime now, IReadOnlyList<WaitItem> waiting, IReadOnlyList<ScheduledItem> schedule, IReadOnlyList<OtherIncome> income, IReadOnlyList<PayDeposit>? received = null, IReadOnlyList<TransferStatus>? transfers = null)
    {
        var sections = new List<ReportSection>();
        var today = DateOnly.FromDateTime(now);

        if (waiting.Count > 0)
        {
            sections.Add(new ReportSection("WAIT LIST", waiting.Select(item => item.ReadyAt <= now
                ? new ReportRow($"{item.Description} is ready to decide on", Briefing.Money(item.PriceCents), true,
                    $"{item.Description} ({Briefing.Money(item.PriceCents)}): your wait is over. Bought it, or let it go? Waiting purchases has both.")
                : new ReportRow(item.Description, $"{Briefing.Money(item.PriceCents)}, ready {item.ReadyAt.ToString("MMM d, h:mm tt", System.Globalization.CultureInfo.InvariantCulture)}")).ToList()));
        }

        if (schedule.Count > 0)
        {
            sections.Add(new ReportSection("COMING UP", schedule
                .Select(item => (Item: item, Date: BudgetMath.NextOccurrence(item, today, received)))
                .Where(next => next.Date is not null)
                .OrderBy(next => next.Date)
                .Select(next => new ReportRow(
                    (next.Item.AmountCents > 0 ? $"{next.Item.Label}, expected {Short(next.Date!.Value)}"
                        : next.Item.Account is { } card ? $"{next.Item.Label} on {card}, {Short(next.Date!.Value)}"
                        : $"{next.Item.Label}, {Short(next.Date!.Value)}") + LastTransfer(next.Item, transfers),
                    Briefing.Money(next.Item.AmountOn(next.Date!.Value))))
                .ToList()));
        }

        if (income.Count > 0)
        {
            var total = income.Sum(entry => entry.AmountCents);
            sections.Add(new ReportSection("OTHER INCOME", [
                new ReportRow($"This year, from {income.Count} payments", Briefing.Money(total)),
                new ReportRow("Suggested tax set-aside, about 25%", Briefing.Money((long)(total * BudgetMath.FreelanceTaxShare))),
            ]));
        }

        return sections;
    }

    private static string LastTransfer(ScheduledItem item, IReadOnlyList<TransferStatus>? transfers) =>
        transfers?.LastOrDefault(status => status.Label == item.Label) is { } last
            ? $" (last one, {Short(last.Due)}: {TransferCheck.StateText(last.State).ToLowerInvariant()})"
            : "";

    /// <summary>
    /// The money picture for a "what do I do now" answer: card close dates and pay-down amounts,
    /// plus anything flagged, like cushion money that's ready to invest.
    /// </summary>
    public static IReadOnlyList<string> MoneyNotes(IReadOnlyList<ReportSection> sections)
    {
        var cards = sections.Where(section => section.Title == "CARDS").SelectMany(section => section.Rows)
            .Select(row => row.Value.Length > 0 ? $"{ShortAttention(row.Label.Trim())}: {row.Value}" : ShortAttention(row.Label.Trim()));
        return cards.Concat(Attention(sections).Select(ShortAttention)).Distinct().ToList();
    }

    /// <summary>An attention line without the as-of date, to read quickly.</summary>
    public static string ShortAttention(string line)
    {
        var start = Math.Max(line.IndexOf(" (as of ", StringComparison.Ordinal), line.IndexOf(" (confirmed ", StringComparison.Ordinal));
        var end = line.IndexOf(')', Math.Max(start, 0));
        return start >= 0 && end > start ? line[..start] + line[(end + 1)..] : line;
    }

    /// <summary>A balance is stale once it was last set more than this many days ago.</summary>
    public const int StaleDays = 7;

    /// <summary>
    /// Checking and card balances last set more than a week ago, in one plain line such as "Checking last set 12 days
    /// ago, Contoso Card 9 days ago", oldest first. Null when they're all recent. It's only said, never sent on its own.
    /// </summary>
    public static string? StaleLine(IReadOnlyList<BudgetAccount> accounts, DateOnly today)
    {
        var stale = accounts
            .Where(account => account.Kind is AccountKind.Checking or AccountKind.Card)
            .Select(account => (Name: account.Kind == AccountKind.Checking ? "Checking" : account.Name, Days: today.DayNumber - account.UpdatedOn.DayNumber))
            .Where(account => account.Days > StaleDays)
            .OrderByDescending(account => account.Days)
            .Select((account, index) => index == 0 ? $"{account.Name} last set {account.Days} days ago" : $"{account.Name} {account.Days} days ago")
            .ToList();
        return stale.Count > 0 ? string.Join(", ", stale) : null;
    }

    /// <summary>Only what needs attention, for the briefing and the Sunday reset.</summary>
    public static IReadOnlyList<string> Attention(IReadOnlyList<ReportSection> sections) =>
        sections
            .SelectMany(section => section.Rows)
            .Where(row => row.Flagged)
            .Select(row => row.Note ?? $"{row.Label}: {row.Value}")
            .ToList();

    private static ReportRow CapRow(BudgetCategory category, long spent, long? cap)
    {
        if (cap is not { } limit)
        {
            return new ReportRow(category.Name, Briefing.Money(spent));
        }

        var left = limit - spent;
        var status = left >= 0 ? $"{Briefing.Money(left)} left" : $"{Briefing.Money(-left)} over";
        return new ReportRow(category.Name, $"{Briefing.Money(spent)} of {Briefing.Money(limit)}, {status}", left < 0,
            $"{category.Name} is {Briefing.Money(-left)} over {(category.Period == BudgetPeriod.Week ? "this week's" : "this month's")} {Briefing.Money(limit)} budget.");
    }

    /// <summary>How close a statement close has to be for paying before it to be the thing to do.</summary>
    public const int CloseSoonDays = 3;

    /// <summary>Utilization across every card with a limit, which counts toward the score along with each card's own. Only with two or more.</summary>
    private static IEnumerable<ReportRow> AllCards(IReadOnlyList<BudgetAccount> cards)
    {
        var limited = cards.Where(card => card.LimitCents is > 0).ToList();
        if (limited.Count < 2)
        {
            yield break;
        }

        var owed = limited.Sum(card => card.BalanceCents);
        var limit = limited.Sum(card => card.LimitCents!.Value);
        yield return new ReportRow("All cards", $"{Briefing.Money(owed)} of {Briefing.Money(limit)}, {(decimal)owed / limit:P0}");
    }

    private static IEnumerable<ReportRow> CardRows(BudgetAccount card, DateOnly today)
    {
        var limit = card.LimitCents ?? 0;
        var used = limit > 0 ? (decimal)card.BalanceCents / limit : 0;
        var label = $"{card.Name} (confirmed {Short(card.UpdatedOn)})";
        var pay30 = limit > 0 ? BudgetMath.PayDownTo(card.BalanceCents, limit, 0.30m) : 0;
        var closesIn = card.CloseDay is { } day ? BudgetMath.NextMonthDay(today, day).DayNumber - today.DayNumber : (int?)null;

        // The balance on the statement is what's reported, so with the close a few days off, paying before it is what helps.
        var note = used >= 0.30m && pay30 > 0
            ? closesIn is { } soon and <= CloseSoonDays
                ? soon == 0
                    ? $"{card.Name} closes today. Pay {Briefing.Money(pay30)} today to get to 30%."
                    : $"{card.Name} closes {(soon == 1 ? "tomorrow" : today.AddDays(soon).DayOfWeek.ToString())}. Pay {Briefing.Money(pay30)} before then to get to 30%."
                : $"{card.Name} is at {(int)Math.Round(used * 100)}%. Paying {Briefing.Money(pay30)} brings it down to 30%."
            : card.AlertCents is { } alert && card.BalanceCents > alert
                ? $"{card.Name} is at {Briefing.Money(card.BalanceCents)}, above the {Briefing.Money(alert)} you set to hear about."

                // Exactly at 30% there's nothing to pay to get there, and with no alert set there's none to name.
                : $"{card.Name} is at {(int)Math.Round(used * 100)}%.";
        yield return new ReportRow(label, $"{Briefing.Money(card.BalanceCents)} of {Briefing.Money(limit)}, {used:P0}", used >= 0.30m || card.BalanceCents > card.AlertCents, note);

        if (card.CloseDay is { } closeDay)
        {
            var close = BudgetMath.NextMonthDay(today, closeDay);
            var days = close.DayNumber - today.DayNumber;
            yield return new ReportRow(days switch
            {
                0 => "  Closes today",
                1 => "  Closes tomorrow",
                _ => $"  Closes {Short(close)}, in {days} days",
            }, "");

            // A card already under a share has nothing to pay for it, so that line is left out.
            foreach (var share in new[] { 0.30m, 0.10m })
            {
                if (limit > 0 && BudgetMath.PayDownTo(card.BalanceCents, limit, share) is > 0 and var pay)
                {
                    yield return new ReportRow($"  Pay {Briefing.Money(pay)} to get down to {(int)(share * 100)}%", "");
                }
            }
        }
    }

    private static string Short(DateOnly date) => date.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture);
}
