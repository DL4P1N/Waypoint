using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>One real purchase can reach the budget from an alert email, a card or checking CSV, and typing. It counts once.</summary>
public sealed class ImportMatchTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 10, 14);
    private static readonly DateOnly Anchor = new(2026, 10, 10);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public ImportMatchTests()
    {
        _database = new WaypointDatabase(_path);
        _database.SaveBudgetAccount(new BudgetAccount("Contoso Card", AccountKind.Card, 10000, Anchor, 250000, 9, 3));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private long Spent() => _database.GetSpending(Anchor.AddDays(-10), Anchor.AddDays(20)).Sum(row => row.AmountCents);

    private long Card() => _database.GetCurrentAccounts(Anchor.AddDays(20)).Single(account => account.Name == "Contoso Card").BalanceCents;

    private void Typed(string title, long cents, DateOnly on) =>
        _database.CompleteCapture(_database.AddCapture(title), [new SortedItem(ItemKind.Expense, title, on, cents)]);

    [Fact]
    public void A_purchase_seen_in_both_an_alert_email_and_a_card_csv_counts_once()
    {
        _database.ImportTransactions("Contoso Card", [new(Day, "OAKHAVEN COFFEE", 550, "AAMkAGI-message-1")]);
        ImportedTransaction[] csv = [new(Day.AddDays(-1), "OAKHAVEN COFFEE #12 OAKHAVEN ZZ", 550)];

        Assert.Equal(new ImportResult(0, 1), _database.ImportTransactions("Contoso Card", csv));
        Assert.Equal(new ImportResult(0, 1), _database.ImportTransactions("Contoso Card", csv));

        Assert.Equal(550, Spent());
        Assert.Equal(10550, Card());
    }

    [Fact]
    public void A_card_csv_then_the_alert_email_for_it_counts_once()
    {
        _database.ImportTransactions("Contoso Card", [new(Day.AddDays(-1), "OAKHAVEN COFFEE #12 OAKHAVEN ZZ", 550)]);

        Assert.Equal(0, _database.ImportTransactions("Contoso Card", [new(Day, "OAKHAVEN COFFEE", 550, "AAMkAGI-message-1")]).Added);
        Assert.Equal(0, _database.ImportTransactions("Contoso Card", [new(Day, "OAKHAVEN COFFEE", 550, "AAMkAGI-message-1")]).Added);

        Assert.Equal(550, Spent());
        Assert.Equal(10550, Card());
    }

    [Fact]
    public void An_alert_email_stands_in_for_only_one_csv_row()
    {
        _database.ImportTransactions("Contoso Card", [new(Day, "OAKHAVEN COFFEE", 550, "AAMkAGI-message-1")]);

        _database.ImportTransactions("Contoso Card", [new(Day, "TRADER JOES #12", 550), new(Day, "OAKHAVEN COFFEE #12", 550)]);

        var titles = _database.GetSpending(Day, Day).Select(row => row.Title).ToList();
        Assert.Equal(["OAKHAVEN COFFEE", "TRADER JOES #12"], titles);
        Assert.Equal(11100, Card());
    }

    [Fact]
    public void An_alert_on_another_card_is_a_different_purchase()
    {
        _database.ImportTransactions("Lucerne Card", [new(Day, "OAKHAVEN COFFEE", 550, "AAMkAGI-message-1")]);

        _database.ImportTransactions("Contoso Card", [new(Day, "OAKHAVEN COFFEE #12", 550)]);

        Assert.Equal(1100, Spent());
    }

    [Fact]
    public void One_typed_purchase_hides_only_one_imported_charge()
    {
        Typed("coffee", 500, Day);

        var result = _database.ImportTransactions("Contoso Card", [new(Day, "CAFE ONE", 500), new(Day, "CAFE TWO", 500), new(Day, "CAFE THREE", 500)]);

        Assert.Equal(new ImportResult(2, 1), result);
        Assert.Equal(1500, Spent());
    }

    [Fact]
    public void A_typed_purchase_matches_the_charge_with_its_merchant_first()
    {
        Typed("Target", 2000, Day);

        _database.ImportTransactions("Contoso Card", [new(Day, "PANERA #12", 2000), new(Day.AddDays(1), "TARGET #12", 2000)]);

        var titles = _database.GetSpending(Day.AddDays(-3), Day.AddDays(3)).Select(row => row.Title).ToList();
        Assert.Contains("Target", titles);
        Assert.Contains("PANERA #12", titles);
        Assert.DoesNotContain("TARGET #12", titles);
    }

    [Fact]
    public void An_alert_email_after_a_balance_typed_that_day_moves_the_card()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        // Typed earlier today and later today, so the test holds at any hour, even just after midnight.
        var startOfToday = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue));
        var endOfToday = new DateTimeOffset(today.ToDateTime(new TimeOnly(23, 59, 59)));
        _database.SaveBudgetAccount(new BudgetAccount("Contoso Card", AccountKind.Card, 10000, today, 250000, 9, 3, SetAt: startOfToday));
        _database.SaveBudgetAccount(new BudgetAccount("Lucerne Card", AccountKind.Card, 10000, today, 80000, 22, 17, SetAt: endOfToday));

        _database.ImportTransactions("Contoso Card", [new(today, "OAKHAVEN COFFEE", 550, "AAMkAGI-late")]);
        _database.ImportTransactions("Contoso Card", [new(today, "SHOP", 700)]);
        _database.ImportTransactions("Lucerne Card", [new(today, "OAKHAVEN COFFEE", 550, "AAMkAGI-early")]);

        var accounts = _database.GetCurrentAccounts(today);
        Assert.Equal(10550, accounts.Single(account => account.Name == "Contoso Card").BalanceCents);
        Assert.Equal(10000, accounts.Single(account => account.Name == "Lucerne Card").BalanceCents);
        Assert.Equal(9300, _database.GetCardBalance(accounts.Single(account => account.Name == "Contoso Card") with { BalanceCents = 10000 }, today.AddDays(-1)));
    }

    [Fact]
    public void Typing_a_balance_keeps_when_it_was_typed()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        _database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.SetBalance, "Contoso Card", 20000, null), today);

        var setAt = _database.GetBudgetAccounts().Single().SetAt;
        Assert.NotNull(setAt);
        Assert.InRange(setAt!.Value, DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now);

        _database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.SetLimit, "Contoso Card", 200000, null), today);
        Assert.Equal(setAt, _database.GetBudgetAccounts().Single().SetAt);
    }

    [Fact]
    public void Deleting_a_typed_purchase_lets_the_imported_row_it_matched_count_again()
    {
        var typed = _database.AddExpense("Target", 2000, null, Day, DateTime.Now).ItemId!.Value;
        _database.ImportTransactions("Contoso Card", [new(Day, "TARGET #12", 2000)]);
        Assert.Equal(["Target"], _database.GetSpending(Day, Day).Select(row => row.Title));

        var deleted = _database.DeletePurchase(typed, DateTime.Now)!;
        Assert.Equal(["TARGET #12"], _database.GetSpending(Day, Day).Select(row => row.Title));
        Assert.Equal(12000, Card());

        _database.UndoDelete(deleted, DateTime.Now);
        Assert.Equal(["Target"], _database.GetSpending(Day, Day).Select(row => row.Title));
        Assert.Equal(12000, Card());
    }

    [Fact]
    public void Purchases_counted_twice_before_are_matched_once_at_startup_and_can_be_taken_back()
    {
        // The old way: both copies stored, nothing tying them together.
        Typed("coffee", 500, Day.AddDays(1));
        using (var connection = new SqliteConnection($"Data Source={_path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO captures (id, text, created_at, sorted_at) VALUES (900, 'Imported: Contoso Card', '2026-10-14T09:00:00+00:00', '2026-10-14T09:00:00+00:00');
                INSERT INTO captures (id, text, created_at, sorted_at) VALUES (901, 'Imported: Contoso Card', '2026-10-16T09:00:00+00:00', '2026-10-16T09:00:00+00:00');
                INSERT INTO captures (id, text, created_at, sorted_at) VALUES (902, 'Imported: Lucerne Card', '2026-10-16T09:00:00+00:00', '2026-10-16T09:00:00+00:00');
                INSERT INTO items (capture_id, kind, title, due_date, amount_cents, done, category, import_key) VALUES
                    (900, 'Expense', 'OAKHAVEN COFFEE', '2026-10-14', 550, 1, 'Buffer', 'AAMkAGI-message-1'),
                    (900, 'Expense', 'TRADER JOES', '2026-10-14', 4000, 1, 'Buffer', 'AAMkAGI-message-2'),
                    (901, 'Expense', 'OAKHAVEN COFFEE #12', '2026-10-13', 550, 1, 'Buffer', 'Contoso Card|2026-10-13|OAKHAVEN COFFEE #12|550|1'),
                    (901, 'Expense', 'TRADER JOES #12', '2026-10-14', 4000, 1, 'Buffer', 'Contoso Card|2026-10-14|TRADER JOES #12|4000|1'),
                    (901, 'Expense', 'PANERA', '2026-10-15', 500, 1, 'Buffer', 'Contoso Card|2026-10-15|PANERA|500|1'),
                    (901, 'Expense', 'TARGET', '2026-10-15', 900, 1, 'Buffer', 'Contoso Card|2026-10-15|TARGET|900|1'),
                    (902, 'Expense', 'OAKHAVEN COFFEE', '2026-10-14', 550, 1, 'Buffer', 'Lucerne Card|2026-10-14|OAKHAVEN COFFEE|550|1');
                DELETE FROM preferences WHERE key = 'import_matches_backfilled';
                """;
            command.ExecuteNonQuery();
        }

        const long doubled = 500 + 550 + 4000 + 550 + 4000 + 500 + 900 + 550;
        Assert.Equal(doubled, Spent());

        var upgraded = new WaypointDatabase(_path);
        const long once = 500 + 550 + 4000 + 900 + 550;
        Assert.Equal(once, upgraded.GetSpending(Anchor.AddDays(-10), Anchor.AddDays(20)).Sum(row => row.AmountCents));
        Assert.Equal(10000 + 550 + 4000 + 500 + 900, upgraded.GetCurrentAccounts(Anchor.AddDays(20)).Single(account => account.Name == "Contoso Card").BalanceCents);

        Assert.Equal(once, new WaypointDatabase(_path).GetSpending(Anchor.AddDays(-10), Anchor.AddDays(20)).Sum(row => row.AmountCents));

        Assert.Equal(3, upgraded.UndoMatchUpgrade());
        Assert.Equal(doubled, new WaypointDatabase(_path).GetSpending(Anchor.AddDays(-10), Anchor.AddDays(20)).Sum(row => row.AmountCents));
    }

    [Fact]
    public void A_typed_card_purchase_moves_the_card_balance_once_the_csv_has_it()
    {
        Typed("Target", 2000, Day);
        Assert.Equal(10000, Card());

        _database.ImportTransactions("Contoso Card", [new(Day, "TARGET #12", 2000)]);
        _database.ImportTransactions("Contoso Card", [new(Day, "TARGET #12", 2000)]);

        Assert.Equal(2000, Spent());
        Assert.Equal(12000, Card());
        Assert.Equal(10000, _database.GetCardBalance(_database.GetBudgetAccounts().Single(), Anchor));
    }
}
