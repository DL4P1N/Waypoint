using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Remote;

/// <summary>
/// The Budget page's calls, sent to the server. The page shows the last read while it's away, and changes say they
/// can't be made yet. The server uses its own clock for today, so the dates passed in here only matter locally.
/// </summary>
public sealed class ServerBudgetData(ServerClient server) : IBudgetData
{
    private static readonly HttpMethod Post = HttpMethod.Post;
    private static readonly HttpMethod Put = HttpMethod.Put;

    public string? GetPreference(string key) => server.GetOr($"api/preferences/{Uri.EscapeDataString(key)}", new PreferenceValue(null)).Value;

    public void SetPreference(string key, string value) => server.Send(Put, $"api/preferences/{Uri.EscapeDataString(key)}", new PreferenceValue(value));

    public BudgetPage GetPage(DateOnly month, DateOnly today) => server.GetOr($"api/budget/page/{BudgetPage.MonthKey(month)}", BudgetPage.Empty);

    public OpReply AddExpense(string title, long? cents, string? category, DateOnly date, DateTime now) =>
        server.Send<OpReply>(Post, "api/budget/expenses", new ExpenseRequest(title, cents, category, date));

    public string ApplyBudgetOp(BudgetOp op, DateOnly today) => server.Send<TextValue>(Post, "api/budget/ops", op).Value ?? "";

    public OpReply AddCategory(string name, long? monthlyCents, BudgetPeriod period) =>
        server.Send<OpReply>(Post, "api/budget/categories", new CategoryRequest(name, monthlyCents, period));

    public OpReply AddExpectedDeposit(string? name, long? cents, DateTime? at) =>
        server.Send<OpReply>(Post, "api/budget/deposits", new DepositRequest(name, cents, at));

    public OpReply SetPaySchedule(string? pattern, IReadOnlyList<string> days, long? cents, DateTime? next) =>
        server.Send<OpReply>(Put, "api/budget/pay", new PayScheduleRequest(pattern, days, cents, next));

    public OpReply SetBill(string? name, long? cents, string? howOften, IReadOnlyList<string> days, DateTime? at, string? account, DateOnly today) =>
        server.Send<OpReply>(Put, "api/budget/bills", new BillRequest(name, cents, howOften, days, at, account));

    public OpReply RemoveBill(string? name) => server.Send<OpReply>(Post, "api/budget/bills/remove", new TextValue(name));

    public OpReply SetSavingsGoal(long? cents, DateTime? by, DateOnly today) => server.Send<OpReply>(Put, "api/budget/goal", new GoalRequest(cents, by));

    public OpReply ClearSavingsGoal() => server.Send<OpReply>(HttpMethod.Delete, "api/budget/goal");

    public OpReply AddCreditScore(string? score, string? source, DateTime? at, string? note, DateTime now) =>
        server.Send<OpReply>(Post, "api/budget/scores", new CreditScoreRequest(score, source, at, note));

    /// <summary>Only a question, but it's asked fresh each time, so it needs the server like a change does.</summary>
    public OpReply CheckAffordable(long? cents, string? card, DateOnly today) => server.Send<OpReply>(Post, "api/budget/afford", new AffordCheckRequest(cents, card));

    public OpReply MatchRepaymentTo(string messageId, long itemId) =>
        server.Send<OpReply>(Post, "api/budget/repayments/match", new RepaymentMatchRequest(messageId, itemId));

    public OpReply ClearRepayment(string messageId) => server.Send<OpReply>(Post, "api/budget/repayments/clear", new TextValue(messageId));

    public OpReply CategorizePurchase(long itemId, string category) =>
        server.Send<OpReply>(Post, $"api/budget/purchases/{itemId}/category", new NameRequest(category));

    public OpReply MarkPurchaseOneOff(long itemId, bool oneOff) => server.Send<OpReply>(Post, $"api/budget/purchases/{itemId}/one-off", new FlagValue(oneOff));

    public OpReply RecordRepayment(long itemId, long? cents) => server.Send<OpReply>(Post, $"api/budget/purchases/{itemId}/repayment", new AmountRequest(cents));

    public DeletedItem? DeletePurchase(long itemId, DateTime now) => server.Send<DeletedItem?>(Post, $"api/budget/purchases/{itemId}/delete");

    /// <summary>The same undo a deleted item on any page uses.</summary>
    public void UndoDeletePurchase(DeletedItem deleted, DateTime now) => server.Send(Post, "api/items/undelete", deleted);

    public OpReply ResolveWaitItem(long id, string decision) => server.Send<OpReply>(Post, $"api/budget/waits/{id}/resolve", new TextValue(decision));

    public OpReply ConfirmPayReceived(long? cents, DateOnly today) => server.Send<OpReply>(Post, "api/budget/pay-received", new AmountRequest(cents));

    public OpReply SetDefaultWaitHours(string hours) => server.Send<OpReply>(Post, "api/budget/wait-hours", new TextValue(hours));

    /// <summary>Asked fresh each time, like Can I afford this?, so it needs the server.</summary>
    public IReadOnlyList<string> GetSpendingPatterns(DateOnly today) => server.Send<List<string>>(HttpMethod.Get, "api/budget/patterns");

    /// <summary>Each search is kept, so while the server is away it shows what that search last found.</summary>
    public IReadOnlyList<SpendingRow> SearchPurchases(string? words, int limit) =>
        server.GetOr<List<SpendingRow>>($"api/budget/purchases?limit={limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}&words={Uri.EscapeDataString(words?.Trim() ?? "")}", []);

    /// <summary>Bank exports read on the PC, imported on the server. It says what came of them, the same as a local import.</summary>
    /// <remarks>A big export can take the server a while, so this waits longer than a page read.</remarks>
    public string ImportStatements(IReadOnlyList<string> statements) =>
        server.SendLong<TextValue>(Post, "api/budget/import", new StatementsRequest(statements), "the Budget page for the new activity").Value ?? "";
}
