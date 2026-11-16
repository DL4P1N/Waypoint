using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>Bills with amounts, and how the forecast, Until payday, and "Can I afford this?" count them.</summary>
public sealed class BillTests : IDisposable
{
    // A Monday. The next paycheck lands Tuesday the 10th, and the one after on Wednesday the 25th.
    private static readonly DateOnly Today = new(2026, 11, 2);
    private static readonly DateTime Now = Today.ToDateTime(new TimeOnly(12, 0));
    private static readonly ScheduledItem Paycheck = new("Paycheck", 85000, [10, 25], true);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-{Guid.NewGuid():N}.db");
    private readonly WaypointDatabase _database;

    public BillTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static BudgetAccount Contoso(long balance = 30000) => new("Contoso Card", AccountKind.Card, balance, Today, 250000, 10, 20);

    private OpReply Say(string? name, long? cents, IReadOnlyList<string>? days = null, string? howOften = null, DateTime? at = null, string? account = null) =>
        AssistantOps.Apply(new AssistantOp(AssistantOpKind.BillSet, name, howOften, days ?? [], cents, at, Account: account), _database, 0, Now);

    private void SeedChecking()
    {
        _database.AddScheduledItem(Paycheck);
        _database.SaveBudgetAccount(new BudgetAccount("Northwind Bank checking", AccountKind.Checking, 50000, Today));
    }

    [Fact]
    public void A_water_bill_on_the_8th_is_saved_as_a_monthly_bill_from_checking()
    {
        var reply = Say("Water bill", 5500, ["8"]);

        var bill = Assert.Single(_database.GetBills());
        Assert.Equal(("Water bill", -5500L, PayPattern.MonthDays, (string?)null), (bill.Label, bill.AmountCents, bill.Pattern, bill.Account));
        Assert.Equal([8], bill.Days);
        Assert.Contains("$55.00", reply.Text);
        Assert.Contains("Nov 8", reply.Text);
    }

    [Fact]
    public void Saying_the_same_bill_again_changes_it_instead_of_adding_another()
    {
        Say("Water bill", 5500, ["8"]);
        Say("water bill", 5000, ["15"]);

        var bill = Assert.Single(_database.GetBills());
        Assert.Equal((-5000L, 15), (bill.AmountCents, bill.Days.Single()));
    }

    [Fact]
    public void Anything_missing_is_asked_for_and_nothing_is_saved()
    {
        Assert.True(Say("Water bill", null, ["8"]).IsAnswer);
        Assert.Contains("day of the month", Say("Water bill", 5500, howOften: "monthly").Text);
        Assert.Contains("next due", Say("Gym", 1500, howOften: "weekly").Text);
        Assert.Contains("How often", Say("Gym", 1500, howOften: "yearly").Text);
        Assert.Empty(_database.GetBills());
    }

    [Fact]
    public void A_weekly_bill_starts_from_its_next_due_date()
    {
        Say("Bus pass", 1000, howOften: "weekly", at: new DateTime(2026, 11, 4));

        var bill = Assert.Single(_database.GetBills());
        Assert.Equal(new DateOnly(2026, 11, 11), BudgetMath.NextOccurrence(bill, new DateOnly(2026, 11, 5)));
    }

    [Fact]
    public void A_bill_on_a_card_names_the_card_and_an_unknown_card_is_asked_about()
    {
        _database.SaveBudgetAccount(Contoso());

        Assert.Contains("Which card", Say("Streamco", 900, ["6"], account: "Amex").Text);
        Assert.Empty(_database.GetBills());

        Say("Streamco", 900, ["6"], account: "contoso card");
        Assert.Equal("Contoso Card", Assert.Single(_database.GetBills()).Account);

        Say("Streamco", 900, ["6"], account: "checking");
        Assert.Null(Assert.Single(_database.GetBills()).Account);
    }

    [Fact]
    public void Bills_can_be_listed_and_removed()
    {
        Say("Water bill", 5500, ["8"]);
        Say("Internet", 6000, ["20"]);

        var shown = AssistantOps.Apply(new AssistantOp(AssistantOpKind.BillShow), _database, 0, Now);
        Assert.Contains("Water bill: $55.00", shown.Text);
        Assert.Contains("Internet: $60.00", shown.Text);

        Assert.Contains("Which bill", _database.RemoveBill("Gym").Text);
        _database.RemoveBill("water");
        Assert.Equal("Internet", Assert.Single(_database.GetBills()).Label);
    }

    [Fact]
    public void Removing_a_bill_never_touches_the_paycheck()
    {
        SeedChecking();

        _database.RemoveBill("Paycheck");

        Assert.Single(_database.GetSchedule());
    }

    [Fact]
    public void Can_I_afford_this_counts_a_bill_due_before_payday()
    {
        SeedChecking();
        Say("Water bill", 5500, ["4"]);

        var answer = _database.CheckAffordable(1000, null, Today);

        Assert.Contains("$435.00 before payday on Nov 10", answer.Text);
        Assert.Contains("Water bill $55.00 on Nov 4", answer.Text);
    }

    [Fact]
    public void Until_payday_and_lowest_ahead_include_bills()
    {
        SeedChecking();
        Say("Rent", 40000, ["5"]);

        var schedule = _database.GetForecastSchedule();
        var outlook = BudgetMath.UntilPayday(50000, Today, schedule, []);
        Assert.Equal((10000L, new DateOnly(2026, 11, 5)), (outlook.LowestCents, outlook.LowestOn));
        Assert.Equal("Rent", Assert.Single(outlook.Bills).Label);

        Assert.Equal((10000L, new DateOnly(2026, 11, 5)), BudgetMath.LowestAhead(50000, Today, BudgetMath.ForecastDays, schedule, [])!.Value);

        var report = _database.GetBudgetReport(Today);
        // Money going out shows with a minus, the same as under Coming up.
        Assert.Contains(report.Single(section => section.Title == "UNTIL PAYDAY").Rows, row => row.Label == "Rent, Nov 5" && row.Value == "-$400.00");
    }

    [Fact]
    public void A_card_bill_reaches_checking_through_the_autopay_for_its_statement()
    {
        // Contoso Card closes on the 10th and autopays on the 20th. A charge by the 10th makes the Nov 20 autopay,
        // and one after it waits for December's.
        var card = Contoso();
        ScheduledItem[] bills =
        [
            new("Streamco", -1200, [5], Account: "Contoso Card"),
            new("Gym", -3000, [15], Account: "Contoso Card"),
        ];

        var flows = BudgetMath.Flows(Today, BudgetMath.ForecastDays, bills, [card]);

        var autopay = Assert.Single(flows);
        Assert.Equal((new DateOnly(2026, 11, 20), -31200L, true), (autopay.Date, autopay.Cents, autopay.IsCard));
    }

    [Fact]
    public void A_card_bill_without_a_close_day_counts_until_the_day_before_autopay()
    {
        var card = Contoso() with { CloseDay = null };
        ScheduledItem[] bills = [new("Gym", -3000, [15], Account: "Contoso Card")];

        Assert.Equal(-33000, Assert.Single(BudgetMath.Flows(Today, BudgetMath.ForecastDays, bills, [card])).Cents);
    }

    [Fact]
    public void A_bill_on_a_card_thats_gone_counts_against_checking()
    {
        ScheduledItem[] bills = [new("Gym", -3000, [15], Account: "Old card")];

        var flow = Assert.Single(BudgetMath.Flows(Today, BudgetMath.ForecastDays, bills, []));
        Assert.Equal(("Gym", -3000L), (flow.Label, flow.Cents));
    }

    [Fact]
    public void A_recurring_to_do_with_the_same_name_as_a_bill_isnt_counted_twice()
    {
        _database.CompleteCapture(_database.AddCapture("x"), [new SortedItem(ItemKind.Task, "Water bill", Today.AddDays(10), 5500, Recurs: new Recurrence(RecurrenceUnit.Month))]);
        _database.CompleteCapture(_database.AddCapture("y"), [new SortedItem(ItemKind.Task, "Car insurance", Today.AddDays(8), 9000, Recurs: new Recurrence(RecurrenceUnit.Month))]);
        Say("Water bill", 5500, ["8"]);

        var schedule = _database.GetForecastSchedule();

        Assert.Single(schedule, item => item.Label == "Water bill");
        Assert.Single(schedule, item => item.Label == "Car insurance");
    }

    [Fact]
    public void An_older_schedule_reads_back_with_no_account()
    {
        _database.AddScheduledItem(new ScheduledItem("Savings transfer", -9500, [12, 28]));

        Assert.Null(Assert.Single(_database.GetSchedule()).Account);
    }

    [Fact]
    public void The_sorter_reads_the_account_on_an_op()
    {
        const string json = """
            {"items":[],"changes":[],"answer":null,"monthly_budget_cents":null,"budget_ops":[],"focus_ref":null,
             "ops":[{"op":"bill_set","name":"Water bill","text":null,"next":null,"account":"Contoso Card","entries":["8"],"amount_cents":5500,"at":null,"until":null}]}
            """;

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var op = Assert.Single(SortedItemParser.ParseResult(document.RootElement).Ops!);
        Assert.Equal((AssistantOpKind.BillSet, "Contoso Card", 5500L), (op.Kind, op.Account, op.AmountCents!.Value));
        Assert.Contains("\"account\"", ClaudeCliSorter.Schema);
        Assert.Contains("bill_set", ClaudeCliSorter.Schema);
    }
}
