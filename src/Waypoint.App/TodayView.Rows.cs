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

        // A dose is kept once for its day, the same as its menu's Took it, so checking one that's already been taken
        // changes nothing and offers no Undo that would put back the real dose from earlier.
        if (done && item.Dose)
        {
            TookIt(item);
            return;
        }

        _doneWanted[key] = (item, done);
        bool saved;
        try
        {
            saved = await Try(() => _database.SetDone(item, done), nameof(OnItemChecked));
        }
        finally
        {
            if (_doneWanted.TryGetValue(key, out var last) && last.Done == done && ReferenceEquals(last.Item, item))
            {
                _doneWanted.Remove(key);
            }
        }

        // While the server is away nothing was saved and the bar says so. The rows are built again below from what
        // was kept, so the box goes back to how it was.
        if (saved)
        {
            Bar.Forget(About(item));
            if (done)
            {
                // With Hide done on, the row leaves the list, so this says what happened and how to bring it back.
                Bar.Show(DoneMessage(item.Title), async () =>
                {
                    if (await Try(() => _database.SetDone(item, false), "UndoDone"))
                    {
                        Acted();
                    }
                }, About(item));
            }
        }

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

    /// <summary>What the bar says when a row's box is checked, with Undo beside it.</summary>
    internal static string DoneMessage(string title) => $"Marked {title} done";

    /// <summary>
    /// Runs a change after the page's earlier ones. If the server is away, busy, turns it down, or is too slow to
    /// answer, the bar says so, the rows are read and drawn again so they show what was really kept, and Ok is false.
    /// A slow answer may still have gone through, which its message says rather than that nothing was saved.
    /// </summary>
    private async System.Threading.Tasks.Task<(bool Ok, T Result)> Try<T>(Func<T> change, string what)
    {
        try
        {
            return (true, await Work.Run(change, what));
        }
        catch (ServerUnavailableException away)
        {
            Bar.Show(PageMessages.CantChange(away));
        }
        catch (ServerTimedOutException slow)
        {
            Bar.Show(slow.Message);
        }
        catch (Exception problem) when (problem is ServerBusyException or ServerRefusedException)
        {
            Bar.Show($"{problem.Message} Nothing was changed.");
        }

        Redraw();
        return (false, default!);
    }

    /// <summary>Reads the rows again and draws them, after a change that may not have been kept.</summary>
    private void Redraw() => Work.ShowAfterChanges(nameof(Redraw), () =>
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

    private async System.Threading.Tasks.Task<bool> Try(Action change, string what) => (await Try<object?>(() =>
    {
        change();
        return null;
    }, what)).Ok;

    private void OnRowMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var box = (FrameworkElement)sender;
        var row = (ItemRow)box.DataContext;
        if (_selected.Count > 1 && _selected.Contains(row.Key))
        {
            e.Handled = true;
            OpenSeveralMenu(box, e.CursorLeft < 0 ? PlacementMode.Bottom : PlacementMode.MousePoint);
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
        IEnumerable<RowMenuEntry> Part(RowActionGroup group) => actions.Where(action => RowActions.GroupOf(action) == group).Select(action => Entry(action, item, today, moved));
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
            if (!OnTitle(box, e.OriginalSource))
            {
                ClearSelection();
                return;
            }

            // Only the box marks a row done, so a click on its words can't finish it by mistake. It picks the row
            // instead, which shows a long capture's whole text, and a second click lets it go.
            e.Handled = true;
            box.Focus();
            var only = _selected.Count == 1 && _selected.Contains(row.Key);
            _selected.Clear();
            if (only)
            {
                _anchor = null;
            }
            else
            {
                _selected.Add(row.Key);
                _anchor = row.Key;
            }

            MarkSelected();
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

    /// <summary>Whether a press landed on a row's words rather than its box or the gap beside the box.</summary>
    private static bool OnTitle(CheckBox box, object source)
    {
        if (box.Content is not DependencyObject words)
        {
            return false;
        }

        for (var at = source as DependencyObject; at is not null && !ReferenceEquals(at, box); at = at is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
        {
            if (ReferenceEquals(at, words))
            {
                return true;
            }
        }

        return false;
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
        else if (e.Key == System.Windows.Input.Key.Delete && Keyboard.Modifiers == ModifierKeys.None
            && (e.OriginalSource as FrameworkElement)?.DataContext is ItemRow focused)
        {
            // A held key repeats, and each repeat would delete the same row again, so only the first press counts.
            e.Handled = true;
            if (!e.IsRepeat)
            {
                DeleteByKey(focused);
            }
        }
    }

    /// <summary>Rows whose Delete is on its way, so a second press or menu choice can't run it again on the same item.</summary>
    private readonly HashSet<string> _deleting = new(StringComparer.Ordinal);

    /// <summary>
    /// Rows deleted but still on screen until the list is next built, which in server mode waits for the server. Kept
    /// so their Delete isn't run again on what's now a stale row, which would replace the first Undo.
    /// </summary>
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);

    private bool Deleting(string key) => _deleting.Contains(key) || _deleted.Contains(key);

    /// <summary>Runs a row's Delete once at a time. False when it's already on its way or done, so the caller does nothing.</summary>
    private async System.Threading.Tasks.Task<bool> DeleteOnce(IReadOnlyList<string> keys, Func<System.Threading.Tasks.Task<bool>> delete)
    {
        if (keys.Any(Deleting))
        {
            return false;
        }

        _deleting.UnionWith(keys);
        try
        {
            var gone = await delete();
            if (gone)
            {
                _deleted.UnionWith(keys);
            }

            return gone;
        }
        finally
        {
            _deleting.ExceptWith(keys);
        }
    }

    /// <summary>
    /// Delete on a focused row does what its menu's Delete does: an open item goes to History as canceled, a finished
    /// one is deleted, each with Undo on the bar. On a row in a selection of several, it's the selection's Delete.
    /// </summary>
    private async void DeleteByKey(ItemRow row)
    {
        if (_selected.Count > 1 && _selected.Contains(row.Key))
        {
            var items = SelectedItems;
            if (RowActions.ForSeveral(items, DateOnly.FromDateTime(DateTime.Now)).Contains(RowAction.Delete))
            {
                RemoveSeveral(items);
            }

            return;
        }

        if (row.Item.Done)
        {
            Delete(row.Item);
        }
        else
        {
            Remove(row.Item);
        }
    }

    /// <summary>The "⋯" button on a row opens the same menu as right-clicking it, under the button.</summary>
    private void OnRowMoreClicked(object sender, RoutedEventArgs e)
    {
        var button = (FrameworkElement)sender;
        var row = (ItemRow)button.DataContext;
        if (_selected.Count > 1 && _selected.Contains(row.Key))
        {
            OpenSeveralMenu(button, PlacementMode.Bottom);
            return;
        }

        ClearSelection();
        OpenItemMenu(button, PlacementMode.Bottom, row.Item);
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

        // A change reads the rows again before it's done, so these rows show whether a delete went through.
        _deleted.Clear();
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
    private void OpenSeveralMenu(FrameworkElement box, PlacementMode placement)
    {
        var items = SelectedItems;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var count = items.Count;
        var entries = RowActions.ForSeveral(items, today).Select(action => action switch
        {
            RowAction.Done => new RowMenuEntry($"Mark {count} done", async () =>
            {
                if (await Try(() => SeveralRows.Finish(_database, items), "FinishSeveral") is (true, { } finished))
                {
                    Several(finished, $"Marked {count} done.");
                }
            }),
            RowAction.Reschedule => new RowMenuEntry($"Reschedule {count}", Children:
            [
                new("Today", () => MoveSeveral(items, today)),
                new("Tomorrow", () => MoveSeveral(items, today.AddDays(1))),
                new("Next week", () => MoveSeveral(items, today.AddDays(7))),
                new("Pick a date...", () => PickDateForSeveral(items, today)),
            ]),
            _ => new RowMenuEntry($"Delete {count}", () => RemoveSeveral(items)),
        }).ToList();

        if (entries.Count == 0)
        {
            entries.Add(new RowMenuEntry("Some of these are done, so they can only be changed one at a time"));
        }

        entries.Add(RowMenuEntry.Separator);
        entries.Add(new RowMenuEntry("Clear selection", ClearSelection));
        _menuOpen = true;
        RowMenu.Open(box, placement, entries, MenuClosed);
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

    /// <summary>The selection's Delete, from its menu or the Delete key, run once however often it's pressed.</summary>
    private async void RemoveSeveral(IReadOnlyList<StoredItem> items) => await DeleteOnce(items.Select(ItemRef.Format).ToList(), async () =>
    {
        if (await Try(() => SeveralRows.Remove(_database, items, DateTime.Now), nameof(RemoveSeveral)) is not (true, { } result))
        {
            return false;
        }

        Several(result, $"Deleted {items.Count}.");
        return true;
    });

    private async void MoveSeveral(IReadOnlyList<StoredItem> items, DateOnly date)
    {
        if (await Try(() => SeveralRows.Move(_database, items, date, DateTime.Now), nameof(MoveSeveral)) is not (true, { } result))
        {
            return;
        }

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
            if (await Try(undo, "UndoSeveral"))
            {
                Acted();
            }
        } : null);
        Acted();
    }

    private RowMenuEntry Entry(RowAction action, StoredItem item, DateOnly today, bool moved) => action switch
    {
        RowAction.Done => item.Dose ? new(CardLabels.TookIt, () => TookIt(item)) : new("Mark done", () => SetDone(item, true)),
        RowAction.Reopen => new("Reopen", () => SetDone(item, false)),
        RowAction.Start => new("Start now", () => Start(item)),
        RowAction.SmallerStep => new("Start with a tiny step", () => StartSmaller(item)),
        RowAction.Study => new("Study for this exam", () => StudyRequested?.Invoke(item)),
        RowAction.Reschedule => new("Reschedule", Children:
        [
            new("Today", () => Move(item, today, null, moved)),
            new("Tomorrow", () => Move(item, today.AddDays(1), null, moved)),
            new("Next week", () => Move(item, today.AddDays(7), null, moved)),
            new("Pick a date...", () => PickDate(item, today, moved)),
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
    private async void Remove(StoredItem item) => await DeleteOnce([ItemRef.Format(item)], async () =>
    {
        Bar.Forget(About(item));
        var (ok, applied) = await Try(() => _database.ChangeItem(new ItemChange(item.Source, item.Id, ChangeAction.Cancel), DateTime.Now), nameof(Remove));
        if (!ok)
        {
            return false;
        }

        if (!applied.Applied)
        {
            Report(applied);
            return false;
        }

        Bar.Show($"Deleted {item.Title}", async () =>
        {
            if (await Try(() => _database.SetDone(item, false), "UndoRemove"))
            {
                Acted();
            }
        });
        Acted();
        return true;
    });

    /// <summary>A finished item's Delete, which takes it out of History too.</summary>
    private async void Delete(StoredItem item) => await DeleteOnce([ItemRef.Format(item)], async () =>
    {
        if (await Try(() => _database.DeleteItem(item, DateTime.Now), nameof(Delete)) is not (true, { } deleted))
        {
            return false;
        }

        Bar.Show($"Deleted {item.Title}", async () =>
        {
            if (await Try(() => _database.UndoDelete(deleted, DateTime.Now), "UndoDelete"))
            {
                Acted();
            }
        });
        Acted();
        return true;
    });

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

    /// <summary>The menu's Mark done and Reopen. Mark done says so on the bar with Undo, the same as checking the box.</summary>
    private async void SetDone(StoredItem item, bool done)
    {
        if (!await Try(() => _database.SetDone(item, done), nameof(SetDone)))
        {
            return;
        }

        Bar.Forget(About(item));
        if (done)
        {
            Bar.Show(DoneMessage(item.Title), () => SetDone(item, false), About(item));
        }

        Acted();
    }

    /// <summary>A dose's Took it. It's kept once for its day, so a second one says when it was already marked.</summary>
    /// <remarks>It has no Undo: there's no taking a dose back, and Reopen would undo the real one from earlier.</remarks>
    private async void TookIt(StoredItem item)
    {
        Bar.Forget(About(item));
        if (await Try(() => _database.TakeDose(item, DateTime.Now), nameof(TookIt)) is not (true, { } reply))
        {
            return;
        }

        _reply(reply.Text);
        Acted();
    }

    private async void MarkDose(StoredItem item, bool dose)
    {
        if (await Try(() => _database.SetDose(item, dose), nameof(MarkDose)) is not (true, var marked))
        {
            return;
        }

        _reply(marked
            ? dose ? $"{item.Title} is now a dose. Its reminder says {CardLabels.TookIt}." : $"{item.Title} is a plain reminder again."
            : "Only a repeating reminder can be a dose.");
        Acted();
    }

    /// <summary>What a message about this item is tied to, so finishing or changing it clears that message.</summary>
    private static object About(StoredItem item) => (item.Source, item.Id);

    private async void Start(StoredItem item)
    {
        var reference = ItemRef.Format(item);
        if (await Try(() => _database.StartActivation(reference, StartMethod, DateTime.Now), nameof(Start)) is (true, { } started))
        {
            _reply(started.Text);
            Acted();
        }
    }

    /// <summary>Uses the fixed actions, so it works offline and never waits on the sorter.</summary>
    private async void StartSmaller(StoredItem item)
    {
        if (await Try(() => _database.StartSmaller(item, StartMethod, DateTime.Now), nameof(StartSmaller)) is (true, { } started))
        {
            _reply(started.Text);
            Acted();
        }
    }

    /// <summary>
    /// Moves one row, and the bar's Undo moves it back to its day and time, when that can be put back. A feed item
    /// that was on its feed's date goes back to following the feed.
    /// </summary>
    private async void Move(StoredItem item, DateOnly date, TimeOnly? time, bool movedFromFeed)
    {
        Bar.Forget(About(item));
        var (ok, applied) = await Try(() => _database.RescheduleItem(item, date, time, DateTime.Now), nameof(Move));
        if (!ok)
        {
            return;
        }

        bool StillMoved(StoredItem now) => now.DueDate == date && (time is null || item.Recurs is not null || (now.DueAt is { } at && TimeOnly.FromDateTime(at.LocalDateTime) == time));
        if (applied.Applied && item.Source == ItemSource.Calendar && !movedFromFeed)
        {
            Report(applied, () => UndoIfUnchanged(item, StillMoved, () => _database.ChangeItem(new ItemChange(item.Source, item.Id, ChangeAction.UseFeedDate), DateTime.Now)));
            return;
        }

        var back = MoveBack(item, time);
        if (!applied.Applied || back.Why is not null)
        {
            Report(applied, note: applied.Applied ? back.Why : null);
            return;
        }

        Report(applied, () => UndoIfUnchanged(item, StillMoved, () => _database.RescheduleItem(item with { DueDate = date }, back.Date, back.Time, DateTime.Now)));
    }

    /// <summary>
    /// Puts a change back only while the item is still as that change left it. If it's been changed or finished since,
    /// it's left alone and this says so, so an Undo never writes over something newer.
    /// </summary>
    private string? UndoIfUnchanged(StoredItem item, Func<StoredItem, bool> stillAsLeft, Action undo)
    {
        var now = _database.GetOpenItems().FirstOrDefault(found => found.Source == item.Source && found.Id == item.Id);
        if (now is null || now.Done || !stillAsLeft(now))
        {
            return ChangedSince(item.Title);
        }

        undo();
        return null;
    }

    /// <summary>What the bar says when an Undo finds the item changed since.</summary>
    internal static string ChangedSince(string title) => $"{title} has changed since, so it was left as it is.";

    /// <summary>
    /// Where Undo moves an item back to. A move only ever sets a day or a time, never clears one, so an item that had
    /// no day, or no time before one was picked, can't be put back, and Why says so.
    /// </summary>
    internal static (DateOnly Date, TimeOnly? Time, string? Why) MoveBack(StoredItem item, TimeOnly? time)
    {
        if (item.DueDate is not { } was)
        {
            return (default, null, ". Undo isn't available, because it had no date before.");
        }

        var wasTime = item.Recurs is null && item.DueAt is { } at ? TimeOnly.FromDateTime(at.LocalDateTime) : (TimeOnly?)null;
        if (item.Recurs is null && time is not null && wasTime is null)
        {
            return (was, null, ". Undo isn't available, because it had no time before.");
        }

        return (was, wasTime, null);
    }

    private void PickDate(StoredItem item, DateOnly today, bool moved)
    {
        var time = item.DueAt is { } dueAt ? TimeOnly.FromDateTime(dueAt.LocalDateTime) : (TimeOnly?)null;
        var picker = new PickDateWindow(Window.GetWindow(this), item.Title, item.DueDate ?? today, time, asksForTime: item.Recurs is null);
        if (picker.ShowDialog() == true)
        {
            Move(item, picker.Date, picker.Time, moved);
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

    /// <summary>A change from the row's menu. A rename can be undone from the bar by renaming it back.</summary>
    private async void Change(StoredItem item, ChangeAction action, string? title = null)
    {
        Bar.Forget(About(item));
        var (ok, applied) = await Try(() => _database.ChangeItem(new ItemChange(item.Source, item.Id, action, title), DateTime.Now), nameof(Change));
        if (!ok)
        {
            return;
        }

        var renamed = action == ChangeAction.Update && title is not null && applied.Applied && title != item.Title;
        Report(applied, renamed
            ? () => UndoIfUnchanged(item, now => now.Title == applied.Title, () => _database.ChangeItem(new ItemChange(item.Source, item.Id, ChangeAction.Update, item.Title), DateTime.Now))
            : null);
    }

    /// <summary>
    /// Says on the page's bar what a row's change did, with Undo when there's a way back. The undo answers what to
    /// say when it couldn't put things back, or null when it did.
    /// </summary>
    private void Report(AppliedChange applied, Func<string?>? undo = null, string? note = null)
    {
        var about = (applied.Change.Source, applied.Change.ItemId);
        Bar.Show(ItemGroups.ChangeSummary(applied) + note, undo is null ? null : async () =>
        {
            if (await Try(undo, "UndoChange") is (true, var left))
            {
                if (left is not null)
                {
                    Bar.Show(left);
                }

                Acted();
            }
        }, about);
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
