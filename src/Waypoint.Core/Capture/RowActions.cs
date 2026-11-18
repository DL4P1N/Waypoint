using Waypoint.Core.Data;
using Waypoint.Core.Remote;

namespace Waypoint.Core.Capture;

public enum RowAction
{
    Done,
    Reopen,
    Start,
    SmallerStep,
    Reschedule,
    UseFeedDate,
    Rename,
    BreakDown,
    File,
    StopReminders,

    /// <summary>
    /// What an open item's Delete does: it leaves To do and is kept in History as canceled, so Undo or Reopen brings it back.
    /// The menu calls it Delete, the one word for taking a task away.
    /// </summary>
    Cancel,

    /// <summary>Takes a finished item out of Waypoint, History included.</summary>
    Delete,
    Copy,

    /// <summary>Practice questions from the class folder's notes. Only offered for an exam.</summary>
    Study,

    /// <summary>Marks a repeating reminder as a dose, answered with Took it, or back to a plain reminder.</summary>
    Dose,
}

/// <summary>
/// What a row's right-click menu offers, in order, so nothing is shown that wouldn't work. The order runs from doing it,
/// to changing it, to copying it, with Delete last and set apart.
/// </summary>
public static class RowActions
{
    /// <summary><paramref name="canFile"/> is true when the item has no class and there are filters of the user's own to put it in.</summary>
    public static IReadOnlyList<RowAction> For(StoredItem item, DateOnly today, bool movedFromFeed, bool canFile = false)
    {
        if (item.Done)
        {
            return [RowAction.Reopen, RowAction.Copy, RowAction.Delete];
        }

        var isFeed = item.Source == ItemSource.Calendar;
        var actions = new List<RowAction> { RowAction.Done };

        // A past feed item that was unchecked stays on the page, but it isn't an open item to start on.
        if (!isFeed || item.DueDate >= today)
        {
            actions.Add(RowAction.Start);
            actions.Add(RowAction.SmallerStep);
            if (item.Kind == ItemKind.Exam && item.ParentRef is null)
            {
                actions.Add(RowAction.Study);
            }
        }

        if (item.ParentRef is null)
        {
            actions.Add(RowAction.BreakDown);
        }

        actions.Add(RowAction.Reschedule);
        if (movedFromFeed)
        {
            actions.Add(RowAction.UseFeedDate);
        }

        if (!isFeed)
        {
            actions.Add(RowAction.Rename);
        }

        if (!isFeed && item.Recurs is not null && item.ParentRef is null)
        {
            actions.Add(RowAction.Dose);
        }

        // Steps go wherever their parent is, so only a top item can be filed.
        if (canFile && !isFeed && item.ParentRef is null)
        {
            actions.Add(RowAction.File);
        }

        // One way to take an open item away, shown as Delete, which keeps it in History.
        actions.AddRange([RowAction.StopReminders, RowAction.Copy, RowAction.Cancel]);
        return actions;
    }

    /// <summary>
    /// What the menu offers when several rows are selected: finish, move, or delete them all. Each is only
    /// offered when every selected item would offer it on its own, so the same paths can run item by item.
    /// Delete covers open and finished rows together, the way each row's own Delete would.
    /// </summary>
    public static IReadOnlyList<RowAction> ForSeveral(IReadOnlyCollection<StoredItem> items, DateOnly today)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var each = items.Select(item => For(item, today, movedFromFeed: false)).ToList();
        RowAction[] together = [RowAction.Done, RowAction.Reschedule];
        var actions = together.Where(action => each.All(offered => offered.Contains(action))).ToList();
        if (each.All(offered => offered.Contains(RowAction.Cancel) || offered.Contains(RowAction.Delete)))
        {
            actions.Add(RowAction.Delete);
        }

        return actions;
    }

    /// <summary>
    /// Whether a move of these items can be put back by moving each one back. One that had no date can't,
    /// since a move only ever sets a date and never clears one.
    /// </summary>
    public static bool CanUndoMove(IEnumerable<StoredItem> items) => items.All(item => item.DueDate is not null);

    /// <summary>Which part of the menu an action sits in, so the menu can set the parts apart.</summary>
    public static RowActionGroup GroupOf(RowAction action) => action switch
    {
        RowAction.Done or RowAction.Reopen or RowAction.Start or RowAction.SmallerStep or RowAction.Study or RowAction.BreakDown => RowActionGroup.Do,
        RowAction.Copy => RowActionGroup.Copy,
        RowAction.Cancel or RowAction.Delete => RowActionGroup.Remove,
        _ => RowActionGroup.Change,
    };

    /// <summary>An open item from an earlier day, which is when the menu offers catching up: move it, a smaller step, or drop it.</summary>
    public static bool IsBehind(StoredItem item, DateOnly today) => !item.Done && item.DueDate is { } due && due < today;
}

/// <summary>The parts of a row's menu, top to bottom.</summary>
public enum RowActionGroup
{
    Do,
    Change,
    Copy,
    Remove,
}

/// <summary>What happened to a selection: how many changed, and a way to put them all back when there is one.</summary>
public sealed record SeveralChanged(int Changed, Action? Undo);

/// <summary>
/// The several-items menu. Each item goes through the same method its own row menu uses, one at a time,
/// and the undo puts each back the way its own undo would: reopening through history, or moving back.
/// </summary>
public static class SeveralRows
{
    public static SeveralChanged Finish(WaypointDatabase database, IReadOnlyList<StoredItem> items) => Finish(new LocalToDoData(database), items);

    public static SeveralChanged Drop(WaypointDatabase database, IReadOnlyList<StoredItem> items, DateTime now) => Drop(new LocalToDoData(database), items, now);

    public static SeveralChanged Delete(WaypointDatabase database, IReadOnlyList<StoredItem> items, DateTime now) => Delete(new LocalToDoData(database), items, now);

    public static SeveralChanged Move(WaypointDatabase database, IReadOnlyList<StoredItem> items, DateOnly date, DateTime now) => Move(new LocalToDoData(database), items, date, now);

    public static SeveralChanged Finish(IToDoData database, IReadOnlyList<StoredItem> items)
    {
        foreach (var item in items)
        {
            database.SetDone(item, true);
        }

        return new SeveralChanged(items.Count, items.Count == 0 ? null : () => Reopen(database, items));
    }

    public static SeveralChanged Drop(IToDoData database, IReadOnlyList<StoredItem> items, DateTime now)
    {
        var dropped = items.Where(item => database.ChangeItem(new ItemChange(item.Source, item.Id, ChangeAction.Cancel), now).Applied).ToList();
        return new SeveralChanged(dropped.Count, dropped.Count == 0 ? null : () => Reopen(database, dropped));
    }

    /// <summary>Deletes each one, and undo brings each back as it was.</summary>
    public static SeveralChanged Delete(IToDoData database, IReadOnlyList<StoredItem> items, DateTime now)
    {
        var deleted = items.Select(item => database.DeleteItem(item, now)).OfType<DeletedItem>().ToList();
        return new SeveralChanged(deleted.Count, deleted.Count == 0 ? null : () =>
        {
            foreach (var item in deleted)
            {
                database.UndoDelete(item, DateTime.Now);
            }
        });
    }

    /// <summary>
    /// The several-rows Delete: each open one is canceled and kept in History, each finished one is deleted, the same
    /// as its own row's Delete. Undo reopens the first kind and brings the second back.
    /// </summary>
    public static SeveralChanged Remove(IToDoData database, IReadOnlyList<StoredItem> items, DateTime now)
    {
        var dropped = Drop(database, items.Where(item => !item.Done).ToList(), now);
        var deleted = Delete(database, items.Where(item => item.Done).ToList(), now);
        var changed = dropped.Changed + deleted.Changed;
        return new SeveralChanged(changed, changed == 0 ? null : () =>
        {
            dropped.Undo?.Invoke();
            deleted.Undo?.Invoke();
        });
    }

    /// <summary>Keeps each item's own time. There's no undo if any of them had no date, since a move can't take a date away.</summary>
    public static SeveralChanged Move(IToDoData database, IReadOnlyList<StoredItem> items, DateOnly date, DateTime now)
    {
        var moved = items.Where(item => database.RescheduleItem(item, date, null, now).Applied).ToList();
        Action? undo = moved.Count > 0 && RowActions.CanUndoMove(moved)
            ? () =>
            {
                foreach (var item in moved)
                {
                    database.RescheduleItem(item with { DueDate = date }, item.DueDate!.Value, null, DateTime.Now);
                }
            }
            : null;
        return new SeveralChanged(moved.Count, undo);
    }

    private static void Reopen(IToDoData database, IEnumerable<StoredItem> items)
    {
        foreach (var item in items)
        {
            database.SetDone(item, false);
        }
    }
}
