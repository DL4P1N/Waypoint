using System.Globalization;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

/// <summary>Changes to an item, typed or made on its row: moving, renaming, breaking down, and inserting or updating the item.</summary>
public sealed partial class WaypointDatabase
{
    /// <summary>A change made on a row rather than typed. Breaking down needs the sorter, so it isn't one.</summary>
    public AppliedChange ChangeItem(ItemChange change, DateTime now)
    {
        if (change.Action == ChangeAction.BreakDown)
        {
            return new AppliedChange(change, "that item", false);
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var applied = ApplyChange(connection, 0, change, now);
        transaction.Commit();
        return applied;
    }

    /// <summary>
    /// Moves an item to a new day. A recurring one moves this time only, so it keeps its usual
    /// time, and its next date still counts from where it would have been.
    /// </summary>
    public AppliedChange RescheduleItem(StoredItem item, DateOnly date, TimeOnly? time, DateTime now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        if (item.Recurs is not null && item.DueDate is { } usual)
        {
            using var remember = connection.CreateCommand();
            remember.CommandText = "UPDATE items SET usual_date = COALESCE(usual_date, $date) WHERE id = $id";
            remember.Parameters.AddWithValue("$date", usual.ToString("O", CultureInfo.InvariantCulture));
            remember.Parameters.AddWithValue("$id", item.Id);
            remember.ExecuteNonQuery();
        }

        var change = new ItemChange(item.Source, item.Id, ChangeAction.Update, DueDate: date, DueTime: item.Recurs is null ? time : null);
        var applied = ApplyChange(connection, 0, change, now);
        transaction.Commit();
        return applied;
    }

    private static AppliedChange ApplyChange(SqliteConnection connection, long captureId, ItemChange change, DateTime now)
    {
        var item = FindItem(connection, change.Source, change.ItemId);
        if (item is null)
        {
            return new AppliedChange(change, "that item", false);
        }

        // Finished, moved, canceled, or deleted: whatever was held for after this no longer stands.
        ForgetAfterThis(connection, item.Source, item.Id);

        switch (change.Action)
        {
            case ChangeAction.Done:
                if (TakeDose(connection, item, null, now) is { } dose)
                {
                    return new AppliedChange(change, item.Title, dose.Recorded, dose.Text);
                }

                Finish(connection, item, now);
                return new AppliedChange(change, item.Title);
            case ChangeAction.Cancel:
                SetDone(connection, item.Source, item.Id, true, now);
                SetCanceled(connection, item, true);
                LogHistory(connection, HistoryAction.Canceled, item, now);
                return new AppliedChange(change, item.Title);
            case ChangeAction.Reopen:
                return new AppliedChange(change, item.Title, Reopen(connection, item, now));
            case ChangeAction.StopReminders:
                StopReminders(connection, item);
                return new AppliedChange(change, item.Title);
            case ChangeAction.BreakDown:
                return BreakDown(connection, captureId, item, change, now);
            case ChangeAction.UseFeedDate:
                return new AppliedChange(change, item.Title, UseFeedDate(connection, item, now));
            default:
                return UpdateItem(connection, item, change, now);
        }
    }

    private static AppliedChange BreakDown(SqliteConnection connection, long captureId, StoredItem item, ItemChange change, DateTime now)
    {
        if (item.ParentRef is not null || change.Steps is not { Count: > 0 } steps)
        {
            return new AppliedChange(change, item.Title, false);
        }

        var (parentItem, parentEvent) = item.Source == ItemSource.Calendar ? ((long?)null, (long?)item.Id) : (item.Id, null);

        CloseSteps(connection, parentItem, parentEvent, now);

        var due = item.DueAt?.LocalDateTime ?? item.DueDate?.ToDateTime(EndOfDay);
        var stepItems = ReminderPlan.StepItems(steps, due);
        foreach (var step in stepItems)
        {
            InsertItem(connection, captureId, step, parentItem, parentEvent);
        }

        // Steps with reminders of their own replace the assignment's nudges, which would only double up.
        if (item.Source == ItemSource.Calendar && stepItems.Any(step => step.Reminders?.Any(reminder => reminder.At > now) == true))
        {
            SkipNudges(connection, item.Id);
        }

        return new AppliedChange(change, item.Title);
    }

    private static void CloseSteps(SqliteConnection connection, long? parentItem, long? parentEvent, DateTime now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE items SET done = 1, done_at = $now WHERE done = 0 AND (parent_item_id = $item OR parent_event_id = $event)";
        command.Parameters.AddWithValue("$now", FormatReminder(now));
        command.Parameters.AddWithValue("$item", (object?)parentItem ?? DBNull.Value);
        command.Parameters.AddWithValue("$event", (object?)parentEvent ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static string? CaptureText(SqliteConnection connection, long captureId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT text FROM captures WHERE id = $id";
        command.Parameters.AddWithValue("$id", captureId);
        return command.ExecuteScalar() as string;
    }

    private static void SetItemBody(SqliteConnection connection, long itemId, string body)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE items SET body = $body WHERE id = $id";
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$id", itemId);
        command.ExecuteNonQuery();
    }

    private static long InsertItem(SqliteConnection connection, long captureId, SortedItem item, long? parentItem, long? parentEvent)
    {
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO items (capture_id, kind, title, due_date, amount_cents, due_time, repeat_minutes, parent_item_id, parent_event_id, recurrence, category, one_off, about, end_time, course, dose)
            VALUES ($captureId, $kind, $title, $dueDate, $amountCents, $dueTime, $repeatMinutes, $parentItem, $parentEvent, $recurrence, $category, $oneOff, $about, $endTime, $course, $dose)
            RETURNING id
            """;
        insert.Parameters.AddWithValue("$dueTime", (object?)item.DueTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? DBNull.Value);
        // Only a repeating reminder can be a dose, and a dose comes back once after Not now rather than over and over.
        var dose = item.Dose && item.Recurs is not null;
        insert.Parameters.AddWithValue("$dose", dose ? 1 : 0);
        insert.Parameters.AddWithValue("$repeatMinutes", (object?)(dose ? null : ReminderPlan.RepeatMinutes(item)) ?? DBNull.Value);
        insert.Parameters.AddWithValue("$captureId", captureId);
        insert.Parameters.AddWithValue("$kind", item.Kind.ToString());
        insert.Parameters.AddWithValue("$title", item.Title);
        insert.Parameters.AddWithValue("$dueDate", (object?)item.DueDate?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        insert.Parameters.AddWithValue("$amountCents", (object?)item.AmountCents ?? DBNull.Value);
        insert.Parameters.AddWithValue("$parentItem", (object?)parentItem ?? DBNull.Value);
        insert.Parameters.AddWithValue("$parentEvent", (object?)parentEvent ?? DBNull.Value);
        insert.Parameters.AddWithValue("$recurrence", (object?)item.Recurs?.Format() ?? DBNull.Value);
        insert.Parameters.AddWithValue("$oneOff", item.OneOff ? 1 : 0);
        insert.Parameters.AddWithValue("$about", (object?)item.About ?? DBNull.Value);
        insert.Parameters.AddWithValue("$endTime", (object?)EventTimes.EndOf(item)?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? DBNull.Value);
        insert.Parameters.AddWithValue("$course", string.IsNullOrWhiteSpace(item.Course) ? DBNull.Value : item.Course.Trim());
        insert.Parameters.AddWithValue("$category", item.Kind == ItemKind.Expense
            ? MatchCategory(connection, item.Title) ?? item.Category ?? BudgetMath.DefaultCategory
            : DBNull.Value);
        var itemId = (long)insert.ExecuteScalar()!;

        foreach (var reminder in ReminderPlan.Build(dose ? item with { RepeatMinutes = null } : item, DateTime.Now))
        {
            AddReminder(connection, itemId, reminder);
        }

        return itemId;
    }

    private static AppliedChange UpdateItem(SqliteConnection connection, StoredItem item, ItemChange change, DateTime now)
    {
        var date = change.DueDate ?? item.DueDate;
        var time = change.DueTime ?? (item.DueAt is { } dueAt ? TimeOnly.FromDateTime(dueAt.DateTime) : null);
        var moves = date is not null && (change.DueDate is not null || change.DueTime is not null);

        if (item.Source == ItemSource.Calendar)
        {
            return MoveEvent(connection, item, change, now);
        }

        if (!moves && string.IsNullOrWhiteSpace(change.Title))
        {
            return new AppliedChange(change, item.Title, false);
        }

        var title = string.IsNullOrWhiteSpace(change.Title) ? item.Title : change.Title.Trim();

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE items SET title = $title, due_date = $dueDate, due_time = $dueTime WHERE id = $id";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$dueDate", (object?)date?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$dueTime", (object?)time?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", item.Id);
        command.ExecuteNonQuery();

        if (moves)
        {
            MoveReminders(connection, item, new SortedItem(item.Kind, title, date, null, time), now);
            ShiftEndTime(connection, item, time);
        }

        return new AppliedChange(change, title);
    }

    /// <summary>The feed owns an event's title, but a date the user gives outlasts any sync.</summary>
    private static AppliedChange MoveEvent(SqliteConnection connection, StoredItem item, ItemChange change, DateTime now)
    {
        if (change.DueDate is null && change.DueTime is null)
        {
            return new AppliedChange(change, item.Title, false);
        }

        var date = change.DueDate ?? item.DueDate!.Value;
        var time = change.DueTime ?? (item.DueAt is { } dueAt ? TimeOnly.FromDateTime(dueAt.LocalDateTime) : null);
        SetEventOverride(connection, item, date, time, overrides: true, now);
        return new AppliedChange(change, item.Title);
    }

    /// <summary>Puts an event back on the feed's date. Returns false if it was already there.</summary>
    private static bool UseFeedDate(SqliteConnection connection, StoredItem item, DateTime now)
    {
        if (item.Source != ItemSource.Calendar)
        {
            return false;
        }

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT due_date, due_at FROM calendar_events WHERE id = $id AND due_override IS NOT NULL";
        read.Parameters.AddWithValue("$id", item.Id);
        using var reader = read.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        var date = DateOnly.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
        TimeOnly? time = reader.IsDBNull(1) ? null : TimeOnly.FromDateTime(DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture).LocalDateTime);
        reader.Close();

        SetEventOverride(connection, item, date, time, overrides: false, now);
        return true;
    }

    private static void SetEventOverride(SqliteConnection connection, StoredItem item, DateOnly date, TimeOnly? time, bool overrides, DateTime now)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE calendar_events SET due_override = $date, due_override_at = $at WHERE id = $id";
            command.Parameters.AddWithValue("$date", overrides ? date.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
            command.Parameters.AddWithValue("$at", overrides && time is { } clock ? new DateTimeOffset(date.ToDateTime(clock)).ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
            command.Parameters.AddWithValue("$id", item.Id);
            command.ExecuteNonQuery();
        }

        // An item with no time was only nominally at the default one, so its plans move by whole days.
        var oldTime = item.DueAt is { } dueAt ? TimeOnly.FromDateTime(dueAt.LocalDateTime) : (TimeOnly?)null;
        var shift = oldTime is null
            ? TimeSpan.FromDays(date.DayNumber - item.DueDate!.Value.DayNumber)
            : ReminderPlan.DueMoment(date, time) - ReminderPlan.DueMoment(item.DueDate!.Value, oldTime);

        MoveEventReminders(connection, item.Id, shift, now);
        MoveStepReminders(connection, item.Id, shift, now, "parent_event_id");
    }
}
