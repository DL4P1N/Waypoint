using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

public sealed class DeleteItemTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-{Guid.NewGuid():N}.db");
    private readonly WaypointDatabase _database;

    public DeleteItemTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static DateTime Now => DateTime.Now;

    private StoredItem Add(string title, DateOnly? due = null)
    {
        _database.CompleteCapture(_database.AddCapture(title), [new SortedItem(ItemKind.Task, title, due, null)]);
        return _database.GetOpenItems().Single(item => item.Title == title);
    }

    [Fact]
    public void A_deleted_item_leaves_the_list_and_history()
    {
        var laundry = Add("laundry");

        Assert.NotNull(_database.DeleteItem(laundry, Now));

        Assert.DoesNotContain(_database.GetOpenItems(), item => item.Title == "laundry");
        Assert.DoesNotContain(_database.GetHistory(), entry => entry.Title == "laundry");
        Assert.DoesNotContain(_database.GetFinishedForList(Now), item => item.Title == "laundry");
    }

    [Fact]
    public void Undo_brings_a_deleted_item_back_open()
    {
        var laundry = Add("laundry", DateOnly.FromDateTime(Now));
        var deleted = _database.DeleteItem(laundry, Now)!;

        _database.UndoDelete(deleted, Now);

        Assert.Contains(_database.GetOpenItems(), item => item.Title == "laundry");
        Assert.DoesNotContain(_database.GetHistory(), entry => entry.Title == "laundry");
    }

    [Fact]
    public void Canceling_still_keeps_a_record()
    {
        var laundry = Add("laundry");

        _database.ChangeItem(new ItemChange(laundry.Source, laundry.Id, ChangeAction.Cancel), Now);

        Assert.Contains(_database.GetHistory(), entry => entry.Title == "laundry" && entry.Action == HistoryAction.Canceled);
    }

    [Fact]
    public void A_finished_item_leaves_the_crossed_out_list_and_undo_puts_it_back()
    {
        var laundry = Add("laundry", DateOnly.FromDateTime(Now));
        _database.SetDone(laundry, true);
        var finished = _database.GetFinishedForList(Now).Single(item => item.Title == "laundry");

        var deleted = _database.DeleteItem(finished, Now)!;

        Assert.DoesNotContain(_database.GetFinishedForList(Now), item => item.Title == "laundry");
        Assert.DoesNotContain(_database.GetHistory(), entry => entry.Title == "laundry");

        _database.UndoDelete(deleted, Now);

        Assert.Contains(_database.GetFinishedForList(Now), item => item.Title == "laundry");
        Assert.Contains(_database.GetHistory(), entry => entry.Title == "laundry" && entry.Action == HistoryAction.Done);
    }

    [Fact]
    public void Deleting_from_history_removes_its_entry()
    {
        var laundry = Add("laundry");
        _database.SetDone(laundry, true);
        var entry = _database.GetHistory().Single(found => found.Title == "laundry");

        Assert.NotNull(_database.DeleteHistoryItem(entry, Now));

        Assert.DoesNotContain(_database.GetHistory(), found => found.Title == "laundry");
    }

    [Fact]
    public void Several_can_be_deleted_and_put_back_together()
    {
        var items = new[] { Add("laundry"), Add("dishes") };

        var result = SeveralRows.Delete(_database, items, Now);

        Assert.Equal(2, result.Changed);
        Assert.Empty(_database.GetOpenItems());
        result.Undo!();
        Assert.Equal(2, _database.GetOpenItems().Count);
    }

    [Fact]
    public void An_open_row_has_one_way_to_go_and_a_finished_row_can_be_deleted()
    {
        var open = new StoredItem(1, ItemKind.Task, "laundry", null, null, false);
        var done = open with { Done = true };
        var today = DateOnly.FromDateTime(Now);

        Assert.Contains(RowAction.Cancel, RowActions.For(open, today, movedFromFeed: false));
        Assert.DoesNotContain(RowAction.Delete, RowActions.For(open, today, movedFromFeed: false));
        Assert.Contains(RowAction.Delete, RowActions.For(done, today, movedFromFeed: false));
        Assert.Contains(RowAction.Delete, RowActions.ForSeveral([open, open with { Id = 2 }], today));
    }
}
