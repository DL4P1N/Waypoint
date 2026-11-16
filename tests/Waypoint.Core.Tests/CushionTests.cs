using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>Money moved to the cushion is seen in a Fabrikam deposit email, a checking export, or both. It counts once.</summary>
public sealed class CushionTests : IDisposable
{
    private static readonly DateOnly Set = new(2026, 11, 1);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public CushionTests()
    {
        _database = new WaypointDatabase(_path);
        _database.SaveBudgetAccount(new BudgetAccount("Northwind Bank checking", AccountKind.Checking, 100000, Set));
        _database.SaveBudgetAccount(new BudgetAccount("Cushion fund", AccountKind.Cushion, 50000, Set));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private long Cushion() => _database.GetCurrentAccounts(Set.AddDays(20)).Single(account => account.Kind == AccountKind.Cushion).BalanceCents;

    private static CheckingRow Fabrikam(DateOnly on, long cents) => new(on, "FABRIKAM INV TRANSFER 261106", -cents, CheckingKind.Other);

    [Fact]
    public void A_deposit_email_and_the_checking_row_a_day_later_count_once()
    {
        _database.AddToCushion(12000, Set.AddDays(4));
        _database.ImportChecking([Fabrikam(Set.AddDays(5), 12000)]);

        Assert.Equal(62000, Cushion());
    }

    [Fact]
    public void A_checking_row_then_its_deposit_email_count_once()
    {
        _database.ImportChecking([Fabrikam(Set.AddDays(5), 12000)]);
        _database.AddToCushion(12000, Set.AddDays(4));

        Assert.Equal(62000, Cushion());
    }

    [Fact]
    public void A_deposit_email_keeps_an_earlier_transfer_it_never_saw()
    {
        _database.ImportChecking([Fabrikam(Set.AddDays(2), 5000)]);
        _database.AddToCushion(12000, Set.AddDays(4));
        _database.ImportChecking([Fabrikam(Set.AddDays(2), 5000), Fabrikam(Set.AddDays(5), 12000)]);

        Assert.Equal(67000, Cushion());
    }

    [Fact]
    public void Two_transfers_of_the_same_amount_with_one_email_count_twice()
    {
        _database.AddToCushion(12000, Set.AddDays(4));
        _database.ImportChecking([Fabrikam(Set.AddDays(5), 12000), Fabrikam(Set.AddDays(12), 12000)]);

        Assert.Equal(74000, Cushion());
    }
}
