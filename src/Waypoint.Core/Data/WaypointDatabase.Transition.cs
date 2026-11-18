using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Data;

/// <summary>
/// Transition Assist on top of the reminders already here. "10 more minutes" moves the same reminder once and
/// marks it as the follow-up, and "Not now" only clears the reminder. Neither touches the item itself.
/// </summary>
public sealed partial class WaypointDatabase
{
    /// <summary>Brings the reminder back once, ten minutes from now. A follow-up is never moved again here.</summary>
    public void FollowUpReminder(DueReminder reminder, DateTime now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        ForgetAfterThis(connection, reminder.Item.Source, reminder.Item.Id);
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {ReminderTable(reminder.Item)} SET remind_at = $until, follow_up = 1 WHERE id = $id";
        command.Parameters.AddWithValue("$until", FormatReminder(TransitionAssist.FollowUpAt(now)));
        command.Parameters.AddWithValue("$id", reminder.Id);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>When the latest spot was saved, so the offer to save one isn't made twice.</summary>
    public DateTime? GetLastSpotSaved() => GetHandoffs("1 = 1 ORDER BY id DESC LIMIT 1").FirstOrDefault()?.CreatedAt;

    /// <summary>The newest saved spot for a task from the last month, if there is one.</summary>
    public Handoff? GetSpotFor(StoredItem task, DateTime now) => TransitionAssist.SpotFor(task, GetHandoffs("1 = 1 ORDER BY id DESC"), now);

    /// <summary>
    /// "10 more minutes" or "Not now" from a Discord card. These work on any card that had them, including
    /// exams and assignment nudges, since neither one finishes anything.
    /// </summary>
    public string? ApplyTransitionAction(string messageId, AlertActionKind kind, DateTime now)
    {
        if (kind is not (AlertActionKind.MoreTime or AlertActionKind.NotNowReminder))
        {
            return null;
        }

        // Claiming the card and doing what it says are one write, so a failure between can't use up the press.
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        long itemId;
        ItemSource source;
        DateOnly? dueOn;
        using (var find = connection.CreateCommand())
        {
            find.CommandText = "SELECT item_id, source, due_on FROM sent_reminders WHERE handled = 0 AND message_id = $message";
            find.Parameters.AddWithValue("$message", messageId);
            using var reader = find.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            itemId = reader.GetInt64(0);
            source = Enum.Parse<ItemSource>(reader.GetString(1));
            dueOn = reader.IsDBNull(2) ? null : DateOnly.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture);
        }

        if (!ClaimSentReminder(connection, messageId))
        {
            return null;
        }

        var reply = TransitionActionOn(connection, source, itemId, dueOn, kind, now);
        transaction.Commit();
        return reply;
    }

    private static string? TransitionActionOn(SqliteConnection connection, ItemSource source, long itemId, DateOnly? dueOn, AlertActionKind kind, DateTime now)
    {
        if (FindItem(connection, source, itemId) is not { Done: false } item)
        {
            return null;
        }

        // On a dose's card, Skip lets that day go. It isn't counted as declining, since nothing about doses is.
        if (item is { Dose: true, Recurs: not null, DueDate: { } current })
        {
            return AnswerDose(connection, item, dueOn ?? current, kind == AlertActionKind.NotNowReminder ? DoseAnswer.Skip : DoseAnswer.NotNow, now, null);
        }

        if (kind == AlertActionKind.NotNowReminder)
        {
            RecordReminderAnswer(connection, item, ReminderAnswer.NotNow, now);
            return $"Okay. {item.Title} is still on your list.";
        }

        AddFollowUp(connection, item, TransitionAssist.FollowUpAt(now));
        return $"Back once in {TransitionAssist.FollowUpMinutes} minutes: {item.Title}";
    }

    private static void AddFollowUp(SqliteConnection connection, StoredItem item, DateTime at)
    {
        ForgetAfterThis(connection, item.Source, item.Id);
        using var command = connection.CreateCommand();
        command.CommandText = item.Source == ItemSource.Calendar
            ? "INSERT INTO calendar_reminders (event_id, remind_at, message, follow_up) VALUES ($id, $at, NULL, 1)"
            : "INSERT INTO reminders (item_id, remind_at, message, follow_up) VALUES ($id, $at, NULL, 1)";
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$at", FormatReminder(at));
        command.ExecuteNonQuery();
    }
}
