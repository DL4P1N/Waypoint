using Waypoint.Core.Capture;

namespace Waypoint.Core.Tests;

public class SortedItemParserTests
{
    [Fact]
    public void Parses_each_item()
    {
        var items = SortedItemParser.Parse("""
            {"items": [
                {"kind": "deadline", "title": "psych paper", "due_date": "2026-11-06", "amount_cents": null},
                {"kind": "expense", "title": "chipotle", "due_date": null, "amount_cents": 1200}
            ]}
            """);

        Assert.Equal(
            [
                new SortedItem(ItemKind.Deadline, "psych paper", new DateOnly(2026, 11, 6), null),
                new SortedItem(ItemKind.Expense, "chipotle", null, 1200),
            ],
            items);
    }

    [Fact]
    public void Unknown_kind_becomes_a_note()
    {
        var items = SortedItemParser.Parse("""{"items": [{"kind": "mystery", "title": "x", "due_date": null, "amount_cents": null}]}""");

        Assert.Equal(ItemKind.Note, items.Single().Kind);
    }

    [Fact]
    public void Malformed_date_is_dropped_but_the_item_is_kept()
    {
        var items = SortedItemParser.Parse("""{"items": [{"kind": "task", "title": "x", "due_date": "friday", "amount_cents": null}]}""");

        Assert.Null(items.Single().DueDate);
    }

    [Fact]
    public void An_events_end_time_is_kept()
    {
        var items = SortedItemParser.Parse("""
            {"items": [{"kind": "event", "title": "study group", "due_date": "2026-11-06", "amount_cents": null, "due_time": "15:00", "end_time": "17:00"}]}
            """);

        Assert.Equal(new TimeOnly(15, 0), items.Single().DueTime);
        Assert.Equal(new TimeOnly(17, 0), EventTimes.EndOf(items.Single()));
    }

    [Fact]
    public void An_event_with_no_end_runs_an_hour()
    {
        var items = SortedItemParser.Parse("""
            {"items": [{"kind": "event", "title": "study group", "due_date": "2026-11-06", "amount_cents": null, "due_time": "15:00", "end_time": null}]}
            """);

        Assert.Equal(new TimeOnly(16, 0), EventTimes.EndOf(items.Single()));
    }

    [Fact]
    public void Blank_titles_are_skipped()
    {
        var items = SortedItemParser.Parse("""{"items": [{"kind": "task", "title": "  ", "due_date": null, "amount_cents": null}]}""");

        Assert.Empty(items);
    }
}
