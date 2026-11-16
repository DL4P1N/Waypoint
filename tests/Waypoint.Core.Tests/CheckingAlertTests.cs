using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Data;
using Waypoint.Core.Email;

namespace Waypoint.Core.Tests;

/// <summary>Northwind Bank alerts, in wording a checking bank might use.</summary>
public sealed class CheckingAlertTests : IDisposable
{
    private const string Deposit = """
        A deposit reached your account ending 1234.
        Amount deposited: $200.00
        From: FABRIKAM INV TRANSFER 261102
        New available balance: $40.00
        Sign in to see details.
        """;

    private const string BelowZero = """
        Heads up: your available balance is -$150.00
        in the account ending 1234,
        checked 11/02/2026 at 6:10 AM.
        """;

    private const string Summary = """
        Your daily summary
        Account ending 1234
        Money out
        TAILSPIN PAY PAYMENT 261031 $8.25
        CONTOSO CARD PAYMENT 261101 $450.00
        TAILSPIN PAY PAYMENT 261031 $6.40
        """;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public CheckingAlertTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void The_available_balance_is_read_from_deposits_and_low_balance_alerts()
    {
        Assert.Equal(4000, AlertParser.ReadAvailableBalance(Deposit));
        Assert.Equal(-15000, AlertParser.ReadAvailableBalance(BelowZero));
        Assert.Null(AlertParser.ReadAvailableBalance(Summary));
        Assert.True(AlertParser.IsCheckingAlert("alerts@notify.northwind.example.com"));
    }

    [Fact]
    public void Only_card_payments_are_taken_from_the_summary_dated_by_their_line()
    {
        var payment = Assert.Single(AlertParser.ReadCardPayments(Summary, new DateOnly(2026, 11, 3)));

        Assert.Equal("Contoso Card", payment.Account);
        Assert.Equal(45000, payment.Payment.AmountCents);
        Assert.Equal(new DateOnly(2026, 11, 1), payment.Payment.Date);
    }

    [Fact]
    public void An_alert_sets_checking_unless_a_newer_balance_is_already_kept()
    {
        _database.SaveBudgetAccount(new BudgetAccount("Northwind Bank checking", AccountKind.Checking, 61000, new DateOnly(2026, 11, 2)));

        Assert.True(_database.SetCheckingFromAlert(4000, new DateOnly(2026, 11, 2)));
        Assert.Equal(4000, _database.GetBudgetAccounts().Single().BalanceCents);

        Assert.False(_database.SetCheckingFromAlert(50000, new DateOnly(2026, 11, 1)));
        Assert.Equal(4000, _database.GetBudgetAccounts().Single().BalanceCents);
    }

    [Fact]
    public void A_card_payment_from_the_summary_lowers_the_card_once()
    {
        _database.SaveBudgetAccount(new BudgetAccount("Contoso Card", AccountKind.Card, 60000, new DateOnly(2026, 10, 30), 250000, 9, 3));
        var (account, payment) = AlertParser.ReadCardPayments(Summary, new DateOnly(2026, 11, 3)).Single();

        _database.AddCardPayments(account, [payment]);
        _database.AddCardPayments(account, [payment]);

        var card = _database.GetBudgetAccounts().Single();
        Assert.Equal(60000 - 45000, _database.GetCardBalance(card, new DateOnly(2026, 11, 3)));
    }
}
