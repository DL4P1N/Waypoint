using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

public sealed class PayScheduleTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 11, 2);
    private static readonly ScheduledItem Paycheck = new("Paycheck", 85000, [10, 25], true);
    private static readonly ScheduledItem Transfer = new("Savings transfer", -9500, [12, 28]);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-{Guid.NewGuid():N}.db");
    private readonly WaypointDatabase _database;

    public PayScheduleTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private void SeedLive()
    {
        _database.AddScheduledItem(Paycheck);
        _database.AddScheduledItem(Transfer);
        _database.SaveBudgetAccount(new BudgetAccount("Northwind Bank checking", AccountKind.Checking, 50000, Today));
    }

    [Fact]
    public void Existing_schedule_rows_read_back_as_they_were()
    {
        SeedLive();

        var rows = _database.GetSchedule();
        Assert.Equal([10, 25], rows[0].Days);
        Assert.True(rows[0].EarlierOnWeekend);
        Assert.Equal(PayPattern.MonthDays, rows[0].Pattern);
        Assert.False(rows[1].EarlierOnWeekend);
    }

    [Fact]
    public void A_database_from_before_pay_schedules_upgrades_in_place()
    {
        var path = Path.Combine(Path.GetTempPath(), $"waypoint-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE budget_schedule (id INTEGER PRIMARY KEY, label TEXT NOT NULL, amount_cents INTEGER NOT NULL, days TEXT NOT NULL, earlier_on_weekend INTEGER NOT NULL DEFAULT 0);
                    INSERT INTO budget_schedule (label, amount_cents, days, earlier_on_weekend) VALUES ('Paycheck', 85000, '10,25', 1), ('Savings transfer', -9500, '12,28', 0);
                    CREATE TABLE wait_list (id INTEGER PRIMARY KEY, description TEXT NOT NULL, price_cents INTEGER NOT NULL, added_at TEXT NOT NULL, decision TEXT);
                    INSERT INTO wait_list (description, price_cents, added_at) VALUES ('Keyboard', 6000, '2026-11-01T12:00:00');
                    """;
                command.ExecuteNonQuery();
            }

            var upgraded = new WaypointDatabase(path);
            var rows = upgraded.GetSchedule();

            Assert.Equal(2, rows.Count);
            Assert.Equal(PayPattern.MonthDays, rows[0].Pattern);
            Assert.Equal([10, 25], rows[0].Days);
            Assert.Equal(new DateOnly(2026, 11, 10), BudgetMath.NextOccurrence(rows[0], Today));
            Assert.Equal(48, upgraded.GetWaitList().Single().Hours);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public void Live_schedule_forecast_is_unchanged_on_non_holiday_dates()
    {
        // A Monday, so the window holds two paydays moved for a weekend.
        // Worked out by hand: Jan 9 (the 10th is a Saturday), Jan 12, Jan 23 (the 25th is a Sunday), Jan 28, Feb 10.
        var start = new DateOnly(2026, 1, 5);
        var flows = BudgetMath.Flows(start, 36, [Paycheck, Transfer], []);

        Assert.Equal(
            [
                (new DateOnly(2026, 1, 9), 85000L),
                (new DateOnly(2026, 1, 12), -9500L),
                (new DateOnly(2026, 1, 23), 85000L),
                (new DateOnly(2026, 1, 28), -9500L),
                (new DateOnly(2026, 2, 10), 85000L),
            ],
            flows.Select(flow => (flow.Date, flow.Cents)));

        var forecast = BudgetMath.Forecast(50000, start, 35, [Paycheck, Transfer], []);
        Assert.Equal(50000, forecast.LowestCents);
        Assert.Equal(50000 + (85000 * 2) - (9500 * 2), forecast.EndCents);
    }

    [Theory]
    [InlineData(2026, 11, 11, "2026-11-10")] // Veterans Day, a Wednesday.
    [InlineData(2026, 12, 25, "2026-12-24")] // Christmas, a Friday.
    [InlineData(2027, 9, 6, "2027-09-03")] // Labor Day, a Monday, so back over the weekend.
    [InlineData(2027, 7, 5, "2027-07-02")] // July 4 is a Sunday, observed Monday.
    public void Paydays_move_earlier_for_bank_holidays(int year, int month, int day, string expected)
    {
        var item = new ScheduledItem("Paycheck", 100, [day], true);
        var nominal = new DateOnly(year, month, day);

        Assert.True(BankHolidays.IsHoliday(nominal));
        Assert.True(BudgetMath.Falls(item, DateOnly.Parse(expected)));
        Assert.False(BudgetMath.Falls(item, nominal));
    }

    [Fact]
    public void Items_that_do_not_move_ignore_holidays()
    {
        Assert.True(BudgetMath.Falls(new ScheduledItem("Rent", -100, [11]), new DateOnly(2026, 11, 11)));
    }

    [Fact]
    public void Biweekly_and_weekly_land_on_their_cycle_and_before_it()
    {
        var biweekly = new ScheduledItem("Paycheck", 85000, [], true, PayPattern.Biweekly, new DateOnly(2026, 11, 5));
        var dates = BudgetMath.Flows(Today, 40, [biweekly], []).Select(flow => flow.Date).ToList();
        Assert.Equal([new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 19), new DateOnly(2026, 12, 3)], dates);

        // Christmas 2026 is a Friday, so that week's pay lands Thursday.
        var weekly = new ScheduledItem("Paycheck", 30000, [], true, PayPattern.Weekly, new DateOnly(2026, 11, 27));
        Assert.True(BudgetMath.Falls(weekly, new DateOnly(2026, 12, 24)));
        Assert.False(BudgetMath.Falls(weekly, new DateOnly(2026, 12, 25)));
    }

    [Fact]
    public void Typing_a_new_schedule_replaces_the_paycheck_rather_than_adding_one()
    {
        SeedLive();

        var reply = _database.SetPaySchedule("biweekly", [], 85000, new DateTime(2026, 11, 12));

        var income = _database.GetSchedule().Where(item => item.AmountCents > 0).ToList();
        Assert.Equal(85000, Assert.Single(income).AmountCents);
        Assert.Equal(PayPattern.Biweekly, income[0].Pattern);
        Assert.Equal(new DateOnly(2026, 11, 12), income[0].Anchor);
        Assert.Empty(income[0].Days);
        Assert.Contains("every other week", reply.Text);
        Assert.Equal(2, _database.GetSchedule().Count);
    }

    [Fact]
    public void A_new_schedule_whose_pay_dates_cant_be_cleared_keeps_the_old_paycheck()
    {
        SeedLive();
        var pay = _database.GetSchedule()[0];
        Run($"INSERT INTO pay_calendar (schedule_id, pay_on) VALUES ({pay.Id}, '2026-11-14')");
        Run("CREATE TRIGGER fail_partway BEFORE DELETE ON pay_calendar BEGIN SELECT RAISE(ABORT, 'made to fail partway'); END");

        Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() => _database.SetPaySchedule("biweekly", [], 90000, new DateTime(2026, 11, 12)));

        var kept = _database.GetSchedule()[0];
        Assert.Equal(85000, kept.AmountCents);
        Assert.Equal(PayPattern.MonthDays, kept.Pattern);
        Assert.Equal([10, 25], kept.Days);
    }

    private void Run(string sql)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Changing_the_amount_keeps_the_schedule()
    {
        SeedLive();

        _database.SetPayAmount(70000);

        var pay = _database.GetSchedule()[0];
        Assert.Equal(70000, pay.AmountCents);
        Assert.Equal([10, 25], pay.Days);
    }

    [Theory]
    [InlineData(null, "How often")]
    [InlineData("biweekly", "next payday")]
    [InlineData("twice_monthly", "two days")]
    [InlineData("monthly", "day of the month")]
    public void Vague_schedules_ask_one_question_and_change_nothing(string? pattern, string question)
    {
        SeedLive();

        var reply = _database.SetPaySchedule(pattern, [], 85000, null);

        Assert.True(reply.IsAnswer);
        Assert.Contains(question, reply.Text);
        Assert.Equal(85000, _database.GetSchedule()[0].AmountCents);
    }

    [Fact]
    public void One_off_deposits_land_once_and_then_stop()
    {
        var day = new DateOnly(2026, 11, 14);
        _database.AddExpectedDeposit("Refund", 5000, day.ToDateTime(TimeOnly.MinValue));
        var item = _database.GetSchedule().Single();

        Assert.Equal(day, BudgetMath.NextOccurrence(item, Today));
        Assert.Null(BudgetMath.NextOccurrence(item, day.AddDays(1)));
    }

    private static CheckingRow Deposit(DateOnly date, long cents, string text = "ACME PAYROLL DIR DEP") =>
        new(date, text, cents, CheckingKind.Other);

    [Fact]
    public void A_real_deposit_confirms_the_paycheck_and_is_not_counted_twice()
    {
        SeedLive();
        var early = new DateOnly(2026, 11, 9);

        _database.ImportChecking([Deposit(early, 85000)]);

        var deposit = Assert.Single(_database.GetPayDeposits());
        Assert.Equal(new DateOnly(2026, 11, 10), deposit.DueOn);
        Assert.False(deposit.Typed);

        var flows = BudgetMath.Flows(Today, 10, _database.GetSchedule(), [], _database.GetPayDeposits());
        Assert.DoesNotContain(flows, flow => flow.Cents > 0);
    }

    [Fact]
    public void Importing_the_same_export_twice_changes_nothing()
    {
        SeedLive();
        var rows = new[] { Deposit(new DateOnly(2026, 11, 10), 85000) };

        _database.ImportChecking(rows);
        var balance = _database.GetCurrentAccounts(new DateOnly(2026, 11, 12)).Single(account => account.Kind == AccountKind.Checking).BalanceCents;
        _database.ImportChecking(rows);

        Assert.Single(_database.GetPayDeposits());
        Assert.Equal(balance, _database.GetCurrentAccounts(new DateOnly(2026, 11, 12)).Single(account => account.Kind == AccountKind.Checking).BalanceCents);
    }

    [Theory]
    [InlineData("SAVINGS TRANSFER IN NORTHWIND 1234", 85000)]
    [InlineData("ACME PAYROLL DIR DEP", 20000)]
    public void Transfers_and_odd_amounts_do_not_confirm_a_paycheck(string text, long cents)
    {
        SeedLive();

        _database.ImportChecking([Deposit(new DateOnly(2026, 11, 10), cents, text)]);

        Assert.Empty(_database.GetPayDeposits());
    }

    [Fact]
    public void A_typed_paycheck_is_replaced_by_the_real_deposit_without_counting_twice()
    {
        SeedLive();
        var payday = new DateOnly(2026, 11, 10);
        var after = payday.AddDays(2);

        _database.ConfirmPayTyped(85000, payday);
        Assert.True(Assert.Single(_database.GetPayDeposits()).Typed);
        Assert.Equal(135000, _database.GetCurrentAccounts(after).Single(account => account.Kind == AccountKind.Checking).BalanceCents);

        _database.ImportChecking([Deposit(payday, 85000)]);

        Assert.False(Assert.Single(_database.GetPayDeposits()).Typed);
        Assert.Equal(135000, _database.GetCurrentAccounts(after).Single(account => account.Kind == AccountKind.Checking).BalanceCents);
    }

    [Fact]
    public void Typed_pay_with_nothing_expected_asks_instead_of_guessing()
    {
        SeedLive();

        var reply = _database.ConfirmPayTyped(85000, new DateOnly(2026, 11, 14));

        Assert.True(reply.IsAnswer);
        Assert.Empty(_database.GetPayDeposits());
    }

    [Fact]
    public void Report_keeps_expected_and_confirmed_apart()
    {
        SeedLive();
        _database.ImportChecking([Deposit(new DateOnly(2026, 10, 22), 85000)]);

        var section = _database.GetBudgetReport(Today).Single(report => report.Title == "UNTIL PAYDAY");

        Assert.Contains(section.Rows, row => row.Label == "Paycheck, expected Nov 10");
        Assert.Contains(section.Rows, row => row.Label == "Last paycheck, confirmed Oct 22");
        Assert.Contains(_database.GetBudgetReport(Today).Single(report => report.Title == "COMING UP").Rows, row => row.Label == "Paycheck, expected Nov 10");
    }

    [Fact]
    public void Until_payday_lists_bills_before_the_deposit_and_its_low_point()
    {
        var cards = new[] { new BudgetAccount("Contoso Card", AccountKind.Card, 20000, Today, AutopayDay: 4) };

        var outlook = BudgetMath.UntilPayday(50000, Today, [Paycheck, Transfer], cards);

        Assert.Equal(new DateOnly(2026, 11, 10), outlook.Payday!.Date);
        Assert.Equal(30000, outlook.LowestCents);
        Assert.Equal(new DateOnly(2026, 11, 4), outlook.LowestOn);
        Assert.Equal("Contoso Card autopay", Assert.Single(outlook.Bills).Label);
    }

    private AffordRequest Request(long price, long checking = 50000, string? card = null, IReadOnlyList<BudgetAccount>? cards = null, DateOnly? seen = null, long? safe = null, BudgetAccount? cushion = null) => new(
        price,
        Today,
        new BudgetAccount("Northwind Bank checking", AccountKind.Checking, checking, Today),
        seen ?? Today,
        [Paycheck, Transfer],
        cards ?? [],
        CardName: card,
        Cushion: cushion,
        SafeToSpendCents: safe);

    [Fact]
    public void Afford_shows_the_consequence_and_what_it_assumes()
    {
        var answer = AffordCheck.Check(Request(6500));

        Assert.Equal(43500, answer.LowestCents);
        Assert.False(answer.UnderFloor);
        Assert.Contains("checking would be about $435.00 before payday on Nov 10", answer.Text);
        Assert.Contains("Paycheck $850.00 expected Nov 10", answer.Text);
        Assert.DoesNotContain("under your", answer.Text);
    }

    [Fact]
    public void Afford_says_when_it_goes_under_the_checking_minimum_or_the_cushion_is_short()
    {
        var cushion = new BudgetAccount("Cushion fund", AccountKind.Cushion, 40000, Today);
        var answer = AffordCheck.Check(Request(40000, cushion: cushion, safe: 2000));

        Assert.True(answer.UnderFloor);
        Assert.Contains("$50.00 under your $150.00 checking minimum", answer.Text);
        Assert.Contains("cushion is $100.00 short of $500.00", answer.Text);
        Assert.DoesNotContain("floor", answer.Text);
        Assert.DoesNotContain("checking cushion", answer.Text);
        Assert.Contains("today's $20.00 safe to spend", answer.Text);
    }

    [Fact]
    public void Afford_leads_with_yes_or_no_then_short_facts_then_what_it_assumes()
    {
        var yes = AffordCheck.Check(Request(6500)).Text.Split('\n');
        Assert.Equal("Yes. Checking stays above your $150.00 checking minimum.", yes[0]);
        Assert.StartsWith("• If you buy it", yes[1]);
        Assert.Equal("", yes[^2]);
        Assert.StartsWith("That assumes these happen", yes[^1]);

        var tight = AffordCheck.Check(Request(6500, safe: 2000)).Text;
        Assert.StartsWith("Yes, but it's tight. It's more than today's $20.00 safe to spend.\n", tight);

        var no = AffordCheck.Check(Request(40000, safe: 2000)).Text;
        Assert.StartsWith("No, not comfortably. Checking would go $50.00 under your $150.00 checking minimum.\n", no);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(no, "safe to spend"));
    }

    [Fact]
    public void Afford_flags_stale_balances_and_missing_ones()
    {
        Assert.Contains("last confirmed Oct 20", AffordCheck.Check(Request(100, seen: new DateOnly(2026, 10, 20))).Text);

        var answer = AffordCheck.Check(Request(100) with { Checking = null });
        Assert.Null(answer.LowestCents);
        Assert.Contains("checking balance", answer.Text);
    }

    [Fact]
    public void Card_purchases_reach_checking_at_the_autopay()
    {
        var card = new BudgetAccount("Contoso Card", AccountKind.Card, 10000, Today, CloseDay: 25, AutopayDay: 4);

        // The Nov 25 close comes after the Nov 4 autopay, so this waits for the next statement.
        Assert.Contains("next statement", AffordCheck.Check(Request(6500, card: "contoso card", cards: [card])).Text);

        var late = card with { CloseDay = 3 };
        var answer = AffordCheck.Check(Request(6500, card: "contoso card", cards: [late]));
        Assert.Contains("comes out with the Nov 4 autopay", answer.Text);
        Assert.Equal(50000 - 10000 - 6500, answer.LowestCents);
    }

    [Fact]
    public void A_card_purchase_says_what_it_does_to_that_cards_share_before_it_closes()
    {
        var card = new BudgetAccount("Contoso Card", AccountKind.Card, 70000, Today, 250000, CloseDay: 15, AutopayDay: 4);

        var high = AffordCheck.Check(Request(10000, card: "contoso card", cards: [card])).Text;
        Assert.Contains("• On Contoso Card that's 32% of its limit when it closes Nov 15, up from 28%. Paying $50.00 before then keeps it at 30%.", high);

        var low = AffordCheck.Check(Request(2000, card: "contoso card", cards: [card])).Text;
        Assert.Contains("• On Contoso Card that's 29% of its limit when it closes Nov 15, up from 28%.\n", low);

        Assert.DoesNotContain("of its limit", AffordCheck.Check(Request(2000)).Text);
        Assert.DoesNotContain("of its limit", AffordCheck.Check(Request(2000, card: "contoso card", cards: [card with { LimitCents = null }])).Text);
    }

    [Fact]
    public void Afford_never_gives_investing_debt_or_credit_advice()
    {
        var text = AffordCheck.Check(Request(40000)).Text.ToLowerInvariant();

        Assert.DoesNotContain("invest", text);
        Assert.DoesNotContain("debt", text);
        Assert.DoesNotContain("credit", text);
    }

    [Fact]
    public void Wait_time_can_be_chosen_per_item_or_by_default()
    {
        _database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.AddWait, "Keyboard", 6000, null, 24, "mine is broken"), Today);
        Assert.Equal(24, _database.GetWaitList().Single().Hours);
        Assert.Equal("mine is broken", _database.GetWaitList().Single().Reason);

        _database.SetDefaultWaitHours("72");
        Assert.Equal("Mouse is on the wait list for 72 hours", _database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.AddWait, "Mouse", 3500, null), Today));
    }

    [Fact]
    public void A_wait_checks_in_once_when_it_ends()
    {
        var added = new DateTime(2026, 11, 2, 12, 0, 0);
        _database.AddWaitItem("Keyboard", 6000, added, 24);

        Assert.Empty(_database.TakeWaitCheckIns(added.AddHours(23)));
        Assert.Equal("Keyboard", Assert.Single(_database.TakeWaitCheckIns(added.AddHours(25))).Description);
        Assert.Empty(_database.TakeWaitCheckIns(added.AddHours(26)));
        Assert.Single(_database.GetWaitList());
    }

    [Fact]
    public void A_parked_purchase_imported_later_is_settled_not_duplicated()
    {
        var added = new DateTime(2026, 11, 2, 12, 0, 0);
        _database.AddWaitItem("Sony headphones", 8000, added);

        var rows = new[] { new ImportedTransaction(new DateOnly(2026, 11, 5), "SONY STORE 1234", 8600) };
        var first = _database.ImportTransactions("Contoso Card", rows);
        var second = _database.ImportTransactions("Contoso Card", rows);

        Assert.Equal(new ImportResult(1, 0, 1), first);
        Assert.Equal(new ImportResult(0, 1), second);
        Assert.Empty(_database.GetWaitList());
        Assert.Single(_database.GetSpending(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)));
    }

    [Theory]
    [InlineData("MEGAMART 1234", 8000, "2026-11-05")] // No word in common.
    [InlineData("SONY STORE", 12000, "2026-11-05")] // Far off the price.
    [InlineData("SONY STORE", 8000, "2026-10-30")] // Before it was parked.
    [InlineData("SONY STORE", 8000, "2027-01-05")] // Long after.
    public void Unsure_matches_leave_the_wait_list_alone(string merchant, long cents, string date)
    {
        _database.AddWaitItem("Sony headphones", 8000, new DateTime(2026, 11, 2, 12, 0, 0));

        var result = _database.ImportTransactions("Contoso Card", [new ImportedTransaction(DateOnly.Parse(date), merchant, cents)]);

        Assert.Equal(0, result.WaitsResolved);
        Assert.Single(_database.GetWaitList());
    }

    [Fact]
    public void Two_parked_purchases_that_both_fit_are_left_alone()
    {
        var added = new DateTime(2026, 11, 2, 12, 0, 0);
        _database.AddWaitItem("Sony headphones", 8000, added);
        _database.AddWaitItem("Sony speaker", 8000, added);

        var result = _database.ImportTransactions("Contoso Card", [new ImportedTransaction(new DateOnly(2026, 11, 5), "SONY STORE", 8000)]);

        Assert.Equal(0, result.WaitsResolved);
        Assert.Equal(2, _database.GetWaitList().Count);
    }

    [Fact]
    public void A_typed_purchase_of_a_parked_item_is_not_imported_again()
    {
        var added = new DateTime(2026, 11, 2, 12, 0, 0);
        _database.AddWaitItem("Sony headphones", 8000, added);
        var capture = _database.AddCapture("bought the headphones");
        _database.CompleteCapture(capture, [new SortedItem(ItemKind.Expense, "Sony headphones", new DateOnly(2026, 11, 5), 8000)]);
        _database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.ResolveWait, "headphones", null, "bought"), Today);

        var result = _database.ImportTransactions("Contoso Card", [new ImportedTransaction(new DateOnly(2026, 11, 5), "SONY STORE", 8000)]);

        Assert.Equal(0, result.Added);
        Assert.Single(_database.GetSpending(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)));
    }

    [Fact]
    public void Waiting_purchases_can_be_found()
    {
        _database.AddWaitItem("Sony headphones", 8000, DateTime.Now);

        Assert.Contains(_database.SearchEverything(new SearchQuery("headphones", null, null, null), DateTime.Now), doc => doc.Text == "Sony headphones");
    }

    private static SpendingRow Row(DateOnly date, long cents, TimeOnly? at = null, string category = "Food", string source = SpendingRow.TypedSource) =>
        new(date, "Cafe", cents, category, false, 0, source, at);

    [Fact]
    public void Patterns_use_only_the_times_that_exist()
    {
        var rows = new[]
        {
            Row(Today, 2000, new TimeOnly(23, 10)),
            Row(Today, 2000, new TimeOnly(1, 15)),
            Row(Today, 2000, new TimeOnly(22, 30)),
            Row(Today, 2000, new TimeOnly(12, 0)),
            Row(Today, 9000, null, source: "Contoso Card"),
        };

        var lines = BudgetCoach.Patterns(Today, [], rows);

        Assert.Contains(lines, line => line.StartsWith("3 of your 4 typed purchases"));
        Assert.Contains(lines, line => line.Contains("only carry dates"));
    }

    [Fact]
    public void Patterns_note_small_purchases_and_overages_without_judging()
    {
        var rows = Enumerable.Range(0, 8).Select(_ => Row(Today, 600)).ToList();

        var lines = BudgetCoach.Patterns(Today, [new BudgetCategory("Food", 1000)], rows);

        Assert.Contains(lines, line => line.StartsWith("8 purchases under $10.00") && line.Contains("$48.00"));
        Assert.Contains(lines, line => line == "Food is at $48.00 of $10.00 this month.");
        Assert.DoesNotContain(lines, line => line.Contains("too", StringComparison.OrdinalIgnoreCase) || line.Contains("should", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Patterns_are_quiet_when_nothing_stands_out()
    {
        Assert.Contains("Nothing stands out", BudgetCoach.Patterns(Today, [], [])[0]);
    }

    [Fact]
    public void Typed_purchases_carry_their_time_and_imports_do_not()
    {
        var capture = _database.AddCapture("spent $20 at cafe", new DateTimeOffset(new DateTime(2026, 11, 2, 23, 30, 0)));
        _database.CompleteCapture(capture, [new SortedItem(ItemKind.Expense, "Cafe", new DateOnly(2026, 11, 2), 2000)]);
        _database.ImportTransactions("Contoso Card", [new ImportedTransaction(new DateOnly(2026, 11, 2), "SHOP", 3000)]);

        var rows = _database.GetSpending(Today, Today);

        Assert.Equal(new TimeOnly(23, 30), rows.Single(row => row.Title == "Cafe").At);
        Assert.Null(rows.Single(row => row.Title == "SHOP").At);
    }

    [Fact]
    public void Sorter_reads_wait_hours_and_pay_ops()
    {
        var json = """
            {"structured_output":{"items":[],"changes":[],"answer":null,"monthly_budget_cents":null,
             "budget_ops":[{"op":"add_wait","name":"Keyboard","amount_cents":6000,"category":null,"hours":24,"reason":"mine broke"}],
             "ops":[{"op":"afford_check","name":null,"text":null,"next":null,"entries":null,"amount_cents":6500,"at":null,"until":null}]}}
            """;

        var result = ClaudeCliSorter.ParseResult(json)!;

        Assert.Equal(new BudgetOp(BudgetOpKind.AddWait, "Keyboard", 6000, null, 24, "mine broke"), result.BudgetOps!.Single());
        Assert.Equal(AssistantOpKind.AffordCheck, result.Ops!.Single().Kind);
    }
}
