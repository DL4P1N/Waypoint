using Microsoft.Data.Sqlite;

namespace Waypoint.Core.Data;

/// <summary>Creating the tables on open, and bringing an older copy's tables up to date.</summary>
public sealed partial class WaypointDatabase
{
    public WaypointDatabase(string path)
    {
        // Asked for on every open, so it doesn't rest on how the bundled SQLite was built or on what a pooled
        // connection was last set to. None of the keys cascade: a row still pointed at can't be deleted.
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString();
        UseWriteAheadLog();
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
        CreateDiscordSendTables();
        AddDiscordSendShownHere();
        CreateNoteJobTables();
        CreateDiscordInboxTables();
        AddDiscordGivenUp();
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
        // An item's latest close, and whether a row was undone, are looked up for every History row shown.
        Execute("""
            CREATE INDEX IF NOT EXISTS item_history_item ON item_history (source, item_id, id);
            CREATE INDEX IF NOT EXISTS item_history_undoes ON item_history (undoes_id);
            """);
        BackfillHistory();
        SeparateBudgetPeriods();
        BackfillImportMatches();
    }

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
}
