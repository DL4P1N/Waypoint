using Microsoft.Data.Sqlite;
using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

public sealed class ToDoFilterTests : IDisposable
{
    // Read once per test, so a run that goes past midnight still sees one day throughout.
    private readonly DateTime _now = DateTime.Now;

    private DateOnly Today => DateOnly.FromDateTime(_now);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public ToDoFilterTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private void Feed(params (string Title, string? Course)[] events) =>
        _database.ReplaceCalendarEvents(events.Select(found => new CalendarEvent(found.Title, found.Title, found.Course, Today.AddDays(3), null)).ToList());

    private StoredItem Add(HandAddedItem item) => _database.AddByHand(item, _now).Item!;

    private StoredItem Open(string title) => _database.GetOpenItems().Single(item => item.Title == title);

    private bool Shows(ToDoFilter? filter, StoredItem item) =>
        ToDoFilters.Matches(filter, item, _database.GetItemPlacements().GetValueOrDefault(item.Id));

    [Fact]
    public void Classes_come_from_the_feed_and_schedule_then_Personal_then_custom()
    {
        Feed(("Paper", "CS 220-01 Term 1"), ("Quiz", "MATH 130-02 Term 1"), ("Holiday", null));
        _database.ScheduleCourses = ["CS 220", "WRIT 110"];
        _database.CreateToDoFilter("Work");

        var filters = _database.GetToDoFilters();

        Assert.Equal(["CS 220", "MATH 130", "WRIT 110", "Personal", "Work"], filters.Select(filter => filter.Name));
        Assert.Equal([FilterSource.Class, FilterSource.Class, FilterSource.Class, FilterSource.Personal, FilterSource.Custom], filters.Select(filter => filter.Source));
    }

    [Fact]
    public void Syncing_again_adds_nothing_twice()
    {
        Feed(("Paper", "CS 220-01 Term 1"));
        var first = _database.GetToDoFilters();
        Feed(("Paper", "CS 220-01 Term 1"), ("Lab", "CS 220-01 Term 1"));

        Assert.Equal(first, _database.GetToDoFilters());
    }

    [Fact]
    public void Renaming_a_class_keeps_matching_its_course_after_a_sync()
    {
        Feed(("Paper", "CS 220-01 Term 1"));
        var cs = _database.GetToDoFilters().Single(filter => filter.Source == FilterSource.Class);

        var renamed = _database.RenameToDoFilter(cs.Id, "Software");
        Feed(("Paper", "CS 220-01 Term 1"), ("Lab", "CS 220-01 Term 1"));

        var filter = _database.GetToDoFilters().Single(found => found.Source == FilterSource.Class);
        Assert.Null(renamed.Problem);
        Assert.Equal("Software", filter.Name);
        Assert.Equal("CS 220", filter.Course);
        Assert.True(Shows(filter, Open("Lab")));
        Assert.False(Shows(_database.GetToDoFilters().Single(found => found.Source == FilterSource.Personal), Open("Lab")));
    }

    [Fact]
    public void Personal_can_be_renamed_but_names_stay_unique()
    {
        _database.CreateToDoFilter("Work");
        var personal = _database.GetToDoFilters().Single(filter => filter.Source == FilterSource.Personal);

        Assert.NotNull(_database.RenameToDoFilter(personal.Id, "work").Problem);
        Assert.NotNull(_database.CreateToDoFilter("  ").Problem);
        Assert.NotNull(_database.CreateToDoFilter("WORK").Problem);
        Assert.Null(_database.RenameToDoFilter(personal.Id, "Home").Problem);
        Assert.Contains(_database.GetToDoFilters(), filter => filter.Name == "Home" && filter.Source == FilterSource.Personal);
    }

    [Fact]
    public void Dragged_order_is_kept()
    {
        var work = _database.CreateToDoFilter("Work").Filter!;
        var chores = _database.CreateToDoFilter("Chores").Filter!;

        _database.SetToDoFilterOrder([chores.Id, work.Id]);

        Assert.Equal(["Personal", "Chores", "Work"], _database.GetToDoFilters().Select(filter => filter.Name));
    }

    [Fact]
    public void An_item_with_no_class_moves_between_Personal_and_a_custom_filter()
    {
        var work = _database.CreateToDoFilter("Work").Filter!;
        var personal = _database.GetToDoFilters().Single(filter => filter.Source == FilterSource.Personal);
        var item = Add(new HandAddedItem(ItemKind.Task, "Send timesheet", null));

        Assert.True(Shows(personal, item));
        Assert.True(_database.AssignToFilter(item, work.Id));
        Assert.True(Shows(work, item));
        Assert.False(Shows(personal, item));
        Assert.True(Shows(null, item));

        _database.AssignToFilter(item, null);
        Assert.True(Shows(personal, item));
    }

    [Fact]
    public void Feed_items_and_items_with_a_class_cant_go_in_a_custom_filter()
    {
        Feed(("Paper", "CS 220-01 Term 1"));
        var work = _database.CreateToDoFilter("Work").Filter!;
        var classItem = Add(new HandAddedItem(ItemKind.Task, "Read chapter 4", null, Course: "CS 220"));

        Assert.False(_database.AssignToFilter(Open("Paper"), work.Id));
        Assert.False(_database.AssignToFilter(classItem, work.Id));
        Assert.True(Shows(_database.GetToDoFilters().Single(filter => filter.Source == FilterSource.Class), classItem));
    }

    [Fact]
    public void Removing_a_filter_sends_its_items_to_Personal_and_restoring_brings_them_back()
    {
        var work = _database.CreateToDoFilter("Work").Filter!;
        var item = Add(new HandAddedItem(ItemKind.Task, "Send timesheet", null, FilterId: work.Id));
        var personal = _database.GetToDoFilters().Single(filter => filter.Source == FilterSource.Personal);

        Assert.True(_database.RemoveToDoFilter(work.Id, _now));
        Assert.False(_database.RemoveToDoFilter(personal.Id, _now));
        Assert.DoesNotContain(_database.GetToDoFilters(), filter => filter.Name == "Work");
        Assert.True(Shows(personal, item));

        _database.RestoreToDoFilter(work.Id);
        Assert.True(Shows(work, item));
    }

    [Fact]
    public void Search_matches_every_word_in_the_title_or_class()
    {
        var item = new StoredItem(1, ItemKind.Deadline, "Paper #2 draft", Today, null, false, ItemSource.Calendar, "CS 220-01 Term 1");

        Assert.True(ToDoFilters.MatchesSearch("", item, null));
        Assert.True(ToDoFilters.MatchesSearch("draft cs", item, null));
        Assert.False(ToDoFilters.MatchesSearch("draft math", item, null));
    }

    [Fact]
    public void A_hand_added_event_runs_an_hour_unless_given_an_end()
    {
        var day = Today.AddDays(2);
        var meeting = Add(new HandAddedItem(ItemKind.Event, "Advisor meeting", day, new TimeOnly(14, 0)));
        var game = Add(new HandAddedItem(ItemKind.Event, "Game", day, new TimeOnly(18, 0), new TimeOnly(21, 30)));

        var placements = _database.GetItemPlacements();
        Assert.Equal(ItemKind.Event, meeting.Kind);
        Assert.Equal(new TimeOnly(15, 0), placements[meeting.Id].EndTime);
        Assert.Equal(new TimeOnly(21, 30), placements[game.Id].EndTime);
        Assert.True(ItemGroups.IsScheduled(meeting));
    }

    [Fact]
    public void A_typed_event_gets_the_same_default_end()
    {
        var capture = _database.AddCapture("dentist friday at 3");
        _database.CompleteCapture(capture, [new SortedItem(ItemKind.Event, "Dentist", Today.AddDays(3), null, new TimeOnly(15, 0))]);

        var dentist = Open("Dentist");
        Assert.Equal(new TimeOnly(16, 0), _database.GetItemPlacements()[dentist.Id].EndTime);
        Assert.Equal(new TimeOnly(16, 0), EventTimes.EndOf(new SortedItem(ItemKind.Event, "Dentist", Today, null, new TimeOnly(15, 0))));
        Assert.Null(EventTimes.EndOf(new SortedItem(ItemKind.Event, "All day", Today, null)));
        Assert.Null(EventTimes.EndOf(new SortedItem(ItemKind.Reminder, "Call", Today, null, new TimeOnly(15, 0))));
    }

    [Fact]
    public void Moving_an_event_keeps_its_length()
    {
        var meeting = Add(new HandAddedItem(ItemKind.Event, "Study group", Today.AddDays(2), new TimeOnly(14, 0), new TimeOnly(16, 0)));

        _database.RescheduleItem(meeting, Today.AddDays(3), new TimeOnly(9, 30), _now);

        Assert.Equal(new TimeOnly(11, 30), _database.GetItemPlacements()[meeting.Id].EndTime);
    }

    [Fact]
    public void The_sorter_can_send_an_event()
    {
        Assert.Contains("\"event\"", ClaudeCliSorter.Schema);
        var parsed = SortedItemParser.Parse("""{"items":[{"kind":"event","title":"Game","due_date":"2026-11-07","due_time":"18:00","amount_cents":null,"reminders":[],"repeat_minutes":null,"steps":[],"recurrence":null,"category":null,"one_off":false,"about":null}]}""");
        Assert.Equal(ItemKind.Event, parsed.Single().Kind);
    }

    [Fact]
    public void A_hand_added_item_is_stored_like_a_sorted_one_with_no_pending_capture()
    {
        var due = Today.AddDays(4);
        var result = _database.AddByHand(new HandAddedItem(ItemKind.Exam, "Midterm", due, new TimeOnly(10, 0), Course: "MATH 130", RemindBefore: TimeSpan.FromHours(3)), _now);

        Assert.Null(result.Problem);
        Assert.Empty(_database.GetPendingCaptures());
        var midterm = Open("Midterm");
        Assert.Equal(ItemKind.Exam, midterm.Kind);
        Assert.Equal("MATH 130", _database.GetItemPlacements()[midterm.Id].Course);

        // The exam's fixed alerts and the asked-for one are all there.
        var reminders = _database.GetDueReminders(due.ToDateTime(new TimeOnly(10, 0))).Where(reminder => reminder.Item.Id == midterm.Id).ToList();
        Assert.Equal(3, reminders.Count);
    }

    [Fact]
    public void The_add_form_says_what_is_missing()
    {
        Assert.NotNull(_database.AddByHand(new HandAddedItem(ItemKind.Task, " ", null), _now).Problem);
        Assert.NotNull(_database.AddByHand(new HandAddedItem(ItemKind.Deadline, "Paper", null), _now).Problem);
        Assert.NotNull(_database.AddByHand(new HandAddedItem(ItemKind.Note, "Thought", null), _now).Problem);
        Assert.NotNull(_database.AddByHand(new HandAddedItem(ItemKind.Event, "Game", Today.AddDays(1), new TimeOnly(18, 0), new TimeOnly(17, 0)), _now).Problem);
        Assert.NotNull(_database.AddByHand(new HandAddedItem(ItemKind.Reminder, "Call", Today.AddDays(-1), new TimeOnly(9, 0), RemindBefore: TimeSpan.Zero), _now).Problem);
        Assert.Empty(_database.GetOpenItems());
        Assert.NotNull(_database.AddByHand(new HandAddedItem(ItemKind.Task, "Laundry", null), _now).Item);
    }

    [Fact]
    public void Only_a_top_item_with_no_class_offers_a_filter()
    {
        var item = new StoredItem(1, ItemKind.Task, "Send timesheet", null, null, false);
        var step = item with { Id = 2, ParentRef = "c1" };
        var feed = item with { Id = 3, Source = ItemSource.Calendar };

        Assert.Contains(RowAction.File, RowActions.For(item, Today, movedFromFeed: false, canFile: true));
        Assert.DoesNotContain(RowAction.File, RowActions.For(item, Today, movedFromFeed: false));
        Assert.DoesNotContain(RowAction.File, RowActions.For(step, Today, movedFromFeed: false, canFile: true));
        Assert.DoesNotContain(RowAction.File, RowActions.For(feed, Today, movedFromFeed: false, canFile: true));
    }
}
