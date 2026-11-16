using System.Windows;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.App.Tests;

/// <summary>The right-click menus on To do and History rows, checked against what <see cref="RowActions"/> decides.</summary>
public sealed class RowMenuTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private static StoredItem Store(WaypointDatabase database, SortedItem item)
    {
        database.CompleteCapture(database.AddCapture(item.Title), [item]);
        return database.GetOpenItems().Single(stored => stored.Title == item.Title);
    }

    private static StoredItem StoreEvent(WaypointDatabase database, string title, DateOnly due)
    {
        database.ReplaceCalendarEvents([new CalendarEvent(title, title, "CS 101", due, null)]);
        return database.GetOpenItems().Single(stored => stored.Title == title);
    }

    private static string Label(RowAction action, StoredItem item) => action switch
    {
        RowAction.Done => "Mark done",
        RowAction.Reopen => "Reopen",
        RowAction.Start => "Start now",
        RowAction.SmallerStep => "Start with a tiny step",
        RowAction.Study => "Study for this exam",
        RowAction.Reschedule => "Reschedule",
        RowAction.UseFeedDate => "Use feed date",
        RowAction.Rename => "Rename",
        RowAction.BreakDown => "Break down",
        RowAction.File => "Move to filter",
        RowAction.StopReminders => "Stop reminders",
        RowAction.Cancel => "Delete",
        RowAction.Delete => "Delete",
        _ => "Copy title",
    };

    /// <summary>What row actions decides, with This didn't help just before Delete, which stays last on its own.</summary>
    private static List<string> Expected(WaypointDatabase database, StoredItem item)
    {
        var labels = RowActions.For(item, Today, database.IsMovedFromFeed(item)).Select(action => Label(action, item)).ToList();
        if (!item.Done)
        {
            labels.Insert(labels.IndexOf("Delete"), TodayView.FeedbackMenuLabel);
        }

        return labels;
    }

    private static TodayView MakeToday(UiTest ui, List<string>? submitted = null)
    {
        var view = new TodayView();
        view.Connect(ui.Database, () => { }, text => submitted?.Add(text), _ => { }, () => { });
        ui.Host(view);
        view.Refresh(null);
        ui.Pump();
        return view;
    }

    private static CheckBox RowFor(UiTest ui, FrameworkElement view, string title)
    {
        view.UpdateLayout();
        return UiTest.Descendants<CheckBox>(view)
            .First(box => box.DataContext is { } row && row.GetType().Name == "ItemRow" && UiTest.Prop<string>(row, "Title") == title);
    }

    [Fact]
    public void A_task_row_offers_what_row_actions_decides_and_mark_done_works() => Ui.Run(ui =>
    {
        var task = Store(ui.Database, new SortedItem(ItemKind.Task, "write paper", Today.AddDays(2), null));
        var view = MakeToday(ui);

        var menu = ui.RightClick(RowFor(ui, view, "write paper"))!;

        Assert.Equal(["Mark done", "Start now", "Start with a tiny step", "Break down", "Reschedule", "Rename", "Stop reminders", "Waiting on someone...", "Copy title", "This didn't help...", "Delete"], UiTest.Labels(menu));

        // Doing, changing, copying, and Delete on its own at the bottom, with a line between each part.
        var parts = string.Join("|", menu.Items.Cast<object>().Select(entry => entry is Separator ? "-" : (string)((MenuItem)entry).Header));
        Assert.Equal("Mark done|Start now|Start with a tiny step|Break down|-|Reschedule|Rename|Stop reminders|Waiting on someone...|-|Copy title|-|This didn't help...|-|Delete", parts);
        Assert.Equal(Expected(ui.Database, task), UiTest.Labels(menu).Where(label => label != "Waiting on someone...").ToList());
        var reschedule = menu.Items.OfType<MenuItem>().Single(item => (string)item.Header == "Reschedule");
        Assert.Equal(["Today", "Tomorrow", "Next week", "Pick a date..."], UiTest.Labels(reschedule));

        ui.Choose(menu, "Mark done");
        Assert.DoesNotContain(ui.Database.GetOpenItems(), item => item.Title == "write paper");
    });

    private const string LongTask = "Ask Morgan Vance whether the book club can meet in the library annex, and if not, find two cafes with a quiet back room before the Saturday meetup.";

    private static TextBlock FullText(CheckBox row) => UiTest.Descendants<TextBlock>(row).Single(block => block.Name == "FullText");

    [Fact]
    public void A_long_task_shows_its_whole_text_only_while_selected_or_opened() => Ui.Run(ui =>
    {
        ui.Database.CompleteCapture(ui.Database.AddCapture(LongTask), [new SortedItem(ItemKind.Task, LongTask, null, null)]);
        var task = ui.Database.GetOpenItems().Single();
        var view = MakeToday(ui);

        var row = RowFor(ui, view, task.Title);
        Assert.Equal(LongTask, FullText(row).Text);
        Assert.Equal(Visibility.Collapsed, FullText(row).Visibility);

        // Selected, as Ctrl-click or a link from Ask does, it shows under the title in the smaller detail text.
        view.Reveal(task);
        ui.Pump();
        row = RowFor(ui, view, task.Title);
        Assert.Equal(Visibility.Visible, FullText(row).Visibility);
        Assert.True(FullText(row).FontSize < UiTest.Descendants<TextBlock>(row).First(block => block.Text == task.Title).FontSize);

        // Opened from the menu, it stays through a refresh after the selection is let go.
        var menu = ui.RightClick(row)!;
        Assert.Contains("Show full text", UiTest.Labels(menu));
        ui.Choose(menu, "Show full text");
        view.Refresh(null);
        ui.Pump();
        row = RowFor(ui, view, task.Title);
        Assert.Equal(Visibility.Visible, FullText(row).Visibility);

        menu = ui.RightClick(row)!;
        ui.Choose(menu, "Hide full text");
        ui.Pump();
        Assert.Equal(Visibility.Collapsed, FullText(RowFor(ui, view, task.Title)).Visibility);
    });

    [Fact]
    public void A_short_task_has_no_full_text_to_show() => Ui.Run(ui =>
    {
        Store(ui.Database, new SortedItem(ItemKind.Task, "buy stamps", null, null));
        var view = MakeToday(ui);

        var row = RowFor(ui, view, "buy stamps");
        Assert.DoesNotContain("Show full text", UiTest.Labels(ui.RightClick(row)!));
        Assert.Equal(Visibility.Collapsed, FullText(row).Visibility);
    });

    [Fact]
    public void A_wait_shows_under_waiting_on_and_offers_follow_up_actions() => Ui.Run(ui =>
    {
        ui.Database.AddWaitingByHand("Prof. Quill", "the extension", null, null, DateTime.Now);
        var view = MakeToday(ui);
        bool Shown() => UiTest.Descendants<CheckBox>(view).Any(box => box.DataContext is { } row && row.GetType().Name == "ItemRow"
            && UiTest.Prop<string>(row, "Title") == "Hear back from Prof. Quill about the extension");

        view.UpdateLayout();
        Assert.False(Shown());

        UiTest.Named<NestedColumn>(view, "Column").Select("waiting");
        view.Refresh(null);
        view.UpdateLayout();
        Assert.True(Shown());

        var menu = ui.RightClick(RowFor(ui, view, "Hear back from Prof. Quill about the extension"))!;
        var labels = UiTest.Labels(menu).ToList();
        var draft = labels.IndexOf("Copy follow-up draft");
        Assert.Equal(["Copy follow-up draft", "Still waiting", "Not waiting anymore"], labels.Skip(draft).Take(3));
        Assert.Equal("Delete", labels[^1]);

        ui.Choose(menu, "Still waiting", "Next week");
        Assert.Equal(Today.AddDays(7), ui.Database.GetWaiting().Single().FollowUpOn);
    });

    [Fact]
    public void A_feed_event_row_has_no_rename_and_can_be_put_back_on_its_feed_date() => Ui.Run(ui =>
    {
        var quiz = StoreEvent(ui.Database, "quiz 4", Today.AddDays(2));
        var view = MakeToday(ui);

        var menu = ui.RightClick(RowFor(ui, view, "quiz 4"))!;
        Assert.Equal(Expected(ui.Database, quiz), UiTest.Labels(menu));
        Assert.DoesNotContain("Rename", UiTest.Labels(menu));
        Assert.DoesNotContain("Use feed date", UiTest.Labels(menu));

        ui.Choose(menu, "Reschedule", "Next week");
        var moved = ui.Database.GetOpenItems().Single(item => item.Title == "quiz 4");
        Assert.Equal(Today.AddDays(7), moved.DueDate);

        view.Refresh(null);
        ui.Pump();
        menu = ui.RightClick(RowFor(ui, view, "quiz 4"))!;
        Assert.Equal(Expected(ui.Database, moved), UiTest.Labels(menu));
        Assert.Contains("Use feed date", UiTest.Labels(menu));

        ui.Choose(menu, "Use feed date");
        Assert.Equal(Today.AddDays(2), ui.Database.GetOpenItems().Single(item => item.Title == "quiz 4").DueDate);
    });

    [Fact]
    public void A_recurring_row_offers_the_same_actions_and_moves_only_this_occurrence() => Ui.Run(ui =>
    {
        var chore = Store(ui.Database, new SortedItem(ItemKind.Reminder, "laundry", Today.AddDays(1), null, new TimeOnly(9, 0), Recurs: new Recurrence(RecurrenceUnit.Week)));
        var view = MakeToday(ui);

        var menu = ui.RightClick(RowFor(ui, view, "laundry"))!;
        Assert.Equal(Expected(ui.Database, chore), UiTest.Labels(menu));
        Assert.Contains("Rename", UiTest.Labels(menu));

        ui.Choose(menu, "Reschedule", "Next week");

        var moved = ui.Database.GetOpenItems().Single(item => item.Title == "laundry");
        Assert.Equal(Today.AddDays(7), moved.DueDate);
        Assert.NotNull(moved.Recurs);
    });

    [Fact]
    public void A_refresh_waits_while_a_row_menu_is_open() => Ui.Run(ui =>
    {
        Store(ui.Database, new SortedItem(ItemKind.Task, "first", Today.AddDays(1), null));
        var view = MakeToday(ui);
        var row = RowFor(ui, view, "first");
        var menu = ui.RightClick(row)!;

        Store(ui.Database, new SortedItem(ItemKind.Task, "second", Today.AddDays(1), null));
        view.Refresh(null);
        ui.Pump();
        Assert.Same(row, RowFor(ui, view, "first"));
        Assert.DoesNotContain(UiTest.Descendants<CheckBox>(view), box => box.DataContext?.GetType().Name == "ItemRow" && UiTest.Prop<string>(box.DataContext, "Title") == "second");

        ui.Close(menu);
        Assert.False(UiTest.Field<bool>(view, "_menuOpen"), $"menu still counted as open; menus={ui.Menus.Count} open={string.Join(",", ui.Menus.Select(m => m.IsOpen))}");
        view.Refresh(null);
        ui.Pump();
        Assert.NotNull(RowFor(ui, view, "second"));
    });

    private static bool HasRow(FrameworkElement view, string title)
    {
        view.UpdateLayout();
        return UiTest.Descendants<CheckBox>(view).Any(box => box.DataContext is { } row && row.GetType().Name == "ItemRow" && UiTest.Prop<string>(row, "Title") == title);
    }

    [Fact]
    public void A_row_deleted_from_its_menu_leaves_the_list_at_once_and_undo_brings_it_back() => Ui.Run(ui =>
    {
        Store(ui.Database, new SortedItem(ItemKind.Task, "clean the fridge", Today.AddDays(1), null));
        Store(ui.Database, new SortedItem(ItemKind.Task, "call the bank", Today.AddDays(1), null));
        var view = MakeToday(ui);
        var row = RowFor(ui, view, "clean the fridge");

        ui.Choose(ui.RightClick(row)!, "Delete");

        Assert.False(HasRow(view, "clean the fridge"));
        Assert.True(HasRow(view, "call the bank"));
        Assert.Equal("Canceled", ui.Database.SearchHistory("clean the fridge", 10).First().Label);

        var bar = UiTest.Named<UndoBar>(view, "Bar");
        Assert.Equal(Visibility.Visible, bar.Visibility);
        UiTest.Call(bar, "OnUndoClicked", bar, new RoutedEventArgs());
        ui.Pump();
        Assert.True(HasRow(view, "clean the fridge"));
    });

    [Fact]
    public void History_offers_reopen_and_reopening_puts_the_item_back() => Ui.Run(ui =>
    {
        var task = Store(ui.Database, new SortedItem(ItemKind.Task, "return books", Today, null));
        ui.Database.SetDone(task, true);
        var changed = 0;
        var view = new HistoryView();
        view.Connect(ui.Database, () => changed++);
        ui.Host(view);
        view.Refresh();
        ui.Pump();

        var entries = UiTest.Named<ItemsControl>(view, "Entries");
        entries.UpdateLayout();
        var row = UiTest.Descendants<DockPanel>(entries).First(panel => panel.DataContext?.GetType().Name == "HistoryRow" && UiTest.Prop<string>(panel.DataContext, "Title") == "return books");

        var menu = ui.RightClick(row)!;
        Assert.Equal(["Reopen", "Delete", "Copy title"], UiTest.Labels(menu));

        ui.Choose(menu, "Reopen");
        Assert.Contains(ui.Database.GetOpenItems(), item => item.Title == "return books");
        Assert.Equal(1, changed);
    });
}
