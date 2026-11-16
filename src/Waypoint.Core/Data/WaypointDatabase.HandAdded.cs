using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

/// <summary>
/// What the To do page's add form fills in. EndTime is for events, and an event with none runs an hour.
/// RemindBefore is how long before it's due to be reminded, with zero meaning right when it's due.
/// </summary>
public sealed record HandAddedItem(
    ItemKind Kind,
    string Title,
    DateOnly? Date,
    TimeOnly? Time = null,
    TimeOnly? EndTime = null,
    string? Course = null,
    TimeSpan? RemindBefore = null,
    long? FilterId = null);

/// <summary>The stored item, or why nothing was added.</summary>
public sealed record HandAddResult(StoredItem? Item, string? Problem = null);

public sealed partial class WaypointDatabase
{
    private static readonly ItemKind[] HandAddedKinds = [ItemKind.Task, ItemKind.Deadline, ItemKind.Reminder, ItemKind.Event, ItemKind.Exam];

    /// <summary>
    /// Adds an item typed into a form. It's stored by <see cref="CompleteCapture"/>, the same as a sorted
    /// note, so it gets the same reminders and history, and nothing goes to the sorter.
    /// </summary>
    public HandAddResult AddByHand(HandAddedItem input, DateTime now)
    {
        var title = input.Title.Trim();
        if (title.Length == 0)
        {
            return new HandAddResult(null, "It needs a title.");
        }

        if (!HandAddedKinds.Contains(input.Kind))
        {
            return new HandAddResult(null, "That kind can't be added here.");
        }

        if (input.Date is null && input.Kind != ItemKind.Task)
        {
            return new HandAddResult(null, $"A {input.Kind.ToString().ToLowerInvariant()} needs a date.");
        }

        var end = input.Kind == ItemKind.Event && input.Time is not null ? input.EndTime : null;
        if (end is { } until && input.Time is { } start && until <= start)
        {
            return new HandAddResult(null, "The end has to be after the start.");
        }

        PlannedReminder[]? reminders = null;
        if (input.RemindBefore is { } before)
        {
            if (input.Date is not { } day)
            {
                return new HandAddResult(null, "A reminder needs a date to count back from.");
            }

            var at = ReminderPlan.DueMoment(day, input.Time) - before;
            if (at <= now)
            {
                return new HandAddResult(null, "That reminder time has already passed.");
            }

            reminders = [new PlannedReminder(at, null)];
        }

        var course = string.IsNullOrWhiteSpace(input.Course) ? null : input.Course.Trim();
        var sorted = new SortedItem(input.Kind, title, input.Date, null, input.Time, reminders, EndTime: end, Course: course);

        var captureId = AddHandTyped(title, now);
        CompleteCapture(captureId, [sorted]);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM items WHERE capture_id = $captureId ORDER BY id LIMIT 1";
        command.Parameters.AddWithValue("$captureId", captureId);
        var itemId = (long)command.ExecuteScalar()!;
        var item = FindItem(connection, ItemSource.Capture, itemId)!;

        if (course is null && input.FilterId is { } filterId)
        {
            AssignToFilter(item, filterId);
        }

        return new HandAddResult(item);
    }
}
