using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>What the chat knows about money, checking email from it, linking replies to what they made, and the lowest point.</summary>
public sealed class ChatMoneyTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public ChatMoneyTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private sealed class FakeSorter(Func<AssistantContext, CaptureResult?> sort) : ICaptureSorter
    {
        public AssistantContext? Seen { get; private set; }

        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default)
        {
            Seen = context;
            return Task.FromResult(sort(context));
        }
    }

    private void AddCard()
    {
        _database.SaveBudgetAccount(new BudgetAccount("Contoso Card", AccountKind.Card, 45000, Today.AddDays(-3), 250000, 9, 3));
        _database.ImportTransactions("Contoso Card", [new ImportedTransaction(Today.AddDays(-1), "OAKHAVEN COFFEE", 685)]);
    }

    private async Task<CaptureOutcome> Ask(CaptureService service, string text)
    {
        CaptureOutcome? outcome = null;
        service.Sorted += found => outcome = found;
        service.Capture(text);
        await service.ProcessPendingAsync();
        return outcome!;
    }

    [Fact]
    public async Task The_chat_sees_balances_with_card_purchases_since_they_were_confirmed()
    {
        AddCard();
        var sorter = new FakeSorter(_ => new CaptureResult([], Answer: "ok"));

        await Ask(new CaptureService(_database, sorter), "what's my contoso card balance");

        var line = Assert.Single(sorter.Seen!.Balances!);
        Assert.Contains("Contoso Card: $456.85 owed of $2,500.00", line);
    }

    [Fact]
    public async Task Checking_email_from_the_chat_reads_it_and_then_says_the_balances()
    {
        AddCard();
        var checks = 0;
        var service = new CaptureService(_database, new FakeSorter(_ => new CaptureResult([], Ops: [new AssistantOp(AssistantOpKind.EmailCheck)])))
        {
            CheckEmail = _ =>
            {
                checks++;
                return Task.FromResult("Checked email: 1 new purchase.");
            },
        };

        var outcome = await Ask(service, "check my email");

        Assert.Equal(1, checks);
        Assert.StartsWith("Checked email: 1 new purchase.", outcome.Answer);
        Assert.Contains("Contoso Card: $456.85 owed", outcome.Answer);
    }

    [Fact]
    public async Task Without_email_connected_the_chat_says_so()
    {
        var service = new CaptureService(_database, new FakeSorter(_ => new CaptureResult([], Ops: [new AssistantOp(AssistantOpKind.EmailCheck)])));

        var outcome = await Ask(service, "check my email");

        Assert.Contains("isn't connected", outcome.Answer);
    }

    [Fact]
    public async Task A_reply_knows_everything_it_made_dated_or_not()
    {
        var friday = Today.AddDays(3);
        var service = new CaptureService(_database, new FakeSorter(_ => new CaptureResult(
        [
            new SortedItem(ItemKind.Event, "study group", friday, null),
            new SortedItem(ItemKind.Task, "laundry", null, null),
            new SortedItem(ItemKind.Shopping, "milk", null, null),
        ])));

        var outcome = await Ask(service, "study group friday, laundry, and milk");

        var made = _database.GetItemsFromCapture(outcome.CaptureId!.Value);
        Assert.Equal(["study group", "laundry", "milk"], made.Select(item => item.Title));
    }

    [Fact]
    public void The_lowest_point_ahead_skips_today()
    {
        var today = new DateOnly(2026, 11, 5);
        ScheduledItem[] schedule = [new("Paycheck", 85000, [6]), new("Savings", -9500, [12])];

        var low = BudgetMath.LowestAhead(61000, today, 10, schedule, []);

        Assert.Equal((61000 + 85000 - 9500, new DateOnly(2026, 11, 12)), low);
        Assert.Null(BudgetMath.LowestAhead(61000, today, 10, [], []));
    }
}
