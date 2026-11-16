using Microsoft.Data.Sqlite;
using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Files;
using Waypoint.Core.Theme;

namespace Waypoint.Core.Tests;

public sealed class FinishedItemsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public FinishedItemsTests() => _database = new WaypointDatabase(_path);

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

    private static DateOnly InDays(int days) => DateOnly.FromDateTime(DateTime.Now).AddDays(days);

    private StoredItem Store(SortedItem item)
    {
        _database.CompleteCapture(_database.AddCapture("x"), [item]);
        return _database.GetOpenItems().Single(stored => stored.Title == item.Title);
    }

    private StoredItem StoreEvent(string title, DateOnly due, DateTimeOffset? dueAt = null)
    {
        _database.ReplaceCalendarEvents([new CalendarEvent(title, title, "CS 101", due, dueAt)]);
        return _database.GetOpenItems().Single(stored => stored.Title == title);
    }

    private async Task<CaptureOutcome> Run(params ItemChange[] changes)
    {
        CaptureOutcome? outcome = null;
        var service = new CaptureService(_database, new FakeSorter(_ => new CaptureResult([], changes)));
        service.Sorted += result => outcome = result;
        service.Capture("note");
        await service.ProcessPendingAsync();
        return outcome!;
    }

    private void SetDoneAt(long id, DateTime at)
    {
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE items SET done_at = $at WHERE id = $id";
        command.Parameters.AddWithValue("$at", at.ToString("yyyy-MM-ddTHH:mm:ss"));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private IReadOnlyList<string> FinishedTitles() =>
        _database.GetFinishedForList(DateTime.Now).Select(item => item.Title).ToList();

    [Fact]
    public void A_finished_item_stays_on_the_list_but_never_counts_as_open()
    {
        var paper = Store(new SortedItem(ItemKind.Deadline, "paper", InDays(3), null));

        _database.SetDone(paper, true);

        Assert.Empty(_database.GetOpenItems());
        Assert.True(_database.GetFinishedForList(DateTime.Now).Single().Done);
    }

    [Fact]
    public void An_undated_item_stays_through_the_day_after_it_was_finished()
    {
        var chore = Store(new SortedItem(ItemKind.Task, "chore", null, null));
        _database.SetDone(chore, true);

        SetDoneAt(chore.Id, InDays(-1).ToDateTime(new TimeOnly(0, 5)));
        Assert.Single(FinishedTitles());

        SetDoneAt(chore.Id, InDays(-2).ToDateTime(new TimeOnly(23, 55)));
        Assert.Empty(FinishedTitles());
    }

    [Fact]
    public void A_date_still_ahead_keeps_it_however_long_ago_it_was_finished()
    {
        var paper = Store(new SortedItem(ItemKind.Deadline, "paper", InDays(0), null));
        _database.SetDone(paper, true);

        SetDoneAt(paper.Id, InDays(-9).ToDateTime(TimeOnly.MinValue));

        Assert.Single(FinishedTitles());
    }

    [Fact]
    public void Canceled_items_expenses_notes_and_shopping_stay_off_the_list()
    {
        var laundry = Store(new SortedItem(ItemKind.Task, "laundry", null, null));
        var milk = Store(new SortedItem(ItemKind.Shopping, "milk", null, null));
        _database.CompleteCapture(_database.AddCapture("x"), [new SortedItem(ItemKind.Expense, "lunch", null, 500)]);
        var note = Store(new SortedItem(ItemKind.Note, "idea", null, null));

        _database.SetDone(milk, true);
        _database.SetDone(note, true);
        _database.CompleteCapture(_database.AddCapture("x"), [], [new ItemChange(ItemSource.Capture, laundry.Id, ChangeAction.Cancel)]);

        Assert.Empty(FinishedTitles());
    }

    [Fact]
    public void A_repeating_item_that_moved_on_shows_once()
    {
        var stretch = Store(new SortedItem(ItemKind.Reminder, "stretch", InDays(0), null, Recurs: new Recurrence(RecurrenceUnit.Day)));

        _database.SetDone(stretch, true);

        Assert.Empty(FinishedTitles());
        Assert.Single(_database.GetOpenItems());
    }

    [Fact]
    public void Steps_of_a_closed_parent_go_but_steps_of_an_open_one_stay()
    {
        var paper = Store(new SortedItem(ItemKind.Deadline, "paper", InDays(3), null, Steps: [new PlannedStep("outline", null), new PlannedStep("draft", null)]));
        var outline = _database.GetOpenItems().Single(item => item.Title == "outline");

        _database.SetDone(outline, true);
        Assert.Contains("outline", FinishedTitles());

        _database.SetDone(paper, true);
        Assert.DoesNotContain("outline", FinishedTitles());
        Assert.Contains("paper", FinishedTitles());
    }

    [Fact]
    public void Finished_feed_events_stay_and_a_past_one_only_if_just_finished()
    {
        var paper = StoreEvent("paper", InDays(1));
        _database.SetDone(paper, true);
        Assert.Single(FinishedTitles());

        var late = StoreEvent("late", InDays(0));
        _database.SetDone(late, true);
        _database.ReplaceCalendarEvents([new CalendarEvent("late", "late", null, InDays(-1), null)]);
        Assert.Single(FinishedTitles());
    }

    [Fact]
    public void An_unchecked_past_feed_event_stays_listed_open_until_rechecked()
    {
        var late = StoreEvent("late", InDays(0));
        _database.SetDone(late, true);
        _database.ReplaceCalendarEvents([new CalendarEvent("late", "late", null, InDays(-1), null)]);

        _database.SetDone(_database.GetFinishedForList(DateTime.Now).Single(), false);

        var listed = _database.GetFinishedForList(DateTime.Now).Single();
        Assert.False(listed.Done);
        Assert.Equal("Overdue", ItemGroups.DayLabel(listed.DueDate, InDays(0)));
        Assert.Empty(_database.GetOpenItems());
        Assert.Empty(_database.GetDueReminders(DateTime.Now));

        _database.SetDone(listed, true);
        var rechecked = _database.GetFinishedForList(DateTime.Now).Single();
        Assert.True(rechecked.Done);

        _database.SetDone(rechecked, false);
        Assert.False(_database.GetFinishedForList(DateTime.Now).Single().Done);
    }

    [Fact]
    public void Finished_rows_sort_after_open_ones_in_their_day()
    {
        var done = new StoredItem(1, ItemKind.Task, "done", InDays(0), null, true);
        var open = new StoredItem(2, ItemKind.Task, "open", InDays(0), null, false);
        var later = new StoredItem(3, ItemKind.Task, "later", InDays(2), null, false);

        var titles = ItemGroups.WithFinished([open, later], [done]).Select(item => item.Title);

        Assert.Equal(["open", "done", "later"], titles);
    }

    [Fact]
    public void A_finished_row_gets_the_plain_mark()
    {
        var done = new StoredItem(1, ItemKind.Exam, "exam", InDays(0), null, true);

        Assert.Equal(ItemMark.Personal, ItemMarks.For(done, InDays(0)));
    }

    [Fact]
    public void Unchecking_reopens_and_logs_it_for_items_and_feed_events()
    {
        var task = Store(new SortedItem(ItemKind.Task, "task", InDays(1), null));
        var paper = StoreEvent("paper", InDays(1));
        _database.SetDone(task, true);
        _database.SetDone(paper, true);

        foreach (var item in _database.GetFinishedForList(DateTime.Now))
        {
            _database.SetDone(item, false);
        }

        Assert.Equal(2, _database.GetOpenItems().Count);
        Assert.Empty(FinishedTitles());
        Assert.Empty(_database.GetHistory());
    }

    [Fact]
    public async Task History_rows_can_reopen_finished_canceled_and_repeating_items()
    {
        var task = Store(new SortedItem(ItemKind.Task, "task", null, null));
        var canceled = Store(new SortedItem(ItemKind.Task, "canceled", null, null));
        var stretch = Store(new SortedItem(ItemKind.Reminder, "stretch", InDays(0), null, Recurs: new Recurrence(RecurrenceUnit.Day)));
        _database.SetDone(task, true);
        _database.SetDone(stretch, true);
        await Run(new ItemChange(ItemSource.Capture, canceled.Id, ChangeAction.Cancel));

        foreach (var entry in _database.GetHistory())
        {
            Assert.True(_database.CanReopen(entry));
            Assert.True(_database.ReopenEntry(entry));
        }

        var open = _database.GetOpenItems();
        Assert.Equal(3, open.Count);
        Assert.Equal(InDays(0), open.Single(item => item.Title == "stretch").DueDate);
    }

    [Fact]
    public void An_older_occurrence_or_an_entry_whose_item_left_the_feed_cant_be_reopened()
    {
        var stretch = Store(new SortedItem(ItemKind.Reminder, "stretch", InDays(-2), null, Recurs: new Recurrence(RecurrenceUnit.Day)));
        _database.SetDone(stretch, true);
        _database.SetDone(_database.GetOpenItems().Single(), true);
        var entries = _database.GetHistory();
        Assert.Equal([true, false], entries.Select(_database.CanReopen));
        Assert.False(_database.ReopenEntry(entries[1]));

        var paper = StoreEvent("paper", InDays(1));
        _database.SetDone(paper, true);
        var entry = _database.GetHistory()[0];
        _database.ReplaceCalendarEvents([]);
        Assert.False(_database.CanReopen(entry));
    }

    [Fact]
    public async Task A_feed_date_can_be_moved_and_lasts_through_syncs()
    {
        var plan = StoreEvent("Group Project Plan", InDays(1));

        var moved = await Run(new ItemChange(ItemSource.Calendar, plan.Id, ChangeAction.Update, null, InDays(4)));

        Assert.True(moved.Changes.Single().Applied);
        Assert.Equal(InDays(4), _database.GetOpenItems().Single().DueDate);

        _database.ReplaceCalendarEvents([new CalendarEvent("Group Project Plan", "Group Project Plan", "CS 101", InDays(2), null)]);
        Assert.Equal(InDays(4), _database.GetOpenItems().Single().DueDate);
    }

    [Fact]
    public async Task A_moved_event_keeps_its_time_and_can_take_a_new_one()
    {
        var at = new DateTimeOffset(InDays(1).ToDateTime(new TimeOnly(17, 0)));
        var chat = StoreEvent("chat", InDays(1), at);

        await Run(new ItemChange(ItemSource.Calendar, chat.Id, ChangeAction.Update, null, InDays(3)));
        var moved = _database.GetOpenItems().Single();
        Assert.Equal(InDays(3).ToDateTime(new TimeOnly(17, 0)), moved.DueAt!.Value.LocalDateTime);

        await Run(new ItemChange(ItemSource.Calendar, chat.Id, ChangeAction.Update, null, null, new TimeOnly(9, 30)));
        moved = _database.GetOpenItems().Single();
        Assert.Equal(InDays(3).ToDateTime(new TimeOnly(9, 30)), moved.DueAt!.Value.LocalDateTime);
    }

    [Fact]
    public async Task Moving_an_event_later_keeps_it_open_past_its_feed_date()
    {
        var plan = StoreEvent("plan", InDays(1));
        await Run(new ItemChange(ItemSource.Calendar, plan.Id, ChangeAction.Update, null, InDays(5)));

        _database.ReplaceCalendarEvents([new CalendarEvent("plan", "plan", null, InDays(-3), null)]);

        Assert.Equal(InDays(5), _database.GetOpenItems().Single().DueDate);
    }

    [Fact]
    public async Task Use_the_feed_date_clears_the_move()
    {
        var plan = StoreEvent("plan", InDays(1));
        await Run(new ItemChange(ItemSource.Calendar, plan.Id, ChangeAction.Update, null, InDays(5)));

        var outcome = await Run(new ItemChange(ItemSource.Calendar, plan.Id, ChangeAction.UseFeedDate));

        Assert.Contains("back on its calendar date", ItemGroups.OutcomeSummary(outcome));
        Assert.Equal(InDays(1), _database.GetOpenItems().Single().DueDate);

        var again = await Run(new ItemChange(ItemSource.Calendar, plan.Id, ChangeAction.UseFeedDate));
        Assert.False(again.Changes.Single().Applied);
    }

    [Fact]
    public async Task Moving_an_event_shifts_its_nudges_and_steps()
    {
        var plan = StoreEvent("plan", InDays(3));
        var nudge = InDays(2).ToDateTime(new TimeOnly(18, 0));
        _database.SaveNudges([new EventNudges(plan.Id, [new PlannedReminder(nudge, "start")])]);
        await Run(new ItemChange(ItemSource.Calendar, plan.Id, ChangeAction.BreakDown, Steps: [new PlannedStep("outline", InDays(2).ToDateTime(new TimeOnly(10, 0)))]));

        await Run(new ItemChange(ItemSource.Calendar, plan.Id, ChangeAction.Update, null, InDays(5)));

        Assert.Empty(_database.GetDueReminders(InDays(3).ToDateTime(new TimeOnly(23, 0))));
        Assert.Contains(_database.GetDueReminders(InDays(4).ToDateTime(new TimeOnly(19, 0))), reminder => reminder.Item.Title == "outline");
    }

    [Fact]
    public async Task Moving_an_event_past_its_nudges_sends_it_to_be_replanned()
    {
        var plan = StoreEvent("plan", InDays(6));
        _database.SaveNudges([new EventNudges(plan.Id, [new PlannedReminder(DateTime.Now.AddHours(1), "start")])]);

        await Run(new ItemChange(ItemSource.Calendar, plan.Id, ChangeAction.Update, null, InDays(1)));

        var waiting = _database.GetEventsNeedingNudges(InDays(0), InDays(14), 10);
        Assert.Equal(InDays(1), waiting.Single().DueDate);
    }

    [Fact]
    public async Task A_file_cant_move_a_feed_date()
    {
        var plan = StoreEvent("plan", InDays(1));
        var service = new CaptureService(_database, new FakeSorter(_ =>
            new CaptureResult([], [new ItemChange(ItemSource.Calendar, plan.Id, ChangeAction.Update, null, InDays(5))])));

        service.CaptureFile(new FileText("plan.txt", "move the plan to next week"));
        await service.ProcessPendingAsync();

        Assert.Equal(InDays(1), _database.GetOpenItems().Single(item => item.Title == "plan").DueDate);
    }

    [Fact]
    public void Move_and_reset_are_in_the_sorter_schema_and_parser()
    {
        Assert.True(Enum.TryParse<ChangeAction>("use_feed_date".Replace("_", ""), ignoreCase: true, out var action));
        Assert.Equal(ChangeAction.UseFeedDate, action);
    }
}
