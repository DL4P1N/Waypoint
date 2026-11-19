using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Waypoint.Core;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Remote;
using Waypoint.Core.Theme;

namespace Waypoint.App;

/// <summary>The rows and their menus: checking an item, the row menu, selecting several rows, and the changes a row can make.</summary>
public partial class TodayView
{
    private async void OnItemChecked(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        var item = ((ItemRow)box.DataContext).Item;
        var key = ItemRef.Format(item);
        var done = box.IsChecked == true;

        // Rows fill their boxes from the saved state, which isn't a change to save. A row clicked again before its
        // last click was saved goes by that click instead.
        var shown = _doneWanted.TryGetValue(key, out var wanted) && ReferenceEquals(wanted.Item, item) ? wanted.Done : item.Done;
        if (done == shown)
        {
            return;
        }

        _doneWanted[key] = (item, done);
        try
        {
            await Work.Run(() => _database.SetDone(item, done));
        }
        finally
        {
            if (_doneWanted.TryGetValue(key, out var last) && last.Done == done && ReferenceEquals(last.Item, item))
            {
                _doneWanted.Remove(key);
            }
        }

        Bar.Forget(About(item));
        Work.ShowAfterChanges(nameof(OnItemChecked), () =>
        {
            ShowFilters();
            ShowItems(force: true);
            ShowSuggestion();
        }, () =>
        {
            ShowFilters();
            ShowItems();
            ShowSuggestion();
        });
    }

    private void OnRowMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var box = (FrameworkElement)sender;
        var row = (ItemRow)box.DataContext;
        if (_selected.Count > 1 && _selected.Contains(row.Key))
        {
            e.Handled = true;
            OpenSeveralMenu(box, e);
            return;
        }

        // Right-clicking outside the selection means this row on its own.
        ClearSelection();
        e.Handled = true;
        OpenItemMenu(box, e.CursorLeft < 0 ? PlacementMode.Bottom : PlacementMode.MousePoint, row.Item);
    }

    /// <summary>A row's menu, also used by the week view's blocks so both act the same way.</summary>
    private async void OpenItemMenu(FrameworkElement box, PlacementMode placement, StoredItem item)
    {
        // Only a calendar item can have been moved from its feed date, and that's asked of the server, so in server
        // mode its menu opens once the answer comes, unless another menu was asked for meanwhile.
        // On this PC's database the answer is there at once, and the menu opens on the row as before.
        var asked = ++_menusAsked;
        var reading = item.Source == ItemSource.Calendar ? Work.Read(() => _database.IsMovedFromFeed(item)) : System.Threading.Tasks.Task.FromResult(false);
        var waited = !reading.IsCompleted;
        var moved = await reading;
        if (waited && (asked != _menusAsked || PresentationSource.FromVisual(box) is null))
        {
            return;
        }

        var entries = Work.Peek(() => ItemMenu(item, moved));
        _menuOpen = true;
        RowMenu.Open(box, placement, entries, MenuClosed);
    }

    private int _menusAsked;

    private IEnumerable<RowMenuEntry> ItemMenu(StoredItem item, bool moved)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var canFile = item.Source == ItemSource.Capture && ToDoFilters.CourseOf(item, Placement(item)) is null && _filters.Any(filter => filter.Source == FilterSource.Custom);
        var actions = RowActions.For(item, today, moved, canFile);
        IEnumerable<RowMenuEntry> Part(RowActionGroup group) => actions.Where(action => RowActions.GroupOf(action) == group).Select(action => Entry(action, item, today));
        var waiting = WaitingMenu(item, today).ToList();
        // "Waiting on someone..." is one more way to change it. A wait's own follow-up lines get a part of their own.
        List<RowMenuEntry> markWaiting = waiting.Count == 1 ? waiting : [];
        List<RowMenuEntry> followUp = waiting.Count == 1 ? [] : waiting;

        // Doing it, changing it, following up a wait, copying, and Delete apart at the bottom.
        return RowMenuEntry.Parts(ResumeMenu(item).Concat(Part(RowActionGroup.Do)), Part(RowActionGroup.Change).Concat(markWaiting), followUp, FullTextMenu(item).Concat(Part(RowActionGroup.Copy)), FeedbackMenu(item), Part(RowActionGroup.Remove)).ToList();
    }

    /// <summary>The words on the row menu's This didn't help, which opens a line to add a note.</summary>
    public const string FeedbackMenuLabel = CardLabels.DidntHelp + "...";

    /// <summary>This didn't help, kept as soon as it's chosen, then a line above the list for an optional note.</summary>
    private IEnumerable<RowMenuEntry> FeedbackMenu(StoredItem item)
    {
        if (item.Done || FeedbackData is not { } feedback)
        {
            return [];
        }

        return [new RowMenuEntry(FeedbackMenuLabel, async () =>
        {
            try
            {
                var reference = ItemRef.Format(item);
                RowFeedback.Open(await Work.Run(() => feedback.AddFeedback(Feedback.Row, reference, DateTime.Now)), item.Title);
            }
            catch (Exception exception) when (exception is ServerUnavailableException or ServerTimedOutException or InvalidOperationException)
            {
                _reply("Couldn't keep that while the server can't be reached.");
            }
        })];
    }

    /// <summary>Catches up on a rebuild the menu held back. It's forced, since focus is still in the rows after the menu key.</summary>
    private void MenuClosed()
    {
        _menuOpen = false;
        if (_rebuildWaiting)
        {
            _rebuildWaiting = false;
            Work.Show(() => ShowItems(force: true), () => ShowItems());
        }
    }

    /// <summary>
    /// A long capture keeps its whole text under a short title. It shows under the title while the row is selected,
    /// and this keeps it open, or folds it up again, without selecting.
    /// </summary>
    private IEnumerable<RowMenuEntry> FullTextMenu(StoredItem item)
    {
        if (item.Body is null)
        {
            return [];
        }

        var key = ItemRef.Format(item);
        return _expanded.Contains(key)
            ? [new RowMenuEntry("Hide full text", () => Expand(key, false))]
            : [new RowMenuEntry("Show full text", () => Expand(key, true))];
    }

    private void Expand(string key, bool open)
    {
        if (open)
        {
            _expanded.Add(key);
        }
        else
        {
            _expanded.Remove(key);
        }

        MarkSelected();
    }

    /// <summary>Ctrl-click adds or takes away one row, Shift-click selects from the last one clicked. A plain click works as always.</summary>
    private void OnRowPressed(object sender, MouseButtonEventArgs e)
    {
        var box = (CheckBox)sender;
        var row = (ItemRow)box.DataContext;
        var modifiers = Keyboard.Modifiers;
        var ctrl = modifiers.HasFlag(ModifierKeys.Control);
        var shift = modifiers.HasFlag(ModifierKeys.Shift);
        if (!ctrl && !shift)
        {
            ClearSelection();
            return;
        }

        // Selecting isn't checking, so the box mustn't toggle.
        e.Handled = true;
        box.Focus();
        if (shift && _anchor is not null && _rowOrder.FindIndex(found => found.Key == _anchor) is var from and >= 0)
        {
            var to = _rowOrder.FindIndex(found => found.Key == row.Key);
            if (!ctrl)
            {
                _selected.Clear();
            }

            foreach (var inRange in _rowOrder.Skip(Math.Min(from, to)).Take(Math.Abs(to - from) + 1))
            {
                _selected.Add(inRange.Key);
            }
        }
        else
        {
            Toggle(row);
        }

        MarkSelected();
    }

    /// <summary>Ctrl+Space selects the focused row, and Escape lets go of the selection.</summary>
    private void OnRowsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && _selected.Count > 0)
        {
            ClearSelection();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Space && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && (e.OriginalSource as FrameworkElement)?.DataContext is ItemRow row)
        {
            Toggle(row);
            MarkSelected();
            e.Handled = true;
        }
    }

    private void Toggle(ItemRow row)
    {
        if (!_selected.Remove(row.Key))
        {
            _selected.Add(row.Key);
        }

        _anchor = row.Key;
    }

    private void ClearSelection()
    {
        if (_selected.Count == 0)
        {
            return;
        }

        _selected.Clear();
        _anchor = null;
        MarkSelected();
    }

    private void MarkSelected()
    {
        foreach (var row in _rowOrder)
        {
            row.IsSelected = _selected.Contains(row.Key);
            row.IsExpanded = _expanded.Contains(row.Key);
        }
    }

    /// <summary>After a rebuild, the new rows take over the selection. Anything no longer shown drops out of it, so an action can't reach a hidden item.</summary>
    private void ShowSelection(IReadOnlyList<GroupRow> groups)
    {
        _rowOrder = groups
            .SelectMany(group => group.ScheduledRows.Concat(group.ToDoRows))
            .SelectMany(row => row.Steps.Prepend(row))
            .ToList();
        var shown = _rowOrder.Select(row => row.Key).ToHashSet();
        _selected.IntersectWith(shown);
        _expanded.IntersectWith(shown);
        if (_anchor is not null && !shown.Contains(_anchor))
        {
            _anchor = null;
        }

        MarkSelected();
    }

    private IReadOnlyList<StoredItem> SelectedItems => _rowOrder.Where(row => _selected.Contains(row.Key)).Select(row => row.Item).ToList();

    /// <summary>Done, Reschedule, Drop, and Delete for every selected row, each through the same path as its own menu, with one undo for all of them.</summary>
    private void OpenSeveralMenu(FrameworkElement box, ContextMenuEventArgs e)
    {
        var items = SelectedItems;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var count = items.Count;
        var entries = RowActions.ForSeveral(items, today).Select(action => action switch
        {
            RowAction.Done => new RowMenuEntry($"Mark {count} done", async () => Several(await Work.Run(() => SeveralRows.Finish(_database, items)), $"Marked {count} done.")),
            RowAction.Reschedule => new RowMenuEntry($"Reschedule {count}", Children:
            [
                new("Today", () => MoveSeveral(items, today)),
                new("Tomorrow", () => MoveSeveral(items, today.AddDays(1))),
                new("Next week", () => MoveSeveral(items, today.AddDays(7))),
                new("Pick a date...", () => PickDateForSeveral(items, today)),
            ]),
            _ => new RowMenuEntry($"Delete {count}", async () => Several(await Work.Run(() => SeveralRows.Remove(_database, items, DateTime.Now)), $"Deleted {count}.")),
        }).ToList();

        if (entries.Count == 0)
        {
            entries.Add(new RowMenuEntry("Some of these are finished, so they can only be changed one at a time"));
        }

        entries.Add(RowMenuEntry.Separator);
        entries.Add(new RowMenuEntry("Clear selection", ClearSelection));
        _menuOpen = true;
        RowMenu.Open(box, e, entries, MenuClosed);
    }

    private void PickDateForSeveral(IReadOnlyList<StoredItem> items, DateOnly today)
    {
        // No time box, so each one keeps its own time, and repeating ones keep their usual time.
        var first = items.Select(item => item.DueDate).Where(date => date is not null).Min() ?? today;
        var picker = new PickDateWindow(Window.GetWindow(this), $"{items.Count} items", first, null, asksForTime: false);
        if (picker.ShowDialog() == true)
        {
            MoveSeveral(items, picker.Date);
        }
    }

    private async void MoveSeveral(IReadOnlyList<StoredItem> items, DateOnly date)
    {
        var result = await Work.Run(() => SeveralRows.Move(_database, items, date, DateTime.Now));
        var day = date.ToString("dddd, MMM d", CultureInfo.CurrentCulture);
        Several(result, result.Undo is null
            ? $"Moved {result.Changed} to {day}. Undo isn't available, because some had no date before."
            : $"Moved {result.Changed} to {day}.");
    }

    private void Several(SeveralChanged result, string message)
    {
        ClearSelection();
        Bar.Show(message, result.Undo is { } undo ? async () =>
        {
            await Work.Run(undo);
            Acted();
        } : null);
        Acted();
    }

    private RowMenuEntry Entry(RowAction action, StoredItem item, DateOnly today) => action switch
    {
        RowAction.Done => item.Dose ? new(CardLabels.TookIt, () => TookIt(item)) : new("Mark done", () => SetDone(item, true)),
        RowAction.Reopen => new("Reopen", () => SetDone(item, false)),
        RowAction.Start => new("Start now", () => Start(item)),
        RowAction.SmallerStep => new("Start with a tiny step", () => StartSmaller(item)),
        RowAction.Study => new("Study for this exam", () => StudyRequested?.Invoke(item)),
        RowAction.Reschedule => new("Reschedule", Children:
        [
            new("Today", () => Move(item, today, null)),
            new("Tomorrow", () => Move(item, today.AddDays(1), null)),
            new("Next week", () => Move(item, today.AddDays(7), null)),
            new("Pick a date...", () => PickDate(item, today)),
        ]),
        RowAction.UseFeedDate => new("Use feed date", () => Change(item, ChangeAction.UseFeedDate)),
        RowAction.Rename => new("Rename", () => Rename(item)),
        RowAction.BreakDown => new("Break down", () => _submit($"break down {item.Title}")),
        RowAction.File => new("Move to filter", Children: FileChoices(item).ToList()),
        RowAction.StopReminders => new("Stop reminders", () => Change(item, ChangeAction.StopReminders)),
        RowAction.Dose => new(item.Dose ? "Not a dose" : "Mark as a dose", () => MarkDose(item, !item.Dose)),
        RowAction.Cancel => new("Delete", () => Remove(item)),
        RowAction.Delete => new("Delete", () => Delete(item)),
        _ => new("Copy title", () => RowMenu.Copy(item.Title)),
    };

    /// <summary>An open item's Delete. It's kept in History as canceled, and the bar's Undo reopens it.</summary>
    private async void Remove(StoredItem item)
    {
        Bar.Forget(About(item));
        var applied = await Work.Run(() => _database.ChangeItem(new ItemChange(item.Source, item.Id, ChangeAction.Cancel), DateTime.Now));
        if (!applied.Applied)
        {
            Report(applied);
            return;
        }

        Bar.Show($"Deleted {item.Title}", async () =>
        {
            await Work.Run(() => _database.SetDone(item, false));
            Acted();
        });
        Acted();
    }

    /// <summary>A finished item's Delete, which takes it out of History too.</summary>
    private async void Delete(StoredItem item)
    {
        if (await Work.Run(() => _database.DeleteItem(item, DateTime.Now)) is not { } deleted)
        {
            return;
        }

        Bar.Show($"Deleted {item.Title}", async () =>
        {
            await Work.Run(() => _database.UndoDelete(deleted, DateTime.Now));
            Acted();
        });
        Acted();
    }

    /// <summary>Every filter it could go in except the one it's in now.</summary>
    private IEnumerable<RowMenuEntry> FileChoices(StoredItem item)
    {
        var current = Placement(item)?.FilterId;
        return _filters
            .Where(filter => filter.Source is FilterSource.Personal or FilterSource.Custom)
            .Where(filter => filter.Source == FilterSource.Personal ? current is not null : filter.Id != current)
            .Select(filter => new RowMenuEntry(filter.Name, () => File(item, filter.Source == FilterSource.Personal ? null : filter)));
    }

    private async void File(StoredItem item, ToDoFilter? filter)
    {
        if (await Work.Run(() => _database.AssignToFilter(item, filter?.Id)))
        {
            Bar.Show($"Moved {item.Title} to {filter?.Name ?? Personal.Name}");
        }

        FiltersChanged();
    }

    private async void SetDone(StoredItem item, bool done)
    {
        await Work.Run(() => _database.SetDone(item, done));
        Bar.Forget(About(item));
        Acted();
    }

    /// <summary>A dose's Took it. It's kept once for its day, so a second one says when it was already marked.</summary>
    private async void TookIt(StoredItem item)
    {
        Bar.Forget(About(item));
        _reply((await Work.Run(() => _database.TakeDose(item, DateTime.Now))).Text);
        Acted();
    }

    private async void MarkDose(StoredItem item, bool dose)
    {
        _reply(await Work.Run(() => _database.SetDose(item, dose))
            ? dose ? $"{item.Title} is now a dose. Its reminder says {CardLabels.TookIt}." : $"{item.Title} is a plain reminder again."
            : "Only a repeating reminder can be a dose.");
        Acted();
    }

    /// <summary>What a message about this item is tied to, so finishing or changing it clears that message.</summary>
    private static object About(StoredItem item) => (item.Source, item.Id);

    private async void Start(StoredItem item)
    {
        var reference = ItemRef.Format(item);
        _reply((await Work.Run(() => _database.StartActivation(reference, StartMethod, DateTime.Now))).Text);
        Acted();
    }

    /// <summary>Uses the fixed actions, so it works offline and never waits on the sorter.</summary>
    private async void StartSmaller(StoredItem item)
    {
        _reply((await Work.Run(() => _database.StartSmaller(item, StartMethod, DateTime.Now))).Text);
        Acted();
    }

    private async void Move(StoredItem item, DateOnly date, TimeOnly? time) => Report(await Work.Run(() => _database.RescheduleItem(item, date, time, DateTime.Now)));

    private void PickDate(StoredItem item, DateOnly today)
    {
        var time = item.DueAt is { } dueAt ? TimeOnly.FromDateTime(dueAt.LocalDateTime) : (TimeOnly?)null;
        var picker = new PickDateWindow(Window.GetWindow(this), item.Title, item.DueDate ?? today, time, asksForTime: item.Recurs is null);
        if (picker.ShowDialog() == true)
        {
            Move(item, picker.Date, picker.Time);
        }
    }

    private void Rename(StoredItem item)
    {
        var rename = new RenameWindow(Window.GetWindow(this), item.Title);
        if (rename.ShowDialog() == true)
        {
            Change(item, ChangeAction.Update, rename.NewName);
        }
    }

    private async void Change(StoredItem item, ChangeAction action, string? title = null)
    {
        Bar.Forget(About(item));
        Report(await Work.Run(() => _database.ChangeItem(new ItemChange(item.Source, item.Id, action, title), DateTime.Now)));
    }

    private void Report(AppliedChange applied)
    {
        _reply(ItemGroups.ChangeSummary(applied));
        Acted();
    }

    private void Acted()
    {
        // An earlier message's Undo may no longer hold once something else has changed.
        _lastSaid = null;
        Bar.Changed();
        Work.ShowAfterChanges(nameof(Acted), () =>
        {
            ShowFilters();
            ShowItems(force: true);
            ShowSuggestion();
        }, () =>
        {
            ShowFilters();
            ShowItems();
            ShowSuggestion();
        });
        _changed();
    }
}
