using Microsoft.Data.Sqlite;
using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Theme;

namespace Waypoint.Core.Tests;

public sealed class RowActionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public RowActionTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

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

    private StoredItem Find(string title) => _database.GetOpenItems().Single(item => item.Title == title);

    private static ItemChange Change(StoredItem item, ChangeAction action, string? title = null) =>
        new(item.Source, item.Id, action, title);

    [Fact]
    public void Rescheduling_an_item_moves_it_and_its_reminders()
    {
        var at = DateTime.Now.AddDays(1);
        var paper = Store(new SortedItem(ItemKind.Task, "paper", Today.AddDays(2), null, null, [new PlannedReminder(at, "Start")]));

        var applied = _database.RescheduleItem(paper, Today.AddDays(5), null, DateTime.Now);

        Assert.True(applied.Applied);
        Assert.Equal(Today.AddDays(5), Find("paper").DueDate);
        Assert.Empty(_database.GetDueReminders(at.AddMinutes(1)));
        Assert.Equal("Start", _database.GetDueReminders(at.AddDays(3).AddMinutes(1)).Single().Message);
    }

    [Fact]
    public void Rescheduling_a_feed_item_lasts_through_a_sync()
    {
        var quiz = StoreEvent("quiz", Today.AddDays(2));

        _database.RescheduleItem(quiz, Today.AddDays(4), null, DateTime.Now);
        _database.ReplaceCalendarEvents([new CalendarEvent("quiz", "quiz", "CS 101", Today.AddDays(2), null)]);

        Assert.Equal(Today.AddDays(4), Find("quiz").DueDate);
        Assert.True(_database.IsMovedFromFeed(Find("quiz")));
    }

    [Fact]
    public void Use_feed_date_is_only_true_for_a_moved_event_and_puts_it_back()
    {
        var quiz = StoreEvent("quiz", Today.AddDays(2));
        Assert.False(_database.IsMovedFromFeed(quiz));

        _database.RescheduleItem(quiz, Today.AddDays(4), null, DateTime.Now);
        var applied = _database.ChangeItem(Change(Find("quiz"), ChangeAction.UseFeedDate), DateTime.Now);

        Assert.True(applied.Applied);
        Assert.Equal(Today.AddDays(2), Find("quiz").DueDate);
        Assert.False(_database.IsMovedFromFeed(Find("quiz")));
    }

    [Fact]
    public void Rescheduling_a_recurring_item_moves_only_this_occurrence()
    {
        var chore = Store(new SortedItem(ItemKind.Reminder, "chore", Today.AddDays(1), null, new TimeOnly(9, 0), Recurs: new Recurrence(RecurrenceUnit.Week)));

        _database.RescheduleItem(chore, Today.AddDays(3), new TimeOnly(15, 0), DateTime.Now);
        Assert.Equal(Today.AddDays(3), Find("chore").DueDate);
        Assert.Equal(9, Find("chore").DueAt!.Value.Hour);

        _database.SetDone(Find("chore"), true);

        Assert.Equal(Today.AddDays(8), Find("chore").DueDate);
    }

    [Fact]
    public void A_second_move_still_counts_from_the_usual_date()
    {
        var chore = Store(new SortedItem(ItemKind.Reminder, "chore", Today.AddDays(1), null, null, Recurs: new Recurrence(RecurrenceUnit.Week)));

        _database.RescheduleItem(chore, Today.AddDays(3), null, DateTime.Now);
        _database.RescheduleItem(Find("chore"), Today.AddDays(4), null, DateTime.Now);
        _database.SetDone(Find("chore"), true);

        Assert.Equal(Today.AddDays(8), Find("chore").DueDate);

        _database.SetDone(Find("chore"), true);
        Assert.Equal(Today.AddDays(15), Find("chore").DueDate);
    }

    [Fact]
    public void Undoing_a_finish_on_a_moved_recurring_item_keeps_its_usual_date()
    {
        var chore = Store(new SortedItem(ItemKind.Reminder, "chore", Today.AddDays(1), null, null, Recurs: new Recurrence(RecurrenceUnit.Week)));
        _database.RescheduleItem(chore, Today.AddDays(3), null, DateTime.Now);
        _database.SetDone(Find("chore"), true);

        Assert.True(_database.ReopenEntry(_database.GetHistory().First(entry => entry.Title == "chore")));
        Assert.Equal(Today.AddDays(3), Find("chore").DueDate);

        _database.SetDone(Find("chore"), true);
        Assert.Equal(Today.AddDays(8), Find("chore").DueDate);
    }

    [Fact]
    public void Canceling_a_recurring_item_ends_it_and_can_be_undone_from_history()
    {
        var chore = Store(new SortedItem(ItemKind.Reminder, "chore", Today, null, null, Recurs: new Recurrence(RecurrenceUnit.Day)));

        Assert.True(_database.ChangeItem(Change(chore, ChangeAction.Cancel), DateTime.Now).Applied);
        Assert.DoesNotContain(_database.GetOpenItems(), item => item.Title == "chore");

        var entry = _database.SearchHistory("chore", 10).First();
        Assert.True(_database.ReopenEntry(entry));
        Assert.Contains(_database.GetOpenItems(), item => item.Title == "chore");
    }

    [Fact]
    public void Renaming_changes_a_capture_item_but_not_a_feed_one()
    {
        var paper = Store(new SortedItem(ItemKind.Task, "paper", null, null));
        var quiz = StoreEvent("quiz", Today.AddDays(2));

        Assert.True(_database.ChangeItem(Change(paper, ChangeAction.Update, "essay"), DateTime.Now).Applied);
        Assert.False(_database.ChangeItem(Change(quiz, ChangeAction.Update, "other"), DateTime.Now).Applied);

        Assert.Contains(_database.GetOpenItems(), item => item.Title == "essay");
        Assert.Contains(_database.GetOpenItems(), item => item.Title == "quiz");
    }

    [Fact]
    public void Stop_reminders_keeps_the_item()
    {
        var at = DateTime.Now.AddMinutes(-1);
        var paper = Store(new SortedItem(ItemKind.Task, "paper", Today.AddDays(1), null, null, [new PlannedReminder(at, "Now")]));

        _database.ChangeItem(Change(paper, ChangeAction.StopReminders), DateTime.Now);

        Assert.Empty(_database.GetDueReminders(DateTime.Now));
        Assert.Contains(_database.GetOpenItems(), item => item.Title == "paper");
    }

    [Fact]
    public void Break_down_needs_the_sorter_so_a_direct_change_refuses_it()
    {
        var paper = Store(new SortedItem(ItemKind.Task, "paper", null, null));

        Assert.False(_database.ChangeItem(Change(paper, ChangeAction.BreakDown), DateTime.Now).Applied);
    }

    [Fact]
    public void A_direct_change_logs_to_history_like_a_typed_one()
    {
        var paper = Store(new SortedItem(ItemKind.Task, "paper", null, null));

        _database.ChangeItem(Change(paper, ChangeAction.Cancel), DateTime.Now);

        Assert.Equal("Canceled", _database.SearchHistory("paper", 10).First().Label);
    }

    [Fact]
    public void Open_capture_item_offers_the_whole_menu()
    {
        var item = new StoredItem(1, ItemKind.Task, "paper", Today, null, false);

        Assert.Equal(
            [RowAction.Done, RowAction.Start, RowAction.SmallerStep, RowAction.BreakDown, RowAction.Reschedule, RowAction.Rename, RowAction.StopReminders, RowAction.Copy, RowAction.Cancel],
            RowActions.For(item, Today, movedFromFeed: false));
    }

    [Fact]
    public void Feed_item_cant_be_renamed_and_offers_its_date_back_only_when_moved()
    {
        var item = new StoredItem(1, ItemKind.Deadline, "quiz", Today, null, false, ItemSource.Calendar);

        Assert.DoesNotContain(RowAction.Rename, RowActions.For(item, Today, movedFromFeed: true));
        Assert.DoesNotContain(RowAction.UseFeedDate, RowActions.For(item, Today, movedFromFeed: false));
        Assert.Contains(RowAction.UseFeedDate, RowActions.For(item, Today, movedFromFeed: true));
    }

    [Fact]
    public void Finished_item_can_be_reopened_deleted_or_copied() =>
        Assert.Equal(
            [RowAction.Reopen, RowAction.Copy, RowAction.Delete],
            RowActions.For(new StoredItem(1, ItemKind.Task, "paper", Today, null, true), Today, movedFromFeed: false));

    [Fact]
    public void A_step_cant_be_broken_down()
    {
        var step = new StoredItem(2, ItemKind.Task, "step", null, null, false, ParentRef: "item:1");

        Assert.DoesNotContain(RowAction.BreakDown, RowActions.For(step, Today, movedFromFeed: false));
        Assert.Contains(RowAction.Reschedule, RowActions.For(step, Today, movedFromFeed: false));
    }

    [Fact]
    public void A_reopened_past_feed_item_cant_be_started_because_it_isnt_an_open_item()
    {
        var item = new StoredItem(1, ItemKind.Deadline, "old", Today.AddDays(-3), null, false, ItemSource.Calendar);

        Assert.DoesNotContain(RowAction.Start, RowActions.For(item, Today, movedFromFeed: false));
    }

    [Fact]
    public void Finished_text_is_grayer_than_secondary_text_but_still_readable()
    {
        foreach (var color in new[] { ThemePalette.FinishedText, ThemePalette.FinishedDetail })
        {
            Assert.True(ThemePalette.Contrast(color, ThemePalette.Background) >= 3, color);
            Assert.True(ThemePalette.Contrast(color, ThemePalette.Background) < ThemePalette.Contrast(Themes.Default.SecondaryText, ThemePalette.Background), color);
        }
    }
}
