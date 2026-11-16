using Waypoint.Core.Schedule;

namespace Waypoint.Core.Tests;

public class BedtimeTests
{
    // 2026-11-02 is a Monday. A Tuesday 9:30 class, leaving at 9:15, is the only one that week.
    private static readonly ClassSchedule Schedule = new(
        new DateOnly(2026, 9, 14),
        new DateOnly(2026, 12, 18),
        [new ClassMeeting("CS 101", "ABC 100", [DayOfWeek.Tuesday], new TimeOnly(9, 30), new TimeOnly(10, 45), 15)],
        [new DateOnly(2026, 11, 10)]);

    private static readonly BedtimeSchedule Bedtimes = new(new BedtimeSettings(), Schedule);

    // Just the two routine reminders, with the wind-down stages turned off.
    private static readonly BedtimeSchedule RoutineOnly = new(
        new BedtimeSettings(HeadsUpMinutes: null, SaveSpotMinutes: null, RoutineMinutes: null, WrapUpMinutes: null, BedMinutes: null),
        Schedule);

    private static DateTime Monday(int hour, int minute = 0) => new(2026, 11, 2, hour, minute, 0);

    [Fact]
    public void Works_back_from_leave_time_through_getting_ready_and_sleep()
    {
        var plan = Bedtimes.GetPlan(Monday(20));

        // 9:15 leave, 8:30 up, 8 hours of sleep.
        Assert.Equal(new DateTime(2026, 11, 3, 0, 30, 0), plan.Bedtime);
        Assert.Equal(new DateTime(2026, 11, 3, 8, 30, 0), plan.NightEnds);
        Assert.Equal("CS 101", plan.FirstClass!.Meeting.Course);
    }

    [Fact]
    public void A_late_class_is_capped_at_the_latest_bedtime()
    {
        var late = Schedule with { Meetings = [new ClassMeeting("Late", "X", [DayOfWeek.Tuesday], new TimeOnly(15, 0), new TimeOnly(16, 0))] };

        var plan = new BedtimeSchedule(new BedtimeSettings(), late).GetPlan(Monday(20));

        Assert.Equal(new DateTime(2026, 11, 3, 1, 0, 0), plan.Bedtime);
    }

    [Fact]
    public void A_morning_without_class_uses_the_latest_bedtime()
    {
        var plan = Bedtimes.GetPlan(new DateTime(2026, 11, 3, 20, 0, 0));

        Assert.Null(plan.FirstClass);
        Assert.Equal(new DateTime(2026, 11, 4, 1, 0, 0), plan.Bedtime);
    }

    [Fact]
    public void A_no_class_date_uses_the_latest_bedtime()
    {
        var plan = Bedtimes.GetPlan(new DateTime(2026, 11, 9, 20, 0, 0));

        Assert.Null(plan.FirstClass);
    }

    [Fact]
    public void An_evening_latest_bedtime_means_the_night_before()
    {
        var bedtimes = new BedtimeSchedule(new BedtimeSettings(LatestBedtime: new TimeOnly(23, 0)), Schedule);

        Assert.Equal(new DateTime(2026, 11, 2, 23, 0, 0), bedtimes.GetPlan(Monday(20)).Bedtime);
    }

    [Fact]
    public void After_midnight_is_still_the_same_night()
    {
        Assert.Equal(new DateTime(2026, 11, 3, 0, 30, 0), Bedtimes.GetPlan(new DateTime(2026, 11, 3, 2, 0, 0)).Bedtime);
    }

    [Fact]
    public void Reminds_once_each_before_a_morning_class()
    {
        var reminders = new BedtimeReminders(RoutineOnly);
        var bedtime = new DateTime(2026, 11, 3, 0, 30, 0);

        Assert.Null(reminders.Check(bedtime.AddMinutes(-76)));
        Assert.Equal(BedtimeTask.EveningStep, reminders.Check(bedtime.AddMinutes(-75))!.Task);
        Assert.Null(reminders.Check(bedtime.AddMinutes(-74)));
        Assert.Equal(BedtimeTask.WindDownStep, reminders.Check(bedtime.AddMinutes(-30))!.Task);
        Assert.Null(reminders.Check(bedtime.AddMinutes(-29)));
    }

    [Fact]
    public void Starting_late_sends_one_reminder_covering_what_was_missed()
    {
        var reminders = new BedtimeReminders(RoutineOnly);
        var late = new DateTime(2026, 11, 3, 0, 10, 0);

        var reminder = reminders.Check(late)!;

        Assert.Equal(BedtimeTask.WindDownStep, reminder.Task);
        Assert.Equal([BedtimeTask.EveningStep], reminder.Also!);
        Assert.Null(reminders.Check(late));
    }

    [Fact]
    public void Nothing_is_sent_past_bedtime()
    {
        Assert.Null(new BedtimeReminders(Bedtimes).Check(new DateTime(2026, 11, 3, 2, 0, 0)));
    }

    [Fact]
    public void Nothing_is_sent_before_a_morning_without_class()
    {
        var reminders = new BedtimeReminders(Bedtimes);

        Assert.Null(reminders.Check(new DateTime(2026, 11, 4, 0, 30, 0)));
    }

    [Fact]
    public void An_afternoon_class_is_not_a_school_morning()
    {
        var late = Schedule with { Meetings = [new ClassMeeting("Late", "X", [DayOfWeek.Tuesday], new TimeOnly(13, 0), new TimeOnly(14, 20))] };

        var reminders = new BedtimeReminders(new BedtimeSchedule(new BedtimeSettings(), late));

        Assert.Null(reminders.Check(new DateTime(2026, 11, 3, 0, 30, 0)));
    }

    [Fact]
    public void Settings_read_from_json_with_defaults()
    {
        var settings = WaypointSettings.Parse("""{ "bedtime": { "sleepHours": 7.5, "latestBedtime": "00:30", "windDownStepMinutes": 60 } }""");

        Assert.Equal(7.5, settings.Bedtime!.SleepHours);
        Assert.Equal(new TimeOnly(0, 30), settings.Bedtime.LatestBedtime);
        Assert.Equal(60, settings.Bedtime.WindDownStepMinutes);
        Assert.Equal(75, settings.Bedtime.EveningStepMinutes);
        Assert.Equal(BedtimeSettings.DefaultEveningStepName, settings.Bedtime.EveningStepLabel());
    }
}
