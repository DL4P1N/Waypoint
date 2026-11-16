using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Email;

namespace Waypoint.Core.Tests;

/// <summary>The Budget and History pages' own controls, which share Core methods with typing.</summary>
public sealed class BudgetControlsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;
    private readonly DateOnly _today = DateOnly.FromDateTime(DateTime.Now);

    public BudgetControlsTests()
    {
        _database = new WaypointDatabase(_path);
        _database.SaveCategory(new BudgetCategory("Groceries", 22500));
        _database.SaveCategory(new BudgetCategory("Meals out", 8000));
        _database.SaveCategory(new BudgetCategory("Fun", 5000));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private SpendingRow Spend(string title, long cents, string category = "Fun", int daysAgo = 1)
    {
        var capture = _database.AddCapture(title);
        _database.CompleteCapture(capture, [new SortedItem(ItemKind.Expense, title, _today.AddDays(-daysAgo), cents, Category: category)]);
        return Row(title);
    }

    private SpendingRow Row(string title) => _database.GetSpending(_today.AddDays(-90), _today).Single(row => row.Title == title);

    [Fact]
    public void Spending_rows_carry_the_purchase_id()
    {
        var first = Spend("TARGET #12", 2500);
        var second = Spend("TARGET 00099999", 4000);

        Assert.NotEqual(0, first.Id);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void A_deleted_purchase_leaves_the_budget_and_undo_brings_it_back()
    {
        var typed = Spend("Movie tickets", 1800);

        var deleted = _database.DeletePurchase(typed.Id, DateTime.Now);
        Assert.NotNull(deleted);
        Assert.DoesNotContain(_database.GetSpending(_today.AddDays(-90), _today), row => row.Id == typed.Id);
        Assert.Null(_database.DeletePurchase(typed.Id, DateTime.Now));

        _database.UndoDelete(deleted, DateTime.Now);
        Assert.Equal(1800, Row("Movie tickets").AmountCents);
    }

    [Fact]
    public void A_typed_expense_says_which_purchase_it_added_so_it_can_be_undone()
    {
        var reply = _database.AddExpense("Bowling", 2500, "Fun", _today, DateTime.Now);

        Assert.Equal(Row("Bowling").Id, reply.ItemId);
        Assert.NotNull(_database.DeletePurchase(reply.ItemId!.Value, DateTime.Now));
        Assert.DoesNotContain(_database.GetSpending(_today.AddDays(-90), _today), row => row.Title == "Bowling");
        Assert.Null(_database.AddExpense("", 2500, null, _today, DateTime.Now).ItemId);
    }

    [Fact]
    public void Only_purchases_can_be_deleted_from_the_budget()
    {
        var capture = _database.AddCapture("call mom");
        _database.CompleteCapture(capture, [new SortedItem(ItemKind.Task, "call mom", _today, null)]);
        var task = _database.GetOpenItems().Single(item => item.Title == "call mom");

        Assert.Null(_database.DeletePurchase(task.Id, DateTime.Now));
        Assert.Contains(_database.GetOpenItems(), item => item.Id == task.Id);
    }

    [Fact]
    public void A_deleted_import_stays_deleted_when_the_same_csv_or_alert_comes_again()
    {
        var charge = new ImportedTransaction(_today.AddDays(-2), "LUNCH SPOT", 1200);
        _database.ImportTransactions("Contoso Card", [charge]);
        var imported = Row("LUNCH SPOT");

        Assert.NotNull(_database.DeletePurchase(imported.Id, DateTime.Now));
        var again = _database.ImportTransactions("Contoso Card", [charge]);

        Assert.Equal(0, again.Added);
        Assert.DoesNotContain(_database.GetSpending(_today.AddDays(-90), _today), row => row.Title == "LUNCH SPOT");
    }

    [Fact]
    public void Changing_one_purchase_category_teaches_the_merchant()
    {
        var target = Spend("TARGET #12", 2500);

        var reply = _database.CategorizePurchase(target.Id, "groceries");
        Assert.Equal("Groceries", Row(target.Title).Category);
        Assert.Contains("Groceries", reply.Text);

        // A later charge with a new store number follows the rule.
        Assert.Equal("Groceries", Spend("TARGET 00077777", 1200).Category);
    }

    [Fact]
    public void Changing_to_a_missing_category_changes_nothing()
    {
        var target = Spend("TARGET #12", 2500);

        var reply = _database.CategorizePurchase(target.Id, "Nope");

        Assert.Equal("Fun", Row(target.Title).Category);
        Assert.Contains("no Nope", reply.Text);
    }

    [Fact]
    public void Marking_one_off_touches_only_the_purchase_picked()
    {
        var older = Spend("Quickride trip", 1500, daysAgo: 5);
        _ = Spend("Quickride trip two", 1800, daysAgo: 1);

        _database.MarkPurchaseOneOff(older.Id);
        Assert.True(Row("Quickride trip").OneOff);
        Assert.False(Row("Quickride trip two").OneOff);

        _database.MarkPurchaseOneOff(older.Id, false);
        Assert.False(Row("Quickride trip").OneOff);
    }

    [Fact]
    public void A_repayment_lands_on_the_picked_purchase_and_never_past_its_cost()
    {
        var older = Spend("Quickride trip", 1500, daysAgo: 5);
        _ = Spend("Quickride trip two", 1800, daysAgo: 1);

        _database.RecordRepayment(older.Id, 1000);
        _database.RecordRepayment(older.Id, 1000);

        Assert.Equal(1500, Row("Quickride trip").ReimbursedCents);
        Assert.Equal(0, Row("Quickride trip two").ReimbursedCents);
        Assert.True(_database.RecordRepayment(older.Id, null).IsAnswer);
    }

    [Fact]
    public void Typed_repayments_and_one_offs_still_find_the_newest_charge()
    {
        _ = Spend("Quickride trip", 1500, daysAgo: 5);
        _ = Spend("Quickride trip two", 1800, daysAgo: 1);

        _database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.Reimburse, "quickride", 500, null), _today);
        _database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.MarkOneOff, "quickride", null, null), _today);

        Assert.Equal(500, Row("Quickride trip two").ReimbursedCents);
        Assert.True(Row("Quickride trip two").OneOff);
        Assert.False(Row("Quickride trip").OneOff);
    }

    [Fact]
    public void A_hand_added_expense_keeps_the_category_picked_over_a_rule()
    {
        _database.SaveMerchantRule("target", "Groceries");

        var reply = _database.AddExpense("Target", 2000, "Fun", _today, DateTime.Now);

        Assert.False(reply.IsAnswer);
        var row = Row("Target");
        Assert.Equal("Fun", row.Category);
        Assert.Equal(2000, row.AmountCents);
        Assert.True(_database.AddExpense("Target", null, "Fun", _today, DateTime.Now).IsAnswer);
        Assert.True(_database.AddExpense(" ", 100, "Fun", _today, DateTime.Now).IsAnswer);
    }

    [Fact]
    public void A_new_category_is_added_once()
    {
        Assert.False(_database.AddCategory("Pets", 4000, BudgetPeriod.Week).IsAnswer);
        Assert.True(_database.AddCategory("pets", null, BudgetPeriod.Month).IsAnswer);

        var pets = _database.GetCategories().Single(category => category.Name == "Pets");
        Assert.Equal(4000, pets.MonthlyCents);
        Assert.Equal(BudgetPeriod.Week, pets.Period);
    }

    [Fact]
    public void A_waiting_purchase_comes_off_the_list_by_its_id()
    {
        _database.AddWaitItem("headphones", 8000, DateTime.Now);
        _database.AddWaitItem("headphones case", 2000, DateTime.Now);
        var caseItem = _database.GetWaitList().Single(item => item.Description == "headphones case");

        _database.ResolveWaitItem(caseItem.Id, "skipped");

        Assert.Equal("headphones", _database.GetWaitList().Single().Description);
        Assert.Contains("already", _database.ResolveWaitItem(caseItem.Id, "bought").Text);
    }

    [Fact]
    public void A_waiting_repayment_offers_purchases_and_matches_the_one_picked()
    {
        var tacos = Spend("Tacos Place", 4000, "Meals out", daysAgo: 2);
        var pizza = Spend("Pizza Spot", 3000, "Meals out", daysAgo: 3);
        var outcome = _database.AddRepayment("m1", new PaymentAppPayment("Sam Lee", 1500, null), _today);
        Assert.False(outcome!.Matched);

        var repayment = _database.GetWaitingRepayments().Single();
        var candidates = _database.GetRepaymentCandidates(repayment);
        Assert.Contains(candidates, candidate => candidate.ItemId == tacos.Id);
        Assert.Contains(candidates, candidate => candidate.ItemId == pizza.Id);

        _database.MatchRepaymentTo("m1", pizza.Id);

        Assert.Equal(1500, Row("Pizza Spot").ReimbursedCents);
        Assert.Equal(0, Row("Tacos Place").ReimbursedCents);
        Assert.Empty(_database.GetWaitingRepayments());
        Assert.Contains("already", _database.MatchRepaymentTo("m1", tacos.Id).Text);
    }

    [Fact]
    public void A_waiting_repayment_can_be_cleared_by_id()
    {
        Spend("Tacos Place", 4000, "Meals out", daysAgo: 2);
        Spend("Pizza Spot", 3000, "Meals out", daysAgo: 3);
        _database.AddRepayment("m1", new PaymentAppPayment("Sam Lee", 1500, null), _today);
        _database.AddRepayment("m2", new PaymentAppPayment("Alex Kim", 1000, null), _today);

        _database.ClearRepaymentById("m2");

        Assert.Equal("m1", _database.GetWaitingRepayments().Single().MessageId);
    }

    [Fact]
    public void History_views_split_by_what_happened_and_reopened_shows_the_undo()
    {
        Store("paper");
        Store("laundry");
        Store("essay");
        var paper = _database.GetOpenItems().Single(item => item.Title == "paper");
        var laundry = _database.GetOpenItems().Single(item => item.Title == "laundry");
        var essay = _database.GetOpenItems().Single(item => item.Title == "essay");

        _database.SetDone(paper, true);
        _database.SetDone(essay, true);
        _database.ChangeItem(new ItemChange(ItemSource.Capture, laundry.Id, ChangeAction.Cancel), DateTime.Now);
        Assert.True(_database.ReopenEntry(_database.GetHistory().Single(entry => entry.Title == "essay")));

        Assert.Equal(["laundry", "paper"], _database.SearchHistory(null, 100, HistoryFilter.All).Select(entry => entry.Title).Order());
        Assert.Equal("paper", _database.SearchHistory(null, 100, HistoryFilter.Completed).Single().Title);
        Assert.Equal("laundry", _database.SearchHistory(null, 100, HistoryFilter.Canceled).Single().Title);
        Assert.Empty(_database.SearchHistory(null, 100, HistoryFilter.Past));

        var reopened = _database.SearchHistory(null, 100, HistoryFilter.Reopened).Single();
        Assert.Equal("essay", reopened.Title);
        Assert.Equal(HistoryAction.Reopened, reopened.Action);
        Assert.Equal("Reopened", reopened.Label);
        Assert.False(_database.CanReopen(reopened));

        Assert.Empty(_database.SearchHistory("paper", 100, HistoryFilter.Reopened));
        Assert.Single(_database.SearchHistory("ess", 100, HistoryFilter.Reopened));
    }

    private void Store(string title) => _database.CompleteCapture(_database.AddCapture(title), [new SortedItem(ItemKind.Task, title, null, null)]);
}
