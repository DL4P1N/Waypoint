using Waypoint.Core.Schedule;

namespace Waypoint.Core.Tests;

public class ClassScheduleTests
{
    private static readonly ClassMeeting Morning = new("Morning", "A 1", [DayOfWeek.Tuesday, DayOfWeek.Thursday], new TimeOnly(9, 0), new TimeOnly(10, 20));
    private static readonly ClassMeeting Late = new("Late", "B 2", [DayOfWeek.Tuesday], new TimeOnly(10, 50), new TimeOnly(12, 10));

    private static readonly ClassSchedule Schedule = new(
        new DateOnly(2026, 9, 14),
        new DateOnly(2026, 12, 18),
        [Late, Morning],
        [new DateOnly(2026, 10, 15)]);

    [Fact]
    public void Picks_the_earliest_class_of_the_day()
    {
        var next = Schedule.GetNextClass(new DateTime(2026, 11, 3, 8, 0, 0));

        Assert.Equal(new ClassOccurrence(Morning, new DateTime(2026, 11, 3, 9, 0, 0)), next);
    }

    [Fact]
    public void Skips_a_class_that_already_started()
    {
        var next = Schedule.GetNextClass(new DateTime(2026, 11, 3, 9, 30, 0));

        Assert.Equal(Late, next!.Meeting);
    }

    [Fact]
    public void Moves_to_a_later_day_when_today_is_finished()
    {
        var next = Schedule.GetNextClass(new DateTime(2026, 11, 3, 13, 0, 0));

        Assert.Equal(new DateTime(2026, 11, 5, 9, 0, 0), next!.Start);
    }

    [Fact]
    public void Skips_no_class_dates()
    {
        var next = Schedule.GetNextClass(new DateTime(2026, 10, 14, 12, 0, 0));

        Assert.Equal(new DateTime(2026, 10, 20, 9, 0, 0), next!.Start);
    }

    [Fact]
    public void Waits_for_the_term_to_start()
    {
        var next = Schedule.GetNextClass(new DateTime(2026, 8, 1, 12, 0, 0));

        Assert.Equal(new DateTime(2026, 9, 15, 9, 0, 0), next!.Start);
    }

    [Fact]
    public void Returns_null_after_the_term_ends()
    {
        Assert.Null(Schedule.GetNextClass(new DateTime(2026, 12, 18, 8, 0, 0)));
    }

    [Fact]
    public void Loads_from_settings_json()
    {
        var settings = WaypointSettings.Parse("""
            {
              "schedule": {
                "termStart": "2026-09-14",
                "termEnd": "2026-12-18",
                "noClassDates": ["2026-10-16"],
                "meetings": [
                  { "course": "CS 220", "room": "Room 1", "days": ["Tuesday", "Thursday"], "start": "09:00", "end": "10:20" }
                ]
              }
            }
            """);

        var meeting = settings.Schedule!.Meetings.Single();
        Assert.Equal([DayOfWeek.Tuesday, DayOfWeek.Thursday], meeting.Days);
        Assert.Equal(new TimeOnly(9, 0), meeting.Start);
        Assert.Null(settings.CalendarFeedUrl);
    }
}
