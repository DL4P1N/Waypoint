using Waypoint.Core.Capture;

namespace Waypoint.Core.Tests;

public class ItemGroupsTests
{
    private static readonly DateOnly Sunday = new(2026, 11, 1);

    private static StoredItem Item(string title, DateOnly? date) => new(1, ItemKind.Task, title, date, null, false);

    [Theory]
    [InlineData(-1, "Overdue")]
    [InlineData(-30, "Overdue")]
    [InlineData(0, "Today")]
    [InlineData(1, "Tomorrow")]
    [InlineData(6, "Saturday")]
    [InlineData(7, "Sun, Nov 8")]
    public void Labels_days_relative_to_today(int offset, string expected)
    {
        Assert.Equal(expected, ItemGroups.DayLabel(Sunday.AddDays(offset), Sunday));
    }

    [Fact]
    public void Items_without_a_date_get_their_own_label()
    {
        Assert.Equal("No date", ItemGroups.DayLabel(null, Sunday));
    }

    [Fact]
    public void Groups_items_by_day_in_order()
    {
        var groups = ItemGroups.ByDay(
            [Item("a", Sunday), Item("b", Sunday), Item("c", Sunday.AddDays(1)), Item("d", null)],
            Sunday);

        Assert.Equal(["Today", "Tomorrow", "No date"], groups.Select(group => group.Label));
        Assert.Equal(2, groups[0].Items.Count);
    }

    [Fact]
    public void Summarizes_what_was_added()
    {
        var summary = ItemGroups.AddedSummary(
        [
            new SortedItem(ItemKind.Reminder, "Call mom", null, null),
            new SortedItem(ItemKind.Shopping, "Coffee", null, null),
        ]);

        Assert.Equal("Added Call mom to To do, with no date. Added Coffee to Shopping", summary);
    }

    [Fact]
    public void Says_where_each_thing_went_and_for_when()
    {
        var summary = ItemGroups.AddedSummary(
        [
            new SortedItem(ItemKind.Task, "Call the bank about the card", null, null),
            new SortedItem(ItemKind.Reminder, "Dentist", new DateOnly(2026, 11, 12), null, new TimeOnly(15, 0)),
            new SortedItem(ItemKind.Deadline, "Paper", new DateOnly(2026, 11, 13), null),
            new SortedItem(ItemKind.Shopping, "printer ink", null, null),
            new SortedItem(ItemKind.Shopping, "milk", null, null),
            new SortedItem(ItemKind.Expense, "Blue Plate", new DateOnly(2026, 11, 10), 1240),
            new SortedItem(ItemKind.Note, "Gift ideas", null, null),
        ]);

        Assert.Equal(
            "Added Call the bank about the card to To do, with no date. Added Dentist to To do, for Thu, Nov 12 at 3:00 PM. "
            + "Added Paper to To do, due Fri, Nov 13. Added printer ink and milk to Shopping. Added $12.40 at Blue Plate to Budget. "
            + "Added Gift ideas to Notes",
            summary);
        Assert.DoesNotContain("(task)", summary);
    }

    [Theory]
    [InlineData("CS 220-01 Term 1", "CS 220")]
    [InlineData("ENGL 110-01 Term 1", "ENGL 110")]
    [InlineData("ABC 1000-01 Term 1", "ABC 1000")]
    [InlineData("Term 1 Start", "Term 1 Start")]
    public void Shortens_course_names(string course, string expected)
    {
        Assert.Equal(expected, ItemGroups.ShortCourse(course));
    }
}
