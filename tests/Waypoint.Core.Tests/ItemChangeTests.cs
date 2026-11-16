using Microsoft.Data.Sqlite;
using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Tests;

public sealed class ItemChangeTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public ItemChangeTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private sealed class FakeSorter(Func<AssistantContext, CaptureResult?> sort) : ICaptureSorter
    {
        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(sort(context));
    }

    private sealed class FakePlanner(Func<IReadOnlyList<StoredItem>, IReadOnlyList<EventNudges>?> plan) : INudgePlanner
    {
        public Task<IReadOnlyList<EventNudges>?> PlanAsync(IReadOnlyList<StoredItem> events, DateTime now, ClassSchedule? schedule, CancellationToken cancellationToken = default) =>
            Task.FromResult(plan(events));
    }

    private StoredItem Store(SortedItem item)
    {
        _database.CompleteCapture(_database.AddCapture("x"), [item]);
        return _database.GetOpenItems().Single(stored => stored.Title == item.Title);
    }

    private StoredItem StoreEvent(string title, DateOnly due)
    {
        _database.ReplaceCalendarEvents([new CalendarEvent(title, title, "CS 101", due, null)]);
        return _database.GetOpenItems().Single(stored => stored.Title == title);
    }

    private async Task<CaptureOutcome> Run(params ItemChange[] changes)
    {
        CaptureOutcome? outcome = null;
        var service = new CaptureService(_database, new FakeSorter(_ => new CaptureResult([], changes)));
        service.Sorted += result => outcome = result;
        service.Capture("change it");
        await service.ProcessPendingAsync();
        return outcome!;
    }

    private static DateOnly InDays(int days) => DateOnly.FromDateTime(DateTime.Now).AddDays(days);

    [Fact]
    public async Task Done_and_cancel_close_an_item_and_stop_its_reminders()
    {
        var laundry = Store(new SortedItem(ItemKind.Reminder, "laundry", InDays(-1), null));
        var paper = Store(new SortedItem(ItemKind.Task, "paper", null, null));

        await Run(new ItemChange(ItemSource.Capture, laundry.Id, ChangeAction.Cancel), new ItemChange(ItemSource.Capture, paper.Id, ChangeAction.Done));

        Assert.Empty(_database.GetOpenItems());
        Assert.Empty(_database.GetDueReminders(DateTime.Now));
    }

    [Fact]
    public async Task Reopen_brings_back_a_recently_finished_item()
    {
        var paper = Store(new SortedItem(ItemKind.Task, "paper", null, null));
        await Run(new ItemChange(ItemSource.Capture, paper.Id, ChangeAction.Done));

        var outcome = await Run(new ItemChange(ItemSource.Capture, paper.Id, ChangeAction.Reopen));

        Assert.Equal("paper", _database.GetOpenItems().Single().Title);
        Assert.Equal("paper is back on the list", ItemGroups.OutcomeSummary(outcome));
    }

    [Fact]
    public async Task Changes_to_items_the_sorter_was_not_shown_are_dropped()
    {
        var paper = Store(new SortedItem(ItemKind.Task, "paper", null, null));

        var outcome = await Run(
            new ItemChange(ItemSource.Capture, 999, ChangeAction.Done),
            new ItemChange(ItemSource.Calendar, paper.Id, ChangeAction.Done),
            new ItemChange(ItemSource.Capture, paper.Id, ChangeAction.Reopen));

        Assert.Empty(outcome.Changes);
        Assert.Equal("change it", _database.GetOpenItems().Single(item => item.Kind == ItemKind.Note).Title);
        Assert.Contains(_database.GetOpenItems(), item => item.Title == "paper");
    }

    [Fact]
    public async Task Answer_alone_adds_nothing()
    {
        var service = new CaptureService(_database, new FakeSorter(_ => new CaptureResult([], null, "Open the outline.")));
        CaptureOutcome? outcome = null;
        service.Sorted += result => outcome = result;

        service.Capture("what do i do now");
        await service.ProcessPendingAsync();

        Assert.Equal("Open the outline.", outcome!.Answer);
        Assert.Empty(_database.GetOpenItems());
        Assert.Empty(_database.GetPendingCaptures());
    }

    [Fact]
    public async Task Sorter_sees_open_items_finished_items_and_the_day()
    {
        var done = Store(new SortedItem(ItemKind.Task, "done one", null, null));
        Store(new SortedItem(ItemKind.Task, "open one", null, null));
        _database.SetDone(done, true);
        AssistantContext? seen = null;
        var schedule = new ClassSchedule(
            InDays(-1), InDays(30),
            [new ClassMeeting("CS 101", "ABC", Enum.GetValues<DayOfWeek>(), new TimeOnly(23, 59), new TimeOnly(23, 59))],
            []);
        var service = new CaptureService(
            _database,
            new FakeSorter(context => { seen = context; return new CaptureResult([]); }),
            schedule,
            new BedtimeSchedule(new BedtimeSettings(), schedule));

        service.Capture("what now");
        await service.ProcessPendingAsync();

        Assert.Equal("open one", seen!.OpenItems.Single().Title);
        Assert.Equal("done one", seen.RecentlyFinished.Single().Title);
        Assert.Equal("CS 101", seen.NextClass!.Meeting.Course);
        Assert.NotNull(seen.Bedtime);

        var prompt = ClaudeCliSorter.BuildPrompt("what now", seen.Now, seen);
        Assert.Contains("item:", prompt);
        Assert.Contains("open one", prompt);
        Assert.Contains("Bedtime:", prompt);
    }

    [Fact]
    public async Task Moving_an_exam_shifts_its_study_plan_and_alerts()
    {
        var study = DateTime.Now.AddDays(2).Date.AddHours(10);
        var midterm = Store(new SortedItem(ItemKind.Exam, "midterm", InDays(5), null, new TimeOnly(14, 0), [new PlannedReminder(study, "Study")]));

        var outcome = await Run(new ItemChange(ItemSource.Capture, midterm.Id, ChangeAction.Update, null, InDays(8)));

        var moved = _database.GetOpenItems().Single();
        Assert.Equal(InDays(8), moved.DueDate);
        Assert.Equal(new TimeOnly(14, 0), TimeOnly.FromDateTime(moved.DueAt!.Value.DateTime));
        Assert.StartsWith("Moved midterm to", ItemGroups.OutcomeSummary(outcome));

        Assert.Empty(_database.GetDueReminders(study.AddDays(2)));
        Assert.Equal("Study", _database.GetDueReminders(study.AddDays(3)).Single().Message);
        Assert.Equal(3, _database.GetDueReminders(InDays(8).ToDateTime(new TimeOnly(13, 30))).Count);
    }

    [Fact]
    public async Task Moving_earlier_drops_reminders_that_land_in_the_past()
    {
        var study = DateTime.Now.AddDays(1);
        var midterm = Store(new SortedItem(ItemKind.Exam, "midterm", InDays(10), null, new TimeOnly(14, 0), [new PlannedReminder(study, "Study")]));

        await Run(new ItemChange(ItemSource.Capture, midterm.Id, ChangeAction.Update, null, InDays(3)));

        Assert.DoesNotContain(_database.GetDueReminders(DateTime.Now.AddDays(4)), reminder => reminder.Message == "Study");
    }

    [Fact]
    public async Task A_repeating_item_keeps_coming_back_after_a_move()
    {
        var rent = Store(new SortedItem(ItemKind.Task, "rent", InDays(3), null, null, null, 30));

        await Run(new ItemChange(ItemSource.Capture, rent.Id, ChangeAction.Update, null, InDays(2)));

        var reminder = _database.GetDueReminders(DateTime.Now.AddMinutes(31)).Single();
        Assert.Equal(30, reminder.RepeatMinutes);
    }

    [Fact]
    public async Task Giving_a_date_only_exam_a_time_keeps_study_steps_and_moves_its_alerts()
    {
        var study = DateTime.Today.AddDays(2).AddHours(20);
        var midterm = Store(new SortedItem(ItemKind.Exam, "midterm", InDays(5), null, null, [new PlannedReminder(study, "Study")]));

        await Run(new ItemChange(ItemSource.Capture, midterm.Id, ChangeAction.Update, null, null, new TimeOnly(17, 0)));

        Assert.Empty(_database.GetDueReminders(study.AddMinutes(-1)));
        Assert.Equal("Study", _database.GetDueReminders(study).Single().Message);
        Assert.Single(_database.GetDueReminders(InDays(4).ToDateTime(new TimeOnly(16, 59))));
        Assert.Equal(2, _database.GetDueReminders(InDays(4).ToDateTime(new TimeOnly(17, 0))).Count);
    }

    [Fact]
    public async Task Reopening_skips_reminders_missed_while_closed_but_keeps_later_ones()
    {
        var soon = DateTime.Now.AddDays(1);
        var paper = Store(new SortedItem(ItemKind.Task, "paper", null, null, null, [new PlannedReminder(soon, "Soon"), new PlannedReminder(soon.AddDays(1), "Later")]));
        _database.SnoozeReminder(_database.GetDueReminders(soon).Single(), DateTime.Now.AddHours(-1));

        await Run(new ItemChange(ItemSource.Capture, paper.Id, ChangeAction.Done));
        await Run(new ItemChange(ItemSource.Capture, paper.Id, ChangeAction.Reopen));

        Assert.Empty(_database.GetDueReminders(DateTime.Now));
        Assert.Equal("Later", _database.GetDueReminders(soon.AddDays(2)).Single().Message);
    }

    [Fact]
    public void A_nudge_stays_quiet_once_its_assignment_is_due_that_day()
    {
        var dueAt = new DateTimeOffset(InDays(1).ToDateTime(new TimeOnly(11, 59)));
        _database.ReplaceCalendarEvents([new CalendarEvent("Quiz", "Quiz", null, InDays(1), dueAt)]);
        var quiz = _database.GetOpenItems().Single();
        _database.SaveNudges([new EventNudges(quiz.Id, [new PlannedReminder(InDays(1).ToDateTime(new TimeOnly(8, 0)), "Study")])]);

        Assert.Single(_database.GetDueReminders(InDays(1).ToDateTime(new TimeOnly(10, 0))));
        Assert.Empty(_database.GetDueReminders(InDays(1).ToDateTime(new TimeOnly(14, 0))));
    }

    [Fact]
    public async Task Breaking_down_an_item_adds_ordered_steps_under_it()
    {
        var paper = Store(new SortedItem(ItemKind.Deadline, "paper", InDays(4), null));
        var at = DateTime.Today.AddDays(2).AddHours(19);

        var outcome = await Run(new ItemChange(ItemSource.Capture, paper.Id, ChangeAction.BreakDown, Steps:
            [new PlannedStep("Read the prompt", at), new PlannedStep("Write the outline", null), new PlannedStep("Too late", DateTime.Today.AddDays(9))]));

        var items = _database.GetOpenItems();
        Assert.Equal("paper", ItemGroups.ByDay(items, InDays(0)).Single().Items.Single().Title);
        Assert.Equal(["Read the prompt", "Write the outline", "Too late"], ItemGroups.StepsOf(paper, items).Select(step => step.Title));
        Assert.Equal("Broke paper into 3 steps. Start with: Read the prompt", ItemGroups.OutcomeSummary(outcome));
        Assert.Equal("Read the prompt", _database.GetDueReminders(at).Single().Item.Title);
        Assert.Single(_database.GetDueReminders(DateTime.Today.AddDays(10)));
    }

    [Fact]
    public async Task Steps_follow_their_parent_closed_and_reopened()
    {
        var paper = Store(new SortedItem(ItemKind.Task, "paper", null, null));
        var at = DateTime.Now.AddDays(1);
        await Run(new ItemChange(ItemSource.Capture, paper.Id, ChangeAction.BreakDown, Steps: [new PlannedStep("Start", at)]));

        await Run(new ItemChange(ItemSource.Capture, paper.Id, ChangeAction.Done));

        Assert.Empty(_database.GetOpenItems());
        Assert.Empty(_database.GetDueReminders(at));

        await Run(new ItemChange(ItemSource.Capture, paper.Id, ChangeAction.Reopen));

        Assert.Equal(["paper", "Start"], _database.GetOpenItems().Select(item => item.Title));
    }

    [Fact]
    public async Task Moving_an_item_moves_its_step_reminders()
    {
        var paper = Store(new SortedItem(ItemKind.Deadline, "paper", InDays(4), null));
        var at = InDays(2).ToDateTime(new TimeOnly(19, 0));
        await Run(new ItemChange(ItemSource.Capture, paper.Id, ChangeAction.BreakDown, Steps: [new PlannedStep("Start", at)]));

        await Run(new ItemChange(ItemSource.Capture, paper.Id, ChangeAction.Update, null, InDays(6)));

        Assert.Empty(_database.GetDueReminders(at.AddDays(2).AddMinutes(-1)));
        Assert.Equal("Start", _database.GetDueReminders(at.AddDays(2)).Single().Item.Title);
    }

    [Fact]
    public async Task Stopping_reminders_includes_the_steps()
    {
        var paper = Store(new SortedItem(ItemKind.Task, "paper", null, null));
        var at = DateTime.Now.AddDays(1);
        await Run(new ItemChange(ItemSource.Capture, paper.Id, ChangeAction.BreakDown, Steps: [new PlannedStep("Start", at)]));

        await Run(new ItemChange(ItemSource.Capture, paper.Id, ChangeAction.StopReminders));

        Assert.Empty(_database.GetDueReminders(at));
    }

    [Fact]
    public async Task Calendar_steps_without_reminders_leave_its_nudges_alone()
    {
        var paper = StoreEvent("Paper 2", InDays(4));
        await new AssignmentNudges(_database, new FakePlanner(_ => [])).PlanAsync(DateTime.Now);

        await Run(new ItemChange(ItemSource.Calendar, paper.Id, ChangeAction.BreakDown, Steps: [new PlannedStep("Start", null)]));

        Assert.Single(_database.GetDueReminders(InDays(3).ToDateTime(new TimeOnly(19, 0))));
    }

    [Fact]
    public void A_sent_reminder_is_not_sent_twice_and_stays_on_the_list()
    {
        Store(new SortedItem(ItemKind.Reminder, "call", InDays(-1), null));
        var now = DateTime.Now;

        _database.SettleSentReminder(_database.GetDueReminders(now).Single(), now);

        Assert.Empty(_database.GetDueReminders(now.AddDays(1)));
        Assert.Equal("call", _database.GetOpenItems().Single().Title);
    }

    [Fact]
    public void A_sent_repeating_reminder_comes_back()
    {
        Store(new SortedItem(ItemKind.Reminder, "call", InDays(-1), null, RepeatMinutes: 30));
        var now = DateTime.Now;

        _database.SettleSentReminder(_database.GetDueReminders(now).Single(), now);

        Assert.Empty(_database.GetDueReminders(now.AddMinutes(29)));
        Assert.Single(_database.GetDueReminders(now.AddMinutes(30)));
    }

    [Fact]
    public void A_sent_recurring_reminder_moves_to_its_next_date()
    {
        Store(new SortedItem(ItemKind.Reminder, "water the plants", InDays(0), null, new TimeOnly(0, 1), Recurs: new Recurrence(RecurrenceUnit.Day)));
        var now = InDays(0).ToDateTime(new TimeOnly(0, 2));

        _database.SettleSentReminder(_database.GetDueReminders(DateTime.Now).Single(), now);

        Assert.Equal(InDays(1), _database.GetOpenItems().Single().DueDate);
        Assert.Single(_database.GetDueReminders(InDays(1).ToDateTime(new TimeOnly(0, 1))));
    }

    [Fact]
    public async Task Breaking_down_again_replaces_open_steps()
    {
        var paper = Store(new SortedItem(ItemKind.Task, "paper", null, null));
        await Run(new ItemChange(ItemSource.Capture, paper.Id, ChangeAction.BreakDown, Steps: [new PlannedStep("Old", null)]));

        await Run(new ItemChange(ItemSource.Capture, paper.Id, ChangeAction.BreakDown, Steps: [new PlannedStep("New", null)]));

        Assert.Equal(["New"], ItemGroups.StepsOf(paper, _database.GetOpenItems()).Select(step => step.Title));
    }

    [Fact]
    public async Task A_step_can_be_finished_but_not_broken_down_further()
    {
        var paper = Store(new SortedItem(ItemKind.Task, "paper", null, null));
        await Run(new ItemChange(ItemSource.Capture, paper.Id, ChangeAction.BreakDown, Steps: [new PlannedStep("Start", null)]));
        var step = ItemGroups.StepsOf(paper, _database.GetOpenItems()).Single();

        var refused = await Run(new ItemChange(ItemSource.Capture, step.Id, ChangeAction.BreakDown, Steps: [new PlannedStep("Smaller", null)]));
        await Run(new ItemChange(ItemSource.Capture, step.Id, ChangeAction.Done));

        Assert.False(refused.Changes.Single().Applied);
        Assert.Equal("paper", _database.GetOpenItems().Single().Title);
    }

    [Fact]
    public async Task Calendar_steps_replace_its_nudges_and_end_with_the_assignment()
    {
        var paper = StoreEvent("Paper 2", InDays(4));
        await new AssignmentNudges(_database, new FakePlanner(_ => [])).PlanAsync(DateTime.Now);
        var at = InDays(2).ToDateTime(new TimeOnly(19, 0));

        await Run(new ItemChange(ItemSource.Calendar, paper.Id, ChangeAction.BreakDown, Steps: [new PlannedStep("Start", at)]));

        Assert.Equal("Start", _database.GetDueReminders(InDays(3).ToDateTime(new TimeOnly(20, 0))).Single().Item.Title);
        Assert.Equal("feed:" + paper.Id, ItemGroups.StepsOf(paper, _database.GetOpenItems()).Single().ParentRef);

        _database.SetDone(paper, true);

        Assert.Empty(_database.GetOpenItems());
    }

    [Fact]
    public async Task A_new_item_can_arrive_with_steps()
    {
        var service = new CaptureService(_database, new FakeSorter(_ => new CaptureResult(
            [new SortedItem(ItemKind.Deadline, "lab report", InDays(3), null, Steps: [new PlannedStep("Open the data", null)])])));

        service.Capture("break down my lab report");
        await service.ProcessPendingAsync();

        var parent = _database.GetOpenItems().Single(item => item.Title == "lab report");
        Assert.Equal("Open the data", ItemGroups.StepsOf(parent, _database.GetOpenItems()).Single().Title);
    }

    [Fact]
    public void Parser_reads_steps_on_changes_and_items()
    {
        var result = ClaudeCliSorter.ParseResult("""
            {"is_error": false, "structured_output": {"answer": null,
             "items": [{"kind": "task", "title": "lab", "due_date": null, "amount_cents": null, "steps": [{"title": "Begin", "at": null}]}],
             "changes": [{"ref": "feed:2", "action": "break_down", "title": null, "due_date": null, "due_time": null,
                          "steps": [{"title": "Read it", "at": "2026-11-03T19:00"}, {"title": " ", "at": null}]}]}}
            """);

        Assert.Equal("Begin", result!.Items.Single().Steps!.Single().Title);
        var change = result.Changes!.Single();
        Assert.Equal(ChangeAction.BreakDown, change.Action);
        Assert.Equal(new PlannedStep("Read it", new DateTime(2026, 11, 3, 19, 0, 0)), change.Steps!.Single());
    }

    [Fact]
    public async Task Giving_a_task_a_reminder_date_adds_the_alert_it_needs()
    {
        var call = Store(new SortedItem(ItemKind.Reminder, "call", null, null));

        await Run(new ItemChange(ItemSource.Capture, call.Id, ChangeAction.Update, null, InDays(2), new TimeOnly(10, 0)));

        Assert.Empty(_database.GetDueReminders(DateTime.Now));
        Assert.Single(_database.GetDueReminders(InDays(2).ToDateTime(new TimeOnly(10, 0))));
    }

    [Fact]
    public async Task Renaming_keeps_the_date()
    {
        var paper = Store(new SortedItem(ItemKind.Deadline, "paper", InDays(3), null));

        await Run(new ItemChange(ItemSource.Capture, paper.Id, ChangeAction.Update, "history paper"));

        var renamed = _database.GetOpenItems().Single();
        Assert.Equal("history paper", renamed.Title);
        Assert.Equal(InDays(3), renamed.DueDate);
    }

    [Fact]
    public async Task Calendar_items_can_be_finished_but_not_renamed()
    {
        var paper = StoreEvent("Paper 2", InDays(3));

        var refused = await Run(new ItemChange(ItemSource.Calendar, paper.Id, ChangeAction.Update, "Paper Two"));

        Assert.False(refused.Changes.Single().Applied);
        Assert.Contains("comes from the calendar", ItemGroups.OutcomeSummary(refused));
        Assert.Equal("Paper 2", _database.GetOpenItems().Single().Title);

        await Run(new ItemChange(ItemSource.Calendar, paper.Id, ChangeAction.Done));

        Assert.Empty(_database.GetOpenItems());
    }

    [Fact]
    public void Parser_reads_changes_and_answer_and_skips_bad_refs()
    {
        var result = ClaudeCliSorter.ParseResult("""
            {"is_error": false, "structured_output": {"items": [], "answer": "Open the outline.", "changes": [
                {"ref": "item:4", "action": "update", "title": null, "due_date": "2026-11-06", "due_time": null},
                {"ref": "feed:7", "action": "stop_reminders", "title": null, "due_date": null, "due_time": null},
                {"ref": "bogus", "action": "done", "title": null, "due_date": null, "due_time": null},
                {"ref": "item:5", "action": "explode", "title": null, "due_date": null, "due_time": null}
            ]}}
            """);

        Assert.Equal("Open the outline.", result!.Answer);
        Assert.Equal(
            [
                new ItemChange(ItemSource.Capture, 4, ChangeAction.Update, null, new DateOnly(2026, 11, 6)),
                new ItemChange(ItemSource.Calendar, 7, ChangeAction.StopReminders),
            ],
            result.Changes!);
    }

    [Fact]
    public async Task Assignments_get_planned_nudges_once()
    {
        var paper = StoreEvent("Paper 2", InDays(4));
        var at = DateTime.Now.AddDays(2);
        var calls = 0;
        var nudges = new AssignmentNudges(_database, new FakePlanner(events =>
        {
            calls++;
            return [new EventNudges(events.Single().Id, [new PlannedReminder(at, "Start the outline")])];
        }));

        await nudges.PlanAsync(DateTime.Now);
        await nudges.PlanAsync(DateTime.Now);

        Assert.Equal(1, calls);
        var due = _database.GetDueReminders(at.AddMinutes(1)).Single();
        Assert.Equal("Start the outline", due.Message);
        Assert.Equal(ItemSource.Calendar, due.Item.Source);
        Assert.Equal(paper.Id, due.Item.Id);
    }

    [Fact]
    public async Task An_assignment_the_planner_missed_still_gets_the_evening_before()
    {
        StoreEvent("Paper 2", InDays(4));

        await new AssignmentNudges(_database, new FakePlanner(_ => [])).PlanAsync(DateTime.Now);

        var evening = InDays(3).ToDateTime(new TimeOnly(19, 0));
        Assert.Empty(_database.GetDueReminders(evening.AddMinutes(-1)));
        Assert.Null(_database.GetDueReminders(evening).Single().Message);
    }

    [Fact]
    public async Task A_failed_plan_is_tried_again_and_reports_the_error()
    {
        StoreEvent("Paper 2", InDays(4));
        var nudges = new AssignmentNudges(_database, new FakePlanner(_ => throw new InvalidOperationException("offline")));

        await nudges.PlanAsync(DateTime.Now);

        Assert.Equal("offline", nudges.LastError);
        Assert.Single(_database.GetEventsNeedingNudges(InDays(0), InDays(14), 10));
    }

    [Fact]
    public async Task Skipped_assignments_stay_quiet_even_if_the_feed_moves_them()
    {
        var paper = StoreEvent("Paper 2", InDays(4));
        await new AssignmentNudges(_database, new FakePlanner(_ => [])).PlanAsync(DateTime.Now);

        _database.SkipNudges(paper.Id);
        _database.ReplaceCalendarEvents([new CalendarEvent("Paper 2", "Paper 2", "CS 101", InDays(6), null)]);

        Assert.Empty(_database.GetDueReminders(DateTime.Now.AddDays(5)));
        Assert.Empty(_database.GetEventsNeedingNudges(InDays(0), InDays(14), 10));
    }

    [Fact]
    public async Task A_feed_date_change_replans_the_nudges()
    {
        StoreEvent("Paper 2", InDays(4));
        await new AssignmentNudges(_database, new FakePlanner(_ => [])).PlanAsync(DateTime.Now);

        _database.ReplaceCalendarEvents([new CalendarEvent("Paper 2", "Paper 2", "CS 101", InDays(6), null)]);

        Assert.Single(_database.GetEventsNeedingNudges(InDays(0), InDays(14), 10));
        Assert.Empty(_database.GetDueReminders(InDays(5).ToDateTime(new TimeOnly(0, 0))));
    }

    [Fact]
    public async Task Nudges_for_a_past_or_finished_assignment_stay_quiet()
    {
        var paper = StoreEvent("Paper 2", InDays(4));
        await new AssignmentNudges(_database, new FakePlanner(_ => [])).PlanAsync(DateTime.Now);

        Assert.Empty(_database.GetDueReminders(InDays(5).ToDateTime(new TimeOnly(9, 0))));

        _database.SetDone(paper, true);

        Assert.Empty(_database.GetDueReminders(InDays(3).ToDateTime(new TimeOnly(20, 0))));
    }

    [Fact]
    public void Nudge_plans_are_trimmed_to_useful_times()
    {
        var now = new DateTime(2026, 11, 1, 12, 0, 0);
        var due = new DateTime(2026, 11, 6, 23, 59, 0);
        PlannedReminder At(int day) => new(new DateTime(2026, 11, day, 10, 0, 0), "x");

        var kept = ReminderPlan.ForAssignment(due, [At(1), At(2), At(3), At(4), At(5), At(7)], new DateTime(2026, 11, 1, 12, 0, 0));

        Assert.Equal([At(2), At(3), At(4)], kept);
        Assert.Empty(ReminderPlan.ForAssignment(now.AddHours(2), null, now));
    }
}
