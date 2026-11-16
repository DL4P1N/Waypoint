using Waypoint.Core.Schedule;

namespace Waypoint.Core.Tests;

public class ClassAlertTrackerTests
{
    private static readonly ClassMeeting Morning = new("Morning", "A 1", [DayOfWeek.Tuesday], new TimeOnly(9, 0), new TimeOnly(10, 20), 15);
    private static readonly ClassMeeting Next = new("Next", "B 2", [DayOfWeek.Tuesday], new TimeOnly(10, 35), new TimeOnly(11, 55), 15);

    private static readonly ClassSchedule Schedule = new(
        new DateOnly(2026, 9, 14),
        new DateOnly(2026, 12, 18),
        [Morning, Next],
        []);

    private static DateTime Tuesday(int hour, int minute) => new(2026, 11, 3, hour, minute, 0);

    [Fact]
    public void Nothing_before_the_warning_window()
    {
        Assert.Null(new ClassAlertTracker(Schedule).Check(Tuesday(8, 29)));
    }

    [Fact]
    public void Warns_once_before_it_is_time_to_leave()
    {
        var tracker = new ClassAlertTracker(Schedule);

        var alert = tracker.Check(Tuesday(8, 30));

        Assert.Equal(ClassAlertStage.Warning, alert!.Stage);
        Assert.Equal(Tuesday(8, 45), alert.LeaveAt);
        Assert.Null(tracker.Check(Tuesday(8, 31)));
    }

    [Fact]
    public void Interrupts_at_leave_time_and_repeats_every_two_minutes()
    {
        var tracker = new ClassAlertTracker(Schedule);

        Assert.Equal(ClassAlertStage.LeaveNow, tracker.Check(Tuesday(8, 45))!.Stage);
        Assert.Null(tracker.Check(Tuesday(8, 46)));
        Assert.Equal(ClassAlertStage.LeaveNow, tracker.Check(Tuesday(8, 47))!.Stage);
    }

    [Fact]
    public void Acknowledging_stops_the_interrupts()
    {
        var tracker = new ClassAlertTracker(Schedule);

        tracker.Acknowledge(tracker.Check(Tuesday(8, 45))!.Class);

        Assert.Null(tracker.Check(Tuesday(8, 50)));
    }

    [Fact]
    public void Acknowledging_one_class_does_not_silence_the_next()
    {
        var tracker = new ClassAlertTracker(Schedule);
        tracker.Acknowledge(tracker.Check(Tuesday(8, 45))!.Class);

        Assert.Equal("Next", tracker.Check(Tuesday(10, 20))!.Class.Meeting.Course);
    }

    [Fact]
    public void No_warning_while_sitting_in_another_class()
    {
        Assert.Null(new ClassAlertTracker(Schedule).Check(Tuesday(10, 10)));
    }

    [Fact]
    public void No_alerts_on_a_no_class_date()
    {
        var holiday = Schedule with { NoClassDates = [new DateOnly(2026, 11, 3)] };

        Assert.Null(new ClassAlertTracker(holiday).Check(Tuesday(8, 45)));
    }
}
