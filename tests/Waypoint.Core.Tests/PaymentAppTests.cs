using System.Net;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Email;

namespace Waypoint.Core.Tests;

public sealed class PaymentAppTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;
    private readonly DateOnly _today = DateOnly.FromDateTime(DateTime.Now);

    public PaymentAppTests()
    {
        _database = new WaypointDatabase(_path);
        _database.SaveCategory(new BudgetCategory("Meals out", 8000));
        _database.SaveCategory(new BudgetCategory("Fun", 10000));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private void Spend(string title, long cents, string category = "Meals out", int daysAgo = 1)
    {
        var capture = _database.AddCapture(title);
        _database.CompleteCapture(capture, [new SortedItem(ItemKind.Expense, title, _today.AddDays(-daysAgo), cents, Category: category)]);
    }

    private long Repaid(string title) =>
        _database.GetSpending(_today.AddDays(-90), _today).Single(row => row.Title == title).ReimbursedCents;

    private PaymentAppOutcome? Add(string id, long cents, string? note = null, string payer = "Sam Lee") =>
        _database.AddRepayment(id, new PaymentAppPayment(payer, cents, note), _today);

    [Theory]
    [InlineData("Sam Lee sent you $12.00", "Sam Lee", 1200)]
    [InlineData("Sam Lee sent you $1,012.50", "Sam Lee", 101250)]
    [InlineData("Fwd: Sam Lee sent you $12.00", "Sam Lee", 1200)]
    public void Reads_incoming_payments(string subject, string payer, long cents)
    {
        var payment = AlertParser.ReadPaymentApp("notify@tailspinpay.example.com", subject, "");

        Assert.Equal(payer, payment?.Payer);
        Assert.Equal(cents, payment?.Cents);
    }

    [Theory]
    [InlineData("You sent Sam Lee $12.00")]
    [InlineData("Sam Lee asked you for $12.00")]
    [InlineData("You requested $12.00 from Sam Lee")]
    [InlineData("Your Tailspin Pay statement is ready")]
    public void Outgoing_requests_and_statements_are_not_repayments(string subject) =>
        Assert.Null(AlertParser.ReadPaymentApp("notify@tailspinpay.example.com", subject, ""));

    [Fact]
    public void Lookalike_senders_dont_count()
    {
        Assert.Null(AlertParser.ReadPaymentApp("notify@nottailspinpay.example.com.evil.test", "Sam sent you $12.00", ""));
        Assert.Null(AlertParser.ReadPaymentApp("friend@example.com", "Sam sent you $12.00", "just a joke"));
    }

    [Fact]
    public void A_forward_counts_when_the_quoted_headers_say_tailspinPay()
    {
        var payment = AlertParser.ReadPaymentApp("Me@Example.com", "Fw: Sam sent you $12.00", "From: Tailspin Pay <notify@tailspinpay.example.com>\nSubject: Sam sent you\n", "me@example.com");

        Assert.Equal(1200, payment?.Cents);
    }

    [Fact]
    public void A_forward_from_anyone_but_you_doesnt_count()
    {
        const string body = "From: Tailspin Pay <notify@tailspinpay.example.com>\nSubject: Sam sent you\n";

        Assert.Null(AlertParser.ReadPaymentApp("stranger@example.com", "Fw: Sam sent you $12.00", body, "me@example.com"));
        Assert.Null(AlertParser.ReadPaymentApp("me@example.com", "Fw: Sam sent you $12.00", body));
        Assert.Null(AlertParser.ReadPaymentApp("me@example.com", "Fw: Sam sent you $12.00", "From: Tailspin Pay <notify@tailspinpay.example.com.evil.test>\n", "me@example.com"));
        Assert.Null(AlertParser.ReadPaymentApp("me@example.com", "Fw: Sam sent you $12.00", "From: x@evil.test notify@tailspinpay.example.com.evil.test\n", "me@example.com"));
    }

    [Theory]
    [InlineData("notify@tailspinpay.example.com", true)]
    [InlineData("notify@email.tailspinpay.example.com", true)]
    [InlineData("notify@tailspinpay.example.com.evil.test", false)]
    [InlineData("notify@nottailspinpay.example.com", false)]
    [InlineData("tailspinpay.example.com@gmail.com", false)]
    public void Only_tailspinPays_own_domain_counts(string from, bool counts) =>
        Assert.Equal(counts, AlertParser.IsPaymentApp(from, ""));

    [Theory]
    [InlineData("Sam sent you\n\ntacos 🌮\n\nView payment", "tacos 🌮")]
    [InlineData("Note: tacos", "tacos")]
    [InlineData("Sam sent you\nView payment details", null)]
    public void Finds_the_note_when_there_is_one(string body, string? note) =>
        Assert.Equal(note, AlertParser.ReadPaymentApp("notify@tailspinpay.example.com", "Sam sent you $12.00", body)?.Note);

    [Fact]
    public void One_food_charge_it_fits_inside_is_a_clear_match()
    {
        Spend("Chipotle", 3000);
        var safeBefore = _database.GetSpending(_today.AddDays(-5), _today).Sum(row => row.NetCents);

        var outcome = Add("m1", 1200);

        Assert.True(outcome?.Matched);
        Assert.Contains("Chipotle", outcome!.Text);
        Assert.Equal(1200, Repaid("Chipotle"));
        Assert.Equal(safeBefore - 1200, _database.GetSpending(_today.AddDays(-5), _today).Sum(row => row.NetCents));
    }

    [Fact]
    public void Non_food_and_old_charges_dont_match()
    {
        Spend("Movie", 3000, "Fun");
        Spend("Old dinner", 3000, daysAgo: 30);

        var outcome = Add("m1", 1200);

        Assert.False(outcome?.Matched);
        Assert.Equal("Sam Lee paid you $12.00. Which purchase was it for?", outcome!.Text);
        Assert.Equal(0, Repaid("Movie"));
        Assert.Equal(0, Repaid("Old dinner"));
    }

    [Fact]
    public void Two_possible_charges_ask_and_pending_money_doesnt_count()
    {
        Spend("Chipotle", 3000);
        Spend("Thai Place", 3000);

        var outcome = Add("m1", 1200, "dinner");

        Assert.False(outcome?.Matched);
        Assert.Contains("for dinner", outcome!.Text);
        Assert.Equal(0, Repaid("Chipotle"));
        Assert.Equal(0, Repaid("Thai Place"));
        Assert.Single(_database.GetWaitingRepayments());
    }

    [Fact]
    public void A_note_can_settle_which_of_two_it_was()
    {
        Spend("Chipotle", 3000);
        Spend("Thai Place", 3000);

        Assert.True(Add("m1", 1200, "chipotle run")?.Matched);
        Assert.Equal(1200, Repaid("Chipotle"));
    }

    [Fact]
    public void A_charge_smaller_than_the_payment_isnt_a_match()
    {
        Spend("Chipotle", 800);

        Assert.False(Add("m1", 1200)?.Matched);
    }

    [Fact]
    public void A_tiny_share_of_a_big_charge_asks()
    {
        Spend("Megamart food run", 15000);

        Assert.False(Add("m1", 500)?.Matched);
    }

    [Fact]
    public void Earlier_repayments_shrink_what_a_charge_can_still_take()
    {
        Spend("Chipotle", 3000);
        Add("m1", 2000);

        Assert.False(Add("m2", 1500, payer: "Alex")?.Matched);
        Assert.Equal(2000, Repaid("Chipotle"));
    }

    [Fact]
    public void Reading_the_same_email_again_changes_nothing()
    {
        Spend("Chipotle", 3000);
        Add("m1", 1200);

        Assert.Null(Add("m1", 1200));
        Assert.Equal(1200, Repaid("Chipotle"));
    }

    [Fact]
    public void The_same_question_isnt_asked_twice()
    {
        Assert.NotNull(Add("m1", 1200));

        Assert.Null(Add("m1", 1200));
        Assert.Single(_database.GetWaitingRepayments());
    }

    [Fact]
    public void A_typed_answer_settles_it_and_stops_at_the_charge()
    {
        Spend("Chipotle", 1000);
        Add("m1", 1200);

        var reply = _database.MatchRepayment(null, "chipotle", _today);

        Assert.Contains("$10.00", reply.Text);
        Assert.Equal(1000, Repaid("Chipotle"));
        Assert.Empty(_database.GetWaitingRepayments());
    }

    [Fact]
    public void A_typed_answer_picks_the_right_payer()
    {
        Spend("Chipotle", 3000);
        Spend("Tacos", 3000);
        Add("m1", 1200, payer: "Sam Lee");
        Add("m2", 800, payer: "Alex Kim");

        _database.MatchRepayment("alex", "tacos", _today);

        Assert.Equal(800, Repaid("Tacos"));
        Assert.Equal("Sam Lee", Assert.Single(_database.GetWaitingRepayments()).Payer);
    }

    [Fact]
    public void A_purchase_it_cant_find_leaves_the_repayment_waiting()
    {
        Add("m1", 1200);

        Assert.Contains("Couldn't find", _database.MatchRepayment(null, "nowhere", _today).Text);
        Assert.Single(_database.GetWaitingRepayments());
    }

    [Theory]
    [InlineData("Trader Joe's", "Trader Joes")]
    [InlineData("TRADER JOES #123", "Trader Joes")]
    [InlineData("Trader Joe's Marketplace", "Trader Joes")]
    [InlineData("TRADER JOES #123", "trader joe's")]
    [InlineData("Trader Joe's", "TRADER JOES")]
    [InlineData("Trader Joe's", "trader joes groceries")]
    [InlineData("TST* PANERA 0042", "panera")]
    public void Typed_names_match_without_case_punctuation_or_store_numbers(string title, string typed) =>
        Assert.True(WaypointDatabase.Names(title, typed));

    [Theory]
    [InlineData("Chipotle", "Trader Joes")]
    [InlineData("TRADER JOES #123", "123")]
    [InlineData("TRADER JOES #123", "  ")]
    public void Other_names_dont_match(string title, string typed) =>
        Assert.False(WaypointDatabase.Names(title, typed));

    [Fact]
    public void A_typed_name_finds_a_purchase_written_differently()
    {
        Spend("TRADER JOES #123", 2000, "Fun");
        Add("m1", 640, payer: "Riley Park");

        var reply = _database.MatchRepayment(null, "Trader Joes", _today);

        Assert.Contains("TRADER JOES #123", reply.Text);
        Assert.Equal(640, Repaid("TRADER JOES #123"));
        Assert.Empty(_database.GetWaitingRepayments());
    }

    [Fact]
    public void Of_several_matches_one_the_money_fits_inside_wins_then_the_newest()
    {
        Spend("Trader Joe's small", 300, daysAgo: 1);
        Spend("Trader Joe's older", 2000, daysAgo: 5);
        Spend("Trader Joe's newer", 2000, daysAgo: 2);
        Add("m1", 640);

        _database.MatchRepayment(null, "trader joes", _today);

        Assert.Equal(640, Repaid("Trader Joe's newer"));
        Assert.Equal(0, Repaid("Trader Joe's older"));
        Assert.Equal(0, Repaid("Trader Joe's small"));
    }

    [Fact]
    public void A_name_it_cant_find_lists_the_purchases_it_could_be()
    {
        Spend("Movie night", 3000, "Fun");
        Add("m1", 1200);

        var reply = _database.MatchRepayment(null, "Trader Joes", _today);

        Assert.StartsWith("Couldn't find a recent Trader Joes purchase", reply.Text);
        Assert.Contains("Movie Night", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$30.00 left", reply.Text);
        Assert.Single(_database.GetWaitingRepayments());
    }

    [Fact]
    public void Not_a_repayment_clears_it_without_touching_spending()
    {
        Spend("Chipotle", 3000);
        Spend("Tacos", 3000);
        Add("m1", 1200);

        _database.ClearRepayment(null);

        Assert.Empty(_database.GetWaitingRepayments());
        Assert.Equal(0, Repaid("Chipotle"));
    }

    [Fact]
    public void Ops_reach_the_repayments()
    {
        Spend("Chipotle", 3000);
        Spend("Tacos", 3000);
        Add("m1", 1200);

        var reply = AssistantOps.Apply(new AssistantOp(AssistantOpKind.RepaymentMatch, Name: "tacos"), _database, 0, DateTime.Now);

        Assert.Contains("Tacos", reply.Text);
        Assert.Equal(1200, Repaid("Tacos"));
    }

    [Fact]
    public void Waiting_repayments_reach_the_sorters_prompt()
    {
        var context = new AssistantContext(DateTime.Now, [], [], WaitingRepayments: [new PaymentAppRepayment("m1", "Sam Lee", 1200, "tacos", _today)]);

        var prompt = ClaudeCliSorter.BuildPrompt("that was chipotle", DateTime.Now, context);

        Assert.Contains("Waiting repayments", prompt);
        Assert.Contains("Sam Lee", prompt);
    }

    [Fact]
    public void A_transfer_to_the_bank_is_balance_only_and_outgoing_tailspinPay_stays_spending()
    {
        var rows = CheckingImport.Parse("""
            Date,Details,Amount,Balance
            11/02/2026,TAILSPIN PAY TO BANK 261102,600.00,1588.00
            11/02/2026,TAILSPIN PAY PAYMENT 261102,-12.00,988.00
            """);

        Assert.Equal(CheckingKind.Other, rows.Single(row => row.Cents > 0).Kind);
        Assert.Equal(CheckingKind.Spending, rows.Single(row => row.Cents < 0).Kind);
    }

    [Fact]
    public void A_transfer_to_the_bank_never_confirms_a_paycheck_even_at_the_same_amount()
    {
        _database.SaveBudgetAccount(new BudgetAccount(CheckingImport.CheckingAccount, AccountKind.Checking, 100000, _today));
        _database.AddScheduledItem(new ScheduledItem("Paycheck", 85000, [_today.Day]));
        var day = _today.ToString("MM/dd/yyyy");

        _database.ImportChecking(CheckingImport.Parse($"Date,Details,Amount,Balance\n{day},TAILSPIN PAY TO BANK 1,850.00,1850.00\n"));

        Assert.Empty(_database.GetPayDeposits());
    }

    private sealed class MemoryTokens : ITokenStore
    {
        public string? Load() => "token";

        public void Save(string token)
        {
        }
    }

    private sealed class FakeServer(string messages) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.ToString().Contains("/token")
                    ? """{"access_token":"a","refresh_token":"new"}"""
                    : messages),
            });
    }

    private static string Message(string id, string subject, string body = "") =>
        $$$"""{"id":"{{{id}}}","subject":"{{{subject}}}","receivedDateTime":"2026-11-02T18:00:00Z","from":{"emailAddress":{"address":"notify@tailspinpay.example.com"}},"body":{"content":"{{{body}}}"}}""";

    [Fact]
    public async Task Polling_handles_a_repayment_once_and_keeps_unknown_tailspinPay_mail()
    {
        var messages = $$"""{"value":[{{Message("m1", "Sam sent you $12.00")}},{{Message("m2", "You sent Alex $5.00")}},{{Message("m3", "Something new from Tailspin Pay")}}]}""";
        var mail = new OutlookMail(new HttpClient(new FakeServer(messages)), _database, new OutlookSettings("id"), new MemoryTokens());
        var outcomes = new List<PaymentAppOutcome>();
        var unread = new List<UnreadAlert>();

        await mail.PollAsync(_ => { }, unread.Add, null, outcomes.Add);
        await mail.PollAsync(_ => { }, unread.Add, null, outcomes.Add);

        Assert.Null(mail.LastError);
        Assert.Single(outcomes);
        Assert.Equal("Something new from Tailspin Pay", Assert.Single(unread).Subject);
    }
}
