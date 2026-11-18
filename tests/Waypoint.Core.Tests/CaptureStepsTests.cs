using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>
/// A capture's steps run once each, even when Waypoint stops partway. Each test stops it with a failing trigger or a
/// cancel, the way a crash or closing would, starts again on the same data, and checks what came of it. Made-up data.
/// </summary>
public sealed class CaptureStepsTests : IDisposable
{
    private static readonly DateTime Now = new(2030, 3, 12, 10, 0, 0);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-steps-{Guid.NewGuid():N}.db");
    private readonly WaypointDatabase _database;

    public CaptureStepsTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private sealed class Sorter(CaptureResult result) : ICaptureSorter
    {
        public int Calls { get; private set; }

        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<CaptureResult?>(result);
        }
    }

    private static readonly BudgetOp Wait = new(BudgetOpKind.AddWait, "Made up lamp", 4000, null, Hours: 24);
    private static readonly BudgetOp Income = new(BudgetOpKind.AddIncome, "Made up gig", 2500, null);

    private CaptureService Service(ICaptureSorter? sorter, List<CaptureOutcome> outcomes)
    {
        var service = new CaptureService(_database, sorter, clock: () => Now);
        service.Sorted += outcomes.Add;
        return service;
    }

    [Fact]
    public async Task A_capture_cut_off_after_a_step_ran_does_the_rest_without_doing_that_step_again()
    {
        var sorter = new Sorter(new CaptureResult([new SortedItem(ItemKind.Task, "Made up errand", null, null)], Answer: "Okay", BudgetOps: [Wait, Income]));
        var outcomes = new List<CaptureOutcome>();
        Service(sorter, outcomes).Capture("made up note with money in it");

        // The first step goes through, then Waypoint stops before noting it was done.
        FailWhen("BEFORE UPDATE ON capture_progress WHEN NEW.progress LIKE '%\"Next\":1,\"Started\":false%'");
        await Service(sorter, outcomes).ProcessPendingAsync();
        Assert.Empty(outcomes);
        Assert.Single(_database.GetPendingCaptures());
        Assert.Equal(1, Count("wait_list"));

        StopFailing();
        await Service(sorter, outcomes).ProcessPendingAsync();

        Assert.Equal(1, sorter.Calls);
        Assert.Empty(_database.GetPendingCaptures());
        Assert.Equal(1, Count("wait_list"));
        Assert.Equal(1, Count("other_income"));
        Assert.Equal(["Made up errand"], _database.GetOpenItems().Select(item => item.Title));
        var outcome = Assert.Single(outcomes);
        Assert.Equal("Okay", outcome.Answer);
        Assert.Equal(CaptureService.CutOff, outcome.BudgetReplies![0]);
        Assert.Equal(2, outcome.BudgetReplies.Count);
        Assert.Equal(0, Count("capture_progress"));
    }

    [Fact]
    public async Task A_monthly_budget_is_saved_with_the_items_or_not_at_all_and_comes_through_on_the_next_try()
    {
        var sorter = new Sorter(new CaptureResult([new SortedItem(ItemKind.Task, "Made up errand", null, null)], MonthlyBudgetCents: 123400));
        var outcomes = new List<CaptureOutcome>();
        Service(sorter, outcomes).Capture("made up budget is 1234 a month");

        FailWhen("BEFORE INSERT ON preferences WHEN NEW.key = 'monthly_budget_cents'");
        await Service(sorter, outcomes).ProcessPendingAsync();
        Assert.Empty(_database.GetOpenItems());
        Assert.Single(_database.GetPendingCaptures());
        Assert.Null(_database.GetPreference("monthly_budget_cents"));

        StopFailing();
        await Service(sorter, outcomes).ProcessPendingAsync();

        Assert.Equal("123400", _database.GetPreference("monthly_budget_cents"));
        Assert.Equal(["Made up errand"], _database.GetOpenItems().Select(item => item.Title));
        Assert.Empty(_database.GetPendingCaptures());
        Assert.Single(outcomes);
    }

    [Fact]
    public async Task A_category_cap_still_to_run_when_waypoint_stopped_is_set_on_the_next_try()
    {
        var cap = new BudgetOp(BudgetOpKind.SetCap, "Made up snacks", 5000, null);
        var sorter = new Sorter(new CaptureResult([], Answer: "Okay", BudgetOps: [Wait, cap]));
        var outcomes = new List<CaptureOutcome>();
        Service(sorter, outcomes).Capture("made up lamp and a snack budget");

        // The first step is noted as done, then Waypoint stops before the cap is begun.
        FailWhen("BEFORE UPDATE ON capture_progress WHEN NEW.progress LIKE '%\"Next\":1,\"Started\":true%'");
        await Service(sorter, outcomes).ProcessPendingAsync();
        Assert.DoesNotContain(_database.GetCategories(), category => category.Name == "Made up snacks");

        StopFailing();
        await Service(sorter, outcomes).ProcessPendingAsync();

        Assert.Equal(5000, _database.GetCategories().Single(category => category.Name == "Made up snacks").MonthlyCents);
        Assert.Equal(1, Count("wait_list"));
        Assert.DoesNotContain(CaptureService.CutOff, Assert.Single(outcomes).BudgetReplies!);
    }

    [Fact]
    public async Task A_step_that_fails_leaves_the_rest_to_run_and_the_reply_still_comes()
    {
        var sorter = new Sorter(new CaptureResult([], Answer: "Okay", BudgetOps: [Wait, Income]));
        var outcomes = new List<CaptureOutcome>();
        var service = Service(sorter, outcomes);
        service.Capture("made up money note");
        FailWhen("BEFORE INSERT ON wait_list");

        await service.ProcessPendingAsync();

        Assert.Empty(_database.GetPendingCaptures());
        Assert.Equal(0, Count("wait_list"));
        Assert.Equal(1, Count("other_income"));
        var outcome = Assert.Single(outcomes);
        Assert.Equal(CaptureService.StepFailed, outcome.BudgetReplies![0]);
        Assert.NotNull(service.LastError);
    }

    [Fact]
    public async Task Reading_email_cut_off_by_closing_is_read_again_without_repeating_what_came_before()
    {
        var sorter = new Sorter(new CaptureResult([], BudgetOps: [Wait], Ops: [new AssistantOp(AssistantOpKind.EmailCheck)]));
        var outcomes = new List<CaptureOutcome>();
        var closing = Service(sorter, outcomes);
        closing.CheckEmail = _ => throw new OperationCanceledException();
        closing.Capture("made up check my email");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => closing.ProcessPendingAsync());
        Assert.Single(_database.GetPendingCaptures());

        var reads = 0;
        var started = Service(sorter, outcomes);
        started.CheckEmail = _ =>
        {
            reads++;
            return Task.FromResult("Made up mail read");
        };
        await started.ProcessPendingAsync();

        Assert.Equal(1, reads);
        Assert.Equal(1, Count("wait_list"));
        Assert.Contains("Made up mail read", Assert.Single(outcomes).Answer);
        Assert.Empty(_database.GetPendingCaptures());
    }

    [Fact]
    public async Task A_done_cut_off_before_its_capture_was_finished_is_not_done_again()
    {
        var task = _database.AddByHand(new HandAddedItem(ItemKind.Task, "Made up essay", null), Now).Item!;
        _database.StartActivation(ItemRef.Format(task), "Made up first", ["Made up second", "Made up third"], Activation.TypedMethod, Now);
        var outcomes = new List<CaptureOutcome>();
        Service(null, outcomes).Capture("did it");

        // The step moves on, then Waypoint stops before the capture is marked done.
        FailWhen("BEFORE UPDATE OF sorted_at ON captures");
        await Service(null, outcomes).ProcessPendingAsync();
        Assert.Equal("Made up second", _database.GetActiveSession(Now)!.Action);
        Assert.Single(_database.GetPendingCaptures());

        StopFailing();
        await Service(null, outcomes).ProcessPendingAsync();

        Assert.Equal("Made up second", _database.GetActiveSession(Now)!.Action);
        Assert.Empty(_database.GetPendingCaptures());
        Assert.Equal(CaptureService.CutOff, Assert.Single(outcomes).Answer);
    }

    [Fact]
    public async Task A_short_reply_with_nothing_to_act_on_goes_on_to_the_sorter_and_keeps_nothing()
    {
        var sorter = new Sorter(new CaptureResult([new SortedItem(ItemKind.Note, "did it", null, null)]));
        var outcomes = new List<CaptureOutcome>();
        var service = Service(sorter, outcomes);
        service.Capture("did it");

        await service.ProcessPendingAsync();

        Assert.Equal(1, sorter.Calls);
        Assert.Empty(_database.GetPendingCaptures());
        Assert.Equal(0, Count("capture_progress"));
    }

    private void FailWhen(string when)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TRIGGER fail_partway {when} BEGIN SELECT RAISE(ABORT, 'made to fail partway'); END";
        command.ExecuteNonQuery();
    }

    private void StopFailing()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TRIGGER fail_partway";
        command.ExecuteNonQuery();
    }

    private long Count(string table)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)command.ExecuteScalar()!;
    }
}
