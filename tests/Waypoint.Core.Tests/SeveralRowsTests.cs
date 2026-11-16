using Microsoft.Data.Sqlite;
using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

public sealed class SeveralRowsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public SeveralRowsTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private StoredItem Store(SortedItem item)
    {
        _database.CompleteCapture(_database.AddCapture("x"), [item]);
        return Find(item.Title);
    }

    private StoredItem Find(string title) => _database.GetOpenItems().Single(item => item.Title == title);

    private bool IsOpen(string title) => _database.GetOpenItems().Any(item => item.Title == title);

    [Fact]
    public void Several_rows_offer_only_what_every_one_of_them_would()
    {
        var open = new StoredItem(1, ItemKind.Task, "paper", Today, null, false);
        var other = new StoredItem(2, ItemKind.Task, "laundry", Today.AddDays(-2), null, false);
        var finished = new StoredItem(3, ItemKind.Task, "dishes", Today, null, true);

        Assert.Equal([RowAction.Done, RowAction.Reschedule, RowAction.Delete], RowActions.ForSeveral([open, other], Today));
        Assert.Equal([RowAction.Delete], RowActions.ForSeveral([open, finished], Today));
        Assert.Empty(RowActions.ForSeveral([], Today));
    }

    [Fact]
    public void Finishing_several_and_undoing_puts_them_all_back()
    {
        var paper = Store(new SortedItem(ItemKind.Task, "paper", Today, null));
        var stretch = Store(new SortedItem(ItemKind.Reminder, "stretch", Today, null, Recurs: new Recurrence(RecurrenceUnit.Day)));

        var result = SeveralRows.Finish(_database, [paper, stretch]);

        Assert.Equal(2, result.Changed);
        Assert.False(IsOpen("paper"));
        Assert.Equal(Today.AddDays(1), Find("stretch").DueDate);

        result.Undo!();

        Assert.True(IsOpen("paper"));
        Assert.Equal(Today, Find("stretch").DueDate);
    }

    [Fact]
    public void Dropping_several_and_undoing_puts_them_all_back()
    {
        var paper = Store(new SortedItem(ItemKind.Task, "paper", Today.AddDays(-3), null));
        _database.ReplaceCalendarEvents([new CalendarEvent("quiz", "quiz", "CS 101", Today.AddDays(1), null)]);
        var quiz = Find("quiz");

        var result = SeveralRows.Drop(_database, [paper, quiz], DateTime.Now);

        Assert.Equal(2, result.Changed);
        Assert.False(IsOpen("paper"));
        Assert.False(IsOpen("quiz"));

        result.Undo!();

        Assert.True(IsOpen("paper"));
        Assert.True(IsOpen("quiz"));
    }

    [Fact]
    public void Deleting_several_keeps_open_ones_in_history_and_undo_brings_every_one_back()
    {
        var paper = Store(new SortedItem(ItemKind.Task, "paper", Today, null));
        var dishes = Store(new SortedItem(ItemKind.Task, "dishes", Today, null));
        _database.SetDone(dishes, true);
        dishes = dishes with { Done = true };

        var result = SeveralRows.Remove(new Waypoint.Core.Remote.LocalToDoData(_database), [paper, dishes], DateTime.Now);

        Assert.Equal(2, result.Changed);
        Assert.False(IsOpen("paper"));
        Assert.Equal("Canceled", _database.SearchHistory("paper", 10).First().Label);
        Assert.DoesNotContain(_database.GetHistory(), entry => entry.Title == "dishes");

        result.Undo!();

        Assert.True(IsOpen("paper"));
        Assert.Contains(_database.GetHistory(), entry => entry.Title == "dishes");
    }

    [Fact]
    public void Moving_several_keeps_each_time_and_undo_moves_each_back()
    {
        var paper = Store(new SortedItem(ItemKind.Task, "paper", Today, null, new TimeOnly(9, 0)));
        var laundry = Store(new SortedItem(ItemKind.Task, "laundry", Today.AddDays(-2), null));

        var result = SeveralRows.Move(_database, [paper, laundry], Today.AddDays(3), DateTime.Now);

        Assert.Equal(2, result.Changed);
        Assert.Equal(Today.AddDays(3), Find("paper").DueDate);
        Assert.Equal(9, Find("paper").DueAt!.Value.LocalDateTime.Hour);
        Assert.Equal(Today.AddDays(3), Find("laundry").DueDate);

        result.Undo!();

        Assert.Equal(Today, Find("paper").DueDate);
        Assert.Equal(9, Find("paper").DueAt!.Value.LocalDateTime.Hour);
        Assert.Equal(Today.AddDays(-2), Find("laundry").DueDate);
    }

    [Fact]
    public void A_move_that_gave_an_undated_item_a_date_has_no_undo()
    {
        var paper = Store(new SortedItem(ItemKind.Task, "paper", Today, null));
        var someday = Store(new SortedItem(ItemKind.Task, "someday", null, null));

        var result = SeveralRows.Move(_database, [paper, someday], Today.AddDays(1), DateTime.Now);

        Assert.Equal(2, result.Changed);
        Assert.Null(result.Undo);
        Assert.Equal(Today.AddDays(1), Find("someday").DueDate);
    }
}
