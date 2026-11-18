using System.Globalization;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Data;

public sealed partial class WaypointDatabase
{
    private const string ItemColumns = "id, kind, title, due_date, amount_cents, done, due_time, parent_item_id, parent_event_id, recurrence, dose";
    // The whole text kept under a shortened title, read after ItemColumns for To do. A note's body is the note itself, so it stays behind.
    private const string ItemBody = "CASE WHEN kind = 'Note' THEN NULL ELSE body END";
    // A date the user set wins over the feed's, which keeps updating underneath it.
    private const string EventDueDate = "COALESCE(due_override, due_date)";
    private const string EventDueAt = "CASE WHEN due_override IS NULL THEN due_at ELSE due_override_at END";
    private const string EventColumns = $"id, title, course, {EventDueDate}, {EventDueAt}, done";
    private const string NudgesPlanned = "planned";
    private const string NudgesSkipped = "skipped";
    private const string MonthlyBudgetKey = "monthly_budget_cents";
    private const int MaxRecentlyFinished = 15;

    // A step stays out of sight once its parent is closed or past, and comes back if the parent reopens.
    private const string StepIsLive = $"""
        (parent_item_id IS NULL OR parent_item_id IN (SELECT id FROM items WHERE done = 0))
        AND (parent_event_id IS NULL OR parent_event_id IN (SELECT id FROM calendar_events WHERE done = 0 AND {EventDueDate} >= $today))
        """;

    private static readonly TimeOnly EndOfDay = new(23, 59);

    private readonly string _connectionString;

    public WaypointDatabase(string path)
    {
        // Asked for on every open, so it doesn't rest on how the bundled SQLite was built or on what a pooled
        // connection was last set to. None of the keys cascade: a row still pointed at can't be deleted.
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString();
        RenameOldRepaymentsTable();
        Execute("""
            CREATE TABLE IF NOT EXISTS captures (
                id INTEGER PRIMARY KEY,
                text TEXT NOT NULL,
                created_at TEXT NOT NULL,
                sorted_at TEXT
            );
            CREATE TABLE IF NOT EXISTS items (
                id INTEGER PRIMARY KEY,
                capture_id INTEGER NOT NULL REFERENCES captures(id),
                kind TEXT NOT NULL,
                title TEXT NOT NULL,
                due_date TEXT,
                amount_cents INTEGER,
                done INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS calendar_events (
                id INTEGER PRIMARY KEY,
                uid TEXT NOT NULL UNIQUE,
                title TEXT NOT NULL,
                course TEXT,
                due_date TEXT NOT NULL,
                due_at TEXT,
                done INTEGER NOT NULL DEFAULT 0
            );
            """);
        Execute("""
            CREATE TABLE IF NOT EXISTS reminders (
                id INTEGER PRIMARY KEY,
                item_id INTEGER NOT NULL REFERENCES items(id),
                remind_at TEXT NOT NULL,
                message TEXT
            );
            CREATE TABLE IF NOT EXISTS budget_categories (
                name TEXT PRIMARY KEY,
                monthly_cents INTEGER,
                position INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS budget_accounts (
                name TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                balance_cents INTEGER NOT NULL,
                updated_on TEXT NOT NULL,
                limit_cents INTEGER,
                close_day INTEGER,
                autopay_day INTEGER,
                position INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS wait_list (
                id INTEGER PRIMARY KEY,
                description TEXT NOT NULL,
                price_cents INTEGER NOT NULL,
                added_at TEXT NOT NULL,
                decision TEXT
            );
            CREATE TABLE IF NOT EXISTS other_income (
                id INTEGER PRIMARY KEY,
                received_on TEXT NOT NULL,
                source TEXT NOT NULL,
                amount_cents INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sent_reminders (
                message_id TEXT PRIMARY KEY,
                item_id INTEGER NOT NULL,
                handled INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS checking_activity (
                import_key TEXT PRIMARY KEY,
                posted_on TEXT NOT NULL,
                description TEXT NOT NULL,
                cents INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS card_payments (
                import_key TEXT PRIMARY KEY,
                account TEXT NOT NULL,
                paid_on TEXT NOT NULL,
                cents INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS budget_schedule (
                id INTEGER PRIMARY KEY,
                label TEXT NOT NULL,
                amount_cents INTEGER NOT NULL,
                days TEXT NOT NULL,
                earlier_on_weekend INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS merchant_rules (
                match TEXT PRIMARY KEY,
                category TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS preferences (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS calendar_reminders (
                id INTEGER PRIMARY KEY,
                event_id INTEGER NOT NULL REFERENCES calendar_events(id),
                remind_at TEXT NOT NULL,
                message TEXT
            );
            CREATE TABLE IF NOT EXISTS item_history (
                id INTEGER PRIMARY KEY,
                happened_at TEXT NOT NULL,
                action TEXT NOT NULL,
                source TEXT NOT NULL,
                item_id INTEGER NOT NULL,
                kind TEXT NOT NULL,
                title TEXT NOT NULL,
                course TEXT,
                due_date TEXT,
                next_due_date TEXT,
                reminders TEXT,
                undoes_id INTEGER
            );
            CREATE TABLE IF NOT EXISTS lists (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                created_at TEXT NOT NULL,
                archived_at TEXT
            );
            CREATE TABLE IF NOT EXISTS list_entries (
                id INTEGER PRIMARY KEY,
                list_id INTEGER NOT NULL REFERENCES lists(id),
                text TEXT NOT NULL,
                done INTEGER NOT NULL DEFAULT 0,
                done_at TEXT,
                added_at TEXT NOT NULL
            );
            """);
        CreateDoseTables();
        CreateActivationTables();
        CreateBedtimeTables();
        CreateNoteFolderTables();
        CreateFilterTables();
        CreateWaitingTables();
        CreateNetWorthTables();
        CreateTransferTables();
        CreateImportMatchTables();
        CreateCreditScoreTables();
        CreateHabitTables();
        CreateUndeliveredTables();
        CreateFeedbackTables();
        CreateDiscordThreadTables();
        CreateNoteJobTables();
        CreateDiscordInboxTables();
        CreateCaptureProgressTables();
        AddColumnIfMissing("sent_reminders", "source", "TEXT NOT NULL DEFAULT 'Capture'");
        AddColumnIfMissing("sent_reminders", "start_only", "INTEGER NOT NULL DEFAULT 0");
        CreateAfterThisTables();
        AddColumnIfMissing("captures", "origin", "TEXT NOT NULL DEFAULT 'App'");
        AddColumnIfMissing("captures", "file_name", "TEXT");
        AddColumnIfMissing("captures", "client_id", "TEXT");
        Execute("CREATE UNIQUE INDEX IF NOT EXISTS captures_client_id ON captures (client_id)");
        AddColumnIfMissing("items", "due_time", "TEXT");
        AddColumnIfMissing("items", "repeat_minutes", "INTEGER");
        AddColumnIfMissing("items", "done_at", "TEXT");
        AddColumnIfMissing("items", "parent_item_id", "INTEGER");
        AddColumnIfMissing("items", "parent_event_id", "INTEGER");
        AddColumnIfMissing("items", "recurrence", "TEXT");
        AddColumnIfMissing("items", "canceled", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("items", "category", "TEXT");
        AddColumnIfMissing("items", "one_off", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("items", "reimbursed_cents", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("items", "import_key", "TEXT");
        AddColumnIfMissing("items", "about", "TEXT");
        AddColumnIfMissing("items", "body", "TEXT");
        AddColumnIfMissing("items", "edited_at", "TEXT");
        AddColumnIfMissing("items", "usual_date", "TEXT");
        AddColumnIfMissing("item_history", "usual_date", "TEXT");
        AddColumnIfMissing("list_entries", "removed_at", "TEXT");
        AddColumnIfMissing("lists", "position", "INTEGER");
        AddColumnIfMissing("budget_categories", "period", "TEXT NOT NULL DEFAULT 'Month'");
        AddColumnIfMissing("budget_accounts", "alert_cents", "INTEGER");
        AddColumnIfMissing("budget_accounts", "set_at", "TEXT");
        AddColumnIfMissing("budget_schedule", "pattern", "TEXT NOT NULL DEFAULT 'MonthDays'");
        AddColumnIfMissing("budget_schedule", "anchor", "TEXT");
        AddColumnIfMissing("budget_schedule", "account", "TEXT");
        AddColumnIfMissing("budget_schedule", "added_on", "TEXT");
        AddColumnIfMissing("wait_list", "hours", "INTEGER NOT NULL DEFAULT 48");
        AddColumnIfMissing("wait_list", "reason", "TEXT");
        AddColumnIfMissing("wait_list", "checked_in", "INTEGER NOT NULL DEFAULT 0");
        AddHistoryHiding();
        Execute("""
            CREATE TABLE IF NOT EXISTS pay_calendar (
                schedule_id INTEGER NOT NULL,
                pay_on TEXT NOT NULL,
                PRIMARY KEY (schedule_id, pay_on)
            );
            CREATE TABLE IF NOT EXISTS conversation (
                id INTEGER PRIMARY KEY,
                origin TEXT NOT NULL,
                at TEXT NOT NULL,
                note TEXT NOT NULL,
                reply TEXT NOT NULL,
                refs TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS repayments (
                message_id TEXT PRIMARY KEY,
                payer TEXT NOT NULL,
                cents INTEGER NOT NULL,
                note TEXT,
                received_on TEXT NOT NULL,
                status TEXT NOT NULL,
                item_id INTEGER
            );
            CREATE TABLE IF NOT EXISTS pay_deposits (
                label TEXT NOT NULL,
                due_on TEXT NOT NULL,
                received_on TEXT NOT NULL,
                cents INTEGER NOT NULL,
                typed_key TEXT,
                PRIMARY KEY (label, due_on)
            );
            """);
        AddColumnIfMissing("calendar_events", "done_at", "TEXT");
        AddColumnIfMissing("calendar_events", "nudges", "TEXT");
        AddColumnIfMissing("calendar_events", "canceled", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("calendar_events", "due_override", "TEXT");
        AddColumnIfMissing("calendar_events", "due_override_at", "TEXT");
        AddColumnIfMissing("calendar_events", "reopened", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("reminders", "follow_up", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("repayments", "card_message_id", "TEXT");
        AddColumnIfMissing("calendar_reminders", "follow_up", "INTEGER NOT NULL DEFAULT 0");
        BackfillHistory();
        SeparateBudgetPeriods();
        BackfillImportMatches();
    }

    public string? GetPreference(string key)
    {
        using var connection = Open();
        return ReadPreference(connection, key);
    }

    public void SetPreference(string key, string value)
    {
        using var connection = Open();
        WritePreference(connection, key, value);
    }

    /// <summary>Sets several preferences in one write, for values that only make sense together.</summary>
    public void SetPreferences(params (string Key, string Value)[] values)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var (key, value) in values)
        {
            WritePreference(connection, key, value);
        }

        transaction.Commit();
    }

    private static string? ReadPreference(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM preferences WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void WritePreference(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO preferences (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    public void SetMonthlyBudget(long cents) => SetPreference(MonthlyBudgetKey, cents.ToString(CultureInfo.InvariantCulture));

    /// <summary>Today's spending room, or null until a budget has been set.</summary>
    public long? GetSafeToSpend(DateOnly today) =>
        SpendingSoFar(today) is { } spent ? Briefing.SafeToSpendToday(spent.Budget, spent.Before, spent.Today, today) : null;

    /// <summary>Why safe to spend is $0.00 today, or null when there's room or no budget.</summary>
    public string? GetSafeToSpendReason(DateOnly today) =>
        SpendingSoFar(today) is { } spent ? Briefing.SafeToSpendReason(spent.Budget, spent.Before, spent.Today, today) : null;

    private (long Budget, long Before, long Today)? SpendingSoFar(DateOnly today)
    {
        if (GetPreference(MonthlyBudgetKey) is not { } budget)
        {
            return null;
        }

        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var spentToday = GetSpentCents(today, today);
        var spentBefore = today > monthStart ? GetSpentCents(monthStart, today.AddDays(-1)) : 0;
        return (long.Parse(budget, CultureInfo.InvariantCulture), spentBefore, spentToday);
    }

    /// <summary>Total expenses in a date range. Checked-off expenses count, and canceled ones don't.</summary>
    private long GetSpentCents(DateOnly from, DateOnly through)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COALESCE(SUM(i.amount_cents - i.reimbursed_cents), 0) FROM items i JOIN captures c ON c.id = i.capture_id
            WHERE i.kind = 'Expense' AND i.canceled = 0 AND i.one_off = 0 AND {CountedSql("i")}
              AND COALESCE(i.due_date, substr(c.created_at, 1, 10)) BETWEEN $from AND $through
            """;
        command.Parameters.AddWithValue("$from", from.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$through", through.ToString("O", CultureInfo.InvariantCulture));
        return (long)command.ExecuteScalar()!;
    }

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
                    reader.GetInt64(15) != 0)));
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
                        reader.GetInt64(9) != 0)));
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

    public long AddCapture(string text, DateTimeOffset? createdAt = null, CaptureOrigin origin = CaptureOrigin.App, string? fileName = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO captures (text, created_at, origin, file_name) VALUES ($text, $createdAt, $origin, $fileName) RETURNING id";
        command.Parameters.AddWithValue("$fileName", (object?)fileName ?? DBNull.Value);
        command.Parameters.AddWithValue("$origin", origin.ToString());
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$createdAt", (createdAt?.ToLocalTime() ?? DateTimeOffset.Now).ToString("O", CultureInfo.InvariantCulture));
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// A capture sent from the PC under its own ID. The ID is kept with it, so the PC sending it again, even after
    /// a restart, saves nothing more. Null when it was already saved.
    /// </summary>
    public long? AddSentCapture(string clientId, string text, DateTimeOffset? createdAt, string? fileName)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO captures (text, created_at, origin, file_name, client_id)
            VALUES ($text, $createdAt, $origin, $fileName, $clientId) RETURNING id
            """;
        command.Parameters.AddWithValue("$fileName", (object?)fileName ?? DBNull.Value);
        command.Parameters.AddWithValue("$origin", CaptureOrigin.App.ToString());
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$createdAt", (createdAt?.ToLocalTime() ?? DateTimeOffset.Now).ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$clientId", clientId);
        return command.ExecuteScalar() is long id ? id : null;
    }

    public IReadOnlyList<PendingCapture> GetPendingCaptures()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, text, created_at, origin, file_name FROM captures WHERE sorted_at IS NULL ORDER BY id";

        var captures = new List<PendingCapture>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            captures.Add(new PendingCapture(
                reader.GetInt64(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                Enum.Parse<CaptureOrigin>(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return captures;
    }

    /// <summary>Stores a capture's items and applies its changes together, so a retry can't repeat them.</summary>
    public IReadOnlyList<AppliedChange> CompleteCapture(long captureId, IReadOnlyList<SortedItem> items, IReadOnlyList<ItemChange>? changes = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var applied = CompleteCapture(connection, captureId, items, changes);
        transaction.Commit();
        return applied;
    }

    /// <summary>
    /// The same, inside the caller's write, for a page that makes its own capture and items together. Without
    /// <paramref name="finish"/>, the capture stays pending, for one whose further steps are still to run.
    /// </summary>
    private static List<AppliedChange> CompleteCapture(SqliteConnection connection, long captureId, IReadOnlyList<SortedItem> items, IReadOnlyList<ItemChange>? changes, bool finish = true)
    {
        string? captureText = null;
        foreach (var sorted in items)
        {
            var item = Recurrence.Prepare(sorted, DateOnly.FromDateTime(DateTime.Now));

            // A long capture gets its first sentence as the title, and the whole text goes on the item's note.
            var (title, full) = CaptureTitle.Shorten(item.Title);
            if (full is not null)
            {
                item = item with { Title = title };
            }

            var itemId = InsertItem(connection, captureId, item, null, null);
            if (full is not null)
            {
                // A note made from the whole capture already reads its text from there, and stays a sorted note.
                captureText ??= CaptureText(connection, captureId) ?? "";
                if (item.Kind != ItemKind.Note || full != captureText.Trim())
                {
                    SetItemBody(connection, itemId, full);
                }
            }

            if (item.Steps is { } steps)
            {
                var due = item.DueDate?.ToDateTime(item.DueTime ?? EndOfDay);
                foreach (var step in ReminderPlan.StepItems(steps, due))
                {
                    InsertItem(connection, captureId, step, itemId, null);
                }
            }
        }

        var applied = (changes ?? []).Select(change => ApplyChange(connection, captureId, change, DateTime.Now)).ToList();
        if (!finish)
        {
            return applied;
        }

        // Its steps are all done by now, so what was kept of how far they got goes with it.
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE captures SET sorted_at = $sortedAt WHERE id = $id; DELETE FROM capture_progress WHERE capture_id = $id";
        update.Parameters.AddWithValue("$sortedAt", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
        update.Parameters.AddWithValue("$id", captureId);
        update.ExecuteNonQuery();
        return applied;
    }

    /// <summary>Open items, soonest first. Calendar events from past days are left out.</summary>
    public IReadOnlyList<StoredItem> GetOpenItems()
    {
        using var connection = Open();
        var items = new List<StoredItem>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {ItemColumns}, {ItemBody} FROM items WHERE done = 0 AND {StepIsLive} ORDER BY id";
            command.Parameters.AddWithValue("$today", DateOnly.FromDateTime(DateTime.Now).ToString("O", CultureInfo.InvariantCulture));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadItemWithBody(reader));
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT {EventColumns} FROM calendar_events
                WHERE done = 0 AND {EventDueDate} >= $today
                """;
            command.Parameters.AddWithValue("$today", DateOnly.FromDateTime(DateTime.Now).ToString("O", CultureInfo.InvariantCulture));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadEvent(reader));
            }
        }

        return items
            .OrderBy(item => item.DueDate is null)
            .ThenBy(item => item.DueDate)
            .ThenBy(item => item.DueAt ?? DateTimeOffset.MaxValue)
            .ToList();
    }

    /// <summary>Items finished since <paramref name="since"/>, newest first, so they can be undone.</summary>
    public IReadOnlyList<StoredItem> GetRecentlyFinished(DateTime since)
    {
        using var connection = Open();
        var finished = new List<(string At, StoredItem Item)>();

        using (var command = connection.CreateCommand())
        {
            // An expense carries the card it came in on, so the sorter can tell same-named charges apart.
            command.CommandText = $"SELECT {ItemColumns}, done_at, (SELECT text FROM captures WHERE id = items.capture_id) FROM items WHERE done = 1 AND done_at >= $since AND {CountedSql("items")}";
            command.Parameters.AddWithValue("$since", FormatReminder(since));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var item = ReadItem(reader);
                finished.Add((reader.GetString(11), item.Kind == ItemKind.Expense && !reader.IsDBNull(12) && reader.GetString(12).StartsWith(SpendingRow.ImportedPrefix, StringComparison.Ordinal)
                    ? item with { Account = reader.GetString(12)[SpendingRow.ImportedPrefix.Length..] }
                    : item));
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {EventColumns}, done_at FROM calendar_events WHERE done = 1 AND done_at >= $since";
            command.Parameters.AddWithValue("$since", FormatReminder(since));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                finished.Add((reader.GetString(6), ReadEvent(reader)));
            }
        }

        finished.AddRange(GetRecentRepeats(connection, since));

        return finished
            .OrderByDescending(entry => entry.At, StringComparer.Ordinal)
            .Take(MaxRecentlyFinished)
            .Select(entry => entry.Item)
            .ToList();
    }

    /// <summary>
    /// Finished items that stay on the list a while, so they can be unchecked. One stays while its
    /// date is ahead, or otherwise through the day after it was finished. Canceled ones go to history.
    /// A past feed event that was unchecked comes along open, since nothing else lists it until it's rechecked.
    /// </summary>
    public IReadOnlyList<StoredItem> GetFinishedForList(DateTime now)
    {
        using var connection = Open();
        var today = DateOnly.FromDateTime(now);
        var items = new List<StoredItem>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT {ItemColumns}, {ItemBody} FROM items
                WHERE done = 1 AND canceled = 0 AND kind NOT IN ('Expense', 'Note', 'Shopping') AND {StepIsLive}
                  AND (due_date >= $today OR done_at >= $since)
                ORDER BY id
                """;
            command.Parameters.AddWithValue("$today", today.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$since", FormatReminder(today.AddDays(-1).ToDateTime(TimeOnly.MinValue)));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadItemWithBody(reader));
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT {EventColumns} FROM calendar_events
                WHERE (done = 1 AND canceled = 0 AND ({EventDueDate} >= $today OR done_at >= $since))
                   OR (done = 0 AND reopened = 1 AND {EventDueDate} < $today)
                ORDER BY id
                """;
            command.Parameters.AddWithValue("$today", today.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$since", FormatReminder(today.AddDays(-1).ToDateTime(TimeOnly.MinValue)));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadEvent(reader));
            }
        }

        return items;
    }

    /// <summary>Checks an item off or back on. The change and its History entry are one write.</summary>
    public void SetDone(StoredItem item, bool done)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (done)
        {
            // A dose is marked taken instead, and only once for its day.
            if (TakeDose(connection, item, null, DateTime.Now) is null)
            {
                Finish(connection, item, DateTime.Now);
            }
        }
        else
        {
            Reopen(connection, FindItem(connection, item.Source, item.Id) ?? item, DateTime.Now);
        }

        transaction.Commit();
    }

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

    /// <summary>True if the date on this feed item is one the user gave.</summary>
    public bool IsMovedFromFeed(StoredItem item)
    {
        if (item.Source != ItemSource.Calendar)
        {
            return false;
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT due_override IS NOT NULL FROM calendar_events WHERE id = $id";
        command.Parameters.AddWithValue("$id", item.Id);
        return command.ExecuteScalar() is 1L;
    }

    private static DateOnly? ReadUsualDate(SqliteConnection connection, long itemId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT usual_date FROM items WHERE id = $id";
        command.Parameters.AddWithValue("$id", itemId);
        return command.ExecuteScalar() is string date ? DateOnly.Parse(date, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// Closes an item, or sends a recurring one on to its next date. Past means it moved on without
    /// anyone saying it was done.
    /// </summary>
    /// <param name="keepToday">Moves an earlier day's on to today's, if today has one, even once today's time has passed.</param>
    private static void Finish(SqliteConnection connection, StoredItem item, DateTime now, HistoryAction action = HistoryAction.Done, bool keepToday = false)
    {
        ForgetAfterThis(connection, item.Source, item.Id);
        if (item.Recurs is not { } recurs)
        {
            SetDone(connection, item.Source, item.Id, true, now);
            LogHistory(connection, action, item, now);
            return;
        }

        var today = DateOnly.FromDateTime(now);
        var time = item.DueAt is { } dueAt ? TimeOnly.FromDateTime(dueAt.DateTime) : (TimeOnly?)null;

        // Finishing yesterday's late doesn't use up today's, if today's hasn't come yet.
        var isOverdue = item.DueDate < today;
        var todayIsAhead = isOverdue && now < ReminderPlan.DueMoment(today, time);
        var usual = ReadUsualDate(connection, item.Id);
        var next = recurs.Next(usual ?? item.DueDate ?? today, todayIsAhead || (keepToday && isOverdue) ? today.AddDays(-1) : today);

        CloseSteps(connection, item.Id, null, now);
        LogHistory(connection, action, item, now, next, ReadReminders(connection, item.Id), usualDate: usual);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE items SET due_date = $dueDate, usual_date = NULL WHERE id = $id;
                DELETE FROM reminders WHERE item_id = $id;
                """;
            command.Parameters.AddWithValue("$dueDate", next.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$id", item.Id);
            command.ExecuteNonQuery();
        }

        foreach (var reminder in ReminderPlan.Build(new SortedItem(item.Kind, item.Title, next, null, time), now))
        {
            AddReminder(connection, item.Id, reminder);
        }
    }

    private static void SetCanceled(SqliteConnection connection, StoredItem item, bool canceled)
    {
        var table = item.Source == ItemSource.Calendar ? "calendar_events" : "items";

        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {table} SET canceled = $canceled WHERE id = $id";
        command.Parameters.AddWithValue("$canceled", canceled ? 1 : 0);
        command.Parameters.AddWithValue("$id", item.Id);
        command.ExecuteNonQuery();
    }

    private static void SetDone(SqliteConnection connection, ItemSource source, long id, bool done, DateTime now)
    {
        var table = source == ItemSource.Calendar ? "calendar_events" : "items";

        using var command = connection.CreateCommand();
        var clearReopened = source == ItemSource.Calendar && done ? ", reopened = 0" : "";
        command.CommandText = $"UPDATE {table} SET done = $done, done_at = $doneAt{clearReopened} WHERE id = $id";
        command.Parameters.AddWithValue("$done", done ? 1 : 0);
        command.Parameters.AddWithValue("$doneAt", done ? FormatReminder(now) : DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
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

    private static StoredItem? FindItem(SqliteConnection connection, ItemSource source, long id)
    {
        // A list entry's id belongs to list_entries, so it must never be looked up as an item.
        if (source == ItemSource.List)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = source == ItemSource.Calendar
            ? $"SELECT {EventColumns} FROM calendar_events WHERE id = $id"
            : $"SELECT {ItemColumns} FROM items WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return !reader.Read() ? null : source == ItemSource.Calendar ? ReadEvent(reader) : ReadItem(reader);
    }

    private static string ReminderTable(StoredItem item) =>
        item.Source == ItemSource.Calendar ? "calendar_reminders" : "reminders";

    private static StoredItem ReadEvent(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        ItemKind.Deadline,
        reader.GetString(1),
        DateOnly.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
        null,
        reader.GetInt64(5) != 0,
        ItemSource.Calendar,
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture));

    private static StoredItem ReadItem(SqliteDataReader reader)
    {
        DateOnly? date = reader.IsDBNull(3) ? null : DateOnly.Parse(reader.GetString(3), CultureInfo.InvariantCulture);
        TimeOnly? time = reader.IsDBNull(6) ? null : TimeOnly.Parse(reader.GetString(6), CultureInfo.InvariantCulture);

        return new StoredItem(
            reader.GetInt64(0),
            Enum.Parse<ItemKind>(reader.GetString(1)),
            reader.GetString(2),
            date,
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.GetInt64(5) != 0,
            DueAt: date is { } day && time is { } clock ? new DateTimeOffset(day.ToDateTime(clock)) : null,
            ParentRef: reader.IsDBNull(7)
                ? reader.IsDBNull(8) ? null : ItemRef.Format(ItemSource.Calendar, reader.GetInt64(8))
                : ItemRef.Format(ItemSource.Capture, reader.GetInt64(7)),
            Recurs: reader.IsDBNull(9) ? null : Recurrence.Parse(reader.GetString(9)),
            Dose: reader.GetInt64(10) != 0);
    }

    /// <summary>An item read with <see cref="ItemBody"/> after its columns. A blank body is the same as none.</summary>
    private static StoredItem ReadItemWithBody(SqliteDataReader reader)
    {
        var item = ReadItem(reader);
        return reader.IsDBNull(11) || string.IsNullOrWhiteSpace(reader.GetString(11)) ? item : item with { Body = reader.GetString(11) };
    }

    private static string FormatReminder(DateTime time) => time.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// Older copies kept payment app repayments in a table with another name ending in _repayments. It's renamed to
    /// repayments, so waiting payments and their cards carry on. Nothing happens once repayments exists.
    /// </summary>
    private void RenameOldRepaymentsTable()
    {
        using var connection = Open();
        using var find = connection.CreateCommand();
        find.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name LIKE '%\_repayments' ESCAPE '\'
              AND NOT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'repayments')
            ORDER BY name LIMIT 1
            """;
        if (find.ExecuteScalar() is not string old || old.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
        {
            return;
        }

        using var rename = connection.CreateCommand();
        rename.CommandText = $"ALTER TABLE {old} RENAME TO repayments";
        rename.ExecuteNonQuery();
    }

    private void AddColumnIfMissing(string table, string column, string type)
    {
        using var connection = Open();
        using var check = connection.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        if ((long)check.ExecuteScalar()! == 0)
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// SQLite's own busy wait is set on every connection. The library only retries a locked database while a statement
    /// steps, but an INSERT ... RETURNING commits when its reader closes, so a read running at the same moment (a
    /// Discord view refresh, say) would fail it straight away with "database is locked" instead of waiting.
    /// Foreign keys are checked on every connection too (see the constructor), so a row can't point at one that
    /// isn't there. Rows from older copies that already do are left alone: SQLite doesn't recheck them, and the
    /// reads join to what they point at, so they stay out of sight.
    /// </summary>
    internal SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var wait = connection.CreateCommand();
        wait.CommandText = $"PRAGMA busy_timeout = {(int)BusyWait.TotalMilliseconds}";
        wait.ExecuteNonQuery();
        return connection;
    }

    private static readonly TimeSpan BusyWait = TimeSpan.FromSeconds(30);

    private void Execute(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
