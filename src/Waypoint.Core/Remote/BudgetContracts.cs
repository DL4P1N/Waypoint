using System.Globalization;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Email;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Remote;

/// <summary>A Tailspin Pay payment no purchase clearly fit, with the purchases it could be for.</summary>
public sealed record RepaymentQuestion(PaymentAppRepayment Repayment, IReadOnlyList<RepaymentCandidate> Candidates);

/// <summary>
/// Everything the Budget page and its forms show, read at once. Server mode asks for it in one call, so a refresh
/// is one round trip, and while the server is away the page and its forms show the last one.
/// </summary>
public sealed record BudgetPage(
    IReadOnlyList<WaitItem> WaitList,
    IReadOnlyList<BudgetCategory> Categories,
    IReadOnlyList<CategoryDetail> Month,
    IReadOnlyList<CategoryDetail> ThisMonth,
    IReadOnlyList<ReportSection> Report,
    ReportSection Review,
    string? Tip,
    long? SafeToSpend,
    string PayLine,
    IReadOnlyList<RepaymentQuestion> Repayments,
    IReadOnlyList<BudgetAccount> Accounts,
    IReadOnlyList<BudgetAccount> CurrentAccounts,
    IReadOnlyList<ReportRow> CardsAtMonthEnd,
    SavingsGoalStatus? Goal,
    SavingsGoal? SavingsGoal,
    string GoalLine,
    IReadOnlyList<ReportSection> Summary,
    IReadOnlyList<NetWorthSnapshot> Trend,
    ReportSection TrendMonths,
    ReportSection CreditScores,
    IReadOnlyList<string> Bills,
    string BillsLine,
    int DefaultWaitHours,
    ReportSection? Outcomes = null,
    PayScheduleForm? PayForm = null,
    string? SafeToSpendReason = null,
    string? StaleBalances = null)
{
    /// <summary>How far back the net worth line goes.</summary>
    public const int TrendDays = 90;

    /// <summary>What's shown before anything has been read, with the server away.</summary>
    public static BudgetPage Empty { get; } = new(
        [], [], [], [], [], new ReportSection("", []), null, null, "", [], [], [], [], null, null, "", [], [], new ReportSection("", []), new ReportSection("", []), [], "",
        BudgetMath.WaitHours);

    /// <summary>
    /// The page for <paramref name="month"/>. Card balances are as of the month's last day for a past month, since
    /// only those are kept by month, and as of today otherwise.
    /// </summary>
    public static BudgetPage Read(WaypointDatabase database, DateOnly month, DateOnly today)
    {
        var firstOfThisMonth = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = month >= firstOfThisMonth ? today : month.AddMonths(1).AddDays(-1);
        var accounts = database.GetBudgetAccounts();
        var cards = accounts
            .Where(account => account.Kind == AccountKind.Card)
            .Select(card => new ReportRow(card.Name, Briefing.Money(database.GetCardBalance(card, monthEnd))))
            .ToList();

        return new BudgetPage(
            database.GetWaitList(),
            database.GetCategories(),
            database.GetCategoryDetails(month, today),
            database.GetCategoryDetails(firstOfThisMonth, today),
            database.GetBudgetReport(today),
            database.GetSpendingReview(today),
            database.GetSpendingTip(today),
            database.GetSafeToSpend(today),
            database.DescribePay(today),
            database.GetWaitingRepayments().Select(repayment => new RepaymentQuestion(repayment, database.GetRepaymentCandidates(repayment))).ToList(),
            accounts,
            database.GetCurrentAccounts(today),
            cards,
            database.GetSavingsGoalStatus(today),
            database.GetSavingsGoal(),
            database.DescribeSavingsGoal(today),
            database.GetMonthlySummary(month, today),
            database.GetNetWorthSnapshots(today.AddDays(-TrendDays), today),
            database.GetNetWorthByMonth(today),
            database.GetCreditScoreSection(),
            database.GetBills().Select(bill => bill.Label).ToList(),
            database.DescribeBills(today),
            database.GetDefaultWaitHours(),
            database.GetSpendingOutcomes(today),
            database.GetPayScheduleForm(today),
            database.GetSafeToSpendReason(today),
            database.GetStaleBalances(today));
    }

    /// <summary>The month as it goes in a path.</summary>
    public static string MonthKey(DateOnly month) => month.ToString("O", CultureInfo.InvariantCulture);
}

public sealed record ExpenseRequest(string Title, long? Cents, string? Category, DateOnly Date);

public sealed record CategoryRequest(string Name, long? MonthlyCents, BudgetPeriod Period);

public sealed record DepositRequest(string? Name, long? Cents, DateTime? At);

public sealed record PayScheduleRequest(string? Pattern, IReadOnlyList<string> Days, long? Cents, DateTime? Next);

public sealed record BillRequest(string? Name, long? Cents, string? HowOften, IReadOnlyList<string> Days, DateTime? At, string? Account);

public sealed record GoalRequest(long? Cents, DateTime? By);

public sealed record CreditScoreRequest(string? Score, string? Source, DateTime? At, string? Note);

public sealed record AffordCheckRequest(long? Cents, string? Card);

public sealed record RepaymentMatchRequest(string MessageId, long ItemId);

public sealed record AmountRequest(long? Cents);

/// <summary>Bank exports read on the PC. The server imports them, the same as one dropped in Discord.</summary>
public sealed record StatementsRequest(IReadOnlyList<string> Statements);

/// <summary>The session card's session, or none, which a bare null in JSON can't say as clearly.</summary>
public sealed record SessionValue(ActivationSession? Session);

public sealed record TonightValue(BedtimeStatus? Tonight);

public sealed record PauseRequest(string? Note, DateTime? Until);
