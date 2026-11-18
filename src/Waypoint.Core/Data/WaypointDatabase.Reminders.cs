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
}
