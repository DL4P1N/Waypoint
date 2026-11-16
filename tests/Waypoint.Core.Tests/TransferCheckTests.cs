using Waypoint.Core.Budget;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>Whether scheduled savings transfers and investing purchases were seen happening.</summary>
public sealed class TransferCheckTests : IDisposable
{
    // A Friday. Looking back 35 days reaches Oct 16, so only the Nov 3 and Nov 10 dates are checked.
    private static readonly DateOnly Today = new(2026, 11, 20);
    private static readonly DateOnly AddedLongAgo = new(2026, 9, 1);
    private static readonly ScheduledItem Transfer = new("Fabrikam transfer", -10000, [3], Id: 1);
    private static readonly ScheduledItem Investing = new("Invest in index fund", -5000, [10], Id: 2);
    private static readonly ScheduledItem Phone = new("Phone bill", -4500, [12], Id: 3);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-{Guid.NewGuid():N}.db");
    private readonly WaypointDatabase _database;

    public TransferCheckTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static Dictionary<long, DateOnly> Added(DateOnly day) => new() { [1] = day, [2] = day, [3] = day };

    private static TransferSighting Checking(int day, long cents, string description) => new(new DateOnly(2026, 11, day), cents, description, false);

    private static IReadOnlyList<TransferStatus> Check(DateOnly today, IReadOnlyList<TransferSighting> sightings, DateOnly? through, DateOnly? added = null) =>
        TransferCheck.Check(today, [Transfer, Investing, Phone], Added(added ?? AddedLongAgo), sightings, through);

    [Fact]
    public void A_transfer_in_the_checking_export_is_confirmed_and_one_the_export_covers_without_it_is_missed()
    {
        var statuses = Check(Today, [Checking(4, 10000, "FABRIKAM INV TRANSFER")], new DateOnly(2026, 11, 18));

        Assert.Equal(
            [
                ("Fabrikam transfer", new DateOnly(2026, 11, 3), TransferState.Confirmed, "Seen in the checking export Nov 4"),
                ("Invest in index fund", new DateOnly(2026, 11, 10), TransferState.Missed, "Not in the checking export through Nov 18"),
            ],
            statuses.Select(status => (status.Label, status.Due, status.State, status.Note)));
        Assert.False(statuses[0].Investing);
        Assert.True(statuses[1].Investing);
    }

    [Fact]
    public void A_fabrikam_deposit_email_confirms_a_transfer()
    {
        var statuses = Check(Today, [new TransferSighting(new DateOnly(2026, 11, 11), 5000, "Fabrikam deposit", true)], null);

        var investing = statuses.Single(status => status.Label == Investing.Label);
        Assert.Equal((TransferState.Confirmed, "Seen in a Fabrikam deposit email Nov 11"), (investing.State, investing.Note));
    }

    [Fact]
    public void Without_any_source_it_says_it_cant_tell_instead_of_calling_it_missed()
    {
        var statuses = Check(Today, [], null);

        Assert.All(statuses, status => Assert.Equal(TransferState.NotSeenYet, status.State));
        Assert.All(statuses, status => Assert.Contains("can't tell", status.Note));

        var section = TransferCheck.Section(statuses, null, false);
        Assert.Contains("Nothing to check these against yet", section.Rows[^2].Label);
        Assert.Contains("Purchases inside Fabrikam aren't read", section.Rows[^1].Label);
    }

    [Fact]
    public void An_export_that_stops_before_the_grace_period_ends_cant_show_a_miss()
    {
        var statuses = Check(Today, [], new DateOnly(2026, 11, 12));

        var investing = statuses.Single(status => status.Label == Investing.Label);
        Assert.Equal((TransferState.NotSeenYet, "Checking exports only go through Nov 12, so I can't tell yet"), (investing.State, investing.Note));
        Assert.Equal(TransferState.Missed, statuses.Single(status => status.Label == Transfer.Label).State);
    }

    [Fact]
    public void A_recent_transfer_gets_a_few_days_before_it_can_be_missed()
    {
        var statuses = Check(new DateOnly(2026, 11, 13), [], new DateOnly(2026, 11, 13));

        var investing = statuses.Last(status => status.Label == Investing.Label);
        Assert.Equal(new DateOnly(2026, 11, 10), investing.Due);
        Assert.Equal((TransferState.NotSeenYet, "Transfers can take a few business days"), (investing.State, investing.Note));
    }

    [Fact]
    public void A_bill_of_the_same_amount_isnt_taken_for_a_transfer()
    {
        var statuses = Check(Today, [Checking(3, 10000, "RENT PAYMENT ACME APTS")], new DateOnly(2026, 11, 18));

        Assert.Equal(TransferState.Missed, statuses.Single(status => status.Label == Transfer.Label).State);
        Assert.DoesNotContain(statuses, status => status.Label == Phone.Label);
    }

    [Fact]
    public void A_word_from_the_transfers_name_or_a_close_amount_still_matches_and_each_row_counts_once()
    {
        var twice = new ScheduledItem("Cushion top up", -2000, [3, 4], Id: 4);
        var statuses = TransferCheck.Check(Today, [twice], new Dictionary<long, DateOnly>(), [Checking(4, 2050, "ONLINE CUSHION TOP UP")], new DateOnly(2026, 11, 18));

        Assert.Equal([TransferState.Confirmed, TransferState.Missed], statuses.Select(status => status.State));
    }

    [Fact]
    public void Dates_before_a_transfer_was_added_arent_checked()
    {
        var statuses = Check(Today, [], new DateOnly(2026, 11, 18), added: new DateOnly(2026, 11, 5));

        Assert.Equal([Investing.Label], statuses.Select(status => status.Label));
    }

    [Fact]
    public void The_budget_shows_transfers_and_coming_up_says_how_the_last_one_went()
    {
        _database.AddScheduledItem(new ScheduledItem("Fabrikam transfer", -10000, [3]), AddedLongAgo);
        _database.AddScheduledItem(new ScheduledItem("Paycheck", 85000, [10, 25], true), AddedLongAgo);
        _database.SaveBudgetAccount(new BudgetAccount("Northwind Bank checking", AccountKind.Checking, 50000, new DateOnly(2026, 11, 1)));
        _database.ImportChecking([
            new CheckingRow(new DateOnly(2026, 11, 4), "FABRIKAM INV TRANSFER", -10000, CheckingKind.Other),
            new CheckingRow(new DateOnly(2026, 11, 17), "CARD PURCHASE 11/17 KROGER", -2500, CheckingKind.Spending),
        ]);

        var report = _database.GetBudgetReport(Today);

        var transfers = report.Single(section => section.Title == "TRANSFERS");
        Assert.Equal(("Fabrikam transfer, Nov 3: Confirmed. Seen in the checking export Nov 4.", "$100.00"), (transfers.Rows[0].Label, transfers.Rows[0].Value));
        Assert.Equal("Checked against checking exports through Nov 17.", transfers.Rows[^1].Label);
        var coming = report.Single(section => section.Title == "COMING UP");
        Assert.Contains(coming.Rows, row => row.Label == "Fabrikam transfer, Dec 3 (last one, Nov 3: confirmed)");
    }

    [Fact]
    public void A_fabrikam_deposit_from_email_is_kept_for_matching()
    {
        _database.AddScheduledItem(new ScheduledItem("Invest in index fund", -5000, [10]), AddedLongAgo);
        _database.SaveBudgetAccount(new BudgetAccount("Fabrikam cushion", AccountKind.Cushion, 50000, new DateOnly(2026, 11, 1)));

        _database.AddToCushion(5000, new DateOnly(2026, 11, 11));

        var status = Assert.Single(_database.GetTransferStatuses(Today));
        Assert.Equal(TransferState.Confirmed, status.State);
        Assert.Equal("Checked against Fabrikam deposit emails.", _database.GetTransferSection(Today).Rows[^2].Label);
    }

    [Fact]
    public void A_typed_paycheck_isnt_a_checking_export()
    {
        _database.AddScheduledItem(new ScheduledItem("Paycheck", 85000, [10, 25], true), AddedLongAgo);
        _database.AddScheduledItem(new ScheduledItem("Fabrikam transfer", -10000, [3]), AddedLongAgo);
        _database.ConfirmPayTyped(85000, new DateOnly(2026, 11, 10));

        var status = Assert.Single(_database.GetTransferStatuses(Today));
        Assert.Equal(TransferState.NotSeenYet, status.State);
        Assert.Contains("No checking export", status.Note);
    }

    [Fact]
    public void A_bill_added_today_starts_from_today()
    {
        _database.SetBill("Savings transfer", 2500, "monthly", ["3"], null, null, Today);

        Assert.Empty(_database.GetTransferStatuses(Today));
        Assert.Empty(_database.GetTransferSection(Today).Rows);
    }

    [Fact]
    public void A_transfer_the_export_doesnt_show_reads_as_not_seen_yet_rather_than_missed()
    {
        Assert.Equal("Not seen yet", TransferCheck.StateText(TransferState.Missed));
        Assert.Equal("Not seen yet", TransferCheck.StateText(TransferState.NotSeenYet));
    }
}
