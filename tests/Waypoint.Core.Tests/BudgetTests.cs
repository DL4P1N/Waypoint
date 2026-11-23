using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

public sealed class BudgetTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public BudgetTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private static BudgetAccount Card(string name, long balance, long limit, int close, int autopay) =>
        new(name, AccountKind.Card, balance, new DateOnly(2026, 10, 7), limit, close, autopay);

    [Theory]
    [InlineData(30000, 7000)]
    [InlineData(15000, 3500)]
    [InlineData(5000, 1200)]
    [InlineData(0, 0)]
    public void Monthly_caps_round_up_to_weekly_dollars(long monthly, long weekly) =>
        Assert.Equal(weekly, BudgetMath.WeeklyCap(monthly));

    [Fact]
    public void Weeks_run_monday_to_sunday()
    {
        Assert.Equal(new DateOnly(2026, 10, 26), BudgetMath.WeekStart(new DateOnly(2026, 11, 1)));
        Assert.Equal(new DateOnly(2026, 11, 2), BudgetMath.WeekStart(new DateOnly(2026, 11, 2)));
    }

    [Fact]
    public void Forecast_finds_the_lowest_day_of_a_month()
    {
        ScheduledItem[] schedule =
        [
            new("Paycheck", 85000, [10, 25], true),
            new("Savings", -5000, [11, 26]),
            new("Savings top-up", -50000, [26]),
            new("Contoso Card pay-down", -10000, [31]),
        ];

        var forecast = BudgetMath.Forecast(50000, new DateOnly(2026, 10, 7), 29, schedule,
            [Card("Lucerne Card", 8000, 80000, 22, 17), Card("Contoso Card", 120000, 250000, 9, 3)]);

        Assert.Equal(22000, forecast.LowestCents);
        Assert.Equal(new DateOnly(2026, 11, 3), forecast.LowestOn);
        Assert.Equal(22000, forecast.EndCents);
    }

    [Theory]
    [InlineData("2027-07-22", 6000000)]
    [InlineData("2027-07-23", 6085000)]
    public void Paydays_move_earlier_off_a_weekend(string start, long expected)
    {
        var forecast = BudgetMath.Forecast(6000000, DateOnly.Parse(start).AddDays(-1), 1, [new("Paycheck", 85000, [10, 25], true)], []);

        Assert.Equal(expected, forecast.EndCents);
    }

    [Fact]
    public void Pay_down_lands_on_the_target_share()
    {
        Assert.Equal(50000, BudgetMath.PayDownTo(125000, 250000, 0.30m));
        Assert.Equal(100000, BudgetMath.PayDownTo(125000, 250000, 0.10m));
        Assert.Equal(0, BudgetMath.PayDownTo(1000, 250000, 0.10m));
    }

    [Fact]
    public void Report_flags_what_is_over_and_leaves_out_one_offs_and_repaid_money()
    {
        var today = new DateOnly(2026, 11, 4);
        var report = BudgetReport.Build(today,
            [new BudgetCategory("Groceries", 22500, BudgetPeriod.Week), new BudgetCategory("Shopping", 5000, BudgetPeriod.Week), new BudgetCategory("Rides", null, BudgetPeriod.Week)],
            [
                new SpendingRow(today, "Trader Joe's", 8000, "Groceries", false, 0),
                new SpendingRow(today, "Gift", 9000, "Shopping", true, 0),
                new SpendingRow(today, "Quickride", 2000, "Rides", false, 2000),
            ],
            [
                new BudgetAccount("Checking", AccountKind.Checking, 10000, today),
                Card("Contoso Card", 125000, 250000, 9, 3),
                new BudgetAccount("Cushion fund", AccountKind.Cushion, 40000, today),
            ],
            []);

        var week = report.Single(section => section.Title == "THIS WEEK").Rows;
        Assert.Equal("$80.00 of $52.00, $28.00 over", week[0].Value);
        Assert.True(week[0].Flagged);
        Assert.False(week[1].Flagged);
        Assert.Equal("$0.00", week[2].Value);

        var attention = BudgetReport.Attention(report);
        Assert.Contains(attention, line => line.StartsWith("Groceries"));
        Assert.Contains(attention, line => line.StartsWith("Contoso Card"));
        Assert.Contains(attention, line => line.StartsWith("Cushion is $") && line.EndsWith(" short of $500.00."));
        Assert.Contains(attention, line => line == "Checking is at $100.00, under your $150.00 checking minimum.");
        Assert.DoesNotContain(attention, line => line.Contains("checking cushion") || line.Contains("like to keep"));
        Assert.Contains(attention, line => line == "Groceries is $28.00 over this week's $52.00 budget.");
        Assert.Contains(attention, line => line == "Contoso Card is at 50%. Paying $500.00 brings it down to 30%.");

        var net = report.Single(section => section.Title == "NET WORTH").Rows;
        Assert.Equal("-$750.00", net[2].Value.Replace("$-", "-$"));
    }

    [Fact]
    public void Wait_list_coming_up_and_other_income_show_in_the_report()
    {
        var now = new DateTime(2026, 11, 4, 12, 0, 0);
        var sections = BudgetReport.Extras(now,
            [new WaitItem("Headphones", 8000, now.AddHours(-1)), new WaitItem("Keyboard", 6000, now.AddHours(-49))],
            [new ScheduledItem("Paycheck", 85000, [10, 25], true)],
            [new OtherIncome(new DateOnly(2026, 11, 1), "Freelance", 40000)]);

        var wait = sections[0].Rows;
        Assert.Equal("$80.00, ready Nov 6, 11:00 AM", wait[0].Value);
        Assert.True(wait[1].Flagged);
        Assert.Equal("Paycheck, expected Nov 10", sections[1].Rows.Single().Label);
        Assert.Equal("$100.00", sections[2].Rows[1].Value);
    }

    [Fact]
    public async Task Typed_wait_list_and_income_updates_work()
    {
        var service = new CaptureService(_database, new FakeSorter(new CaptureResult([], BudgetOps:
        [
            new BudgetOp(BudgetOpKind.AddWait, "Headphones", 8000, null),
            new BudgetOp(BudgetOpKind.AddIncome, "Freelance", 40000, null),
        ])));
        service.Capture("x");
        await service.ProcessPendingAsync();

        Assert.Equal("Headphones", _database.GetWaitList().Single().Description);
        Assert.Equal(40000, _database.GetOtherIncome(Today.Year).Single().AmountCents);

        Assert.Equal("headphones is off the wait list", _database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.ResolveWait, "headphones", null, "skipped"), Today));
        Assert.Empty(_database.GetWaitList());
    }

    [Theory]
    [InlineData(4, "  Closes today")]
    [InlineData(5, "  Closes tomorrow")]
    [InlineData(16, "  Closes Nov 16, in 12 days")]
    public void The_close_line_reads_naturally_for_today_and_tomorrow(int closeDay, string expected)
    {
        var report = BudgetReport.Build(new DateOnly(2026, 11, 4), [], [], [Card("Contoso Card", 87500, 250000, closeDay, 20)], []);

        Assert.Contains(report.Single(section => section.Title == "CARDS").Rows, row => row.Label == expected);
    }

    [Fact]
    public void Pay_down_lines_only_show_when_there_is_something_to_pay()
    {
        // At 10% already, neither line has anything to say. At 35%, both do.
        var low = BudgetReport.Build(new DateOnly(2026, 11, 4), [], [], [Card("Lucerne Card", 8000, 80000, 22, 17)], []);
        Assert.DoesNotContain(low.Single(section => section.Title == "CARDS").Rows, row => row.Label.Contains("Pay"));

        var high = BudgetReport.Build(new DateOnly(2026, 11, 4), [], [], [Card("Contoso Card", 87500, 250000, 9, 3)], []);
        var labels = high.Single(section => section.Title == "CARDS").Rows.Select(row => row.Label.Trim()).ToList();
        Assert.Contains("Pay $125.00 to get down to 30%", labels);
        Assert.Contains("Pay $625.00 to get down to 10%", labels);
    }

    [Fact]
    public void Budget_text_says_budget_and_cushion_rather_than_cap_floor_plan_or_top()
    {
        var today = new DateOnly(2026, 11, 4);
        var report = BudgetReport.Build(today,
            [new BudgetCategory("Groceries", 22500, BudgetPeriod.Week), new BudgetCategory("Wants", 5000)],
            [new SpendingRow(today, "Trader Joe's", 8000, "Groceries", false, 0), new SpendingRow(today, "Megamart", 9000, "Wants", false, 0)],
            [
                new BudgetAccount("Checking", AccountKind.Checking, 10000, today),
                Card("Contoso Card", 125000, 250000, 9, 3),
                new BudgetAccount("Cushion fund", AccountKind.Cushion, 70000, today),
            ],
            []);
        var text = string.Join(" ", report.SelectMany(section => section.Rows).Select(row => $"{row.Label} {row.Value} {row.Note}"));

        foreach (var word in new[] { " cap", "Cap", "floor", "Floor", " plan", " top" })
        {
            Assert.DoesNotContain(word, text);
        }

        Assert.Contains("Wants is $40.00 over this month's $50.00 budget.", text);
    }

    [Fact]
    public void A_card_over_its_alert_amount_is_flagged()
    {
        var report = BudgetReport.Build(new DateOnly(2026, 11, 4), [],
            [],
            [Card("Lucerne Card", 6000, 80000, 22, 17) with { AlertCents = 5000 }],
            []);

        Assert.Contains(BudgetReport.Attention(report), line => line.StartsWith("Lucerne Card"));
    }

    [Fact]
    public void Balances_set_more_than_a_week_ago_are_named_in_one_line()
    {
        var today = new DateOnly(2026, 11, 11);
        BudgetAccount[] accounts =
        [
            new("Northwind Bank checking", AccountKind.Checking, 50000, today.AddDays(-12)),
            Card("Contoso Card", 10000, 250000, 9, 3) with { UpdatedOn = today.AddDays(-9) },
            Card("Lucerne Card", 10000, 80000, 22, 17) with { UpdatedOn = today.AddDays(-7) },
            new("Cushion fund", AccountKind.Cushion, 50000, today.AddDays(-40)),
        ];

        Assert.Equal("Checking last set 12 days ago, Contoso Card 9 days ago", BudgetReport.StaleLine(accounts, today));
        Assert.Equal("Contoso Card last set 9 days ago", BudgetReport.StaleLine(accounts[1..], today));
        Assert.Null(BudgetReport.StaleLine(accounts[2..], today));
    }

    [Fact]
    public void The_briefing_says_a_stale_balance_on_its_safe_to_spend_line()
    {
        _database.SaveBudgetAccount(new BudgetAccount("Northwind Bank checking", AccountKind.Checking, 50000, Today.AddDays(-12)));

        var text = Briefing.Build(DateTime.Now, [], null, 1250, _database.GetStaleBalances(Today));

        Assert.EndsWith("Safe to spend today: $12.50. Checking last set 12 days ago", text);
    }

    [Fact]
    public void Cards_show_utilization_across_all_of_them()
    {
        var report = BudgetReport.Build(new DateOnly(2026, 11, 4), [], [], [Card("Contoso Card", 87500, 250000, 9, 3), Card("Lucerne Card", 8000, 80000, 22, 17)], []);

        var all = report.Single(section => section.Title == "CARDS").Rows.Single(row => row.Label == "All cards");
        Assert.Equal($"$955.00 of $3,300.00, {955m / 3300:P0}", all.Value);
        Assert.False(all.Flagged);
    }

    [Theory]
    [InlineData(5, "Contoso Card closes tomorrow. Pay $125.00 before then to get to 30%.")]
    [InlineData(7, "Contoso Card closes Saturday. Pay $125.00 before then to get to 30%.")]
    [InlineData(4, "Contoso Card closes today. Pay $125.00 today to get to 30%.")]
    public void A_card_closing_within_three_days_over_30_percent_says_what_to_pay_before_then(int closeDay, string expected)
    {
        var report = BudgetReport.Build(new DateOnly(2026, 11, 4), [], [], [Card("Contoso Card", 87500, 250000, closeDay, 20)], []);

        var attention = BudgetReport.Attention(report);
        Assert.Contains(expected, attention);
        Assert.Single(attention, line => line.StartsWith("Contoso Card"));
    }

    [Fact]
    public void A_card_at_exactly_30_percent_with_no_alert_set_says_so_without_a_made_up_alert()
    {
        var report = BudgetReport.Build(new DateOnly(2026, 11, 4), [], [], [Card("Contoso Card", 75000, 250000, 20, 25)], []);

        var line = Assert.Single(BudgetReport.Attention(report), text => text.StartsWith("Contoso Card", StringComparison.Ordinal));
        Assert.Equal("Contoso Card is at 30%.", line);
        Assert.DoesNotContain("you set to hear about", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_card_over_the_alert_it_was_given_says_so()
    {
        var card = Card("Contoso Card", 20000, 250000, 20, 25) with { AlertCents = 10000 };
        var report = BudgetReport.Build(new DateOnly(2026, 11, 4), [], [], [card], []);

        Assert.Contains("Contoso Card is at $200.00, above the $100.00 you set to hear about.", BudgetReport.Attention(report));
    }

    [Fact]
    public void A_card_closing_later_keeps_the_plain_pay_down_note()
    {
        var report = BudgetReport.Build(new DateOnly(2026, 11, 4), [], [], [Card("Contoso Card", 87500, 250000, 8, 20)], []);

        Assert.Contains("Contoso Card is at 35%. Paying $125.00 brings it down to 30%.", BudgetReport.Attention(report));
    }

    [Fact]
    public void Card_csv_is_read_and_payments_are_left_out()
    {
        var rows = CsvImport.Parse("""
            Posted On,Purchase Date,Category,Description,Amount
            10/30/2026,10/29/2026,Grocery Stores,"TRADER JOE'S, OAKHAVEN",42.50
            10/30/2026,10/30/2026,Payments,ONLINE PAYMENT RECEIVED,-150.00
            10/31/2026,10/31/2026,Shopping,MEGAMART REFUND,-9.99
            10/31/2026,10/31/2026,Home Improvement,HARDWARE STORE,15.00
            """);

        Assert.Equal(
            [
                (new DateOnly(2026, 10, 29), "TRADER JOE'S, OAKHAVEN", 4250L, "Groceries"),
                (new DateOnly(2026, 10, 31), "MEGAMART REFUND", -999L, "Shopping"),
                (new DateOnly(2026, 10, 31), "HARDWARE STORE", 1500L, (string?)null),
            ],
            rows.Select(row => (row.Date, row.Description, row.AmountCents, row.SuggestedCategories?.FirstOrDefault())));
    }

    [Theory]
    [InlineData("KROGER #12", "Kroger")]
    [InlineData("BIGBOX STORE 800-555-0100 ZZ", "Bigbox Store")]
    [InlineData("WHOLEFDS MKT 10234", "Whole Foods")]
    [InlineData("MEGAMART ONLINE MEGAMART.EXAMPLE/BILL", "Megamart Online Megamart.Example/Bill")]
    [InlineData("TST*BLUE PLATE Oakhaven ZZ", "Blue Plate")]
    [InlineData("STREAMCO* MONTHLY SUB", "Streamco* Monthly Sub")]
    [InlineData("DOMINO'S 1234", "Domino's")]
    [InlineData("CORNER MART OAKHAVEN ZZ", "Corner Mart")]
    [InlineData("CORNER MART  OAKHAVEN", "Corner Mart")]
    [InlineData("SQ *QUICKRIDE", "Quickride")]
    [InlineData("Pixel Cinema Oakhaven ZZ", "Pixel Cinema")]
    [InlineData("ZENTRIX, INC 8005550199 ZZ", "Zentrix, Inc")]
    public void Merchant_names_become_readable(string raw, string expected) =>
        Assert.Equal(expected, MerchantName.Clean(raw));

    [Fact]
    public void Card_payments_and_rewards_are_left_out_of_a_csv()
    {
        var rows = CsvImport.Parse("""
            Posted On,Purchase Date,Category,Description,Amount
            10/01/2026,10/01/2026,Payments,AUTOPAY STATEMENT BALANCE,-800.00
            10/02/2026,10/02/2026,Rewards,REWARD CREDIT,-5.00
            10/03/2026,10/03/2026,Shopping,MEGAMART ONLINE,23.75
            """);

        Assert.Equal(2375, rows.Single().AmountCents);
    }

    [Fact]
    public void Payment_wording_on_a_purchase_is_still_a_purchase()
    {
        const string plain = """
            Posted On,Purchase Date,Category,Description,Amount
            10/01/2026,10/01/2026,Payments,ONLINE PAYMENT THANK YOU,-300.00
            10/02/2026,10/02/2026,Shopping,QUICKPAYMENT KIOSK,25.00
            10/03/2026,10/03/2026,Entertainment,STREAMCO AUTOPAY,12.99
            """;
        Assert.Equal([30000L], CsvImport.ParsePayments(plain).Select(row => row.AmountCents));
        Assert.Equal([2500L, 1299L], CsvImport.Parse(plain).Select(row => row.AmountCents));

        // A type column file shows purchases as negative, so its payments are the positive rows.
        const string typed = """
            Date,Type,Description,Amount
            2026-10-04,CREDIT,PAYMENT RECEIVED,100.00
            2026-10-05,DEBIT,GYM AUTOPAY,-40.00
            """;
        Assert.Equal([10000L], CsvImport.ParsePayments(typed).Select(row => row.AmountCents));
        Assert.Equal([4000L], CsvImport.Parse(typed).Select(row => row.AmountCents));
    }

    [Fact]
    public void A_quoted_line_break_stays_in_its_row_and_unreadable_rows_are_counted()
    {
        var export = CsvImport.Read(
            "Posted On,Purchase Date,Category,Description,Amount\r\n"
            + "10/01/2026,10/01/2026,Shopping,\"MEGAMART\r\nONLINE \"\"SALE\"\"\",\"1,020.50\"\r\n"
            + "10/02/2026,someday,Shopping,CORNER MART,5.00\r\n"
            + "10/03/2026,10/03/2026,Shopping,BOOKSTORE,five\r\n"
            + ",,,,\r\n"
            + "10/04/2026,10/04/2026\r\n"
            + "10/05/2026,10/05/2026,Shopping,CAFE,4.25\r\n");

        Assert.Equal([("MEGAMART\r\nONLINE \"SALE\"", 102050L), ("CAFE", 425L)], export.Purchases.Select(row => (row.Description, row.AmountCents)));
        Assert.Equal(3, export.Unreadable);

        var (rows, unreadable) = CheckingImport.Read("Date,Details,Amount,Balance\n10/26/2026,\"PURCHASE\nCAFE\",-4.00,10.00\nnot a date,FEE,-1.00,9.00\n");
        Assert.Equal("PURCHASE\nCAFE", rows.Single().Description);
        Assert.Equal(1, unreadable);
    }

    [Fact]
    public void Type_column_csv_flips_the_sign()
    {
        var csv = """
            Date,Type,Description,Amount
            2026-10-29,DEBIT,BLUE PLATE,-18.50
            2026-10-30,CREDIT,PAYMENT RECEIVED,100.00
            """;

        Assert.True(CsvImport.IsTypeColumnExport(csv));
        Assert.Equal(1850, CsvImport.Parse(csv).Single().AmountCents);
    }

    [Fact]
    public void A_csv_without_the_needed_columns_says_so()
    {
        Assert.Throws<FormatException>(() => CsvImport.Parse("a,b\n1,2"));
    }

    [Fact]
    public void Category_details_cover_the_month_and_this_week()
    {
        var today = new DateOnly(2026, 11, 4);
        var details = BudgetReport.Categories(new DateOnly(2026, 11, 1), today,
            [new BudgetCategory("Groceries", 22500, BudgetPeriod.Week), new BudgetCategory("Rides", null)],
            [
                new SpendingRow(new DateOnly(2026, 11, 1), "Trader Joe's", 20000, "Groceries", false, 0),
                new SpendingRow(today, "Trader Joe's", 8000, "Groceries", false, 0),
                new SpendingRow(today, "Gift", 5000, "Groceries", true, 0),
                new SpendingRow(today, "Old category", 700, "Pets", false, 0),
                new SpendingRow(new DateOnly(2026, 10, 30), "Last month", 900, "Rides", false, 0),
            ]);

        var groceries = details[0];
        Assert.Equal(28000, groceries.MonthCents);
        Assert.Equal(8000, groceries.WeekCents);
        Assert.True(groceries.IsOver);
        Assert.Equal(3, groceries.Rows.Count);
        Assert.Equal(0, details[1].MonthCents);
        Assert.Equal("Pets", details[2].Name);

        var top = BudgetReport.TopMerchants(details).Rows;
        Assert.Equal(new ReportRow("Trader Joe's (2)", "$280.00"), top[0]);
    }

    [Fact]
    public void A_past_month_has_no_week_figures()
    {
        var details = BudgetReport.Categories(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 4), [new BudgetCategory("Groceries", 22500)], []);

        Assert.Null(details.Single().WeekCents);
    }

    [Fact]
    public void A_confirmed_deposit_grows_the_cushion_and_the_extra_gets_a_reminder()
    {
        _database.SaveBudgetAccount(new BudgetAccount("Cushion fund", AccountKind.Cushion, 52000, new DateOnly(2026, 10, 7)));

        _database.AddToCushion(18000, Today);

        var cushion = _database.GetBudgetAccounts().Single();
        Assert.Equal(70000, cushion.BalanceCents);
        Assert.Equal(Today, cushion.UpdatedOn);
        Assert.Contains(BudgetReport.Attention(_database.GetBudgetReport(Today)), line => line.Contains("ready to invest") && line.Contains("$100.00"));
    }

    [Fact]
    public void No_invest_reminder_at_or_under_the_top()
    {
        _database.SaveBudgetAccount(new BudgetAccount("Cushion fund", AccountKind.Cushion, 60000, Today));

        Assert.DoesNotContain(BudgetReport.Attention(_database.GetBudgetReport(Today)), line => line.Contains("ready to invest"));
    }

    private static SpendingRow Spend(int month, int day, string title, long cents, string category, bool oneOff = false) =>
        new(new DateOnly(2026, month, day), title, cents, category, oneOff, 0);

    private static List<SpendingRow> Fill(int month, string category, int count, long each, string title = "Cafe") =>
        Enumerable.Range(1, count).Select(day => Spend(month, day, title, each, category)).ToList();

    [Fact]
    public void Review_sizes_the_gap_and_names_the_biggest_merchant()
    {
        var spending = Fill(9, "Food", 12, 2000, "FOOD DELIVERY").Concat(Fill(9, "Groceries", 3, 1000)).ToList();

        var lines = BudgetCoach.Review(new DateOnly(2026, 10, 2), [new BudgetCategory("Food", 8000), new BudgetCategory("Groceries", 22500)], spending).Rows.Select(row => row.Label).ToList();

        Assert.Contains("September total: $270.00 against your $305.00 budget.", lines);
        var food = lines.Single(line => line.StartsWith("Food:"));
        Assert.Contains("$240.00 against $80.00 in September", food);
        Assert.Contains("8 fewer of your 12 purchases, at about $20.00 each", food);
        Assert.Contains("Food Delivery, $240.00 across 12", food);
        Assert.Contains(lines, line => line.StartsWith("Groceries averaged $30.00"));
    }

    [Fact]
    public void Review_notices_a_cap_missed_every_month()
    {
        var spending = new[] { 7, 8, 9 }.SelectMany(month => Fill(month, "Shopping", 10, 1000)).ToList();

        var line = BudgetCoach.Review(new DateOnly(2026, 10, 2), [new BudgetCategory("Shopping", 5000)], spending).Rows.Single(row => row.Label.StartsWith("Shopping:")).Label;

        Assert.Contains("over all 3 months, averaging $100.00", line);
        Assert.EndsWith("Want to set a budget for this?", line);
    }

    [Fact]
    public void Review_skips_thin_months_and_one_offs()
    {
        var spending = Fill(9, "Food", 12, 500);
        spending.Add(Spend(10, 2, "Refund", -1000, "Food"));
        spending.Add(Spend(9, 2, "Gift", 90000, "Food", true));

        var lines = BudgetCoach.Review(new DateOnly(2026, 11, 2), [new BudgetCategory("Food", 8000)], spending).Rows.Select(row => row.Label).ToList();

        Assert.Contains(lines, line => line.StartsWith("September total: $60.00"));
        Assert.DoesNotContain(lines, line => line.StartsWith("Food:"));
    }

    [Fact]
    public void Review_warns_about_pace_mid_month()
    {
        var spending = Fill(9, "Food", 10, 2000);

        var section = BudgetCoach.Review(new DateOnly(2026, 9, 10), [new BudgetCategory("Food", 8000)], spending);

        Assert.Contains("Food is on pace for about $600.00 this month, against $80.00.", section.Rows.Select(row => row.Label));
    }

    [Fact]
    public void Card_balances_follow_purchases_and_payments_both_ways_from_the_confirmed_day()
    {
        var anchor = new DateOnly(2026, 10, 7);
        var card = Card("Contoso Card", 10000, 250000, 9, 3) with { UpdatedOn = anchor };
        _database.SaveBudgetAccount(card);
        _database.ImportTransactions("Contoso Card", [new(anchor.AddDays(-3), "BEFORE", 2000), new(anchor.AddDays(3), "AFTER", 5000)]);
        _database.ImportTransactions("Lucerne Card", [new(anchor.AddDays(3), "OTHER CARD", 9900)]);
        var payments = new ImportedTransaction[] { new(anchor.AddDays(5), "AUTOPAY", 1500) };
        _database.AddCardPayments("Contoso Card", payments);
        _database.AddCardPayments("Contoso Card", payments);

        Assert.Equal(13500, _database.GetCardBalance(card, anchor.AddDays(10)));
        Assert.Equal(15000, _database.GetCardBalance(card, anchor.AddDays(4)));
        Assert.Equal(10000, _database.GetCardBalance(card, anchor));
        Assert.Equal(8000, _database.GetCardBalance(card, anchor.AddDays(-5)));
    }

    [Fact]
    public void Typing_a_balance_resets_the_base()
    {
        _database.SaveBudgetAccount(Card("Contoso Card", 125000, 250000, 9, 3));
        _database.ImportTransactions("Contoso Card", [new(new DateOnly(2026, 10, 20), "OLD", 5000)]);

        _database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.SetBalance, "Contoso Card", 20000, null), Today);

        Assert.Equal(20000, _database.GetCurrentAccounts(Today).Single().BalanceCents);
    }

    [Fact]
    public void A_balance_set_on_a_day_already_holds_that_days_activity()
    {
        var day = new DateOnly(2026, 10, 7);
        _database.SaveBudgetAccount(Card("Contoso Card", 10000, 250000, 9, 3) with { UpdatedOn = day });
        _database.SaveBudgetAccount(new BudgetAccount("Northwind Bank checking", AccountKind.Checking, 50000, day));
        _database.ImportTransactions("Contoso Card", [new(day, "SAME DAY", 2000), new(day.AddDays(1), "NEXT DAY", 3500)]);
        _database.ImportChecking([new CheckingRow(day, "SAME DAY", -1000, CheckingKind.Other), new CheckingRow(day.AddDays(1), "NEXT DAY", -4000, CheckingKind.Other)]);

        var accounts = _database.GetCurrentAccounts(day.AddDays(2));
        Assert.Equal(13500, accounts.Single(account => account.Kind == AccountKind.Card).BalanceCents);
        Assert.Equal(46000, accounts.Single(account => account.Kind == AccountKind.Checking).BalanceCents);
    }

    [Fact]
    public void Csv_keeps_payments_apart_from_purchases()
    {
        var csv = """
            Posted On,Purchase Date,Category,Description,Amount
            10/01/2026,10/01/2026,Payments,AUTOPAY STATEMENT BALANCE,-800.00
            10/03/2026,10/03/2026,Restaurants,CAFE,20.00
            """;

        Assert.Equal(80000, CsvImport.ParsePayments(csv).Single().AmountCents);
        Assert.Equal(2000, CsvImport.Parse(csv).Single().AmountCents);
    }

    private const string CheckingCsv = """
        "Date","Details","Amount","Balance"
        "10/26/2026","FEES PMT 261022","-2500.00","1000.00"
        "10/26/2026","FABRIKAM INV TRANSFER 261026","-120.00","3500.00"
        "10/22/2026","ACME PAYROLL 261015","700.00","3620.00"
        "10/19/2026","TAILSPIN PAY TO BANK 261017","1200.00","2920.00"
        "10/08/2026","CONTOSO CARD PAYMENT 261008","-150.00","1720.00"
        "10/08/2026","LUCERNE CARD PAYMENT 261008","-25.00","1870.00"
        "10/05/2026","TAILSPIN PAY PAYMENT 261005","-4.00","1895.00"
        "10/04/2026","CARD PURCHASE 10/03 TRADER JOES OAKHAVEN ZZ REF 123456789","-12.00","1899.00"
        """;

    [Fact]
    public void Checking_rows_are_sorted_so_only_real_spending_counts()
    {
        var rows = CheckingImport.Parse(CheckingCsv);

        Assert.True(CheckingImport.IsChecking(CheckingCsv));
        Assert.Equal(8, rows.Count);
        Assert.Equal(CheckingKind.Other, rows[0].Kind);
        Assert.Equal(CheckingKind.Other, rows[1].Kind);
        Assert.Equal(CheckingKind.Other, rows[3].Kind);
        Assert.Equal(("Contoso Card", CheckingKind.CardPayment), (rows[4].Card!, rows[4].Kind));
        Assert.Equal(("Lucerne Card", CheckingKind.CardPayment), (rows[5].Card!, rows[5].Kind));
        Assert.Equal(CheckingKind.Spending, rows[6].Kind);
        Assert.Equal(CheckingKind.Spending, rows[7].Kind);
    }

    [Theory]
    [InlineData("CARD PURCHASE 05/17 MEGAMART ONLINE REF 123456789", "Megamart Online")]
    [InlineData("TAILSPIN PAY PAYMENT 261005", "Tailspin Pay")]
    [InlineData("PURCHASE 03/31 ACME APTS PAYMENT 800-555-0102 ZZ REF123456", "Acme Apts Payment")]
    public void Checking_purchases_get_clean_names(string description, string expected) =>
        Assert.Equal(expected, MerchantName.Clean(CheckingImport.PurchaseTitle(description)));

    [Fact]
    public void Checking_import_counts_once_and_updates_balances()
    {
        var anchor = new DateOnly(2026, 10, 7);
        _database.SaveBudgetAccount(new BudgetAccount("Northwind Bank checking", AccountKind.Checking, 80000, anchor));
        _database.SaveBudgetAccount(new BudgetAccount("Cushion fund", AccountKind.Cushion, 50000, anchor));
        _database.SaveBudgetAccount(Card("Contoso Card", 30000, 250000, 9, 3) with { UpdatedOn = anchor });
        _database.AddCardPayments("Contoso Card", [new ImportedTransaction(new DateOnly(2026, 10, 8), "ONLINE PAYMENT RECEIVED", 15000)]);

        var rows = CheckingImport.Parse(CheckingCsv);
        _database.ImportChecking(rows);
        _database.ImportChecking(rows);

        var accounts = _database.GetCurrentAccounts(new DateOnly(2026, 11, 1));
        Assert.Equal(80000 - 15000 - 2500 + 120000 + 70000 - 12000 - 250000, accounts.Single(account => account.Kind == AccountKind.Checking).BalanceCents);
        Assert.Equal(62000, accounts.Single(account => account.Kind == AccountKind.Cushion).BalanceCents);
        Assert.Equal(15000, accounts.Single(account => account.Kind == AccountKind.Card).BalanceCents);

        var spending = _database.GetSpending(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        Assert.Equal(1600, spending.Sum(row => row.AmountCents));
        Assert.Contains(spending, row => row.Title == "Tailspin Pay");
    }

    [Fact]
    public void Refunds_pick_same_named_charges_by_the_card_they_came_in_on()
    {
        _database.ImportTransactions("Lucerne Card", [new(Today, "STREAMCO SUBSCRIPTION", 2000), new(Today, "STREAMCO SUBSCRIPTION ", 1000)]);
        _database.ImportTransactions("Contoso Card", [new(Today, "STREAMCO SUBSCRIPTION", 2000)]);

        var charges = _database.GetRecentlyFinished(DateTime.Now.AddHours(-1)).Where(item => item.Kind == ItemKind.Expense).ToList();
        var lucerne = charges.Where(item => item.Account == "Lucerne Card").ToList();
        Assert.Equal(2, lucerne.Count);
        Assert.Single(charges, item => item.Account == "Contoso Card");

        foreach (var charge in lucerne)
        {
            _database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.Reimburse, ItemRef.Format(charge), charge.AmountCents, null), Today);
        }

        var spending = _database.GetSpending(Today, Today);
        Assert.All(spending.Where(row => row.Source == "Lucerne Card"), row => Assert.Equal(0, row.NetCents));
        Assert.Equal(2000, spending.Single(row => row.Source == "Contoso Card").NetCents);
    }

    [Fact]
    public void Spending_shows_where_each_purchase_came_from()
    {
        _database.CompleteCapture(_database.AddCapture("spent $5 on coffee"), [new SortedItem(ItemKind.Expense, "coffee", Today, 500)]);
        _database.ImportTransactions("Contoso Card", [new ImportedTransaction(Today, "CAFE", 700)]);
        _database.ImportTransactions("Lucerne Card", [new ImportedTransaction(Today, "SHOP", 900)]);
        _database.ImportTransactions("Northwind Bank checking", [new ImportedTransaction(Today, "TAILSPIN PAY", 1100)]);

        var sources = _database.GetSpending(Today, Today).ToDictionary(row => row.Title, row => row.Source);

        Assert.Equal("Typed", sources["coffee"]);
        Assert.Equal("Contoso Card", sources["CAFE"]);
        Assert.Equal("Lucerne Card", sources["SHOP"]);
        Assert.Equal("Checking", sources["TAILSPIN PAY"]);
    }

    [Fact]
    public void Tip_picks_the_biggest_miss_in_one_line()
    {
        var spending = Fill(9, "Food", 12, 2000).Concat(Fill(9, "Shopping", 3, 2000)).ToList();

        var tip = BudgetCoach.Tip(new DateOnly(2026, 10, 2), [new BudgetCategory("Shopping", 5000), new BudgetCategory("Food", 8000)], spending);

        Assert.Equal("Food was $160.00 over in September. 8 fewer purchases would have kept it within budget.", tip);
        Assert.Null(BudgetCoach.Tip(new DateOnly(2026, 10, 2), [new BudgetCategory("Food", 90000)], Fill(9, "Food", 12, 2000)));
    }

    [Fact]
    public void Attention_lines_drop_the_as_of_date()
    {
        Assert.Equal("Contoso Card: $1,250.00 of $2,500.00, 50%", BudgetReport.ShortAttention("Contoso Card (as of Oct 7): $1,250.00 of $2,500.00, 50%"));
        Assert.Equal("Groceries: over", BudgetReport.ShortAttention("Groceries: over"));
    }

    [Fact]
    public void A_past_charge_can_be_marked_one_off()
    {
        _database.SaveCategory(new BudgetCategory("Buffer", 12500));
        _database.CompleteCapture(_database.AddCapture("x"), [new SortedItem(ItemKind.Expense, "Zentrix", Today, 18500)]);

        Assert.Equal("Zentrix won't count against your budgets", _database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.MarkOneOff, "zentrix", null, null), Today));

        Assert.True(_database.GetSpending(Today, Today).Single().OneOff);
    }

    [Fact]
    public void Review_is_empty_without_spending()
    {
        Assert.Empty(BudgetCoach.Review(new DateOnly(2026, 11, 2), [new BudgetCategory("Food", 8000)], []).Rows);
    }

    [Fact]
    public void Import_categorizes_counts_once_and_skips_what_was_typed()
    {
        _database.SaveMerchantRule("trader joe's", "Groceries");
        _database.CompleteCapture(_database.AddCapture("x"), [new SortedItem(ItemKind.Expense, "lunch", Today, 1200)]);
        ImportedTransaction[] rows =
        [
            new(Today, "TRADER JOE'S #12", 4000),
            new(Today, "TRADER JOE'S #12", 4000),
            new(Today, "CAFE", 1200),
        ];

        Assert.Equal(new ImportResult(2, 1), _database.ImportTransactions("Contoso Card", rows));
        Assert.Equal(new ImportResult(0, 3), _database.ImportTransactions("Contoso Card", rows));

        var spending = _database.GetSpending(Today, Today);
        Assert.Equal(8000, spending.Where(row => row.Category == "Groceries").Sum(row => row.AmountCents));
        Assert.DoesNotContain(_database.GetOpenItems(), item => item.Title.Contains("TRADER"));
    }

    [Fact]
    public async Task Typed_updates_change_the_trackers()
    {
        _database.SaveCategory(new BudgetCategory("Groceries", 22500));
        _database.SaveCategory(new BudgetCategory("Buffer", 12500));
        _database.SaveBudgetAccount(Card("Contoso Card", 125000, 250000, 9, 3));
        _database.CompleteCapture(_database.AddCapture("x"), [new SortedItem(ItemKind.Expense, "Target run", Today, 3000), new SortedItem(ItemKind.Expense, "Quickride", Today, 2000)]);

        BudgetOp[] ops =
        [
            new(BudgetOpKind.SetCap, "groceries", 32000, null),
            new(BudgetOpKind.SetBalance, "contoso card", 20000, null),
            new(BudgetOpKind.SetLimit, "contoso card", 200000, null),
            new(BudgetOpKind.Categorize, "Target", null, "groceries"),
            new(BudgetOpKind.Reimburse, "quickride", 1500, null),
            new(BudgetOpKind.SetBalance, "nowhere", 1, null),
        ];
        var service = new CaptureService(_database, new FakeSorter(new CaptureResult([], BudgetOps: ops)));
        CaptureOutcome? outcome = null;
        service.Sorted += result => outcome = result;

        service.Capture("update my budget");
        await service.ProcessPendingAsync();

        Assert.Equal(32000, _database.GetCategories().Single(category => category.Name == "Groceries").MonthlyCents);
        var card = _database.GetBudgetAccounts().Single();
        Assert.Equal(20000, card.BalanceCents);
        Assert.Equal(200000, card.LimitCents);
        Assert.Equal(Today, card.UpdatedOn);
        var spending = _database.GetSpending(Today, Today);
        Assert.Equal("Groceries", spending.Single(row => row.Title == "Target run").Category);
        Assert.Equal(500, spending.Single(row => row.Title == "Quickride").NetCents);
        Assert.Contains("Couldn't update nowhere", ItemGroups.OutcomeSummary(outcome!));
    }

    [Fact]
    public void Typed_expenses_use_the_merchant_rule_then_the_sorter_then_buffer()
    {
        _database.SaveMerchantRule("blue plate", "Meals out");
        _database.CompleteCapture(_database.AddCapture("x"),
        [
            new SortedItem(ItemKind.Expense, "Blue Plate", Today, 100, Category: "Groceries"),
            new SortedItem(ItemKind.Expense, "bananas", Today, 200, Category: "Groceries"),
            new SortedItem(ItemKind.Expense, "mystery", Today, 300),
            new SortedItem(ItemKind.Expense, "birthday gift", Today, 400, OneOff: true),
        ]);

        var rows = _database.GetSpending(Today, Today);
        Assert.Equal("Meals out", rows[0].Category);
        Assert.Equal("Groceries", rows[1].Category);
        Assert.Equal("Buffer", rows[2].Category);
        Assert.True(rows[3].OneOff);
    }

    [Fact]
    public void Parser_reads_budget_fields()
    {
        var result = ClaudeCliSorter.ParseResult("""
            {"is_error": false, "structured_output": {"changes": [], "answer": null, "monthly_budget_cents": null,
             "items": [{"kind": "expense", "title": "gift", "due_date": null, "amount_cents": 3000, "category": "Buffer", "one_off": true}],
             "budget_ops": [{"op": "set_balance", "name": "Contoso Card", "amount_cents": 20000, "category": null}, {"op": "explode", "name": "x", "amount_cents": null, "category": null}]}}
            """);

        Assert.True(result!.Items.Single().OneOff);
        Assert.Equal("Buffer", result.Items.Single().Category);
        Assert.Equal(new BudgetOp(BudgetOpKind.SetBalance, "Contoso Card", 20000, null), result.BudgetOps!.Single());
    }

    private sealed class FakeSorter(CaptureResult result) : ICaptureSorter
    {
        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<CaptureResult?>(result);
    }
}
