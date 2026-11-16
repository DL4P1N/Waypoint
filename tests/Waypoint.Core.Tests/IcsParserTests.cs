using Waypoint.Core.Calendar;

namespace Waypoint.Core.Tests;

public class IcsParserTests
{
    private static string Calendar(params string[] eventLines) =>
        string.Join("\r\n", ["BEGIN:VCALENDAR", .. eventLines, "END:VCALENDAR", ""]);

    [Fact]
    public void Reads_a_timed_event_and_splits_out_the_course()
    {
        var calendarEvent = IcsParser.Parse(Calendar(
            "BEGIN:VEVENT",
            "DTSTART:20261119T174500Z",
            "UID:a@google.com",
            "SUMMARY:Reading response  (due 11/18) [ENGL 110-01 Term 1]",
            "END:VEVENT")).Single();

        Assert.Equal("Reading response (due 11/18)", calendarEvent.Title);
        Assert.Equal("ENGL 110-01 Term 1", calendarEvent.Course);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 17, 45, 0, TimeSpan.Zero), calendarEvent.DueAt);
    }

    [Fact]
    public void All_day_events_keep_their_date_and_have_no_time()
    {
        var calendarEvent = IcsParser.Parse(Calendar(
            "BEGIN:VEVENT",
            "DTSTART;VALUE=DATE:20261218",
            "UID:a@google.com",
            "SUMMARY:Checklist",
            "END:VEVENT")).Single();

        Assert.Equal(new DateOnly(2026, 12, 18), calendarEvent.DueDate);
        Assert.Null(calendarEvent.DueAt);
        Assert.Null(calendarEvent.Course);
    }

    [Fact]
    public void Folded_lines_are_joined_without_adding_a_space()
    {
        var calendarEvent = IcsParser.Parse(Calendar(
            "BEGIN:VEVENT",
            "DTSTART;VALUE=DATE:20261218",
            "UID:a@google.com",
            "SUMMARY:Reading Checklist [Study Group (20",
            " 26-2027)]",
            "END:VEVENT")).Single();

        Assert.Equal("Study Group (2026-2027)", calendarEvent.Course);
    }

    [Fact]
    public void Escaped_characters_are_restored()
    {
        var calendarEvent = IcsParser.Parse(Calendar(
            "BEGIN:VEVENT",
            "DTSTART;VALUE=DATE:20261218",
            "UID:a@google.com",
            @"SUMMARY:Essays\, drafts\; notes",
            "END:VEVENT")).Single();

        Assert.Equal("Essays, drafts; notes", calendarEvent.Title);
    }

    [Fact]
    public void Cancelled_and_undated_events_are_skipped()
    {
        var events = IcsParser.Parse(Calendar(
            "BEGIN:VEVENT",
            "DTSTART;VALUE=DATE:20261218",
            "UID:a@google.com",
            "STATUS:CANCELLED",
            "SUMMARY:Gone",
            "END:VEVENT",
            "BEGIN:VEVENT",
            "UID:b@google.com",
            "SUMMARY:No date",
            "END:VEVENT"));

        Assert.Empty(events);
    }

    [Fact]
    public void A_response_that_is_not_a_calendar_throws()
    {
        Assert.Throws<FormatException>(() => IcsParser.Parse("<html>Sign in</html>"));
    }
}
