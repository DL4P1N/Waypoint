using Waypoint.App;

namespace Waypoint.App.Tests;

public sealed class PickDateWindowTests
{
    [Theory]
    [InlineData("2026-11-10", "2026-11-11", "2026-11-14", "2026-11-16")] // a Tuesday
    [InlineData("2026-11-14", "2026-11-15", "2026-11-14", "2026-11-16")] // a Saturday is already the weekend
    [InlineData("2026-11-15", "2026-11-16", "2026-11-15", "2026-11-16")] // so is a Sunday, and Monday is tomorrow
    [InlineData("2026-11-16", "2026-11-17", "2026-11-21", "2026-11-23")] // on a Monday, next week is the Monday after
    public void Quick_picks_land_on_the_days_they_say(string today, string tomorrow, string weekend, string nextWeek)
    {
        var picks = PickDateWindow.QuickDates(DateOnly.Parse(today));

        Assert.Equal(["Today", "Tomorrow", "This weekend", "Next week"], picks.Select(pick => pick.Label));
        Assert.Equal([DateOnly.Parse(today), DateOnly.Parse(tomorrow), DateOnly.Parse(weekend), DateOnly.Parse(nextWeek)], picks.Select(pick => pick.Day));
    }

    [Fact]
    public void The_button_says_Move_only_when_something_is_moved() => Ui.Run(ui =>
    {
        var move = new PickDateWindow(null!, "Paper", DateOnly.FromDateTime(DateTime.Now), null, asksForTime: true);
        var pick = new PickDateWindow(null!, "Due", DateOnly.FromDateTime(DateTime.Now), null, asksForTime: false, action: "Choose");

        Assert.Equal("Move", UiTest.Named<System.Windows.Controls.Button>(move, "GoButton").Content);
        Assert.Equal("Choose", UiTest.Named<System.Windows.Controls.Button>(pick, "GoButton").Content);
        move.Close();
        pick.Close();
    });
}
