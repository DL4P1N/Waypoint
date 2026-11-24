using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Waypoint.Core;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Schedule;
using Waypoint.Core.Theme;

namespace Waypoint.App;

/// <summary>The column's filters, adding an item or a filter, and Hide finished.</summary>
public partial class TodayView
{
    private ToDoFilter? SelectedFilter => FilterId(Column.SelectedKey) is { } id ? _filters.FirstOrDefault(filter => filter.Id == id) : null;

    private ToDoFilter Personal => _filters.First(filter => filter.Source == FilterSource.Personal);

    private static string Key(ToDoFilter filter) => $"f{filter.Id}";

    private static long? FilterId(string? key) =>
        key is ['f', .. var digits] && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>All stays at the top. Classes and the user's own filters each keep the order they were dragged into.</summary>
    private void ShowFilters(bool force = false)
    {
        _filters = _database.GetToDoFilters();
        _placements = _database.GetItemPlacements();
        var open = _database.GetOpenItems().Where(item => item.ParentRef is null && ItemGroups.IsToDo(item)).ToList();

        // A line sets the classes and filters apart from All and Waiting on, so a wait doesn't read as a class.
        var entries = _filters.Select((filter, index) =>
        {
            var count = open.Count(item => ToDoFilters.Matches(filter, item, Placement(item)));
            var group = filter.Source switch
            {
                FilterSource.Class => "class",
                FilterSource.Custom => "custom",
                _ => null,
            };
            return new ColumnEntry(Key(filter), count > 0 ? $"{filter.Name}  ·  {count}" : filter.Name, group, CanRename: true, filter.Name, RuleAbove: index == 0);
        });

        LoadWaiting();
        Column.SetEntries(entries.Prepend(WaitingEntry()).Prepend(new ColumnEntry(AllKey, "All")).ToList(), force);
    }

    private ItemPlacement? Placement(StoredItem item) =>
        item.Source == ItemSource.Capture ? _placements.GetValueOrDefault(item.Id) : null;

    private IEnumerable<RowMenuEntry> FilterMenu(ColumnEntry entry) =>
        FilterId(entry.Key) is { } id && _filters.FirstOrDefault(filter => filter.Id == id) is { Source: FilterSource.Custom } filter
            ? [new RowMenuEntry("Remove filter", () => RemoveFilter(filter))]
            : [];

    private async void RemoveFilter(ToDoFilter filter)
    {
        if (!await Work.Run(() => _database.RemoveToDoFilter(filter.Id, DateTime.Now)))
        {
            return;
        }

        Bar.Show($"Removed {filter.Name}. Its items are under {Personal.Name} now.", async () =>
        {
            await Work.Run(() => _database.RestoreToDoFilter(filter.Id));
            FiltersChanged();
        });
        FiltersChanged();
    }

    /// <summary>A rename done at once says its problem to the column as it asks. One on its way to the server says it once it's back.</summary>
    private async void OnFilterRenamed(object? sender, ColumnRenameEventArgs e)
    {
        if (FilterId(e.Entry.Key) is not { } id)
        {
            return;
        }

        var name = e.NewName;
        var renaming = Work.Run(() => _database.RenameToDoFilter(id, name));
        if (renaming.IsCompletedSuccessfully)
        {
            e.Problem = renaming.Result.Problem;
            FiltersChanged();
            return;
        }

        var renamed = await renaming;
        Column.ShowProblem(renamed.Problem);
        FiltersChanged();
    }

    private void OnAddChosen(string? choice)
    {
        if (choice == NewFilterKey)
        {
            NewFilter();
        }
        else if (choice == WaitingKey)
        {
            AddWaiting();
        }
        else if (Enum.TryParse<ItemKind>(choice, out var kind))
        {
            AddItem(kind);
        }
    }

    private async void NewFilter()
    {
        var name = new RenameWindow(Window.GetWindow(this), "", "Add", "Filter name, such as Work", "New filter");
        if (name.ShowDialog() != true)
        {
            return;
        }

        var newName = name.NewName;
        var made = await Work.Run(() => _database.CreateToDoFilter(newName));
        Column.ShowProblem(made.Problem);
        if (made.Filter is { } filter)
        {
            Column.Select(Key(filter));
            FiltersChanged();
        }
    }

    /// <summary>The form starts in whichever class or filter is open, so a new item lands where it's being looked for.</summary>
    private void AddItem(ItemKind kind)
    {
        var personal = new FormPlace(Personal.Name);
        var places = _filters
            .Where(filter => filter.Source != FilterSource.Personal)
            .Select(filter => filter.Source == FilterSource.Class ? new FormPlace(filter.Name, Course: filter.Course) : new FormPlace(filter.Name, FilterId: filter.Id))
            .Prepend(personal)
            .ToList();
        var place = SelectedFilter is { } selected ? places.FirstOrDefault(found => found.Label == selected.Name) : null;

        var form = AddItemWindow.Saving(Window.GetWindow(this), kind, DateOnly.FromDateTime(DateTime.Now), places, place ?? personal, async item =>
        {
            var added = await Work.Run(() => _database.AddByHand(item, DateTime.Now));
            if (added.Item is { } stored)
            {
                Bar.Show($"Added {stored.Title}", about: About(stored));
            }

            return added.Problem;
        });

        if (form.ShowDialog() == true)
        {
            FiltersChanged();
            _changed();
        }
    }

    private void FiltersChanged() => Work.ShowAfterChanges(nameof(FiltersChanged), () =>
    {
        ShowFilters(force: true);
        ShowItems(force: true);
    }, () =>
    {
        ShowFilters();
        ShowItems();
    });

    /// <summary>On any change, so a screen reader's toggle saves too. Showing the saved choice isn't a change.</summary>
    private void OnHideFinishedClicked(object sender, RoutedEventArgs e)
    {
        if (_showingHideFinished)
        {
            return;
        }

        var hide = HideFinished.IsChecked == true ? "1" : "0";
        Work.Fire(() => _database.SetPreference(HideFinishedKey, hide));
        Work.Show(() => ShowItems(force: true), () => ShowItems());
    }

    /// <summary>"Hide 2 done", counting the done rows this filter and search would show, so it's clear what the box hides.</summary>
    private void ShowFinishedCount(IReadOnlyList<StoredItem> everything, ToDoFilter? filter, string search, DateOnly today)
    {
        var finished = everything.Count(item => item.Done && item.ParentRef is null
            && ToDoFilters.Matches(filter, item, Placement(item)) && PassesWaiting(item, search, today)
            && ToDoFilters.MatchesSearch(search, item, Placement(item), ItemGroups.StepsOf(item, everything)));
        HideFinishedText.Text = finished > 0 ? $"Hide {finished} done" : "Hide done";
    }
}
