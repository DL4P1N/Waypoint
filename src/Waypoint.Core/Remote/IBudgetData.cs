using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Remote;

/// <summary>
/// Everything the Budget page reads and changes, and the CSV import. Local mode passes straight to this PC's
/// database. Server mode sends the same calls to the server, so the page, its forms, and its menus work the same.
/// </summary>
public interface IBudgetData : IPreferences
{
    BudgetPage GetPage(DateOnly month, DateOnly today);

    OpReply AddExpense(string title, long? cents, string? category, DateOnly date, DateTime now);

    /// <summary>Income, waits, balances, caps, periods, and renames: the same ops typing makes.</summary>
    string ApplyBudgetOp(BudgetOp op, DateOnly today);

    OpReply AddCategory(string name, long? monthlyCents, BudgetPeriod period);

    OpReply AddExpectedDeposit(string? name, long? cents, DateTime? at);

    OpReply SetPaySchedule(string? pattern, IReadOnlyList<string> days, long? cents, DateTime? next);

    OpReply SetBill(string? name, long? cents, string? howOften, IReadOnlyList<string> days, DateTime? at, string? account, DateOnly today);

    OpReply RemoveBill(string? name);

    OpReply SetSavingsGoal(long? cents, DateTime? by, DateOnly today);

    OpReply ClearSavingsGoal();

    OpReply AddCreditScore(string? score, string? source, DateTime? at, string? note, DateTime now);

    OpReply CheckAffordable(long? cents, string? card, DateOnly today);

    OpReply MatchRepaymentTo(string messageId, long itemId);

    OpReply ClearRepayment(string messageId);

    OpReply CategorizePurchase(long itemId, string category);

    OpReply MarkPurchaseOneOff(long itemId, bool oneOff);

    OpReply RecordRepayment(long itemId, long? cents);

    /// <summary>Takes a purchase off the budget. Null if it's already gone. Undo hands back what this returned.</summary>
    DeletedItem? DeletePurchase(long itemId, DateTime now);

    void UndoDeletePurchase(DeletedItem deleted, DateTime now);

    OpReply ResolveWaitItem(long id, string decision);

    /// <summary>"I got paid" before the bank export shows it. No amount means the usual one.</summary>
    OpReply ConfirmPayReceived(long? cents, DateOnly today);

    /// <summary>How long new waiting purchases wait, as typed. A reply that's an answer asks again.</summary>
    OpReply SetDefaultWaitHours(string hours);

    /// <summary>What stands out in recent spending, one line each.</summary>
    IReadOnlyList<string> GetSpendingPatterns(DateOnly today);

    /// <summary>Purchases from any month that match, newest first, for Find anywhere.</summary>
    IReadOnlyList<SpendingRow> SearchPurchases(string? words, int limit);
}

/// <summary>Today's behavior: every call goes to the database on this PC.</summary>
public sealed class LocalBudgetData(WaypointDatabase database) : IBudgetData
{
    public string? GetPreference(string key) => database.GetPreference(key);

    public void SetPreference(string key, string value) => database.SetPreference(key, value);

    public BudgetPage GetPage(DateOnly month, DateOnly today) => BudgetPage.Read(database, month, today);

    public OpReply AddExpense(string title, long? cents, string? category, DateOnly date, DateTime now) => database.AddExpense(title, cents, category, date, now);

    public string ApplyBudgetOp(BudgetOp op, DateOnly today) => database.ApplyBudgetOp(op, today);

    public OpReply AddCategory(string name, long? monthlyCents, BudgetPeriod period) => database.AddCategory(name, monthlyCents, period);

    public OpReply AddExpectedDeposit(string? name, long? cents, DateTime? at) => database.AddExpectedDeposit(name, cents, at);

    public OpReply SetPaySchedule(string? pattern, IReadOnlyList<string> days, long? cents, DateTime? next) => database.SetPaySchedule(pattern, days, cents, next);

    public OpReply SetBill(string? name, long? cents, string? howOften, IReadOnlyList<string> days, DateTime? at, string? account, DateOnly today) =>
        database.SetBill(name, cents, howOften, days, at, account, today);

    public OpReply RemoveBill(string? name) => database.RemoveBill(name);

    public OpReply SetSavingsGoal(long? cents, DateTime? by, DateOnly today) => database.SetSavingsGoal(cents, by, today);

    public OpReply ClearSavingsGoal() => database.ClearSavingsGoal();

    public OpReply AddCreditScore(string? score, string? source, DateTime? at, string? note, DateTime now) => database.AddCreditScore(score, source, at, note, now);

    public OpReply CheckAffordable(long? cents, string? card, DateOnly today) => database.CheckAffordable(cents, card, today);

    public OpReply MatchRepaymentTo(string messageId, long itemId) => database.MatchRepaymentTo(messageId, itemId);

    public OpReply ClearRepayment(string messageId) => database.ClearRepaymentById(messageId);

    public OpReply CategorizePurchase(long itemId, string category) => database.CategorizePurchase(itemId, category);

    public OpReply MarkPurchaseOneOff(long itemId, bool oneOff) => database.MarkPurchaseOneOff(itemId, oneOff);

    public OpReply RecordRepayment(long itemId, long? cents) => database.RecordRepayment(itemId, cents);

    public DeletedItem? DeletePurchase(long itemId, DateTime now) => database.DeletePurchase(itemId, now);

    public void UndoDeletePurchase(DeletedItem deleted, DateTime now) => database.UndoDelete(deleted, now);

    public OpReply ResolveWaitItem(long id, string decision) => database.ResolveWaitItem(id, decision);

    public OpReply ConfirmPayReceived(long? cents, DateOnly today) => database.ConfirmPayTyped(cents, today);

    public OpReply SetDefaultWaitHours(string hours) => database.SetDefaultWaitHours(hours);

    public IReadOnlyList<string> GetSpendingPatterns(DateOnly today) => database.GetSpendingPatterns(today);

    public IReadOnlyList<SpendingRow> SearchPurchases(string? words, int limit) => database.SearchPurchases(words, limit);
}
