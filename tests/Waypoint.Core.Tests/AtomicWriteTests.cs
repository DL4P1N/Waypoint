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

    [Fact]
    public void A_database_from_before_deposit_email_ids_upgrades_and_keeps_working()
    {
        // Made the way an older copy left it: deposits with no email column, a note sent under an id, and a waiting payment.
        _database.SaveBudgetAccount(new BudgetAccount("Made Up Cushion", AccountKind.Cushion, 40000, Day));
        _database.AddToCushion(5000, Day);
        _database.AddToCushion(5000, Day);
        _database.ImportTransactions("Made Up Card", [new ImportedTransaction(Day, "MADE UP BAKERY", 3000)]);
        _database.AddRepayment("made-up-payment", new PaymentAppPayment("A Made Up Friend", 1500, null), Day);
        var note = _database.CreateNoteOnce("made-up-client", "Made up note", "Made up body", DateTime.Now, null);
        Run("DROP INDEX brokerage_deposits_message; ALTER TABLE brokerage_deposits DROP COLUMN message_id");
        Assert.Equal(0, Count("pragma_table_info('brokerage_deposits')", "name = 'message_id'"));
        SqliteConnection.ClearAllPools();

        var upgraded = new WaypointDatabase(_path);
        _ = new WaypointDatabase(_path);

        Assert.Equal(1, Count("pragma_table_info('brokerage_deposits')", "name = 'message_id'"));
        Assert.Equal(1, Count("sqlite_master", "name = 'brokerage_deposits_message'"));
        Assert.Equal(2, Count("brokerage_deposits", "message_id IS NULL"));
        Assert.True(upgraded.AddToCushion(100, Day, "made-up-email"));
        Assert.False(upgraded.AddToCushion(100, Day, "made-up-email"));
        Assert.True(upgraded.AddToCushion(100, Day));
        Assert.Equal(50200, upgraded.GetBudgetAccounts().Single().BalanceCents);
        Assert.Equal(note, upgraded.CreateNoteOnce("made-up-client", "Made up note", "Made up body", DateTime.Now, null));
        Assert.Equal(1, Count("items", "kind = 'Note'"));

        var purchase = Scalar("SELECT id FROM items WHERE kind = 'Expense'");
        Assert.StartsWith("Marked", upgraded.MatchRepaymentTo("made-up-payment", purchase).Text);
        Assert.Equal("That repayment is already settled", upgraded.MatchRepaymentTo("made-up-payment", purchase).Text);
    }

    [Fact]
    public void Preferences_set_together_are_kept_together_or_not_at_all()
    {
        FailWhen("BEFORE INSERT ON preferences WHEN NEW.key = 'made-up-second'");

        Assert.ThrowsAny<SqliteException>(() => _database.SetPreferences(("made-up-first", "1"), ("made-up-second", "2")));

        Assert.Null(_database.GetPreference("made-up-first"));
    }

    [Fact]
    public void Holding_unanswered_cards_that_fails_leaves_them_to_be_held_later()
    {
        var item = AddTask("Call the made up shop");
        _database.TrackSentReminder("made-up-card", item.Id, sentAt: DateTime.Now.AddHours(-1));
        FailWhen("BEFORE INSERT ON after_this");

        Assert.ThrowsAny<SqliteException>(() => _database.HoldUnansweredReminders(DateTime.Now));

        Assert.Equal(0, Scalar("SELECT held FROM sent_reminders WHERE message_id = 'made-up-card'"));
    }

    [Fact]
    public void Taking_a_hold_that_fails_keeps_every_hold()
    {
        var expired = AddTask("Fold the made up laundry");
        var held = AddTask("Sort the made up mail");
        Run($"""
            INSERT INTO after_this (item_id, source, held_at) VALUES ({expired.Id}, 'Capture', '{DateTime.Now.AddHours(-30):yyyy-MM-ddTHH:mm:ss}');
            INSERT INTO after_this (item_id, source, held_at) VALUES ({held.Id}, 'Capture', '{DateTime.Now.AddHours(-1):yyyy-MM-ddTHH:mm:ss}');
            """);
        FailWhen($"BEFORE DELETE ON after_this WHEN OLD.item_id = {held.Id}");

        Assert.ThrowsAny<SqliteException>(() => _database.TakeAfterThis(DateTime.Now));

        Assert.Equal(2, Count("after_this"));
    }

    [Fact]
    public void A_snooze_that_fails_keeps_the_hold_for_after_this()
    {
        var item = AddTask("Book the made up visit");
        Run($"INSERT INTO reminders (item_id, remind_at) VALUES ({item.Id}, '{DateTime.Now.AddMinutes(-5):yyyy-MM-ddTHH:mm:ss}')");
        var reminder = _database.GetDueReminders(DateTime.Now).Single(due => due.Item.Id == item.Id);
        Assert.True(_database.HoldAfterThis(item, DateTime.Now));
        FailWhen("BEFORE UPDATE OF remind_at ON reminders");

        Assert.ThrowsAny<SqliteException>(() => _database.SnoozeReminder(reminder, DateTime.Now.AddMinutes(10)));

        Assert.Equal(1, Count("after_this"));
    }

    [Fact]
    public void A_purchase_added_by_hand_that_fails_leaves_no_capture()
    {
        _database.AddCategory("Made Up Treats", null, BudgetPeriod.Month);
        FailWhen("BEFORE UPDATE OF category ON items");

        Assert.ThrowsAny<SqliteException>(() => _database.AddExpense("Made up cone", 450, "Made Up Treats", Day, DateTime.Now));

        Assert.Equal(0, Count("items"));
        Assert.Equal(0, Count("captures"));
    }

    [Fact]
    public void A_note_whose_text_fails_isnt_made()
    {
        FailWhen("BEFORE UPDATE OF body ON items");

        Assert.ThrowsAny<SqliteException>(() => _database.CreateNote("Made up title", "Made up body", DateTime.Now));

        Assert.Equal(0, Count("items"));
        Assert.Equal(0, Count("captures"));
    }

    [Fact]
    public void A_list_rename_whose_posts_fail_keeps_its_name()
    {
        _database.CreateList("Made up errands", DateTime.Now);
        Run("INSERT INTO discord_posts (thread_id, kind, item_key, forum_id, state) VALUES ('made-up-thread', 'list', 'Made up errands', 'made-up-forum', 'live')");
        FailWhen("BEFORE UPDATE ON discord_posts");

        Assert.ThrowsAny<SqliteException>(() => _database.RenameList("Made up errands", "Made up chores", DateTime.Now));

        Assert.Contains("Made up errands", _database.GetListNames());
        Assert.DoesNotContain("Made up chores", _database.GetListNames());
    }

    [Fact]
    public void A_second_match_of_the_same_repayment_says_already_settled()
    {
        _database.ImportTransactions("Made Up Card", [new ImportedTransaction(Day, "MADE UP BAKERY", 3000)]);
        _database.AddRepayment("made-up-payment", new PaymentAppPayment("A Made Up Friend", 1500, null), Day);
        var purchase = Scalar("SELECT id FROM items WHERE kind = 'Expense'");

        Assert.StartsWith("Marked", _database.MatchRepaymentTo("made-up-payment", purchase).Text);
        Assert.Equal("That repayment is already settled", _database.MatchRepaymentTo("made-up-payment", purchase).Text);
        Assert.Equal(1500, Scalar("SELECT reimbursed_cents FROM items WHERE kind = 'Expense'"));
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
