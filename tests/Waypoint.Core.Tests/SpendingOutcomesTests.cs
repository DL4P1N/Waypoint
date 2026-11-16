using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

public sealed class SpendingOutcomesTests : IDisposable
{
    // Mid-November, so October is the latest full month and nothing reads the real clock.
    private static readonly DateOnly Today = new(2026, 11, 9);
    private static readonly DateTime Now = Today.ToDateTime(new TimeOnly(12, 0));
    private static readonly DateOnly LongAgo = new(2025, 1, 1);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public SpendingOutcomesTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static readonly BudgetCategory[] Caps = [new("Food", 20000), new("Fun", 5000)];

    private static SettledWait Wait(string decision, long cents = 5000) => new("headphones", cents, Now.AddDays(-10), decision);

    /// <summary>Ten purchases in a month, split so the category's total is what's asked for, plus filler in Buffer.</summary>
    private static IEnumerable<SpendingRow> Month(int year, int month, string category, long totalCents)
    {
        var first = new DateOnly(year, month, 1);
        yield return new SpendingRow(first, "Store", totalCents, category, false, 0);
        for (var day = 1; day < SpendingOutcomes.MinPurchasesForMonth; day++)
        {
            yield return new SpendingRow(first.AddDays(day), "Filler", 100, "Buffer", false, 0);
        }
    }

    private static IReadOnlyList<string> Lines(IReadOnlyList<SettledWait>? waits = null, IEnumerable<SpendingRow>? spending = null, DateOnly? startedOn = null) =>
        SpendingOutcomes.Build(Today, waits ?? [], Caps, (spending ?? []).ToList(), startedOn ?? LongAgo).Rows.Select(row => row.Label).ToList();

    // The wait list.

    [Fact]
    public void With_nothing_decided_it_says_there_is_no_way_to_tell()
    {
        var lines = Lines();

        Assert.Equal("Wait list: nothing decided yet, so there's no way to tell if waiting helps.", lines[0]);
        Assert.StartsWith("Spending review: not enough yet.", lines[1]);
        Assert.Contains("aren't kept", lines[^1]);
    }

    [Fact]
    public void Two_decisions_are_not_enough()
    {
        Assert.Equal("Wait list: not enough yet. 2 decisions so far, and it takes 3 to say anything.", Lines([Wait("skipped"), Wait("bought")])[0]);
    }

    [Fact]
    public void Skipped_purchases_count_as_money_not_spent()
    {
        var waits = new[] { Wait("skipped", 8000), Wait("Skipped", 2000), Wait("bought"), Wait("decided") };

        Assert.Equal("Wait list: you let go of 2 of the 3 things you parked, about $100.00 not spent. 1 more came off without saying which.", Lines(waits)[0]);
    }

    [Fact]
    public void Buying_everything_says_waiting_has_not_changed_much()
    {
        Assert.Equal("Wait list: you bought all 3 things you parked, so waiting hasn't changed what you buy yet.", Lines([Wait("bought"), Wait("bought"), Wait("bought")])[0]);
    }

    // The months after going over a cap.

    [Fact]
    public void Counts_how_often_the_month_after_going_over_came_down()
    {
        // Over in June, July, and August; lower in July, August, and September, then flat in October.
        var spending = Month(2026, 6, "Food", 30000)
            .Concat(Month(2026, 7, "Food", 25000))
            .Concat(Month(2026, 8, "Food", 22000))
            .Concat(Month(2026, 9, "Food", 18000))
            .Concat(Month(2026, 10, "Food", 18000));

        var lines = Lines(spending: spending);

        Assert.Equal("Spending review: after a month over budget, the next month was lower 3 of 3 times, about $120.00 less in all (Food).", lines[1]);
        Assert.Contains("today's budgets", lines[2]);
    }

    [Fact]
    public void Says_so_when_spending_went_up_after()
    {
        var spending = Month(2026, 7, "Fun", 6000)
            .Concat(Month(2026, 8, "Fun", 7000))
            .Concat(Month(2026, 9, "Fun", 9000))
            .Concat(Month(2026, 10, "Fun", 9500));

        Assert.Equal("Spending review: after a month over budget, the next month was lower 0 of 3 times, though about $35.00 more in all (Fun).", Lines(spending: spending)[1]);
    }

    [Fact]
    public void Months_before_Waypoint_was_used_and_the_current_month_do_not_count()
    {
        // Every month is over, but use started in October, so only September into October could count, and that's one.
        var spending = Month(2026, 6, "Food", 30000)
            .Concat(Month(2026, 7, "Food", 30000))
            .Concat(Month(2026, 8, "Food", 30000))
            .Concat(Month(2026, 9, "Food", 30000))
            .Concat(Month(2026, 10, "Food", 30000))
            .Concat(Month(2026, 11, "Food", 1000));

        Assert.Equal("Spending review: not enough yet. It takes 3 months over budget, each followed by a full month, and there is 1 so far.", Lines(spending: spending, startedOn: new DateOnly(2026, 10, 3))[1]);
    }

    [Fact]
    public void A_thin_month_and_one_off_charges_are_left_out()
    {
        var thin = new[] { new SpendingRow(new DateOnly(2026, 8, 3), "Store", 30000, "Food", false, 0) };
        var oneOff = Month(2026, 9, "Buffer", 100).Append(new SpendingRow(new DateOnly(2026, 9, 3), "Concert", 90000, "Fun", true, 0));

        Assert.Contains("there are 0 so far", Lines(spending: thin.Concat(Month(2026, 9, "Food", 100)).Concat(oneOff).Concat(Month(2026, 10, "Fun", 100)))[1]);
    }

    // Through the database and the assistant.

    [Fact]
    public void The_assistant_answers_from_the_wait_lists_decisions()
    {
        _database.AddWaitItem("headphones", 8000, Now.AddDays(-5));
        _database.AddWaitItem("jacket", 6000, Now.AddDays(-4));
        _database.AddWaitItem("game", 4000, Now.AddDays(-3));
        _database.AddWaitItem("lamp", 3000, Now.AddDays(-2));
        var waiting = _database.GetWaitList();
        _database.ResolveWaitItem(waiting.Single(item => item.Description == "headphones").Id, "skipped");
        _database.ResolveWaitItem(waiting.Single(item => item.Description == "jacket").Id, "skipped");
        _database.ResolveWaitItem(waiting.Single(item => item.Description == "game").Id, "bought");

        var reply = AssistantOps.Apply(new AssistantOp(AssistantOpKind.SpendingHelped), _database, _database.AddCapture("what's helping my spending?"), Now);

        Assert.True(reply.IsAnswer);
        Assert.StartsWith("- Wait list: you let go of 2 of the 3 things you parked, about $140.00 not spent.", reply.Text);
        Assert.Contains("- Spending review: not enough yet.", reply.Text);
        Assert.Equal(SpendingOutcomes.Title, _database.GetSpendingOutcomes(Today).Title);
    }

    [Fact]
    public void The_op_is_in_the_sorters_schema()
    {
        Assert.Contains("\"spending_helped\"", AssistantOps.SchemaNames);
    }

    [Fact]
    public void The_page_shows_one_short_line_until_there_is_something_to_report()
    {
        var empty = SpendingOutcomes.Build(Today, [], Caps, [], LongAgo);
        Assert.Equal([SpendingOutcomes.NotYet], SpendingOutcomes.ForPage(empty).Rows.Select(row => row.Label));

        var decided = Enumerable.Range(1, 3).Select(index => new SettledWait($"Thing {index}", 2000, DateTime.Now.AddDays(-index), "skipped")).ToList();
        var page = SpendingOutcomes.ForPage(SpendingOutcomes.Build(Today, decided, Caps, [], LongAgo)).Rows.Select(row => row.Label).ToList();
        Assert.StartsWith("Wait list: you let go of 3", Assert.Single(page));
    }
}
