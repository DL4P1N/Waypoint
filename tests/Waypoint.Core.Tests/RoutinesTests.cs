using Waypoint.Core.Capture;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Tests;

public sealed class RoutinesTests
{
    // A Tuesday.
    private static readonly DateOnly Today = new(2026, 11, 10);
    private static readonly DateTime Afternoon = Today.ToDateTime(new TimeOnly(13, 2));

    private static StoredItem Item(long id, ItemKind kind, string title, DateOnly due, TimeOnly? at = null, string? course = null, ItemSource source = ItemSource.Capture) =>
        new(id, kind, title, due, null, false, source, course, at is { } time ? new DateTimeOffset(due.ToDateTime(time)) : null);

    private static ClassSchedule Classes() => new(
        Today.AddDays(-30),
        Today.AddDays(60),
        [new ClassMeeting("MATH 120", "Room 2", [DayOfWeek.Tuesday], new TimeOnly(14, 0), new TimeOnly(15, 15), TravelMinutes: 10)],
        []);

    [Fact]
    public void The_soonest_exam_this_week_comes_before_any_deadline()
    {
        var open = new[]
        {
            Item(1, ItemKind.Deadline, "Essay", Today.AddDays(1)),
            Item(2, ItemKind.Exam, "Calc midterm", Today.AddDays(5), course: "MATH 120"),
            Item(3, ItemKind.Exam, "Bio final", Today.AddDays(20)),
        };

        Assert.Equal("Calc midterm", StudyWindows.PickTarget(open, Afternoon)?.Title);
    }

    [Fact]
    public void Without_an_exam_a_deadline_in_the_next_few_days_is_picked()
    {
        var open = new[]
        {
            Item(1, ItemKind.Deadline, "Lab report", Today.AddDays(6)),
            Item(2, ItemKind.Deadline, "Essay", Today.AddDays(2)),
            Item(3, ItemKind.Task, "Laundry", Today),
        };

        Assert.Equal("Essay", StudyWindows.PickTarget(open, Afternoon)?.Title);
    }

    [Fact]
    public void Missed_and_past_things_are_never_suggested_so_nothing_piles_up()
    {
        var open = new[]
        {
            Item(1, ItemKind.Exam, "Quiz this morning", Today, new TimeOnly(9, 0)),
            Item(2, ItemKind.Deadline, "Essay from last week", Today.AddDays(-5)),
            Item(3, ItemKind.Exam, "Exam yesterday", Today.AddDays(-1)),
        };

        Assert.Null(StudyWindows.PickTarget(open, Afternoon));
    }

    [Fact]
    public void A_week_away_is_a_fresh_start_with_no_history_needed()
    {
        // Nothing is stored between days, so a week with no studying changes nothing about the next suggestion.
        var open = new[] { Item(1, ItemKind.Exam, "Calc midterm", Today.AddDays(10)) };

        Assert.Null(StudyWindows.PickTarget(open, Afternoon.AddDays(-7)));
        Assert.Equal("Calc midterm", StudyWindows.PickTarget(open, Afternoon.AddDays(3))?.Title);
    }

    [Fact]
    public void The_window_works_around_class_and_its_travel()
    {
        var open = new[] { Item(1, ItemKind.Exam, "Calc midterm", Today.AddDays(2), course: "MATH 120") };
        var busy = StudyWindows.Busy(Today, Classes(), [], open);

        // 1:05 to 1:50 is only 45 minutes, which is enough.
        var found = StudyWindows.Find(Afternoon, open, busy, Today.ToDateTime(StudyWindows.DefaultDayEnd));

        Assert.NotNull(found);
        Assert.Equal(Today.ToDateTime(new TimeOnly(13, 5)), found.Start);
        Assert.Equal(Today.ToDateTime(new TimeOnly(13, 50)), found.End);
        Assert.Equal("Open your MATH 120 notes and read the first page", found.FirstAction);
        Assert.Equal("Study window: 1:05 PM to 1:50 PM for Calc midterm (Thu)", found.Line);
    }

    [Fact]
    public void A_short_gap_is_skipped_for_the_next_one_and_capped_at_an_hour()
    {
        var open = new[] { Item(1, ItemKind.Exam, "Calc midterm", Today.AddDays(2)) };
        var busy = StudyWindows.Busy(Today, Classes(), [], open);

        // From 1:40, there are only 10 minutes before leaving for class, so it waits until after travel back.
        var found = StudyWindows.Find(Today.ToDateTime(new TimeOnly(13, 40)), open, busy, Today.ToDateTime(StudyWindows.DefaultDayEnd));

        Assert.Equal(Today.ToDateTime(new TimeOnly(15, 25)), found!.Start);
        Assert.Equal(Today.ToDateTime(new TimeOnly(16, 25)), found.End);
    }

    [Fact]
    public void Shifts_and_timed_events_count_as_busy()
    {
        var open = new[]
        {
            Item(1, ItemKind.Deadline, "Essay", Today.AddDays(1)),
            Item(2, ItemKind.Event, "Dentist", Today, new TimeOnly(17, 0)),
        };
        var shift = new WorkShift(Today, new TimeOnly(13, 30), new TimeOnly(16, 45), CommuteMinutes: 15);
        var busy = StudyWindows.Busy(Today, null, [shift], open);

        var found = StudyWindows.Find(Afternoon, open, busy, Today.ToDateTime(StudyWindows.DefaultDayEnd));

        Assert.Equal(Today.ToDateTime(new TimeOnly(18, 0)), found!.Start);
        Assert.Equal("Open Essay and read what it asks for", found.FirstAction);
    }

    [Fact]
    public void A_window_starting_now_shows_over_the_pick_and_a_later_one_waits()
    {
        var exam = Item(2, ItemKind.Exam, "Calc midterm", Today.AddDays(2));
        var study = StudyWindows.Find(Afternoon, [exam], [], Today.ToDateTime(StudyWindows.DefaultDayEnd))!;
        var later = study with { Start = Afternoon.AddHours(2), End = Afternoon.AddHours(3) };
        var none = new HashSet<string>();

        Assert.True(StudyWindows.ShowsNow(study, hasPick: true, Afternoon, none, none));
        Assert.False(StudyWindows.ShowsNow(later, hasPick: true, Afternoon, none, none));
        Assert.True(StudyWindows.ShowsNow(later, hasPick: false, Afternoon, none, none));
    }

    [Fact]
    public void A_window_turned_down_or_skipped_steps_aside()
    {
        var exam = Item(2, ItemKind.Exam, "Calc midterm", Today.AddDays(2));
        var study = StudyWindows.Find(Afternoon, [exam], [], Today.ToDateTime(StudyWindows.DefaultDayEnd))!;
        var none = new HashSet<string>();
        var target = new HashSet<string> { ItemRef.Format(exam) };

        Assert.False(StudyWindows.ShowsNow(study, hasPick: false, Afternoon, none, target));
        Assert.False(StudyWindows.ShowsNow(study, hasPick: false, Afternoon, target, none));
    }

    [Fact]
    public void Nothing_is_suggested_once_the_day_is_over()
    {
        var open = new[] { Item(1, ItemKind.Exam, "Calc midterm", Today.AddDays(2)) };

        Assert.Null(StudyWindows.Find(Today.ToDateTime(new TimeOnly(21, 40)), open, [], Today.ToDateTime(StudyWindows.DefaultDayEnd)));
    }

    [Fact]
    public void The_day_ends_where_tonights_wind_down_starts()
    {
        // Class at 8:00 tomorrow with 10 minutes' travel: leave 7:50, ready 45 minutes, 8 hours of sleep, so bed at 11:05 PM.
        var classes = new ClassSchedule(Today.AddDays(-30), Today.AddDays(60), [new ClassMeeting("CS 110", "Room 1", [DayOfWeek.Wednesday], new TimeOnly(8, 0), new TimeOnly(9, 0), 10)], []);
        var bedtime = new BedtimeSchedule(new BedtimeSettings(), classes);

        Assert.Equal(Today.ToDateTime(new TimeOnly(21, 35)), StudyWindows.DayEnd(Afternoon, bedtime));
        Assert.Equal(Today.ToDateTime(StudyWindows.DefaultDayEnd), StudyWindows.DayEnd(Afternoon, null));
    }

    [Fact]
    public void A_saved_spot_quiets_the_save_your_spot_stage_but_not_a_missed_wind_down_step()
    {
        var plan = new BedtimePlan(Today.ToDateTime(new TimeOnly(23, 0)), Today.AddDays(1).ToDateTime(new TimeOnly(7, 0)), null);

        Assert.True(BedtimeRoutine.Skips(new BedtimeReminder(BedtimeTask.SaveSpot, plan), spotSaved: true));
        Assert.False(BedtimeRoutine.Skips(new BedtimeReminder(BedtimeTask.SaveSpot, plan), spotSaved: false));
        Assert.False(BedtimeRoutine.Skips(new BedtimeReminder(BedtimeTask.SaveSpot, plan, [BedtimeTask.EveningStep]), spotSaved: true));
        Assert.False(BedtimeRoutine.Skips(new BedtimeReminder(BedtimeTask.WrapUp, plan), spotSaved: true));
    }

    [Fact]
    public void Wind_down_stages_name_the_task_in_progress_and_stop_asking_once_saved()
    {
        var plan = new BedtimePlan(Today.ToDateTime(new TimeOnly(23, 0)), Today.AddDays(1).ToDateTime(new TimeOnly(7, 0)), null);

        var (_, ask) = BedtimeRoutine.Describe(new BedtimeReminder(BedtimeTask.SaveSpot, plan), false, "Waypoint");
        var (wrapHeadline, wrap) = BedtimeRoutine.Describe(new BedtimeReminder(BedtimeTask.WrapUp, plan), true, null);

        Assert.StartsWith("Use Save my spot to write where you are on Waypoint and what's next.", ask);
        Assert.Equal("Last call before bed", wrapHeadline);
        Assert.StartsWith("Your spot is saved, so you can stop here.", wrap);
        Assert.Equal("Save your stopping point?", BedtimeRoutine.Describe(new BedtimeReminder(BedtimeTask.WrapUp, plan)).Headline);
    }
}
