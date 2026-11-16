using Waypoint.Core.Capture;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Tests;

public sealed class NextActionTests
{
    // A Monday morning, so every date below is fixed and nothing reads the real clock.
    private static readonly DateTime Now = new(2026, 11, 9, 10, 0, 0);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now);
    private static readonly string[] None = [];

    private static long _nextId = 1;

    private static StoredItem Item(string title, ItemKind kind = ItemKind.Task, int? inDays = 0, TimeOnly? at = null, ItemSource source = ItemSource.Capture, string? parent = null, Recurrence? recurs = null)
    {
        DateOnly? date = inDays is { } days ? Today.AddDays(days) : null;
        DateTimeOffset? dueAt = date is { } day && at is { } time ? new DateTimeOffset(day.ToDateTime(time)) : null;
        return new StoredItem(_nextId++, kind, title, date, null, false, source, DueAt: dueAt, ParentRef: parent, Recurs: recurs);
    }

    private static StoredItem[] WithSteps(StoredItem parent, int count) =>
        [parent, .. Enumerable.Range(1, count).Select(index => Item($"{parent.Title} step {index}", parent.Kind, parent.DueDate is null ? null : parent.DueDate.Value.DayNumber - Today.DayNumber, parent: ItemRef.Format(parent)))];

    private static NextActionPick? Pick(IReadOnlyList<StoredItem> open, IReadOnlyList<Commitment>? busy = null, DateTime? now = null, IReadOnlyCollection<string>? skipped = null) =>
        NextAction.Pick(open, busy ?? [], now ?? Now, skipped ?? None);

    private static Commitment At(int hour, int minute, string label = "class", int length = 60)
    {
        var starts = Today.ToDateTime(new TimeOnly(hour, minute));
        return new Commitment(starts, starts.AddMinutes(length), label);
    }

    [Fact]
    public void Suggests_nothing_when_nothing_is_open()
    {
        Assert.Null(Pick([]));
    }

    [Fact]
    public void Picks_what_is_due_soonest_when_sizes_match()
    {
        var later = Item("Later task", inDays: 3);
        var sooner = Item("Sooner task", inDays: 1);

        Assert.Equal("Sooner task", Pick([later, sooner])!.Item.Title);
    }

    [Fact]
    public void An_exam_with_more_left_to_prepare_beats_a_task_due_sooner()
    {
        // Three days out, an exam still needs two days of studying, so it has less to spare than a task due in two.
        var exam = Item("Calc exam", ItemKind.Exam, 3, new TimeOnly(9, 0));
        var task = Item("Email advisor", inDays: 2);

        Assert.Equal("Calc exam", Pick([task, exam])!.Item.Title);
    }

    [Fact]
    public void A_far_off_exam_waits_behind_a_task_due_soon()
    {
        var exam = Item("Final", ItemKind.Exam, 10, new TimeOnly(9, 0));
        var task = Item("Email advisor", inDays: 1);

        Assert.Equal("Email advisor", Pick([exam, task])!.Item.Title);
    }

    [Fact]
    public void More_steps_left_makes_a_deadline_more_pressing()
    {
        // Both are due Friday. Twelve steps take two days at a day's worth at a time, and two steps fit in one.
        var big = WithSteps(Item("Lab report", ItemKind.Deadline, 4), 12);
        var small = WithSteps(Item("Reading quiz", ItemKind.Deadline, 4), 2);

        Assert.Equal("Lab report", Pick([.. small, .. big])!.Item.Title);
    }

    [Fact]
    public void On_the_same_day_the_one_with_more_work_left_goes_first()
    {
        var task = Item("Small task", inDays: 1);
        var deadline = Item("Paper", ItemKind.Deadline, 1);

        Assert.Equal("Paper", Pick([task, deadline])!.Item.Title);
    }

    [Fact]
    public void On_the_same_day_an_earlier_time_goes_first()
    {
        var evening = Item("Evening", inDays: 1, at: new TimeOnly(20, 0));
        var morning = Item("Morning", inDays: 1, at: new TimeOnly(8, 0));

        Assert.Equal("Morning", Pick([evening, morning])!.Item.Title);
    }

    [Fact]
    public void Items_with_no_date_come_last_but_are_still_suggested()
    {
        var undated = Item("Someday", inDays: null);
        var dated = Item("Next week", inDays: 8);

        Assert.Equal("Next week", Pick([undated, dated])!.Item.Title);
        var only = Pick([undated])!;
        Assert.Equal("Someday", only.Item.Title);
        Assert.Equal("No due date", only.Reason);
    }

    [Fact]
    public void Late_items_count_as_due_today_and_say_so_without_guilt()
    {
        var late = Item("Old task", inDays: -20);
        var exam = Item("Exam tomorrow", ItemKind.Exam, 1, new TimeOnly(9, 0));

        // The exam needs today and more, so a late task doesn't crowd it out.
        Assert.Equal("Exam tomorrow", Pick([late, exam])!.Item.Title);
        Assert.Equal("Overdue", Pick([late])!.Reason);
    }

    [Fact]
    public void Todays_work_comes_before_something_left_from_weeks_ago()
    {
        var old = Item("Old task", inDays: -20);
        var today = Item("Today task", inDays: 0);

        Assert.Equal("Today task", Pick([old, today])!.Item.Title);
    }

    [Fact]
    public void Among_late_items_the_most_recent_comes_first()
    {
        var weeksAgo = Item("Weeks ago", inDays: -20);
        var yesterday = Item("Yesterday", inDays: -1);

        Assert.Equal("Yesterday", Pick([weeksAgo, yesterday])!.Item.Title);
    }

    [Fact]
    public void Ties_are_settled_the_same_way_every_time()
    {
        var first = Item("A", inDays: 1);
        var second = Item("B", inDays: 1);

        Assert.Equal(Pick([first, second])!.Item, Pick([second, first])!.Item);
    }

    [Fact]
    public void Steps_reminders_events_notes_and_repeating_items_are_never_the_pick()
    {
        var parent = Item("Parent", ItemKind.Deadline, 2);
        var step = Item("A step", inDays: 0, parent: ItemRef.Format(parent));
        var open = new[]
        {
            Item("Reminder", ItemKind.Reminder),
            Item("Event", ItemKind.Event, 0, new TimeOnly(18, 0)),
            Item("Note", ItemKind.Note),
            Item("Shopping", ItemKind.Shopping),
            Item("Weekly chore", recurs: new Recurrence(RecurrenceUnit.Week)),
            step,
        };

        Assert.Null(Pick(open));
        Assert.Equal("Parent", Pick([.. open, parent])!.Item.Title);
    }

    [Fact]
    public void Finished_items_are_left_out()
    {
        var done = Item("Done already", inDays: 0) with { Done = true };

        Assert.Null(Pick([done]));
    }

    [Fact]
    public void Feed_events_and_past_feed_items_are_left_out()
    {
        var hours = Item("Office Hours", ItemKind.Deadline, 0, new TimeOnly(15, 0), ItemSource.Calendar);
        var past = Item("Old assignment", ItemKind.Deadline, -1, source: ItemSource.Calendar);
        var assignment = Item("Homework 4", ItemKind.Deadline, 2, new TimeOnly(23, 59), ItemSource.Calendar);

        Assert.Equal("Homework 4", Pick([hours, past, assignment])!.Item.Title);
        Assert.Null(Pick([hours, past]));
    }

    [Fact]
    public void An_exam_that_has_started_is_past_preparing_for()
    {
        var started = Item("Midterm", ItemKind.Exam, 0, new TimeOnly(9, 30));

        Assert.Null(Pick([started]));
        Assert.Equal("Midterm", Pick([started], now: Today.ToDateTime(new TimeOnly(8, 0)))!.Item.Title);
    }

    [Fact]
    public void A_big_item_is_not_suggested_shortly_before_class()
    {
        var paper = Item("Paper", ItemKind.Deadline, 1);

        // Class at 10:30 leaves 25 minutes, less the wrap-up, which a full sitting doesn't fit in.
        Assert.Null(Pick([paper], [At(10, 30)]));
    }

    [Fact]
    public void A_small_thing_is_suggested_instead_when_a_big_one_does_not_fit()
    {
        var paper = Item("Paper", ItemKind.Deadline, 1);
        var email = Item("Email advisor", inDays: 3);

        var pick = Pick([paper, email], [At(10, 30)])!;

        Assert.Equal("Email advisor", pick.Item.Title);
        Assert.Equal("Due Thursday, fits before class", pick.Reason);
    }

    [Fact]
    public void A_big_item_with_steps_fits_a_short_gap_one_step_at_a_time()
    {
        var paper = WithSteps(Item("Paper", ItemKind.Deadline, 1), 3);

        var pick = Pick(paper, [At(10, 30)])!;

        Assert.Equal("Paper", pick.Item.Title);
        Assert.Equal("Due tomorrow, 3 steps left, fits before class", pick.Reason);
    }

    [Fact]
    public void Nothing_is_suggested_during_a_commitment()
    {
        var task = Item("Email advisor", inDays: 0);

        Assert.Null(Pick([task], [At(9, 30)]));
    }

    [Fact]
    public void Nothing_is_suggested_when_the_gap_is_too_short_for_anything()
    {
        var task = Item("Email advisor", inDays: 0);

        Assert.Null(Pick([task], [At(10, 15)]));
    }

    [Fact]
    public void A_commitment_that_has_ended_doesnt_count()
    {
        var paper = Item("Paper", ItemKind.Deadline, 1);

        Assert.Equal("Paper", Pick([paper], [At(8, 0)])!.Item.Title);
    }

    [Fact]
    public void A_far_off_commitment_isnt_named_in_the_reason()
    {
        var paper = Item("Paper", ItemKind.Deadline, 1);

        Assert.Equal("Due tomorrow", Pick([paper], [At(16, 0)])!.Reason);
    }

    [Fact]
    public void Only_the_soonest_commitment_limits_the_gap()
    {
        var paper = Item("Paper", ItemKind.Deadline, 1);

        Assert.Null(Pick([paper], [At(16, 0), At(10, 40, "work")]));
        Assert.Equal("Due tomorrow, fits before work", Pick([paper], [At(16, 0, "class"), At(13, 0, "work")])!.Reason);
    }

    [Fact]
    public void Skipped_items_give_way_to_the_next_suggestion()
    {
        var first = Item("First", inDays: 0);
        var second = Item("Second", inDays: 1);

        Assert.Equal("Second", Pick([first, second], skipped: [ItemRef.Format(first)])!.Item.Title);
        Assert.Null(Pick([first, second], skipped: [ItemRef.Format(first), ItemRef.Format(second)]));
    }

    [Theory]
    [InlineData(ItemKind.Deadline, 0, "Due today")]
    [InlineData(ItemKind.Deadline, 1, "Due tomorrow")]
    [InlineData(ItemKind.Deadline, 4, "Due Friday")]
    [InlineData(ItemKind.Deadline, 9, "Due Wed, Nov 18")]
    [InlineData(ItemKind.Exam, 1, "Exam tomorrow")]
    [InlineData(ItemKind.Exam, 2, "Exam Wednesday")]
    public void The_reason_says_when_in_plain_words(ItemKind kind, int inDays, string expected)
    {
        var item = Item("Thing", kind, inDays, kind == ItemKind.Exam ? new TimeOnly(9, 0) : null);

        Assert.Equal(expected, Pick([item])!.Reason);
    }

    [Fact]
    public void One_step_left_is_said_singly()
    {
        var item = WithSteps(Item("Thing", ItemKind.Deadline, 2), 1);

        Assert.Equal("Due Wednesday, 1 step left", Pick(item)!.Reason);
    }

    [Fact]
    public void Finished_steps_dont_count_as_left()
    {
        var item = WithSteps(Item("Thing", ItemKind.Deadline, 2), 3);
        item[1] = item[1] with { Done = true };

        Assert.Equal("Due Wednesday, 2 steps left", Pick(item)!.Reason);
    }

    [Fact]
    public void Commitments_come_from_classes_shifts_events_and_bedtime()
    {
        var schedule = new ClassSchedule(
            Today.AddDays(-30),
            Today.AddDays(30),
            [new ClassMeeting("CS 220", "Room 2", [DayOfWeek.Monday, DayOfWeek.Tuesday], new TimeOnly(13, 0), new TimeOnly(14, 15), TravelMinutes: 20)],
            []);
        var shift = new WorkShift(Today, new TimeOnly(17, 0), new TimeOnly(21, 0));
        var dentist = Item("Dentist", ItemKind.Event, 0, new TimeOnly(15, 0));
        var exam = Item("Midterm", ItemKind.Exam, 1, new TimeOnly(9, 0));
        var assignment = Item("Homework", ItemKind.Deadline, 0, new TimeOnly(23, 59));
        var reminder = Item("Call home", ItemKind.Reminder, 0, new TimeOnly(12, 0));
        var bed = Today.ToDateTime(new TimeOnly(23, 30));

        var found = NextAction.Commitments(Now, schedule, [shift], [dentist, exam, assignment, reminder], item => item == dentist ? new TimeOnly(16, 30) : null, bed);

        Assert.Equal(
            [
                (Today.ToDateTime(new TimeOnly(12, 40)), "class"),
                (Today.ToDateTime(new TimeOnly(15, 0)), "Dentist"),
                (Today.ToDateTime(new TimeOnly(16, 45)), "work"),
                (bed, "bed"),
                (Today.AddDays(1).ToDateTime(new TimeOnly(9, 0)), "the exam"),
                (Today.AddDays(1).ToDateTime(new TimeOnly(12, 40)), "class"),
            ],
            found.Select(commitment => (commitment.Starts, commitment.Label)));
        Assert.Equal(Today.ToDateTime(new TimeOnly(16, 30)), found[1].Ends);
        Assert.Equal(Today.ToDateTime(new TimeOnly(14, 15)), found[0].Ends);
    }

    [Fact]
    public void Commitments_that_are_over_are_left_out()
    {
        var early = Item("Breakfast meeting", ItemKind.Event, 0, new TimeOnly(7, 0));
        var shift = new WorkShift(Today, new TimeOnly(6, 0), new TimeOnly(9, 0));

        Assert.Empty(NextAction.Commitments(Now, null, [shift], [early]));
    }

    [Fact]
    public void An_overnight_shift_ends_the_next_day()
    {
        var shift = new WorkShift(Today, new TimeOnly(22, 0), new TimeOnly(6, 0));

        var found = Assert.Single(NextAction.Commitments(Now, null, [shift], []));
        Assert.Equal(Today.AddDays(1).ToDateTime(new TimeOnly(6, 0)), found.Ends);
    }

    [Fact]
    public void A_long_event_title_is_named_plainly()
    {
        var appointment = Item("Advising appointment with the department chair", ItemKind.Event, 0, new TimeOnly(11, 0));

        Assert.Equal("the next event", Assert.Single(NextAction.Commitments(Now, null, [], [appointment])).Label);
    }

    [Fact]
    public void A_feed_lecture_is_a_commitment_and_a_feed_assignment_is_not()
    {
        var lecture = Item("Lecture 12", ItemKind.Deadline, 0, new TimeOnly(11, 0), ItemSource.Calendar);
        var assignment = Item("Homework 4", ItemKind.Deadline, 0, new TimeOnly(11, 0), ItemSource.Calendar);

        Assert.Equal("Lecture 12", Assert.Single(NextAction.Commitments(Now, null, [], [lecture, assignment])).Label);
    }

    [Fact]
    public void Skipped_refs_last_only_for_the_day()
    {
        var stored = NextAction.AddSkipped(null, Today, "item:3");
        stored = NextAction.AddSkipped(stored, Today, "feed:7");
        stored = NextAction.AddSkipped(stored, Today, "item:3");

        Assert.True(NextAction.ReadSkipped(stored, Today).SetEquals(["item:3", "feed:7"]));
        Assert.Equal(2, stored.Split('|')[1].Split(',').Length);
        Assert.Empty(NextAction.ReadSkipped(stored, Today.AddDays(1)));
        Assert.Equal(["item:9"], NextAction.AddSkipped(stored, Today.AddDays(1), "item:9").Split('|')[1].Split(','));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("2026-11-09")]
    public void Unreadable_skips_mean_nothing_is_skipped(string? stored)
    {
        Assert.Empty(NextAction.ReadSkipped(stored, Today));
    }

    [Fact]
    public void A_work_from_home_shift_is_a_commitment_from_its_start()
    {
        var shift = new WorkShift(DateOnly.FromDateTime(Now), TimeOnly.FromDateTime(Now.AddHours(2)), TimeOnly.FromDateTime(Now.AddHours(6)), 0);

        var work = Assert.Single(NextAction.Commitments(Now, null, [shift], []));

        Assert.Equal(shift.Starts, work.Starts);
    }

    [Fact]
    public void While_something_is_going_the_busy_line_says_until_when()
    {
        Commitment[] work = [new(Now.AddHours(-1), Now.AddHours(7), "work")];
        Commitment[] bed = [new(Now.AddHours(-1), Now.AddHours(7), "bed")];
        Commitment[] later = [new(Now.AddHours(1), Now.AddHours(2), "class")];
        Commitment[] party = [new(Now.AddMinutes(-5), Now.AddMinutes(85), "study group")];

        Assert.Equal("Work until 5:00 PM. A suggestion will be here after.", NextAction.BusyLine(work, Now));
        Assert.Equal("Study group until 11:25 AM. A suggestion will be here after.", NextAction.BusyLine(party, Now));
        Assert.Null(NextAction.BusyLine(bed, Now));
        Assert.Null(NextAction.BusyLine(later, Now));
        Assert.Null(NextAction.Pick([Item("paper", inDays: 1)], work, Now, None));
    }
}
