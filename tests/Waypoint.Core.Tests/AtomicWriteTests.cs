using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Email;

namespace Waypoint.Core.Tests;

/// <summary>
/// Writes made of several steps land whole or not at all. Each test makes a later step fail with a trigger, the way a
/// crash or a broken row would stop it partway, and checks that nothing from the earlier steps was kept.
/// </summary>
public sealed class AtomicWriteTests : IDisposable
{
    private static readonly DateOnly Day = new(2030, 3, 12);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-atomic-{Guid.NewGuid():N}.db");
    private readonly WaypointDatabase _database;

    public AtomicWriteTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void A_checking_export_that_fails_partway_keeps_none_of_its_rows_or_payments()
    {
        FailWhen("BEFORE INSERT ON items WHEN NEW.kind = 'Expense'");
        CheckingRow[] rows =
        [
            new(Day, "MADE UP DEPOSIT", 50000, CheckingKind.Other),
            new(Day, "MADE UP CARD PAYMENT", -12000, CheckingKind.CardPayment, "Made Up Card"),
            new(Day, "MADE UP SHOP", -2500, CheckingKind.Spending),
        ];

        Assert.ThrowsAny<SqliteException>(() => _database.ImportChecking(rows));

        Assert.Equal(0, Count("checking_activity"));
        Assert.Equal(0, Count("card_payments"));
        Assert.Equal(0, Count("captures"));
    }

    [Fact]
    public void A_card_export_whose_payments_fail_keeps_none_of_its_purchases()
    {
        FailWhen("BEFORE INSERT ON card_payments");
        const string csv = """
            Posted On,Purchase Date,Category,Description,Amount
            03/10/2030,03/10/2030,Payments,AUTOPAY STATEMENT BALANCE,-80.00
            03/11/2030,03/11/2030,Restaurants,MADE UP DINER,14.50
            """;

        Assert.ThrowsAny<SqliteException>(() => _database.ImportStatement(csv));

        Assert.Equal(0, Count("items"));
        Assert.Equal(0, Count("captures"));
    }

    [Fact]
    public void A_repayment_whose_match_fails_isnt_counted_on_the_purchase()
    {
        _database.ImportTransactions("Made Up Card", [new ImportedTransaction(Day, "MADE UP BAKERY", 3000)]);
        var purchase = Scalar("SELECT id FROM items WHERE kind = 'Expense'");
        Assert.NotNull(_database.AddRepayment("made-up-payment", new PaymentAppPayment("A Made Up Friend", 1500, null), Day));
        FailWhen("BEFORE UPDATE OF status ON repayments");

        Assert.ThrowsAny<SqliteException>(() => _database.MatchRepaymentTo("made-up-payment", purchase));

        Assert.Equal(0, Scalar("SELECT reimbursed_cents FROM items WHERE kind = 'Expense'"));
    }

    [Fact]
    public void A_deposit_whose_balance_fails_isnt_kept_and_the_same_email_counts_once()
    {
        _database.SaveBudgetAccount(new BudgetAccount("Made Up Cushion", AccountKind.Cushion, 40000, Day));
        FailWhen("BEFORE UPDATE ON budget_accounts");

        Assert.ThrowsAny<SqliteException>(() => _database.AddToCushion(5000, Day.AddDays(1), "made-up-email"));
        Assert.Equal(0, Count("brokerage_deposits"));

        Run("DROP TRIGGER fail_partway");
        Assert.True(_database.AddToCushion(5000, Day.AddDays(1), "made-up-email"));
        Assert.False(_database.AddToCushion(5000, Day.AddDays(1), "made-up-email"));
        Assert.Equal(1, Count("brokerage_deposits"));
        Assert.Equal(45000, _database.GetBudgetAccounts().Single().BalanceCents);
    }

    [Fact]
    public void Categorizing_a_purchase_that_fails_keeps_no_merchant_rule()
    {
        _database.AddCategory("Made Up Snacks", null, BudgetPeriod.Month);
        _database.ImportTransactions("Made Up Card", [new ImportedTransaction(Day, "MADE UP KIOSK", 800)]);
        var purchase = Scalar("SELECT id FROM items WHERE kind = 'Expense'");
        FailWhen("BEFORE UPDATE OF category ON items");

        Assert.ThrowsAny<SqliteException>(() => _database.CategorizePurchase(purchase, "Made Up Snacks"));

        Assert.Equal(0, Count("merchant_rules"));
    }

    [Fact]
    public void Checking_off_an_item_whose_history_fails_leaves_it_open()
    {
        var item = AddTask("Return the made up form");
        FailWhen("BEFORE INSERT ON item_history");

        Assert.ThrowsAny<SqliteException>(() => _database.SetDone(item, true));

        Assert.Equal(0, Scalar($"SELECT done FROM items WHERE id = {item.Id}"));
    }

    [Fact]
    public void An_undo_whose_history_fails_leaves_the_item_finished()
    {
        var item = AddTask("Water the made up plant");
        _database.SetDone(item, true);
        FailWhen("BEFORE INSERT ON item_history");

        Assert.ThrowsAny<SqliteException>(() => _database.SetDone(item, false));

        Assert.Equal(1, Scalar($"SELECT done FROM items WHERE id = {item.Id}"));
    }

    [Fact]
    public void A_reminder_card_whose_action_fails_can_still_be_pressed()
    {
        var item = AddTask("Send the made up email");
        _database.TrackSentReminder("made-up-card", item.Id);
        FailWhen("BEFORE INSERT ON item_history");

        Assert.ThrowsAny<SqliteException>(() => _database.ApplyAlertAction(new AlertAction("made-up-card", AlertActionKind.Done), DateTime.Now, 10));

        Assert.Equal(0, Scalar("SELECT handled FROM sent_reminders WHERE message_id = 'made-up-card'"));
        Assert.Equal(0, Scalar($"SELECT done FROM items WHERE id = {item.Id}"));
    }

    [Fact]
    public void A_list_check_whose_history_fails_stays_unchecked()
    {
        _database.AddToList("Made up packing", ["Spare socks"], 0, DateTime.Now);
        var entry = _database.GetListEntries("Made up packing").Single();
        FailWhen("BEFORE INSERT ON item_history");

        Assert.ThrowsAny<SqliteException>(() => _database.SetEntryDone(entry, true, DateTime.Now));

        Assert.False(_database.GetListEntries("Made up packing").Single().Done);
    }

    [Fact]
    public void A_shopping_entry_whose_item_fails_leaves_no_capture()
    {
        FailWhen("BEFORE INSERT ON items WHEN NEW.kind = 'Shopping'");

        Assert.ThrowsAny<SqliteException>(() => _database.AddListEntry("Shopping", "Made up oats", DateTime.Now));

        Assert.Equal(0, Count("captures"));
    }

    [Fact]
    public void A_note_sent_once_whose_mark_fails_isnt_made()
    {
        FailWhen("BEFORE UPDATE OF client_id ON items WHEN NEW.client_id IS NOT NULL");

        Assert.ThrowsAny<SqliteException>(() => _database.CreateNoteOnce("made-up-client", "Made up title", "Made up body", DateTime.Now, null));

        Assert.Equal(0, Count("items"));
        Assert.Equal(0, Count("captures"));
    }

    [Fact]
    public void A_transcript_sent_once_whose_mark_fails_isnt_kept()
    {
        var note = _database.CreateNote("Made up lecture", "", DateTime.Now);
        FailWhen("BEFORE UPDATE OF client_id ON note_sources WHEN NEW.client_id IS NOT NULL");

        Assert.ThrowsAny<SqliteException>(() => _database.AddTranscriptSource(note, "Made up recording", "Made up words", "[]", DateTime.Now, null, "made-up-client"));

        Assert.Equal(0, Count("note_sources"));
    }

    [Fact]
    public void A_wait_whose_record_fails_leaves_no_task_behind()
    {
        FailWhen("BEFORE INSERT ON waiting_on");

        Assert.ThrowsAny<SqliteException>(() => _database.MarkWaiting("A Made Up Office", "Hear back about the form", null, null, null, null, DateTime.Now));

        Assert.Equal(0, Count("items"));
        Assert.Equal(0, Count("captures"));
    }

    [Fact]
    public void Deposits_with_no_email_id_or_a_blank_one_each_count()
    {
        _database.SaveBudgetAccount(new BudgetAccount("Made Up Cushion", AccountKind.Cushion, 40000, Day));

        Assert.True(_database.AddToCushion(100, Day));
        Assert.True(_database.AddToCushion(100, Day));
        Assert.True(_database.AddToCushion(100, Day, ""));
        Assert.True(_database.AddToCushion(100, Day, ""));
        Assert.True(_database.AddToCushion(100, Day, "   "));

        Assert.Equal(5, Count("brokerage_deposits"));
        Assert.Equal(0, Count("brokerage_deposits", "message_id IS NOT NULL"));
        Assert.Equal(40500, _database.GetBudgetAccounts().Single().BalanceCents);
    }

    [Fact]
    public void A_deposit_keeps_a_balance_saved_while_it_waited_to_write()
    {
        _database.SaveBudgetAccount(new BudgetAccount("Made Up Cushion", AccountKind.Cushion, 40000, Day));

        WhileAnotherWriteIsOpen(
            () => _database.AddToCushion(5000, Day, "made-up-email"),
            "UPDATE budget_accounts SET balance_cents = 70000 WHERE kind = 'Cushion'");

        Assert.Equal(75000, _database.GetBudgetAccounts().Single().BalanceCents);
    }

    [Fact]
    public void Not_a_repayment_after_it_was_matched_meanwhile_says_already_settled()
    {
        _database.ImportTransactions("Made Up Card", [new ImportedTransaction(Day, "MADE UP BAKERY", 3000)]);
        _database.AddRepayment("made-up-payment", new PaymentAppPayment("A Made Up Friend", 1500, null), Day);
        OpReply? reply = null;

        WhileAnotherWriteIsOpen(
            () => reply = _database.ClearRepaymentById("made-up-payment"),
            "UPDATE repayments SET status = 'matched' WHERE message_id = 'made-up-payment'");

        Assert.Equal("That repayment is already settled", reply!.Text);
        Assert.Equal(1, Count("repayments", "status = 'matched'"));
    }

    [Fact]
    public void A_repayment_whose_settling_changes_nothing_isnt_kept_as_settled()
    {
        _database.AddCategory("Made Up Dining", null, BudgetPeriod.Month);
        _database.SaveMerchantRule("made up bistro", "Made Up Dining");
        _database.ImportTransactions("Made Up Card", [new ImportedTransaction(Day, "MADE UP BISTRO", 3000)]);
        Run("CREATE TRIGGER skip_update BEFORE UPDATE ON repayments BEGIN SELECT RAISE(IGNORE); END");

        Assert.Throws<InvalidOperationException>(() => _database.AddRepayment("made-up-payment", new PaymentAppPayment("A Made Up Friend", 1500, null), Day));

        Assert.Equal(0, Count("repayments"));
        Assert.Equal(0, Scalar("SELECT reimbursed_cents FROM items WHERE kind = 'Expense'"));
    }

    private StoredItem AddTask(string title)
    {
        _database.CompleteCapture(_database.AddCapture(title), [new SortedItem(ItemKind.Task, title, null, null)]);
        return _database.GetOpenItems().Single(item => item.Title == title);
    }

    private long Scalar(string sql)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private void Run(string sql)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Runs <paramref name="call"/> while this test holds the write, then makes <paramref name="change"/> and lets go,
    /// the way a save from another page lands between what the call reads and what it writes.
    /// </summary>
    private void WhileAnotherWriteIsOpen(Action call, string change)
    {
        Task running;
        using (var held = _database.Open())
        using (var transaction = held.BeginTransaction())
        {
            running = Task.Run(call);

            // Long enough for the call to read and reach its write, which waits here.
            Thread.Sleep(TimeSpan.FromSeconds(1));
            using var command = held.CreateCommand();
            command.CommandText = change;
            command.ExecuteNonQuery();
            transaction.Commit();
        }

        Assert.True(running.Wait(TimeSpan.FromSeconds(60)));
    }

    /// <summary>Makes any write the trigger names fail, the way a constraint on that step would.</summary>
    private void FailWhen(string when)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TRIGGER fail_partway {when} BEGIN SELECT RAISE(ABORT, 'made to fail partway'); END";
        command.ExecuteNonQuery();
    }

    private long Count(string table, string where = "1 = 1")
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {where}";
        return (long)command.ExecuteScalar()!;
    }
}
