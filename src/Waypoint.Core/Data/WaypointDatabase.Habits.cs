using System.Globalization;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

/// <summary>
/// What the suggestion learns from, read from what's already kept: finished Activation sessions, History, and how
/// startable reminders were answered. The answers are the only thing added, saved as the buttons are pressed, so
/// nothing here asks the user for anything.
/// </summary>
public sealed partial class WaypointDatabase
{
    private void CreateHabitTables() => Execute("""
        CREATE TABLE IF NOT EXISTS reminder_answers (
            id INTEGER PRIMARY KEY,
            answered_at TEXT NOT NULL,
            item_ref TEXT NOT NULL,
            answer TEXT NOT NULL
        );
        """);

    /// <summary>Keeps how a startable reminder was answered, for the suggestion's sense of which times work.</summary>
    public void RecordReminderAnswer(StoredItem item, ReminderAnswer answer, DateTime now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO reminder_answers (answered_at, item_ref, answer) VALUES ($at, $ref, $answer)";
        command.Parameters.AddWithValue("$at", FormatReminder(now));
        command.Parameters.AddWithValue("$ref", ItemRef.Format(item));
        command.Parameters.AddWithValue("$answer", answer.ToString());
        command.ExecuteNonQuery();

        if (answer == ReminderAnswer.NotNow)
        {
            RecordNotNow(ItemRef.Format(item), now);
        }
    }

    /// <summary>The suggestion's sizes and timing, learned from the last few months. Defaults fill in wherever there's too little.</summary>
    public SuggestionHabits GetSuggestionHabits(DateTime now)
    {
        var since = FormatReminder(now.AddDays(-SuggestionHabits.LookBackDays));
        using var connection = Open();

        // A session keeps only its task's ref, so the kind comes from the item itself, which stays after it's done.
        // Both kinds of item are joined in the one query rather than looked up one session at a time.
        var sittings = new List<PastSitting>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT s.step_id, s.started_at, s.begun_at, s.ended_at, CASE WHEN e.id IS NOT NULL THEN '{ItemKind.Deadline}' ELSE i.kind END
                FROM activation_sessions s
                LEFT JOIN items i ON s.task_ref LIKE 'item:%' AND i.id = CAST(substr(s.task_ref, 6) AS INTEGER)
                LEFT JOIN calendar_events e ON s.task_ref LIKE 'feed:%' AND e.id = CAST(substr(s.task_ref, 6) AS INTEGER)
                WHERE s.status = $status AND s.ended_at IS NOT NULL AND s.ended_at > $since AND s.went_stale = 0 AND (i.id IS NOT NULL OR e.id IS NOT NULL)
                """;
            command.Parameters.AddWithValue("$status", SessionStatus.Finished.ToString());
            command.Parameters.AddWithValue("$since", since);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var began = DateTime.Parse(reader.IsDBNull(2) ? reader.GetString(1) : reader.GetString(2), CultureInfo.InvariantCulture);
                var ended = DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture);

                // A session that ran this long was set down and picked up later, so it isn't a real sitting.
                if (ended - began > Activation.StaleAfter)
                {
                    continue;
                }

                sittings.Add(new PastSitting(Enum.Parse<ItemKind>(reader.GetString(4)), !reader.IsDBNull(0), ended, (ended - began).TotalMinutes));
            }
        }

        var answers = new List<PastAnswer>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT answered_at, answer FROM reminder_answers WHERE answered_at > $since";
            command.Parameters.AddWithValue("$since", since);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (Enum.TryParse<ReminderAnswer>(reader.GetString(1), out var answer))
                {
                    answers.Add(new PastAnswer(DateTime.Parse(reader.GetString(0), CultureInfo.InvariantCulture), answer));
                }
            }
        }

        // Only the window being learned from is read, not the whole history.
        var finishes = new List<PastFinish>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {HistoryColumns} FROM item_history WHERE {HistoryIsCurrent} AND action = $action AND due_date IS NOT NULL AND happened_at > $since";
            command.Parameters.AddWithValue("$action", HistoryAction.Done.ToString());
            command.Parameters.AddWithValue("$since", since);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var entry = ReadHistory(reader);
                finishes.Add(new PastFinish(entry.Kind, entry.DueDate!.Value, DateOnly.FromDateTime(entry.At)));
            }
        }

        return SuggestionHabits.Learn(sittings, finishes, answers, now);
    }
}
