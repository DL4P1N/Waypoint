using System.Globalization;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Data;

/// <summary>Reminders that come due, and what a press or reply on a sent reminder does to its item.</summary>
public sealed partial class WaypointDatabase
{
    /// <summary>Pop-ups due at or before <paramref name="now"/> for items that aren't done, oldest first.</summary>
    public IReadOnlyList<DueReminder> GetDueReminders(DateTime now)
    {
        using var connection = Open();
        var reminders = new List<(string At, DueReminder Reminder)>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT i.id, i.kind, i.title, i.due_date, i.amount_cents, i.done, i.due_time, i.parent_item_id, i.parent_event_id, i.recurrence, i.dose,
                       r.id, r.message, i.repeat_minutes, r.remind_at, r.follow_up
                FROM reminders r JOIN items i ON i.id = r.item_id
                WHERE i.done = 0 AND r.remind_at <= $now AND {StepIsLive}
                """;
            command.Parameters.AddWithValue("$now", FormatReminder(now));
            command.Parameters.AddWithValue("$today", DateOnly.FromDateTime(now).ToString("O", CultureInfo.InvariantCulture));

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                reminders.Add((reader.GetString(14), new DueReminder(
                    reader.GetInt64(11),
                    ReadItem(reader),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetInt32(13),
                    reader.GetInt64(15) != 0,
                    reader.GetString(14))));
            }
        }

        // A nudge for an assignment that's already past has nothing left to say.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT e.id, e.title, e.course, {EventDueDate}, {EventDueAt}, e.done, r.id, r.message, r.remind_at, r.follow_up
                FROM calendar_reminders r JOIN calendar_events e ON e.id = r.event_id
                WHERE e.done = 0 AND r.remind_at <= $now AND {EventDueDate} >= $today
                """;
            command.Parameters.AddWithValue("$now", FormatReminder(now));
            command.Parameters.AddWithValue("$today", DateOnly.FromDateTime(now).ToString("O", CultureInfo.InvariantCulture));

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var item = ReadEvent(reader);
                if (item.DueAt is not { } dueAt || dueAt.LocalDateTime > now)
                {
                    reminders.Add((reader.GetString(8), new DueReminder(
                        reader.GetInt64(6),
                        item,
                        reader.IsDBNull(7) ? null : reader.GetString(7),
                        null,
                        reader.GetInt64(9) != 0,
                        reader.GetString(8))));
                }
            }
        }

        return reminders
            .OrderBy(entry => entry.At, StringComparer.Ordinal)
            .ThenBy(entry => entry.Reminder.Id)
            .Select(entry => entry.Reminder)
            .ToList();
    }

    public void SnoozeReminder(DueReminder reminder, DateTime until)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        ForgetAfterThis(connection, reminder.Item.Source, reminder.Item.Id);
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {ReminderTable(reminder.Item)} SET remind_at = $until WHERE id = $id";
        command.Parameters.AddWithValue("$until", FormatReminder(until));
        command.Parameters.AddWithValue("$id", reminder.Id);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void DismissReminder(DueReminder reminder)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {ReminderTable(reminder.Item)} WHERE id = $id";
        if (reminder.Item.Source != ItemSource.Calendar)
        {
            // A wait's nudge that's been seen is settled, so the wait stops pointing at an id that can be reused.
            command.CommandText += "; UPDATE waiting_on SET reminder_id = NULL WHERE reminder_id = $id";
        }

        command.Parameters.AddWithValue("$id", reminder.Id);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>
    /// Settles a reminder that was sent somewhere with no buttons. A repeating one comes back later, a
    /// recurring item moves to its next date, and anything else stays on the list without popping up again.
    /// </summary>
    public void SettleSentReminder(DueReminder reminder, DateTime now)
    {
        // A dose stays on its day until Took it or Skip answers it, or the day is over (see MoveOnPastDoses).
        if (reminder.Item is { Dose: true, Recurs: not null })
        {
            DismissReminder(reminder);
        }
        else if (reminder.RepeatMinutes is { } minutes)
        {
            SnoozeReminder(reminder, now.AddMinutes(minutes));
        }
        else if (reminder.Item.Recurs is not null)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Finish(connection, reminder.Item, now, HistoryAction.Past);
            transaction.Commit();
        }
        else
        {
            DismissReminder(reminder);
        }
    }

    /// <summary>Remembers which item a Discord message was about, so it can be finished from there.</summary>
    /// <param name="sentAt">When it went out, so a card with Start that gets no answer can be held for after this.</param>
    /// <param name="dueOn">For a dose, the day the card was for, so Took it on it counts for that day even after the reminder moves on.</param>
    public void TrackSentReminder(string messageId, long itemId, ItemSource source = ItemSource.Capture, bool startOnly = false, DateTime? sentAt = null, DateOnly? dueOn = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO sent_reminders (message_id, item_id, source, start_only, sent_at, due_on) VALUES ($message, $item, $source, $startOnly, $sentAt, $dueOn)";
        command.Parameters.AddWithValue("$dueOn", dueOn is { } day ? day.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$sentAt", sentAt is { } at ? FormatReminder(at) : DBNull.Value);
        command.Parameters.AddWithValue("$source", source.ToString());
        command.Parameters.AddWithValue("$startOnly", startOnly ? 1 : 0);
        command.Parameters.AddWithValue("$message", messageId);
        command.Parameters.AddWithValue("$item", itemId);
        command.ExecuteNonQuery();
    }

    /// <summary>Finishes or snoozes the item behind a message, and returns what to tell the user.</summary>
    public string? ApplyAlertAction(AlertAction action, DateTime now, int snoozeMinutes)
    {
        // Claiming the card and doing what it says are one write, so a failure between can't use up the press.
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        string? messageId = null;
        long itemId = 0;
        DateOnly? dueOn = null;

        // Without a message, this means the most recent reminder, and for a typed answer one sent before it.
        if ((action.MessageId ?? LatestSentReminder(connection, action.SentAs)) is not { } target)
        {
            return null;
        }

        using (var find = connection.CreateCommand())
        {
            find.CommandText = "SELECT message_id, item_id, due_on FROM sent_reminders WHERE handled = 0 AND start_only = 0 AND message_id = $message";
            find.Parameters.AddWithValue("$message", target);
            using var reader = find.ExecuteReader();
            if (reader.Read())
            {
                messageId = reader.GetString(0);
                itemId = reader.GetInt64(1);
                dueOn = reader.IsDBNull(2) ? null : DateOnly.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
            }
        }

        // A typed reply is marked as dealt with in this same write, so one handed over again can't do it twice.
        if (messageId is null || action.SentAs is { } typed && !FinishDiscordMessage(connection, typed, now) || !ClaimSentReminder(connection, messageId))
        {
            return null;
        }

        var reply = AlertActionOn(connection, itemId, dueOn, action.Kind, now, snoozeMinutes);
        transaction.Commit();
        return reply;
    }

    private static string? AlertActionOn(SqliteConnection connection, long itemId, DateOnly? dueOn, AlertActionKind kind, DateTime now, int snoozeMinutes)
    {
        if (FindItem(connection, ItemSource.Capture, itemId) is not { Done: false } item)
        {
            return null;
        }

        // A dose card's Took it and Not now work on the day it was for, whatever happened elsewhere since.
        if (item is { Dose: true, Recurs: not null, DueDate: { } current })
        {
            return AnswerDose(connection, item, dueOn ?? current, kind == AlertActionKind.Done ? DoseAnswer.Took : DoseAnswer.NotNow, now, null);
        }

        if (kind == AlertActionKind.Done)
        {
            Finish(connection, item, now);
            return $"Done: {item.Title}";
        }

        // Marked as the follow-up, so the card it brings back doesn't offer more time again.
        AddFollowUp(connection, item, now.AddMinutes(snoozeMinutes));
        return $"Back in {snoozeMinutes} minutes: {item.Title}";
    }

    /// <summary>
    /// Marks a sent reminder handled, if nothing else has yet. Only the one that changes the row goes on, so a
    /// button and a reaction, or the gateway and a poll, at the same moment can't both apply.
    /// </summary>
    private static bool ClaimSentReminder(SqliteConnection connection, string messageId)
    {
        using var claim = connection.CreateCommand();
        claim.CommandText = "UPDATE sent_reminders SET handled = 1 WHERE message_id = $message AND handled = 0";
        claim.Parameters.AddWithValue("$message", messageId);
        if (claim.ExecuteNonQuery() != 1)
        {
            return false;
        }

        DropAfterThis(connection, messageId);
        return true;
    }

    /// <summary>If every nudge landed in the past, the event goes back to be planned afresh.</summary>
    private static void MoveEventReminders(SqliteConnection connection, long eventId, TimeSpan shift, DateTime now)
    {
        var existing = new List<(long Id, DateTime At)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT id, remind_at FROM calendar_reminders WHERE event_id = $eventId";
            read.Parameters.AddWithValue("$eventId", eventId);
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                existing.Add((reader.GetInt64(0), DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture)));
            }
        }

        foreach (var (id, at) in existing)
        {
            var moved = at + shift;
            using var command = connection.CreateCommand();
            command.CommandText = moved > now ? "UPDATE calendar_reminders SET remind_at = $at WHERE id = $id" : "DELETE FROM calendar_reminders WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$at", FormatReminder(moved));
            command.ExecuteNonQuery();
        }

        using var replan = connection.CreateCommand();
        replan.CommandText = """
            UPDATE calendar_events SET nudges = NULL
            WHERE id = $eventId AND nudges = $planned AND NOT EXISTS (SELECT 1 FROM calendar_reminders WHERE event_id = $eventId)
            """;
        replan.Parameters.AddWithValue("$eventId", eventId);
        replan.Parameters.AddWithValue("$planned", NudgesPlanned);
        replan.ExecuteNonQuery();
    }

    /// <summary>
    /// Shifts reminders along with the item, so a study plan keeps its spacing. Any that land in
    /// the past are dropped, and any fixed alert the new date calls for is added.
    /// </summary>
    private static void MoveReminders(SqliteConnection connection, StoredItem before, SortedItem after, DateTime now)
    {
        var existing = new List<(long Id, DateTime At, string? Message)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT id, remind_at, message FROM reminders WHERE item_id = $itemId";
            read.Parameters.AddWithValue("$itemId", before.Id);
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                existing.Add((
                    reader.GetInt64(0),
                    DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        var oldTime = before.DueAt is { } dueAt ? TimeOnly.FromDateTime(dueAt.DateTime) : (TimeOnly?)null;
        var shift = before.DueDate is { } oldDate
            ? ReminderPlan.DueMoment(after.DueDate!.Value, after.DueTime) - ReminderPlan.DueMoment(oldDate, oldTime)
            : TimeSpan.Zero;

        // An item with no time was only nominally at the default one, so its planned steps move by whole days.
        var planShift = before.DueDate is { } date && oldTime is null
            ? TimeSpan.FromDays(after.DueDate!.Value.DayNumber - date.DayNumber)
            : shift;

        var kept = new HashSet<DateTime>();
        foreach (var (id, at, message) in existing)
        {
            var moved = at + (message is null ? shift : planShift);
            if (MoveReminder(connection, id, moved, now))
            {
                kept.Add(moved);
            }
        }

        MoveStepReminders(connection, before.Id, planShift, now);

        // A repeating item only keeps coming back if it still has a reminder to snooze.
        var plan = kept.Count == 0 ? after with { RepeatMinutes = ReadRepeatMinutes(connection, before.Id) } : after;
        foreach (var reminder in ReminderPlan.Build(plan, now).Where(reminder => !kept.Contains(reminder.At)))
        {
            AddReminder(connection, before.Id, reminder);
        }
    }

    private static void MoveStepReminders(SqliteConnection connection, long parentId, TimeSpan shift, DateTime now, string parentColumn = "parent_item_id")
    {
        var existing = new List<(long Id, DateTime At)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = $"""
                SELECT r.id, r.remind_at FROM reminders r JOIN items i ON i.id = r.item_id
                WHERE i.{parentColumn} = $parentId AND i.done = 0
                """;
            read.Parameters.AddWithValue("$parentId", parentId);
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                existing.Add((reader.GetInt64(0), DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture)));
            }
        }

        foreach (var (id, at) in existing)
        {
            MoveReminder(connection, id, at + shift, now);
        }
    }

    /// <summary>Returns false if the new time has passed, which drops the reminder.</summary>
    private static bool MoveReminder(SqliteConnection connection, long id, DateTime moved, DateTime now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = moved > now ? "UPDATE reminders SET remind_at = $at WHERE id = $id" : "DELETE FROM reminders WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$at", FormatReminder(moved));
        command.ExecuteNonQuery();
        return moved > now;
    }

    private static int? ReadRepeatMinutes(SqliteConnection connection, long itemId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT repeat_minutes FROM items WHERE id = $id";
        command.Parameters.AddWithValue("$id", itemId);
        return command.ExecuteScalar() is long minutes ? (int)minutes : null;
    }

    /// <summary>Clears what came due while an item was closed, so reopening doesn't fire a backlog.</summary>
    private static void DropMissedReminders(SqliteConnection connection, StoredItem item, DateTime now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = item.Source == ItemSource.Calendar
            ? "DELETE FROM calendar_reminders WHERE event_id = $id AND remind_at <= $now"
            : """
              DELETE FROM reminders WHERE item_id = $id AND remind_at <= $now
                  AND (SELECT kind <> 'Reminder' AND repeat_minutes IS NULL FROM items WHERE id = $id)
              """;
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$now", FormatReminder(now));
        command.ExecuteNonQuery();
    }

    private static void StopReminders(SqliteConnection connection, StoredItem item)
    {
        var isCalendar = item.Source == ItemSource.Calendar;
        if (isCalendar)
        {
            SkipNudges(connection, item.Id);
        }

        // Steps count as the item's reminders, so they stop too.
        using var command = connection.CreateCommand();
        command.CommandText = isCalendar
            ? "DELETE FROM reminders WHERE item_id IN (SELECT id FROM items WHERE parent_event_id = $id)"
            : "DELETE FROM reminders WHERE item_id = $id OR item_id IN (SELECT id FROM items WHERE parent_item_id = $id)";
        command.Parameters.AddWithValue("$id", item.Id);
        command.ExecuteNonQuery();
    }

    private static void SkipNudges(SqliteConnection connection, long eventId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE calendar_events SET nudges = $skipped WHERE id = $id;
            DELETE FROM calendar_reminders WHERE event_id = $id;
            """;
        command.Parameters.AddWithValue("$skipped", NudgesSkipped);
        command.Parameters.AddWithValue("$id", eventId);
        command.ExecuteNonQuery();
    }

    private static void AddReminder(SqliteConnection connection, long itemId, PlannedReminder reminder)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO reminders (item_id, remind_at, message) VALUES ($itemId, $remindAt, $message)";
        command.Parameters.AddWithValue("$itemId", itemId);
        command.Parameters.AddWithValue("$remindAt", FormatReminder(reminder.At));
        command.Parameters.AddWithValue("$message", (object?)reminder.Message ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
