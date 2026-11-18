using System.Globalization;
using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

/// <summary>Calendar feed events: matching them to the feed, and the nudges planned before each assignment.</summary>
public sealed partial class WaypointDatabase
{
    /// <summary>Upcoming assignments that have no nudges yet, soonest first.</summary>
    public IReadOnlyList<StoredItem> GetEventsNeedingNudges(DateOnly today, DateOnly through, int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {EventColumns} FROM calendar_events
            WHERE done = 0 AND nudges IS NULL AND {EventDueDate} >= $today AND {EventDueDate} <= $through
            ORDER BY {EventDueDate}, id LIMIT $limit
            """;
        command.Parameters.AddWithValue("$today", today.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$through", through.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$limit", limit);

        var events = new List<StoredItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            events.Add(ReadEvent(reader));
        }

        return events;
    }

    /// <summary>Stores planned nudges. An event the user skipped in the meantime stays skipped.</summary>
    public void SaveNudges(IReadOnlyList<EventNudges> plans)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        foreach (var plan in plans)
        {
            using var mark = connection.CreateCommand();
            mark.CommandText = "UPDATE calendar_events SET nudges = $planned WHERE id = $id AND nudges IS NULL";
            mark.Parameters.AddWithValue("$planned", NudgesPlanned);
            mark.Parameters.AddWithValue("$id", plan.EventId);
            if (mark.ExecuteNonQuery() == 0)
            {
                continue;
            }

            foreach (var reminder in plan.Reminders)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO calendar_reminders (event_id, remind_at, message) VALUES ($eventId, $remindAt, $message)";
                insert.Parameters.AddWithValue("$eventId", plan.EventId);
                insert.Parameters.AddWithValue("$remindAt", FormatReminder(reminder.At));
                insert.Parameters.AddWithValue("$message", (object?)reminder.Message ?? DBNull.Value);
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    /// <summary>Ends all nudges for an assignment, and keeps them off if the feed moves its date.</summary>
    public void SkipNudges(long eventId)
    {
        using var connection = Open();
        SkipNudges(connection, eventId);
    }

    /// <summary>Matches stored events to the feed, keeping what's already checked off.</summary>
    public void ReplaceCalendarEvents(IReadOnlyList<CalendarEvent> events)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        using (var markStale = connection.CreateCommand())
        {
            markStale.CommandText = "CREATE TEMP TABLE current_uids (uid TEXT PRIMARY KEY)";
            markStale.ExecuteNonQuery();
        }

        foreach (var calendarEvent in events)
        {
            using var upsert = connection.CreateCommand();
            upsert.CommandText = """
                INSERT INTO calendar_events (uid, title, course, due_date, due_at)
                VALUES ($uid, $title, $course, $dueDate, $dueAt)
                ON CONFLICT (uid) DO UPDATE SET
                    title = excluded.title,
                    course = excluded.course,
                    nudges = CASE
                        WHEN calendar_events.nudges = 'planned' AND calendar_events.due_override IS NULL
                            AND (calendar_events.due_date <> excluded.due_date OR calendar_events.due_at IS NOT excluded.due_at)
                        THEN NULL ELSE calendar_events.nudges END,
                    due_date = excluded.due_date,
                    due_at = excluded.due_at;
                INSERT OR IGNORE INTO current_uids (uid) VALUES ($uid);
                """;
            upsert.Parameters.AddWithValue("$uid", calendarEvent.Uid);
            upsert.Parameters.AddWithValue("$title", calendarEvent.Title);
            upsert.Parameters.AddWithValue("$course", (object?)calendarEvent.Course ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$dueDate", calendarEvent.DueDate.ToString("O", CultureInfo.InvariantCulture));
            upsert.Parameters.AddWithValue("$dueAt", (object?)calendarEvent.DueAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
            upsert.ExecuteNonQuery();
        }

        // A past event that leaves the feed still happened, so the log keeps it.
        using var removeStale = connection.CreateCommand();
        removeStale.CommandText = $"""
            INSERT INTO item_history (happened_at, action, source, item_id, kind, title, course, due_date)
            SELECT {EventDueDate} || 'T00:00:00', 'Past', 'Calendar', id, 'Deadline', title, course, {EventDueDate} FROM calendar_events
            WHERE done = 0 AND {EventDueDate} < $today AND uid NOT IN (SELECT uid FROM current_uids);
            DELETE FROM calendar_reminders WHERE event_id IN (
                SELECT id FROM calendar_events WHERE nudges IS NULL OR uid NOT IN (SELECT uid FROM current_uids));
            DELETE FROM calendar_events WHERE uid NOT IN (SELECT uid FROM current_uids);
            DROP TABLE current_uids;
            """;
        removeStale.Parameters.AddWithValue("$today", DateOnly.FromDateTime(DateTime.Now).ToString("O", CultureInfo.InvariantCulture));
        removeStale.ExecuteNonQuery();

        transaction.Commit();
    }
}
