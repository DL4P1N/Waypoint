using Microsoft.Data.Sqlite;
using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Tests;

/// <summary>A Discord card for a calendar deadline acts on that deadline, never on a typed item with the same number.</summary>
public sealed class CalendarCardActionTests : IDisposable
{
    private static readonly DateTime Now = DateTime.Now;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public CalendarCardActionTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    /// <summary>A typed task and a calendar deadline that share an ID, the way they can in a real database.</summary>
    private (StoredItem Typed, StoredItem Deadline) SameNumber()
    {
        var day = DateOnly.FromDateTime(Now).AddDays(2);
        var typed = _database.AddByHand(new HandAddedItem(ItemKind.Task, "Made up typed task", day), Now).Item!;
        _database.ReplaceCalendarEvents([new CalendarEvent("made-up-lab", "Made up lab report", "CS 101", day, null)]);
        var deadline = _database.GetOpenItems().Single(item => item.Source == ItemSource.Calendar);
        while (typed.Id < deadline.Id)
        {
            typed = _database.AddByHand(new HandAddedItem(ItemKind.Task, $"Made up typed task {typed.Id + 1}", day), Now).Item!;
        }

        Assert.Equal(deadline.Id, typed.Id);
        return (typed, deadline);
    }

    [Fact]
    public void Done_on_a_calendar_deadline_card_finishes_the_deadline_and_leaves_the_typed_item_alone()
    {
        var (typed, deadline) = SameNumber();
        _database.TrackSentReminder("made-up-card", deadline.Id, ItemSource.Calendar, sentAt: Now);

        Assert.Equal("Done: Made up lab report", _database.ApplyAlertAction(new AlertAction("made-up-card", AlertActionKind.Done), Now, 15));

        var open = _database.GetOpenItems();
        Assert.DoesNotContain(open, item => item.Source == ItemSource.Calendar && item.Id == deadline.Id);
        Assert.Contains(open, item => item.Source == ItemSource.Capture && item.Id == typed.Id);
    }

    [Fact]
    public void Snooze_on_a_calendar_deadline_card_brings_back_the_deadline_and_not_the_typed_item()
    {
        var (typed, deadline) = SameNumber();
        _database.TrackSentReminder("made-up-card", deadline.Id, ItemSource.Calendar, sentAt: Now);

        Assert.Equal("Back in 15 minutes: Made up lab report", _database.ApplyAlertAction(new AlertAction("made-up-card", AlertActionKind.Snooze), Now, 15));

        var due = _database.GetDueReminders(Now.AddMinutes(15));
        Assert.Contains(due, reminder => reminder.Item.Source == ItemSource.Calendar && reminder.Item.Id == deadline.Id);
        Assert.DoesNotContain(due, reminder => reminder.Item.Source == ItemSource.Capture && reminder.Item.Id == typed.Id);
        Assert.Contains(_database.GetOpenItems(), item => item.Source == ItemSource.Capture && item.Id == typed.Id);
    }
}
