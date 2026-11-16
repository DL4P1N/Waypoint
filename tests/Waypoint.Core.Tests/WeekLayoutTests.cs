using Waypoint.Core.Capture;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Tests;

public sealed class WeekLayoutTests
{
    private static readonly DateOnly Monday = new(2026, 11, 2);

    private static WeekEntry Block(string title, int startHour, int startMinute, int endHour, int endMinute, DateOnly? date = null, WeekBlockKind kind = WeekBlockKind.Event) =>
        new(date ?? Monday, new TimeOnly(startHour, startMinute), new TimeOnly(endHour, endMinute), title, kind);

    private static TimeSpan At(int hour, int minute = 0) => new(hour, minute, 0);

    [Fact]
    public void Weeks_start_on_Sunday()
    {
        var sunday = new DateOnly(2026, 11, 1);
        Assert.Equal(sunday, WeekLayout.StartOf(Monday));
        Assert.Equal(sunday, WeekLayout.StartOf(sunday));
        Assert.Equal(sunday, WeekLayout.StartOf(new DateOnly(2026, 11, 7)));
        Assert.Equal(new DateOnly(2026, 11, 8), WeekLayout.StartOf(new DateOnly(2026, 11, 8)));
    }

    [Fact]
    public void The_label_names_the_month_once_unless_the_week_crosses_one()
    {
        Assert.Equal("Nov 1 to 7", WeekLayout.Label(new DateOnly(2026, 11, 1)));
        Assert.Equal("Nov 29 to Dec 5", WeekLayout.Label(new DateOnly(2026, 11, 29)));
        Assert.Equal("Dec 27 to Jan 2, 2027", WeekLayout.Label(new DateOnly(2026, 12, 27)));
    }

    [Fact]
    public void Lengths_read_plainly()
    {
        Assert.Equal("45m", WeekLayout.Length(TimeSpan.FromMinutes(45)));
        Assert.Equal("2h", WeekLayout.Length(TimeSpan.FromHours(2)));
        Assert.Equal("1h 30m", WeekLayout.Length(TimeSpan.FromMinutes(90)));
    }

    [Fact]
    public void Blocks_that_dont_overlap_each_get_the_full_width()
    {
        var placed = WeekLayout.Place([Block("a", 9, 0, 10, 0), Block("b", 10, 0, 11, 0)], TimeSpan.FromMinutes(30));

        Assert.All(placed, block => Assert.Equal((0, 1), (block.Column, block.Columns)));
    }

    [Fact]
    public void Overlapping_blocks_share_the_width_side_by_side()
    {
        var placed = WeekLayout.Place([Block("b", 9, 30, 10, 30), Block("a", 9, 0, 10, 0)], TimeSpan.FromMinutes(30));

        var a = placed.Single(block => block.Entry.Title == "a");
        var b = placed.Single(block => block.Entry.Title == "b");
        Assert.Equal((0, 2), (a.Column, a.Columns));
        Assert.Equal((1, 2), (b.Column, b.Columns));
    }

    [Fact]
    public void A_chain_of_overlaps_is_one_cluster_and_a_freed_column_is_reused()
    {
        // a overlaps b, b overlaps c, but a and c don't touch, so c goes back in a's column.
        var placed = WeekLayout.Place([Block("a", 9, 0, 10, 0), Block("b", 9, 30, 11, 0), Block("c", 10, 0, 12, 0), Block("d", 13, 0, 14, 0)], TimeSpan.FromMinutes(30));

        Assert.Equal((0, 2), (placed.Single(block => block.Entry.Title == "a").Column, placed.Single(block => block.Entry.Title == "a").Columns));
        Assert.Equal(1, placed.Single(block => block.Entry.Title == "b").Column);
        Assert.Equal((0, 2), (placed.Single(block => block.Entry.Title == "c").Column, placed.Single(block => block.Entry.Title == "c").Columns));
        Assert.Equal((0, 1), (placed.Single(block => block.Entry.Title == "d").Column, placed.Single(block => block.Entry.Title == "d").Columns));
    }

    [Fact]
    public void Three_at_once_make_three_columns()
    {
        var placed = WeekLayout.Place([Block("a", 9, 0, 11, 0), Block("b", 9, 0, 10, 0), Block("c", 9, 15, 9, 45)], TimeSpan.FromMinutes(30));

        Assert.All(placed, block => Assert.Equal(3, block.Columns));
        Assert.Equal([0, 1, 2], placed.Select(block => block.Column).Order());
    }

    [Fact]
    public void A_marker_is_drawn_with_a_short_length_and_late_events_stop_at_midnight()
    {
        var marker = new WeekEntry(Monday, new TimeOnly(9, 0), null, "stretch", WeekBlockKind.Marker);
        var late = Block("party", 23, 0, 1, 0);

        var placed = WeekLayout.Place([marker, late], TimeSpan.FromMinutes(20));

        Assert.Equal((At(9), At(9, 20)), (placed[0].From, placed[0].To));
        Assert.Equal((At(23), At(24)), (placed[1].From, placed[1].To));
    }

    [Fact]
    public void Free_gaps_fill_between_busy_stretches_and_skip_short_ones()
    {
        var gaps = WeekLayout.FreeGaps(Monday, [(At(9), At(10)), (At(9, 30), At(11)), (At(11, 15), At(12))], At(8), At(14), TimeSpan.FromMinutes(30));

        Assert.Equal([(At(8), At(9)), (At(12), At(14))], gaps.Select(gap => (gap.From, gap.To)));
        Assert.Equal(TimeSpan.FromHours(2), gaps[1].Length);
    }

    [Fact]
    public void Busy_time_outside_the_window_is_clipped_and_an_empty_day_is_all_free()
    {
        Assert.Equal([(At(10), At(22))], WeekLayout.FreeGaps(Monday, [(At(6), At(10))], At(8), At(22), TimeSpan.FromMinutes(30)).Select(gap => (gap.From, gap.To)));
        Assert.Equal([(At(8), At(22))], WeekLayout.FreeGaps(Monday, [], At(8), At(22), TimeSpan.FromMinutes(30)).Select(gap => (gap.From, gap.To)));
        Assert.Empty(WeekLayout.FreeGaps(Monday, [(At(7), At(23))], At(8), At(22), TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void Events_get_blocks_reminders_and_exams_get_markers_and_the_rest_go_in_the_top_strip()
    {
        var at = new DateTimeOffset(Monday.ToDateTime(new TimeOnly(14, 0)));
        var meeting = new StoredItem(1, ItemKind.Event, "meeting", Monday, null, false, DueAt: at);
        var stretch = new StoredItem(2, ItemKind.Reminder, "stretch", Monday, null, false, DueAt: at);
        var exam = new StoredItem(3, ItemKind.Exam, "midterm", Monday, null, false, DueAt: at);
        var paper = new StoredItem(4, ItemKind.Deadline, "paper", Monday, null, false, DueAt: at);
        var laundry = new StoredItem(5, ItemKind.Task, "laundry", Monday, null, false);
        var someday = new StoredItem(6, ItemKind.Task, "someday", null, null, false);

        Assert.Equal(new TimeOnly(15, 0), WeekLayout.ItemEntry(meeting, null)!.End);
        Assert.Equal(new TimeOnly(16, 30), WeekLayout.ItemEntry(meeting, new TimeOnly(16, 30))!.End);
        Assert.Equal(WeekBlockKind.Marker, WeekLayout.ItemEntry(stretch, null)!.Kind);
        Assert.Equal(WeekBlockKind.Marker, WeekLayout.ItemEntry(exam, null)!.Kind);
        Assert.Null(WeekLayout.ItemEntry(paper, null));
        Assert.True(WeekLayout.IsAllDay(paper));
        Assert.True(WeekLayout.IsAllDay(laundry));
        Assert.False(WeekLayout.IsAllDay(someday));
        Assert.False(WeekLayout.IsAllDay(meeting));
    }

    [Fact]
    public void Classes_come_from_the_schedule_for_each_day_of_the_week()
    {
        var monday = new DateOnly(2026, 10, 12);
        var schedule = new ClassSchedule(
            new DateOnly(2026, 9, 14),
            new DateOnly(2026, 12, 18),
            [new ClassMeeting("CS 220-01 Term 1", "Room 1", [DayOfWeek.Monday, DayOfWeek.Friday], new TimeOnly(10, 50), new TimeOnly(12, 10))],
            [new DateOnly(2026, 10, 16)]);

        var classes = WeekLayout.ClassEntries(schedule, monday);

        var only = Assert.Single(classes);
        Assert.Equal(monday, only.Date);
        Assert.Equal("CS 220", only.Title);
        Assert.Equal("Room 1", only.Detail);
        Assert.Empty(WeekLayout.ClassEntries(null, Monday));
    }

    [Fact]
    public void A_class_filtered_out_of_view_still_takes_up_its_time()
    {
        var shown = new[] { Block("meeting", 13, 0, 14, 0, Monday.AddDays(1)) };
        var busy = shown.Append(Block("CS 220", 9, 0, 12, 0, Monday.AddDays(1), WeekBlockKind.Class));

        var plan = WeekLayout.Build(Monday, shown, [], busy, Monday.ToDateTime(new TimeOnly(7, 0)));

        var tuesday = plan.Days[1];
        Assert.Single(tuesday.Blocks);
        Assert.Equal([(At(8), At(9)), (At(12), At(13)), (At(14), At(22))], tuesday.Free.Select(gap => (gap.From, gap.To)));
    }

    [Fact]
    public void Past_days_have_no_free_time_and_today_starts_from_now()
    {
        var wednesday = Monday.AddDays(2);
        var plan = WeekLayout.Build(Monday, [], [], [], wednesday.ToDateTime(new TimeOnly(15, 5)));

        Assert.Empty(plan.Days[0].Free);
        Assert.Empty(plan.Days[1].Free);
        Assert.Equal([(At(15, 15), At(22))], plan.Days[2].Free.Select(gap => (gap.From, gap.To)));
        Assert.Equal([(At(8), At(22))], plan.Days[3].Free.Select(gap => (gap.From, gap.To)));
        Assert.Equal(7, plan.Days.Count);
    }

    [Fact]
    public void Markers_never_take_up_free_time()
    {
        var stretch = new WeekEntry(Monday.AddDays(3), new TimeOnly(12, 0), null, "stretch", WeekBlockKind.Marker);

        var plan = WeekLayout.Build(Monday, [stretch], [], [stretch], Monday.ToDateTime(TimeOnly.MinValue));

        Assert.Single(plan.Days[3].Blocks);
        Assert.Equal([(At(8), At(22))], plan.Days[3].Free.Select(gap => (gap.From, gap.To)));
    }

    [Fact]
    public void The_grid_grows_to_fit_early_and_late_blocks()
    {
        var plan = WeekLayout.Build(Monday, [Block("shift", 6, 30, 9, 0, kind: WeekBlockKind.Work), Block("show", 21, 0, 23, 15)], [], [], Monday.ToDateTime(TimeOnly.MinValue));

        Assert.Equal((6, 24), (plan.FirstHour, plan.LastHour));
        var empty = WeekLayout.Build(Monday, [], [], [], Monday.ToDateTime(TimeOnly.MinValue));
        Assert.Equal((8, 22), (empty.FirstHour, empty.LastHour));
    }

    [Fact]
    public void Dated_items_go_in_their_days_top_strip()
    {
        var paper = new StoredItem(4, ItemKind.Deadline, "paper", Monday.AddDays(4), null, false);

        var plan = WeekLayout.Build(Monday, [], [paper], [], Monday.ToDateTime(TimeOnly.MinValue));

        Assert.Same(paper, Assert.Single(plan.Days[4].AllDay));
        Assert.All(plan.Days.Where(day => day.Date != Monday.AddDays(4)), day => Assert.Empty(day.AllDay));
    }

    [Fact]
    public void Shifts_become_work_blocks()
    {
        var shift = new WorkShift(Monday, new TimeOnly(17, 0), new TimeOnly(21, 0));

        var entry = Assert.Single(WeekLayout.ShiftEntries([shift]));
        Assert.Equal((WeekBlockKind.Work, "Work", new TimeOnly(21, 0)), (entry.Kind, entry.Title, entry.End));
    }
}
