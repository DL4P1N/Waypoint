using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;
using Waypoint.Core.Remote;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Tests;

/// <summary>
/// The page buttons for assistant ops that had no manual control. Each goes through the same Core method as the op,
/// so the button and the typed words can't disagree, and none of them needs Claude.
/// </summary>
public sealed class ManualOpControlsTests : IDisposable
{
    // 2026-11-02 is a Monday. Tuesday's 9:30 class gives a 12:30 AM bedtime with the defaults, so the wind-down is open at Now.
    private static readonly ClassSchedule Schedule = new(
        new DateOnly(2026, 9, 14),
        new DateOnly(2026, 12, 18),
        [new ClassMeeting("CS 101", "ABC 100", [DayOfWeek.Tuesday], new TimeOnly(9, 30), new TimeOnly(10, 45), 15)],
        []);

    private static readonly DateTime Now = new(2026, 11, 3, 0, 0, 0);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly string _twinPath = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;
    private readonly WaypointDatabase _twin;

    public ManualOpControlsTests()
    {
        _database = new WaypointDatabase(_path);
        _twin = new WaypointDatabase(_twinPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
        File.Delete(_twinPath);
    }

    private BedtimeSchedule Bedtime(WaypointDatabase database) => new(new BedtimeSettings(), Schedule, database.GetFirstShift);

    private static StoredItem AddTask(WaypointDatabase database, string title)
    {
        database.CompleteCapture(database.AddCapture(title), [new SortedItem(ItemKind.Task, title, null, null)]);
        return database.GetOpenItems().Single(item => item.Title == title);
    }

    /// <summary>The op and the button, each on its own database with the same start, give the same reply and the same result.</summary>
    private void Same(Action<WaypointDatabase> setUp, AssistantOp op, Func<ISessionData, OpReply> button, Func<WaypointDatabase, object?> result)
    {
        setUp(_database);
        setUp(_twin);
        var typed = AssistantOps.Apply(op, _database, _database.AddCapture("typed"), Now, bedtime: Bedtime(_database));
        var pressed = button(new LocalSessionData(_twin, Bedtime(_twin)));

        Assert.Equal(typed.Text, pressed.Text);
        Assert.Equal(result(_database), result(_twin));
    }

    private static void Paused(WaypointDatabase database)
    {
        database.StartActivation(ItemRef.Format(AddTask(database, "Write the essay")), "Open the outline", null, "test", Now);
        database.PauseSession(null, null, Now);
    }

    [Fact]
    public void A_paused_session_can_be_found_and_picked_back_up()
    {
        var sessions = new LocalSessionData(_database);
        Assert.Null(sessions.GetPausedSession());

        Same(Paused, new AssistantOp(AssistantOpKind.ActivationResume), data =>
        {
            Assert.Equal("Write the essay", data.GetPausedSession()?.TaskTitle);
            Assert.Null(data.GetActiveSession(Now));
            return data.ResumeSession();
        }, database => database.GetActiveSession(Now)?.TaskTitle);
        Assert.Equal("Write the essay", _twin.GetActiveSession(Now)?.TaskTitle);
        Assert.Null(new LocalSessionData(_twin).GetPausedSession());
    }

    [Fact]
    public void End_on_the_session_card_is_the_same_as_cancel()
    {
        static void Going(WaypointDatabase database) =>
            database.StartActivation(ItemRef.Format(AddTask(database, "Write the essay")), "Open the outline", null, "test", Now);

        Same(Going, new AssistantOp(AssistantOpKind.ActivationCancel), data => data.CancelSession(Now), database => database.GetActiveSession(Now));
        Assert.Null(_twin.GetActiveSession(Now));
        Assert.Contains(_twin.GetOpenItems(), item => item.Title == "Write the essay");
    }

    [Fact]
    public void Tonight_checks_snooze_and_skip_go_through_the_bedtime_routine()
    {
        Same(_ => { }, new AssistantOp(AssistantOpKind.BedtimeCheck, Name: "evening step"), data => data.MarkRoutineStep(BedtimeSettings.DefaultEveningStepName, true, Now),
            database => string.Join(",", BedtimeRoutine.GetStatus(database, Bedtime(database), Now)!.Steps.Where(step => step.Done).Select(step => step.Label)));
        Assert.Contains("Left tonight", new LocalSessionData(_twin, Bedtime(_twin)).MarkRoutineStep(BedtimeRoutine.Teeth, true, Now).Text);

        // A step that isn't on tonight's list is said plainly, not saved.
        var unknown = new LocalSessionData(_twin, Bedtime(_twin)).MarkRoutineStep("the laundry", true, Now);
        Assert.StartsWith("Which one?", unknown.Text);

        Same(_ => { }, new AssistantOp(AssistantOpKind.BedtimeSnooze), data => data.SnoozeBedtime(Now), database => database.BedtimeSnoozedUntil);
        Assert.NotNull(_twin.BedtimeSnoozedUntil);

        Same(_ => { }, new AssistantOp(AssistantOpKind.BedtimeSkip), data => data.SkipBedtime(Now), database => database.BedtimeSnoozedUntil);
        Assert.Null(_twin.BedtimeSnoozedUntil);
    }

    /// <summary>The op and the Budget page's call, each on its own database with the same start.</summary>
    private void SameBudget(Action<WaypointDatabase> setUp, AssistantOp op, Func<IBudgetData, OpReply> button, Func<WaypointDatabase, object?> result)
    {
        setUp(_database);
        setUp(_twin);
        var typed = AssistantOps.Apply(op, _database, _database.AddCapture("typed"), Now);
        var pressed = button(new LocalBudgetData(_twin));

        Assert.Equal(typed.Text, pressed.Text);
        Assert.Equal(typed.IsAnswer, pressed.IsAnswer);
        Assert.Equal(result(_database), result(_twin));
    }

    [Fact]
    public void I_got_paid_wait_hours_and_patterns_on_budget_match_typing_them()
    {
        static void Paid(WaypointDatabase database) => database.SetPaySchedule("biweekly", [], 85000, Now);
        static object Deposits(WaypointDatabase database) => string.Join(",", database.GetPayDeposits().Select(deposit => deposit.Cents));

        // With no pay schedule, both say there's no paycheck expected and count nothing.
        SameBudget(_ => { }, new AssistantOp(AssistantOpKind.PayReceived), data => data.ConfirmPayReceived(null, DateOnly.FromDateTime(Now)), Deposits);
        SameBudget(Paid, new AssistantOp(AssistantOpKind.PayReceived, AmountCents: 51234), data => data.ConfirmPayReceived(51234, DateOnly.FromDateTime(Now)), Deposits);
        Assert.Equal("51234", Deposits(_twin));

        SameBudget(_ => { }, new AssistantOp(AssistantOpKind.WaitHoursSet, Text: "24"), data => data.SetDefaultWaitHours("24"), database => database.GetDefaultWaitHours());
        Assert.Equal(24, _twin.GetDefaultWaitHours());
        SameBudget(_ => { }, new AssistantOp(AssistantOpKind.WaitHoursSet, Text: "soon"), data => data.SetDefaultWaitHours("soon"), database => database.GetDefaultWaitHours());

        var typed = AssistantOps.Apply(new AssistantOp(AssistantOpKind.SpendingPatterns), _database, _database.AddCapture("typed"), Now);
        Assert.Equal(typed.Text, string.Join("\n", new LocalBudgetData(_twin).GetSpendingPatterns(DateOnly.FromDateTime(Now))));
    }

    [Fact]
    public void A_saved_spot_waiting_for_its_next_step_can_be_answered_from_saved_spots()
    {
        INotesData notes = new LocalNotesData(_database);
        Assert.Null(notes.GetWaitingSpot(Now));

        _database.SaveHandoff("essay", "the intro", null, [], _database.AddCapture("save my spot"), Now);
        var waiting = notes.GetWaitingSpot(Now.AddMinutes(5));
        Assert.Equal("essay", waiting?.Project);

        var reply = notes.SetSpotNext("write the second paragraph", Now.AddMinutes(5));
        Assert.Equal("Saved. Next time on essay: write the second paragraph", reply.Text);
        Assert.Equal("write the second paragraph", notes.GetSavedSpots().Single().Next);
        Assert.Null(notes.GetWaitingSpot(Now.AddMinutes(5)));

        // The same as typing the answer: once the question is old it's no longer waiting.
        _database.SaveHandoff("lab", null, null, [], _database.AddCapture("save my spot"), Now);
        Assert.Null(notes.GetWaitingSpot(Now.AddHours(3)));
    }
}
