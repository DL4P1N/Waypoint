using System.Net;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Data;
using Waypoint.Core.Email;

namespace Waypoint.Core.Tests;

public sealed class OutlookMailTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public OutlookMailTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private sealed class MemoryTokens(string? token) : ITokenStore
    {
        public string? Token { get; private set; } = token;

        public string? Load() => Token;

        public void Save(string value) => Token = value;
    }

    private sealed class FakeServer(string messages, HttpStatusCode mailStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            return Task.FromResult(url.Contains("/token")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"a","refresh_token":"new"}""") }
                : new HttpResponseMessage(mailStatus) { Content = new StringContent(messages) });
        }
    }

    private static string Message(string id, string from, string subject, string body, string received = "2026-11-02T18:00:00Z") =>
        $$$"""{"id":"{{{id}}}","subject":"{{{subject}}}","receivedDateTime":"{{{received}}}","from":{"emailAddress":{"address":"{{{from}}}"}},"body":{"content":"{{{body}}}"}}""";

    private const string CardAlertEmail = """
        You asked us to tell you about card purchases. Here is the latest one.
        Merchant: Streamco*Online
        Date: November 01, 2026
        Amount: $1.00
        Pending amounts can change when the purchase posts.
        """;

    [Fact]
    public void Reads_a_contoso_purchase_alert()
    {
        var purchase = AlertParser.Read("alerts@contoso.example.com", "Purchase alert", CardAlertEmail, new DateOnly(2026, 11, 2), "m1");

        Assert.Equal(new EmailPurchase("Contoso Card", new ImportedTransaction(new DateOnly(2026, 11, 1), "Streamco*Online", 100, "m1")), purchase);
    }

    [Fact]
    public void Reads_contoso_with_everything_on_one_line()
    {
        var purchase = AlertParser.Read("alerts@contoso.example.com", "Purchase alert", "Merchant: Panera Date: November 01, 2026 Amount: $1,012.50 The amount", new DateOnly(2026, 11, 2), "m1");

        Assert.Equal("Panera", purchase!.Transaction.Description);
        Assert.Equal(101250, purchase.Transaction.AmountCents);
    }

    [Fact]
    public void A_card_senders_other_emails_are_ignored_so_a_purchase_counts_once()
    {
        var day = new DateOnly(2026, 11, 1);
        const string from = "alerts@contoso.example.com";

        Assert.Null(AlertParser.Read(from, "Online purchase notice", CardAlertEmail, day, "m1"));
        Assert.Null(AlertParser.Read(from, "Your alert settings were updated", "Purchase over $0.01 by email. Call us if you did not make this change.", day, "m2"));
        Assert.False(AlertParser.LooksLikeMoney(from, "Online purchase notice"));
        Assert.False(AlertParser.LooksLikeMoney(from, "Your alert settings were updated"));
        Assert.True(AlertParser.LooksLikeMoney(from, "Purchase alert"));
    }

    [Fact]
    public void Reads_a_rewards_card_purchase_and_leaves_other_mail_alone()
    {
        var day = new DateOnly(2026, 11, 2);
        const string from = "alerts@alert.lucerne.example.com";

        var purchase = AlertParser.Read(from, "New charge on your card", "A charge of $38.25 at Target was made.", day, "m1");

        Assert.Equal(new EmailPurchase("Lucerne Card", new ImportedTransaction(day, "Target", 3825, "m1")), purchase);
        Assert.Null(AlertParser.Read("friend@outlook.com", "Transaction", "$5.00 at Target", day, "m2"));
        Assert.Null(AlertParser.Read(from, "Your payment was received", "Your purchase payment of $50.00 at Target", day, "m3"));
        Assert.Null(AlertParser.Read(from, "Your sign-in code", "Your code", day, "m4"));
    }

    [Fact]
    public void Reads_a_deposit_only_from_fabrikam()
    {
        Assert.Equal(9500, AlertParser.ReadDeposit("alerts@fabrikam.example.com", "Your deposit was received", "A deposit of $95.00 was made."));
        Assert.Null(AlertParser.ReadDeposit("alerts@bank.com", "Your deposit was received", "$95.00"));
        Assert.Null(AlertParser.ReadDeposit("alerts@fabrikam.example.com", "Your statement", "$95.00"));
    }

    [Theory]
    [InlineData("casey@x.com")]
    [InlineData("contoso.rewards@gmail.com")]
    [InlineData("alerts@contoso.example.com.evil.test")]
    [InlineData("alerts@notcontoso.example.com")]
    [InlineData("fabrikam@outlook.com")]
    [InlineData("alerts@lucerne.example.com.evil.test")]
    [InlineData("northwind@gmail.com")]
    [InlineData("alerts@notify.northwind.example.com.evil.test")]
    [InlineData("no-at-sign")]
    public void Lookalike_senders_dont_count_as_banks(string from)
    {
        var day = new DateOnly(2026, 11, 2);

        Assert.Null(AlertParser.AccountFor(from));
        Assert.False(AlertParser.IsCheckingAlert(from));
        Assert.Null(AlertParser.Read(from, "New charge on your card", "A charge of $38.25 at Target was made.", day, "m1"));
        Assert.Null(AlertParser.ReadDeposit(from, "Your deposit was received", "A deposit of $95.00 was made."));
        Assert.False(AlertParser.LooksLikeMoney(from, "Purchase alert"));
    }

    [Theory]
    [InlineData("alerts@contoso.example.com", "Contoso Card")]
    [InlineData("contoso@contoso.example.com", "Contoso Card")]
    [InlineData("Alerts@Lucerne.example.com", "Lucerne Card")]
    [InlineData("alerts@alert.lucerne.example.com", "Lucerne Card")]
    public void Bank_domains_and_their_subdomains_count(string from, string account) =>
        Assert.Equal(account, AlertParser.AccountFor(from));

    [Fact]
    public void Dmarc_has_to_pass_for_the_senders_domain()
    {
        const string from = "alerts@fabrikam.example.com";

        Assert.True(AlertParser.PassedDmarc(null, from));
        Assert.True(AlertParser.PassedDmarc("spf=pass smtp.mailfrom=fabrikam.example.com; dkim=pass header.d=fabrikam.example.com;dmarc=pass action=none header.from=fabrikam.example.com;compauth=pass", from));
        Assert.False(AlertParser.PassedDmarc("spf=fail smtp.mailfrom=fabrikam.example.com; dmarc=fail action=none header.from=fabrikam.example.com", from));
        Assert.False(AlertParser.PassedDmarc("dmarc=pass action=none header.from=evil.test", from));
        Assert.False(AlertParser.PassedDmarc("", from));
    }

    [Fact]
    public async Task A_deposit_that_fails_dmarc_is_ignored()
    {
        _database.SaveBudgetAccount(new BudgetAccount("Cushion fund", AccountKind.Cushion, 50000, new DateOnly(2026, 10, 7)));
        const string spoofed = """{"id":"d1","subject":"Deposit received","receivedDateTime":"2026-11-02T18:00:00Z","from":{"emailAddress":{"address":"alerts@fabrikam.example.com"}},"body":{"content":"Your deposit of $95.00 posted."},"internetMessageHeaders":[{"name":"Authentication-Results","value":"spf=fail smtp.mailfrom=evil.test; dmarc=fail action=none header.from=fabrikam.example.com"},{"name":"Authentication-Results","value":"dmarc=pass header.from=fabrikam.example.com"}]}""";
        var server = new FakeServer("{\"value\":[" + spoofed + "]}");
        var mail = new OutlookMail(new HttpClient(server), _database, new OutlookSettings("id"), new MemoryTokens("old"));
        var deposits = new List<long>();

        await mail.PollAsync(_ => { }, _ => { }, deposits.Add);

        Assert.Empty(deposits);
        Assert.Equal(50000, _database.GetBudgetAccounts().Single().BalanceCents);
        Assert.Contains(server.Urls, url => url.Contains("internetMessageHeaders"));

        // It's said, not silently dropped, in case forwarding is what broke the check.
        Assert.Contains("fabrikam.example.com", mail.SkippedSender);
    }

    [Fact]
    public async Task Someone_else_failing_dmarc_isnt_mentioned()
    {
        const string stranger = """{"id":"s1","subject":"Hello","receivedDateTime":"2026-11-02T18:00:00Z","from":{"emailAddress":{"address":"casey@example.com"}},"body":{"content":"Hi"},"internetMessageHeaders":[{"name":"Authentication-Results","value":"dmarc=fail header.from=example.com"}]}""";
        var mail = new OutlookMail(new HttpClient(new FakeServer("{\"value\":[" + stranger + "]}")), _database, new OutlookSettings("id"), new MemoryTokens("old"));

        await mail.PollAsync(_ => { }, _ => { }, _ => { });

        Assert.Null(mail.SkippedSender);
    }

    [Fact]
    public async Task A_deposit_email_adds_to_the_cushion_once()
    {
        _database.SaveBudgetAccount(new BudgetAccount("Cushion fund", AccountKind.Cushion, 50000, new DateOnly(2026, 10, 7)));
        var body = "{\"value\":[" + Message("d1", "alerts@fabrikam.example.com", "Deposit received", "Your deposit of $95.00 posted.") + "]}";
        var mail = new OutlookMail(new HttpClient(new FakeServer(body)), _database, new OutlookSettings("id"), new MemoryTokens("old"));
        var deposits = new List<long>();

        await mail.PollAsync(_ => { }, _ => { }, deposits.Add);
        await mail.PollAsync(_ => { }, _ => { }, deposits.Add);

        Assert.Equal([9500L], deposits);
        Assert.Equal(59500, _database.GetBudgetAccounts().Single().BalanceCents);
    }

    [Fact]
    public async Task New_purchases_are_stored_once_and_unread_alerts_are_passed_on()
    {
        var body = "{\"value\":[" + string.Join(",",
            Message("m1", "alerts@lucerne.example.com", "New charge on your card", "A charge of $12.00 at Panera was made."),
            Message("m2", "alerts@lucerne.example.com", "New charge on your card", "Unknown format"),
            Message("m3", "news@shop.com", "Sale", "Nothing here")) + "]}";
        var tokens = new MemoryTokens("old");
        var mail = new OutlookMail(new HttpClient(new FakeServer(body)), _database, new OutlookSettings("id"), tokens);
        var purchases = new List<EmailPurchase>();
        var unread = new List<UnreadAlert>();

        await mail.PollAsync(purchases.Add, unread.Add);
        await mail.PollAsync(purchases.Add, unread.Add);

        Assert.Null(mail.LastError);
        Assert.Equal("new", tokens.Token);
        Assert.Equal("Lucerne Card", Assert.Single(purchases).Account);
        Assert.Single(unread);
        Assert.Equal("2026-11-02T18:00:00Z", _database.GetPreference("outlook_last_received"));
        var day = new DateOnly(2026, 11, 2);
        Assert.Single(_database.GetSpending(day.AddDays(-1), day.AddDays(1)));
    }

    [Fact]
    public async Task Two_identical_purchases_in_different_emails_both_count()
    {
        var day = new DateOnly(2026, 11, 2);

        _database.ImportTransactions("Contoso Card", [new ImportedTransaction(day, "Cafe", 500, "m1")]);
        _database.ImportTransactions("Contoso Card", [new ImportedTransaction(day, "Cafe", 500, "m2")]);
        _database.ImportTransactions("Contoso Card", [new ImportedTransaction(day, "Cafe", 500, "m1")]);

        Assert.Equal(2, _database.GetSpending(day, day).Count);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_failed_poll_is_reported_and_keeps_the_place()
    {
        _database.SetPreference("outlook_last_received", "2026-11-01T00:00:00Z");
        var mail = new OutlookMail(new HttpClient(new FakeServer("{}", HttpStatusCode.Unauthorized)), _database, new OutlookSettings("id"), new MemoryTokens("old"));

        await mail.PollAsync(_ => { }, _ => { });

        Assert.NotNull(mail.LastError);
        Assert.Equal("2026-11-01T00:00:00Z", _database.GetPreference("outlook_last_received"));
    }

    [Fact]
    public async Task Nothing_runs_until_signed_in()
    {
        var server = new FakeServer("{}");
        var mail = new OutlookMail(new HttpClient(server), _database, new OutlookSettings("id"), new MemoryTokens(null));

        await mail.PollAsync(_ => { }, _ => { });

        Assert.False(mail.IsConnected);
        Assert.Empty(server.Urls);
    }
}
