using System.Globalization;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

public sealed partial class WaypointDatabase
{
    private const int ImportMatchDays = 2;

    // An alert email is dated when it arrives, and a CSV row by the day of the purchase or when it posted.
    private const int AlertMatchDays = 3;
    private const int PaymentMatchDays = 3;

    // A brokerage deposit email and the checking row for the same transfer land within a few days of each other.
    private const int CushionMatchDays = 4;
    private const string BudgetPeriodsKey = "budget_periods_separated";
    private const string CheckingKeyPrefix = "checking|";

    public IReadOnlyList<BudgetCategory> GetCategories()
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, monthly_cents, period FROM budget_categories ORDER BY position, name";

        var categories = new List<BudgetCategory>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            categories.Add(new BudgetCategory(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), Enum.Parse<BudgetPeriod>(reader.GetString(2))));
        }

        return categories;
    }

    public void SaveCategory(BudgetCategory category)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO budget_categories (name, monthly_cents, position, period)
            VALUES ($name, $monthly, (SELECT COALESCE(MAX(position), 0) + 1 FROM budget_categories), $period)
            ON CONFLICT (name) DO UPDATE SET monthly_cents = excluded.monthly_cents, period = excluded.period
            """;
        command.Parameters.AddWithValue("$name", category.Name);
        command.Parameters.AddWithValue("$monthly", (object?)category.MonthlyCents ?? DBNull.Value);
        command.Parameters.AddWithValue("$period", category.Period.ToString());
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Runs once on a database from before weekly and monthly caps were separate. The flag means
    /// periods the user sets later are left alone.
    /// </summary>
    private void SeparateBudgetPeriods()
    {
        if (GetPreference(BudgetPeriodsKey) is not null)
        {
            return;
        }

        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);

        using (var weekly = connection.CreateCommand())
        {
            weekly.CommandText = "UPDATE budget_categories SET period = 'Week' WHERE name IN ('Groceries', 'Meals out')";
            weekly.ExecuteNonQuery();
        }

        using (var flag = connection.CreateCommand())
        {
            flag.CommandText = "INSERT INTO preferences (key, value) VALUES ($key, '1')";
            flag.Parameters.AddWithValue("$key", BudgetPeriodsKey);
            flag.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static string? FindCategoryName(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM budget_categories WHERE lower(name) = lower($name)";
        command.Parameters.AddWithValue("$name", name.Trim());
        return command.ExecuteScalar() as string;
    }

    /// <summary>Moves a category's purchases and merchant rules to a new name. If that name exists, they merge and its cap wins.</summary>
    private static void MoveCategory(SqliteConnection connection, string from, string to)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE items SET category = $to WHERE lower(category) = lower($from);
            UPDATE merchant_rules SET category = $to WHERE lower(category) = lower($from);
            UPDATE budget_categories SET monthly_cents = COALESCE(monthly_cents, (SELECT monthly_cents FROM budget_categories WHERE name = $from)) WHERE name = $to;
            UPDATE budget_categories SET name = $to WHERE name = $from AND NOT EXISTS (SELECT 1 FROM budget_categories WHERE name = $to);
            DELETE FROM budget_categories WHERE name = $from AND $from <> $to;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        command.ExecuteNonQuery();
    }

    private string SetPeriod(BudgetCategory category, string? period)
    {
        var chosen = period?.Trim().ToLowerInvariant() switch
        {
            "week" or "weekly" => BudgetPeriod.Week,
            "month" or "monthly" => BudgetPeriod.Month,
            _ => (BudgetPeriod?)null,
        };
        if (chosen is not { } value)
        {
            return $"Couldn't update {category.Name}";
        }

        SaveCategory(category with { Period = value });
        return $"{category.Name} is now {(value == BudgetPeriod.Week ? "weekly" : "monthly")}";
    }

    private string RenameCategory(string from, string to)
    {
        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        MoveCategory(connection, from, FindCategoryName(connection, to) ?? to.Trim());
        transaction.Commit();
        return $"{from} is now {to.Trim()}";
    }

    public IReadOnlyList<BudgetAccount> GetBudgetAccounts()
    {
        using var lease = Connect(out var connection);
        return ReadBudgetAccounts(connection);
    }

    private static List<BudgetAccount> ReadBudgetAccounts(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, kind, balance_cents, updated_on, limit_cents, close_day, autopay_day, alert_cents, set_at FROM budget_accounts ORDER BY position, name";

        var accounts = new List<BudgetAccount>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            accounts.Add(new BudgetAccount(
                reader.GetString(0),
                Enum.Parse<AccountKind>(reader.GetString(1)),
                reader.GetInt64(2),
                DateOnly.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture)));
        }

        return accounts;
    }

    public void SaveBudgetAccount(BudgetAccount account)
    {
        using var lease = ConnectForWrite(out var connection);
        SaveBudgetAccount(connection, account);
    }

    private static void SaveBudgetAccount(SqliteConnection connection, BudgetAccount account)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO budget_accounts (name, kind, balance_cents, updated_on, limit_cents, close_day, autopay_day, alert_cents, set_at, position)
            VALUES ($name, $kind, $balance, $updated, $limit, $close, $autopay, $alert, $setAt, (SELECT COALESCE(MAX(position), 0) + 1 FROM budget_accounts))
            ON CONFLICT (name) DO UPDATE SET
                kind = excluded.kind, balance_cents = excluded.balance_cents, updated_on = excluded.updated_on,
                limit_cents = excluded.limit_cents, close_day = excluded.close_day, autopay_day = excluded.autopay_day,
                alert_cents = excluded.alert_cents, set_at = excluded.set_at
            """;
        command.Parameters.AddWithValue("$name", account.Name);
        command.Parameters.AddWithValue("$kind", account.Kind.ToString());
        command.Parameters.AddWithValue("$balance", account.BalanceCents);
        command.Parameters.AddWithValue("$updated", account.UpdatedOn.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$limit", (object?)account.LimitCents ?? DBNull.Value);
        command.Parameters.AddWithValue("$close", (object?)account.CloseDay ?? DBNull.Value);
        command.Parameters.AddWithValue("$autopay", (object?)account.AutopayDay ?? DBNull.Value);
        command.Parameters.AddWithValue("$alert", (object?)account.AlertCents ?? DBNull.Value);
        command.Parameters.AddWithValue("$setAt", (object?)account.SetAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Stores a checking export. Its card payments and purchases also go where they belong. It's all one write, so
    /// a failure partway leaves nothing behind and the same export can simply be imported again.
    /// </summary>
    public ImportResult ImportChecking(IReadOnlyList<CheckingRow> rows)
    {
        var spending = rows
            .Where(row => row.Kind == CheckingKind.Spending)
            .Select(row => new ImportedTransaction(row.Date, CheckingImport.PurchaseTitle(row.Description), -row.Cents, $"{CheckingKeyPrefix}{row.Date:O}|{row.Description}|{row.Cents}"))
            .ToList();
        var waiting = GetWaitList().ToList();
        var deposits = PlanDeposits(rows);

        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        var repeats = new Dictionary<string, int>();
        foreach (var row in rows)
        {
            var key = $"{row.Date:O}|{row.Description}|{row.Cents}";
            repeats[key] = repeats.GetValueOrDefault(key) + 1;

            using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO checking_activity (import_key, posted_on, description, cents) VALUES ($key, $date, $description, $cents)";
            command.Parameters.AddWithValue("$key", $"{key}|{repeats[key]}");
            command.Parameters.AddWithValue("$date", row.Date.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$description", row.Description);
            command.Parameters.AddWithValue("$cents", row.Cents);
            command.ExecuteNonQuery();
        }

        foreach (var payments in rows.Where(row => row.Kind == CheckingKind.CardPayment).GroupBy(row => row.Card!))
        {
            AddCardPayments(connection, payments.Key, payments.Select(row => new ImportedTransaction(row.Date, row.Description, -row.Cents)).ToList());
        }

        var result = ImportTransactions(connection, CheckingImport.CheckingAccount, spending, waiting);
        ConfirmDeposits(connection, deposits);
        transaction.Commit();
        return result;
    }

    /// <summary>Records card payments. The same payment seen twice, even from two exports a few days apart, adds nothing.</summary>
    public void AddCardPayments(string account, IReadOnlyList<ImportedTransaction> payments)
    {
        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        AddCardPayments(connection, account, payments);
        transaction.Commit();
    }

    private static void AddCardPayments(SqliteConnection connection, string account, IReadOnlyList<ImportedTransaction> payments)
    {
        var repeats = new Dictionary<string, int>();
        foreach (var payment in payments)
        {
            var key = $"{account}|{payment.Date:O}|{payment.Description}|{payment.AmountCents}";
            repeats[key] = repeats.GetValueOrDefault(key) + 1;
            if (PaymentSeen(connection, account, payment, $"{key}|{repeats[key]}"))
            {
                continue;
            }

            using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO card_payments (import_key, account, paid_on, cents) VALUES ($key, $account, $date, $cents)";
            command.Parameters.AddWithValue("$key", $"{key}|{repeats[key]}");
            command.Parameters.AddWithValue("$account", account);
            command.Parameters.AddWithValue("$date", payment.Date.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$cents", payment.AmountCents);
            command.ExecuteNonQuery();
        }
    }

    private static bool PaymentSeen(SqliteConnection connection, string account, ImportedTransaction payment, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM card_payments
            WHERE account = $account AND import_key <> $key AND cents = $cents
              AND paid_on BETWEEN $from AND $through AND import_key NOT LIKE $sameFile
            """;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$cents", payment.AmountCents);
        command.Parameters.AddWithValue("$from", payment.Date.AddDays(-PaymentMatchDays).ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$through", payment.Date.AddDays(PaymentMatchDays).ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$sameFile", $"{account}|{payment.Date:O}|{payment.Description}|{payment.AmountCents}|%");
        return (long)command.ExecuteScalar()! > 0;
    }

    /// <summary>
    /// A card's balance on a day, worked out from its last confirmed balance plus the purchases and
    /// payments on either side of it. Only known activity counts, so a typed balance resets the base.
    /// </summary>
    public long GetCardBalance(BudgetAccount card, DateOnly on)
    {
        var anchor = card.UpdatedOn;
        var late = LateAlerts(card);
        return on >= anchor
            ? card.BalanceCents + CardActivity(card.Name, anchor, on) + late
            : card.BalanceCents - (CardActivity(card.Name, on, anchor) - late);
    }

    /// <summary>
    /// Alert emails that came in on the day a card's balance was set, after the moment it was set, so the balance
    /// couldn't have held them. Only alerts carry a time. A CSV row has only its date, so it keeps the day rule.
    /// </summary>
    private long LateAlerts(BudgetAccount card)
    {
        if (card.SetAt is not { } setAt || DateOnly.FromDateTime(setAt.LocalDateTime) != card.UpdatedOn)
        {
            return 0;
        }

        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COALESCE(SUM(i.amount_cents), 0) FROM items i JOIN captures c ON c.id = i.capture_id
            WHERE c.text = $source AND i.kind = 'Expense' AND i.canceled = 0 AND i.due_date = $day AND {OnCardSql("i")}
              AND substr(i.import_key, 1, length($file)) <> $file AND substr(i.import_key, 1, length($checking)) <> $checking
              AND julianday(c.created_at) > julianday($setAt)
            """;
        command.Parameters.AddWithValue("$source", $"{SpendingRow.ImportedPrefix}{card.Name}");
        command.Parameters.AddWithValue("$day", card.UpdatedOn.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$file", $"{card.Name}|");
        command.Parameters.AddWithValue("$checking", CheckingKeyPrefix);
        command.Parameters.AddWithValue("$setAt", setAt.ToString("O", CultureInfo.InvariantCulture));
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Accounts as they stand today, with card balances brought up to date.</summary>
    /// <remarks>
    /// Activity counts after the day a balance was set (posted_on &gt; UpdatedOn), for cards, checking, and the cushion
    /// alike. A balance read off the bank's site already holds that day's posted and pending charges, and CSV rows
    /// carry only a date, so a same-day row can't be told apart from one the balance has, and counting it would count
    /// it twice. The one exception is a card alert email that came in after the moment the balance was typed
    /// (<see cref="BudgetAccount.SetAt"/>), which the balance can't have held, so it counts.
    /// </remarks>
    public IReadOnlyList<BudgetAccount> GetCurrentAccounts(DateOnly today) =>
        GetBudgetAccounts()
            .Select(account => account.Kind switch
            {
                AccountKind.Card => account with { BalanceCents = GetCardBalance(account, today) },
                AccountKind.Checking => account with { BalanceCents = account.BalanceCents + CheckingActivity(account.UpdatedOn, today) },
                AccountKind.Cushion => account with { BalanceCents = account.BalanceCents + CushionActivity(account.UpdatedOn, today) },
                _ => account,
            })
            .ToList();

    /// <summary>Checking and card balances last set more than a week ago, in one line, or null.</summary>
    public string? GetStaleBalances(DateOnly today) => BudgetReport.StaleLine(GetBudgetAccounts(), today);

    /// <summary>The net of checking activity after one day, through another.</summary>
    private long CheckingActivity(DateOnly after, DateOnly through)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(cents), 0) FROM checking_activity WHERE posted_on > $after AND posted_on <= $through";
        command.Parameters.AddWithValue("$after", after.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$through", through.ToString("O", CultureInfo.InvariantCulture));
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// Charges minus payments after one day, through another. A card row that matched a typed purchase counts here,
    /// since the typed one can't say which card it was on, and a second copy of an alert or CSV row doesn't.
    /// </summary>
    private long CardActivity(string account, DateOnly after, DateOnly through)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COALESCE((SELECT SUM(i.amount_cents) FROM items i JOIN captures c ON c.id = i.capture_id
                             WHERE c.text = $source AND i.kind = 'Expense' AND i.canceled = 0 AND i.due_date > $after AND i.due_date <= $through
                               AND {OnCardSql("i")}), 0)
                 - COALESCE((SELECT SUM(cents) FROM card_payments
                             WHERE account = $account AND paid_on > $after AND paid_on <= $through), 0)
            """;
        command.Parameters.AddWithValue("$source", $"{SpendingRow.ImportedPrefix}{account}");
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$after", after.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$through", through.ToString("O", CultureInfo.InvariantCulture));
        return (long)command.ExecuteScalar()!;
    }

    public IReadOnlyList<ScheduledItem> GetSchedule()
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT label, amount_cents, days, earlier_on_weekend, pattern, anchor, id, account FROM budget_schedule ORDER BY id";

        var items = new List<ScheduledItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new ScheduledItem(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray(),
                reader.GetInt64(3) != 0,
                Enum.Parse<PayPattern>(reader.GetString(4)),
                reader.IsDBNull(5) ? null : DateOnly.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                reader.GetInt64(6),
                Account: reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        var calendar = ReadPayCalendar();
        if (calendar.Count > 0)
        {
            items = items.Select(item => calendar.TryGetValue(item.Id, out var dates) ? item with { Dates = dates } : item).ToList();
        }

        return items;
    }

    /// <summary>Adds to the schedule. AddedOn is kept so a transfer isn't checked for dates before it was added.</summary>
    public void AddScheduledItem(ScheduledItem item, DateOnly? addedOn = null)
    {
        using var lease = Connect(out var connection);
        AddScheduledItem(connection, item, addedOn);
    }

    private static void AddScheduledItem(SqliteConnection connection, ScheduledItem item, DateOnly? addedOn = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO budget_schedule (label, amount_cents, days, earlier_on_weekend, pattern, anchor, account, added_on) VALUES ($label, $amount, $days, $earlier, $pattern, $anchor, $account, $added)";
        command.Parameters.AddWithValue("$added", (addedOn ?? DateOnly.FromDateTime(DateTime.Now)).ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$label", item.Label);
        command.Parameters.AddWithValue("$amount", item.AmountCents);
        command.Parameters.AddWithValue("$days", string.Join(',', item.Days));
        command.Parameters.AddWithValue("$earlier", item.EarlierOnWeekend ? 1 : 0);
        command.Parameters.AddWithValue("$pattern", item.Pattern.ToString());
        command.Parameters.AddWithValue("$anchor", (object?)item.Anchor?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$account", (object?)item.Account ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Sets checking from a bank alert that says its balance, sent at <paramref name="alertAt"/>. One older than the
    /// balance already kept is left alone: from an earlier day, or from earlier the same day than a balance typed in
    /// or another alert already read. The alert's time is kept with the balance, so alerts read out of order still
    /// end on the newest. Read and saved in one write, so a balance saved from the Budget page meanwhile isn't lost.
    /// </summary>
    public bool SetCheckingFromAlert(long cents, DateTimeOffset alertAt)
    {
        var on = DateOnly.FromDateTime(alertAt.LocalDateTime);
        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        if (ReadBudgetAccounts(connection).FirstOrDefault(account => account.Kind == AccountKind.Checking) is not { } checking
            || on < checking.UpdatedOn
            || (on == checking.UpdatedOn && checking.SetAt is { } setAt && alertAt <= setAt))
        {
            return false;
        }

        SaveBudgetAccount(connection, checking with { BalanceCents = cents, UpdatedOn = on, SetAt = alertAt });
        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Adds a confirmed deposit to the cushion, and keeps it so a scheduled transfer can be matched to it. Transfers
    /// seen in checking up to that day are kept in the new balance, and the checking row for this one won't add it again.
    /// The deposit and the balance are one write. With the email's id, the same email read again adds nothing, and
    /// this returns false. A blank id is no id, so it never stands in for another email.
    /// </summary>
    public bool AddToCushion(long cents, DateOnly today, string? messageId = null)
    {
        // The balance is read inside the write, so one saved from the Budget page meanwhile is added to, not replaced.
        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        var cushion = ReadBudgetAccounts(connection).FirstOrDefault(account => account.Kind == AccountKind.Cushion);
        if (!RecordBrokerageDeposit(connection, cents, today, string.IsNullOrWhiteSpace(messageId) ? null : messageId))
        {
            return false;
        }

        if (cushion is not null)
        {
            var current = today > cushion.UpdatedOn ? cushion.BalanceCents + CushionActivity(connection, cushion.UpdatedOn, today) : cushion.BalanceCents;
            SaveBudgetAccount(connection, cushion with { BalanceCents = current + cents, UpdatedOn = today > cushion.UpdatedOn ? today : cushion.UpdatedOn });
        }

        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Money moved to the brokerage in checking after one day, through another. A transfer whose deposit email was already
    /// added to the cushion is left out, each email standing in for one checking row of the same amount a few days apart.
    /// </summary>
    private long CushionActivity(DateOnly after, DateOnly through)
    {
        using var lease = Connect(out var connection);
        return CushionActivity(connection, after, through);
    }

    private static long CushionActivity(SqliteConnection connection, DateOnly after, DateOnly through)
    {
        var rows = new List<(DateOnly On, long Cents)>();
        using (var command = connection.CreateCommand())
        {
            var descriptors = BankProfiles.Current.Brokerage.CheckingDescriptors;
            var anyDescriptor = descriptors.Count == 0 ? "0" : string.Join(" OR ", descriptors.Select((_, i) => $"instr(description, $text{i}) > 0"));
            command.CommandText = $"SELECT posted_on, -cents FROM checking_activity WHERE posted_on > $from AND posted_on <= $through AND ({anyDescriptor}) ORDER BY posted_on";
            command.Parameters.AddWithValue("$from", after.AddDays(-2 * CushionMatchDays).ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$through", through.ToString("O", CultureInfo.InvariantCulture));
            for (var i = 0; i < descriptors.Count; i++)
            {
                command.Parameters.AddWithValue($"$text{i}", descriptors[i]);
            }
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((DateOnly.Parse(reader.GetString(0), CultureInfo.InvariantCulture), reader.GetInt64(1)));
            }
        }

        var emails = new List<(DateOnly On, long Cents)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT received_on, cents FROM brokerage_deposits WHERE received_on BETWEEN $from AND $through ORDER BY received_on, id";
            command.Parameters.AddWithValue("$from", after.AddDays(-3 * CushionMatchDays).ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$through", through.AddDays(CushionMatchDays).ToString("O", CultureInfo.InvariantCulture));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                emails.Add((DateOnly.Parse(reader.GetString(0), CultureInfo.InvariantCulture), reader.GetInt64(1)));
            }
        }

        var total = 0L;
        foreach (var row in rows)
        {
            var match = emails
                .Where(email => email.Cents == row.Cents && Math.Abs(email.On.DayNumber - row.On.DayNumber) <= CushionMatchDays)
                .OrderBy(email => Math.Abs(email.On.DayNumber - row.On.DayNumber))
                .Cast<(DateOnly On, long Cents)?>()
                .FirstOrDefault();
            if (match is { } email)
            {
                emails.Remove(email);
            }
            else if (row.On > after)
            {
                total += row.Cents;
            }
        }

        return total;
    }

    /// <summary>Teaches which category a merchant belongs to, by text found in its name.</summary>
    public void SaveMerchantRule(string match, string category)
    {
        using var lease = Connect(out var connection);
        SaveMerchantRule(connection, match, category);
    }

    private static void SaveMerchantRule(SqliteConnection connection, string match, string category)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO merchant_rules (match, category) VALUES ($match, $category)
            ON CONFLICT (match) DO UPDATE SET category = excluded.category
            """;
        command.Parameters.AddWithValue("$match", match.ToLowerInvariant());
        command.Parameters.AddWithValue("$category", category);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<SpendingRow> GetSpending(DateOnly from, DateOnly through) => ReadSpending(from, through, newestFirst: false).ToList();

    /// <summary>The purchases in a date range, oldest first or newest first, read one at a time as they're asked for.</summary>
    private IEnumerable<SpendingRow> ReadSpending(DateOnly from, DateOnly through, bool newestFirst)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SpendDay}, i.title, i.amount_cents, CASE WHEN i.category = $none THEN $uncategorized ELSE COALESCE(i.category, $default) END, i.one_off, i.reimbursed_cents, c.text, c.created_at, i.id
            FROM items i JOIN captures c ON c.id = i.capture_id
            WHERE i.kind = 'Expense' AND i.canceled = 0 AND i.amount_cents IS NOT NULL AND {SpendDay} BETWEEN $from AND $through
              AND {CountedSql("i")}
            ORDER BY {(newestFirst ? "1 DESC, i.id DESC" : "1, i.id")}
            """;
        command.Parameters.AddWithValue("$default", BudgetMath.DefaultCategory);
        command.Parameters.AddWithValue("$none", BudgetMath.Uncategorized);
        command.Parameters.AddWithValue("$uncategorized", BudgetMath.UncategorizedLabel);
        command.Parameters.AddWithValue("$from", from.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$through", through.ToString("O", CultureInfo.InvariantCulture));

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var date = DateOnly.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
            var source = SpendingRow.SourceOf(reader.GetString(6));
            var written = DateTimeOffset.TryParse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp) ? stamp.LocalDateTime : (DateTime?)null;

            yield return new SpendingRow(
                date,
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetInt64(4) != 0,
                reader.GetInt64(5),
                source,
                source == SpendingRow.TypedSource && written is { } at && DateOnly.FromDateTime(at) == date ? TimeOnly.FromDateTime(at) : null,
                reader.GetInt64(8));
        }
    }

    public IReadOnlyList<WaitItem> GetWaitList()
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT description, price_cents, added_at, hours, reason, id FROM wait_list WHERE decision IS NULL ORDER BY added_at";

        var items = new List<WaitItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new WaitItem(
                reader.GetString(0),
                reader.GetInt64(1),
                DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5)));
        }

        return items;
    }

    public void AddWaitItem(string description, long priceCents, DateTime now, int hours = BudgetMath.WaitHours, string? reason = null)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO wait_list (description, price_cents, added_at, hours, reason) VALUES ($description, $price, $added, $hours, $reason)";
        command.Parameters.AddWithValue("$hours", hours);
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$description", description);
        command.Parameters.AddWithValue("$price", priceCents);
        command.Parameters.AddWithValue("$added", FormatReminder(now));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<OtherIncome> GetOtherIncome(int year)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT received_on, source, amount_cents FROM other_income WHERE substr(received_on, 1, 4) = $year ORDER BY received_on";
        command.Parameters.AddWithValue("$year", year.ToString(CultureInfo.InvariantCulture));

        var income = new List<OtherIncome>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            income.Add(new OtherIncome(DateOnly.Parse(reader.GetString(0), CultureInfo.InvariantCulture), reader.GetString(1), reader.GetInt64(2)));
        }

        return income;
    }

    public IReadOnlyList<CategoryDetail> GetCategoryDetails(DateOnly month, DateOnly today)
    {
        var last = month.AddMonths(1).AddDays(-1);
        var weekStart = BudgetMath.WeekStart(today);
        var from = weekStart < month ? weekStart : month;
        var through = today > last ? today : last;
        return BudgetReport.Categories(month, today, GetCategories(), GetSpending(from, through));
    }

    /// <summary>Feedback on recent months against the caps.</summary>
    public ReportSection GetSpendingReview(DateOnly today)
    {
        var from = new DateOnly(today.Year, today.Month, 1).AddMonths(-3);
        return BudgetCoach.Review(today, GetCategories(), GetSpending(from, today));
    }

    public string? GetSpendingTip(DateOnly today)
    {
        var from = new DateOnly(today.Year, today.Month, 1).AddMonths(-3);
        return BudgetCoach.Tip(today, GetCategories(), GetSpending(from, today));
    }

    /// <summary>Everything the Budget window shows.</summary>
    public IReadOnlyList<ReportSection> GetBudgetReport(DateOnly today)
    {
        var weekStart = BudgetMath.WeekStart(today);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var from = weekStart < monthStart ? weekStart : monthStart;
        var rows = GetSpending(from, today).Where(row => row.Date >= monthStart || row.Date >= weekStart).ToList();
        var schedule = GetForecastSchedule(today);
        var received = GetPayDeposits();

        // The extras follow the day asked for, and use the time of day only when that day is today.
        var isToday = DateOnly.FromDateTime(DateTime.Now) == today;
        var now = isToday ? DateTime.Now : today.ToDateTime(TimeOnly.MinValue);
        var accounts = GetCurrentAccounts(today);

        // Working out today's budget is also when net worth is kept for the day, so the trend needs no job of its own.
        if (isToday)
        {
            RecordNetWorthSnapshot(today, accounts);
        }

        var transfers = GetTransferStatuses(today);
        return BudgetReport.Build(today, GetCategories(), rows, accounts, schedule, received)
            .Concat(BudgetReport.Extras(now, GetWaitList(), schedule, GetOtherIncome(today.Year), received, transfers))
            .Append(TransferCheck.Section(transfers, CheckingExportThrough(), HasBrokerageDeposits()))
            .ToList();
    }

    /// <summary>
    /// Adds card purchases as finished expenses. The same purchase twice adds nothing. One already counted another
    /// way isn't counted again: an alert email and a CSV row for the same purchase on the same card, or one typed in
    /// within a couple of days at the same amount. Each counted purchase stands in for at most one row, and one
    /// whose merchant name matches is picked first.
    /// </summary>
    public ImportResult ImportTransactions(string account, IReadOnlyList<ImportedTransaction> transactions)
    {
        var waiting = GetWaitList().ToList();
        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        var result = ImportTransactions(connection, account, transactions, waiting);
        transaction.Commit();
        return result;
    }

    /// <summary>The import itself, inside the caller's write. The wait list is read beforehand, so nothing else opens meanwhile.</summary>
    private static ImportResult ImportTransactions(SqliteConnection connection, string account, IReadOnlyList<ImportedTransaction> transactions, List<WaitItem> waiting)
    {
        using var capture = connection.CreateCommand();
        capture.CommandText = "INSERT INTO captures (text, created_at, sorted_at) VALUES ($text, $now, $now) RETURNING id";
        capture.Parameters.AddWithValue("$text", $"{SpendingRow.ImportedPrefix}{account}");
        capture.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
        var captureId = (long)capture.ExecuteScalar()!;

        var added = 0;
        var skipped = 0;
        var repeats = new Dictionary<string, int>();
        var waitsResolved = 0;

        var keyed = new List<(ImportedTransaction Row, string Key)>();
        foreach (var row in transactions)
        {
            var key = $"{account}|{row.Date:O}|{row.Description}|{row.AmountCents}";
            repeats[key] = repeats.GetValueOrDefault(key) + 1;
            keyed.Add((row, row.Key ?? $"{key}|{repeats[key]}"));
        }

        // Every new row is kept, so a match can be let go later. One that matched a purchase already counted is
        // kept out of the budget by its match. Merchant matches go first, so a row with only the amount in common
        // can't take a match another row fits better.
        var left = new List<(ImportedTransaction Row, string Key, long Id)>();
        foreach (var (row, importKey) in keyed)
        {
            if (ImportExists(connection, importKey))
            {
                skipped++;
                continue;
            }

            var id = InsertImported(connection, captureId, row, importKey);
            if (Absorb(connection, account, row, importKey, id, merchantOnly: true, backfill: false))
            {
                skipped++;
                continue;
            }

            left.Add((row, importKey, id));
        }

        foreach (var (row, importKey, id) in left)
        {
            if (Absorb(connection, account, row, importKey, id, merchantOnly: false, backfill: false))
            {
                skipped++;
                continue;
            }

            added++;

            // A parked purchase isn't spending yet, so this settles the wait without adding a second expense.
            if (WaitItem.MatchImport(waiting, row) is { } parked)
            {
                SettleWait(connection, parked.Id, "bought");
                waiting.Remove(parked);
                waitsResolved++;
            }
        }

        return new ImportResult(added, skipped, waitsResolved);
    }

    private static long InsertImported(SqliteConnection connection, long captureId, ImportedTransaction row, string importKey)
    {
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO items (capture_id, kind, title, due_date, amount_cents, done, done_at, category, import_key)
            VALUES ($captureId, 'Expense', $title, $date, $amount, 1, $doneAt, $category, $key)
            RETURNING id
            """;
        insert.Parameters.AddWithValue("$captureId", captureId);
        insert.Parameters.AddWithValue("$title", row.Description);
        insert.Parameters.AddWithValue("$date", row.Date.ToString("O", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$amount", row.AmountCents);
        insert.Parameters.AddWithValue("$doneAt", FormatReminder(DateTime.Now));
        // A bank's category only counts if a category here goes by it, or the purchase would sit outside every cap.
        var suggested = (row.SuggestedCategories ?? []).Select(name => FindCategoryName(connection, name)).FirstOrDefault(name => name is not null);
        insert.Parameters.AddWithValue("$category", MatchCategory(connection, row.Description) ?? suggested ?? Fallback(connection, row));
        insert.Parameters.AddWithValue("$key", importKey);
        return (long)insert.ExecuteScalar()!;
    }

    /// <summary>Applies a typed budget update and returns what to tell the user.</summary>
    public string ApplyBudgetOp(BudgetOp op, DateOnly today)
    {
        switch (op.Kind)
        {
            case BudgetOpKind.SetCap when op.AmountCents is { } cents:
                var existing = GetCategories().FirstOrDefault(category => Same(category.Name, op.Name));
                SaveCategory(new BudgetCategory(existing?.Name ?? op.Name, cents, existing?.Period ?? BudgetPeriod.Month));
                return $"{existing?.Name ?? op.Name} budget is now {Briefing.Money(cents)} a month";

            case BudgetOpKind.SetPeriod when GetCategories().FirstOrDefault(category => Same(category.Name, op.Name)) is { } timed:
                return SetPeriod(timed, op.Category);

            case BudgetOpKind.RenameCategory when op.Category is { } newName && GetCategories().FirstOrDefault(category => Same(category.Name, op.Name)) is { } renamed:
                return RenameCategory(renamed.Name, newName);

            case BudgetOpKind.SetBalance when op.AmountCents is { } cents && FindAccount(op.Name) is { } account:
                SaveBudgetAccount(account with { BalanceCents = cents, UpdatedOn = today, SetAt = SetMoment(today) });
                return $"{account.Name} is now {Briefing.Money(cents)}";

            case BudgetOpKind.AddAccount when op.AmountCents is { } opening && Enum.TryParse<AccountKind>(op.Category, true, out var kind):
                return AddAccount(op.Name, kind, opening, today);

            case BudgetOpKind.SetLimit when op.AmountCents is { } cents && FindAccount(op.Name) is { } card:
                SaveBudgetAccount(card with { LimitCents = cents });
                return $"{card.Name} limit is now {Briefing.Money(cents)}";

            case BudgetOpKind.Reimburse when op.AmountCents is { } cents:
                return Reimburse(op.Name, cents, today);

            case BudgetOpKind.Categorize when op.Category is { } name && GetCategories().FirstOrDefault(category => Same(category.Name, name)) is { } target:
                return Categorize(op.Name, target.Name);

            case BudgetOpKind.AddWait when op.AmountCents is { } cents:
                var hours = op.Hours is > 0 ? op.Hours.Value : GetDefaultWaitHours();
                AddWaitItem(op.Name, cents, DateTime.Now, hours, op.Reason);
                return $"{op.Name} is on the wait list for {hours} hours";

            case BudgetOpKind.ResolveWait:
                return ResolveWait(op.Name, op.Category ?? "decided");

            case BudgetOpKind.AddIncome when op.AmountCents is { } cents:
                AddIncome(op.Name, cents, today);
                return $"Logged {Briefing.Money(cents)} from {op.Name}. Set aside about {Briefing.Money((long)(cents * BudgetMath.FreelanceTaxShare))} for taxes";

            case BudgetOpKind.MarkOneOff:
                return MarkOneOff(op.Name, today);

            default:
                return $"Couldn't update {op.Name}";
        }
    }

    private const string SpendDay = "COALESCE(i.due_date, substr(c.created_at, 1, 10))";

    private static bool Same(string left, string right) => string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>A new account and its balance. The same name again sets that account's balance instead.</summary>
    private string AddAccount(string name, AccountKind kind, long cents, DateOnly today)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return "What's the account called?";
        }

        if (GetBudgetAccounts().FirstOrDefault(account => Same(account.Name, trimmed)) is { } existing)
        {
            SaveBudgetAccount(existing with { BalanceCents = cents, UpdatedOn = today, SetAt = SetMoment(today) });
            return $"{existing.Name} is now {Briefing.Money(cents)}";
        }

        SaveBudgetAccount(new BudgetAccount(trimmed, kind, cents, today, SetAt: SetMoment(today)));
        return $"Added {trimmed} at {Briefing.Money(cents)}";
    }

    /// <summary>When a typed balance was set, kept only when it's set for today, since a time on another day means nothing.</summary>
    private static DateTimeOffset? SetMoment(DateOnly day) => DateOnly.FromDateTime(DateTime.Now) == day ? DateTimeOffset.Now : null;

    private BudgetAccount? FindAccount(string name) =>
        GetBudgetAccounts().FirstOrDefault(account => account.Name.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase));

    private string Reimburse(string merchant, long cents, DateOnly today)
    {
        using var lease = Connect(out var connection);
        return RecentCharge(connection, merchant, today.AddDays(-60), onlyCounted: false) is { } id && AddRepaid(connection, id, cents) is { } title
            ? $"Marked {Briefing.Money(cents)} repaid on {MerchantName.Clean(title)}"
            : $"Couldn't find a recent {merchant} charge";
    }

    private string MarkOneOff(string merchant, DateOnly today)
    {
        using var lease = Connect(out var connection);
        return RecentCharge(connection, merchant, today.AddDays(-90), onlyCounted: true) is { } id && SetOneOff(connection, id, true) is { } title
            ? $"{MerchantName.Clean(title)} won't count against your budgets"
            : $"Couldn't find a recent {merchant} charge";
    }

    /// <summary>
    /// The charge a ref names, when the sorter picked one out of several, or else the newest charge since
    /// <paramref name="from"/> whose name holds the merchant, which is what typing one means.
    /// </summary>
    private static long? RecentCharge(SqliteConnection connection, string merchant, DateOnly from, bool onlyCounted)
    {
        if (ItemRef.TryParse(merchant.Trim(), out var source, out var itemId))
        {
            return source == ItemSource.Capture ? itemId : null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT i.id FROM items i JOIN captures c ON c.id = i.capture_id
            WHERE i.kind = 'Expense' AND i.canceled = 0 AND ($all = 1 OR i.one_off = 0) AND instr(lower(i.title), $match) > 0 AND {SpendDay} >= $from
              AND {CountedSql("i")}
            ORDER BY {SpendDay} DESC, i.id DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$all", onlyCounted ? 0 : 1);
        command.Parameters.AddWithValue("$match", merchant.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("$from", from.ToString("O", CultureInfo.InvariantCulture));
        return command.ExecuteScalar() is long id ? id : null;
    }

    /// <summary>Adds repaid money to one charge, never past what it cost. Returns its title, or null if it isn't a purchase.</summary>
    private static string? AddRepaid(SqliteConnection connection, long itemId, long cents)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE items SET reimbursed_cents = MIN(amount_cents, reimbursed_cents + $cents)
            WHERE id = $id AND kind = 'Expense' AND canceled = 0
            RETURNING title
            """;
        command.Parameters.AddWithValue("$cents", cents);
        command.Parameters.AddWithValue("$id", itemId);
        return command.ExecuteScalar() as string;
    }

    private static string? SetOneOff(SqliteConnection connection, long itemId, bool oneOff)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE items SET one_off = $oneOff WHERE id = $id AND kind = 'Expense' AND canceled = 0 RETURNING title";
        command.Parameters.AddWithValue("$oneOff", oneOff ? 1 : 0);
        command.Parameters.AddWithValue("$id", itemId);
        return command.ExecuteScalar() as string;
    }

    /// <summary>Money paid back on one purchase, from its right-click menu. Typed repayments use the same update.</summary>
    public OpReply RecordRepayment(long itemId, long? cents)
    {
        if (cents is not > 0)
        {
            return new OpReply("How much was paid back?", true);
        }

        using var lease = Connect(out var connection);
        return AddRepaid(connection, itemId, cents.Value) is { } title
            ? new OpReply($"Marked {Briefing.Money(cents.Value)} repaid on {MerchantName.Clean(title)}")
            : new OpReply("Couldn't find that purchase");
    }

    /// <summary>A one-off doesn't count against the caps. Turning it off counts it again.</summary>
    public OpReply MarkPurchaseOneOff(long itemId, bool oneOff = true)
    {
        using var lease = Connect(out var connection);
        if (SetOneOff(connection, itemId, oneOff) is not { } title)
        {
            return new OpReply("Couldn't find that purchase");
        }

        var name = MerchantName.Clean(title);
        return new OpReply(oneOff ? $"{name} won't count against your budgets" : $"{name} counts against your budgets again");
    }

    /// <summary>
    /// Takes one purchase off the budget, from its right-click menu. It's canceled the way any item is deleted, so
    /// undo can bring it back. An imported one keeps its import key, so the next CSV import or email poll still
    /// knows it and doesn't add it again. Null if there's no such purchase.
    /// </summary>
    public DeletedItem? DeletePurchase(long itemId, DateTime now)
    {
        StoredItem? purchase;
        using (Connect(out var connection))
        {
            purchase = FindItem(connection, ItemSource.Capture, itemId);
            if (purchase is not { Kind: ItemKind.Expense } || IsCanceled(connection, purchase))
            {
                return null;
            }
        }

        return DeleteItem(purchase, now);
    }

    /// <summary>
    /// Moves one purchase to a category and teaches the merchant, the same as typing "Target was groceries".
    /// The rule uses the merchant's clean name when the raw name holds it, so later charges with new
    /// store numbers still match.
    /// </summary>
    public OpReply CategorizePurchase(long itemId, string category)
    {
        if (GetCategories().FirstOrDefault(found => Same(found.Name, category)) is not { } target)
        {
            return new OpReply($"There's no {category.Trim()} category");
        }

        string? title;
        using (Connect(out var connection))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT title FROM items WHERE id = $id AND kind = 'Expense'";
            command.Parameters.AddWithValue("$id", itemId);
            title = command.ExecuteScalar() as string;
        }

        if (title is null)
        {
            return new OpReply("Couldn't find that purchase");
        }

        var clean = MerchantName.Clean(title);
        var match = title.Contains(clean, StringComparison.OrdinalIgnoreCase) ? clean : title;

        // The rule and the purchases it moves are one write, so a failure between can't leave a rule nothing follows.
        using (Connect(out var connection))
        using (var transaction = Begin(connection))
        {
            Categorize(connection, match, target.Name);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE items SET category = $category WHERE id = $id";
                command.Parameters.AddWithValue("$category", target.Name);
                command.Parameters.AddWithValue("$id", itemId);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return new OpReply($"{clean} now counts as {target.Name}");
    }

    /// <summary>
    /// A purchase typed into the Budget page's form. It's stored like a sorted note, so with no category picked
    /// the merchant rule places it. A category picked in the form wins over any merchant rule. One no rule places,
    /// or whose category isn't one of the budget's, is left under Uncategorized so the page asks for a category,
    /// rather than going quietly into Buffer.
    /// </summary>
    public OpReply AddExpense(string title, long? cents, string? category, DateOnly date, DateTime now)
    {
        var clean = title.Trim();
        if (clean.Length == 0)
        {
            return new OpReply("What was it for?", true);
        }

        if (cents is not > 0)
        {
            return new OpReply("How much was it?", true);
        }

        var chosen = string.IsNullOrWhiteSpace(category) ? null : GetCategories().FirstOrDefault(found => Same(found.Name, category))?.Name;

        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        var captureId = InsertHandTyped(connection, clean, now);
        CompleteCapture(connection, captureId, [new SortedItem(ItemKind.Expense, clean, date, cents, Category: chosen ?? BudgetMath.Uncategorized)], null);
        if (chosen is not null)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE items SET category = $category WHERE capture_id = $captureId AND kind = 'Expense'";
            command.Parameters.AddWithValue("$category", chosen);
            command.Parameters.AddWithValue("$captureId", captureId);
            command.ExecuteNonQuery();
        }

        using var find = connection.CreateCommand();
        find.CommandText = "SELECT id FROM items WHERE capture_id = $captureId AND kind = 'Expense' LIMIT 1";
        find.Parameters.AddWithValue("$captureId", captureId);
        var itemId = find.ExecuteScalar() as long?;
        transaction.Commit();
        return new OpReply($"Added {Briefing.Money(cents.Value)} for {clean}", ItemId: itemId);
    }

    /// <summary>A new category from the Budget page. A cap is optional, and a weekly one keeps its monthly amount.</summary>
    public OpReply AddCategory(string name, long? monthlyCents, BudgetPeriod period)
    {
        var clean = name.Trim();
        if (clean.Length == 0)
        {
            return new OpReply("It needs a name.", true);
        }

        if (GetCategories().Any(found => Same(found.Name, clean)))
        {
            return new OpReply($"There's already a {clean} category.", true);
        }

        SaveCategory(new BudgetCategory(clean, monthlyCents, period));
        var cap = monthlyCents is { } cents ? $" with {Briefing.Money(cents)} a month" : "";
        return new OpReply($"Added {clean}{cap}");
    }

    /// <summary>Takes one purchase off the wait list, from its right-click menu, with what was decided.</summary>
    public OpReply ResolveWaitItem(long id, string decision)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE wait_list SET decision = $decision WHERE id = $id AND decision IS NULL RETURNING description";
        command.Parameters.AddWithValue("$decision", decision);
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string description
            ? new OpReply($"{description} is off the wait list")
            : new OpReply("That's already off the wait list");
    }

    private string ResolveWait(string name, string decision)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE wait_list SET decision = $decision
            WHERE id = (SELECT id FROM wait_list WHERE decision IS NULL AND instr(lower(description), $match) > 0 ORDER BY added_at LIMIT 1)
            """;
        command.Parameters.AddWithValue("$decision", decision);
        command.Parameters.AddWithValue("$match", name.Trim().ToLowerInvariant());
        return command.ExecuteNonQuery() > 0 ? $"{name} is off the wait list" : $"Couldn't find {name} on the wait list";
    }

    private void AddIncome(string source, long cents, DateOnly today)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO other_income (received_on, source, amount_cents) VALUES ($date, $source, $amount)";
        command.Parameters.AddWithValue("$date", today.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$amount", cents);
        command.ExecuteNonQuery();
    }

    private string Categorize(string merchant, string category)
    {
        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        var reply = Categorize(connection, merchant, category);
        transaction.Commit();
        return reply;
    }

    private static string Categorize(SqliteConnection connection, string merchant, string category)
    {
        SaveMerchantRule(connection, merchant, category);

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE items SET category = $category WHERE kind = 'Expense' AND instr(lower(title), $match) > 0";
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$match", merchant.Trim().ToLowerInvariant());
        var changed = command.ExecuteNonQuery();
        return $"{merchant} now counts as {category} ({changed} moved)";
    }

    /// <summary>
    /// For an imported purchase no rule or bank category placed. Eating out goes in a food category if there is one,
    /// and is otherwise left uncategorized, never put in Buffer. A grocery store goes in a groceries category if there
    /// is one. Anything else goes in Buffer as before.
    /// </summary>
    private static string Fallback(SqliteConnection connection, ImportedTransaction row)
    {
        var eatingOut = row.Restaurant || (row.SuggestedCategories is null && RestaurantPurchases.LooksLikeRestaurant(row.Description));
        var grocery = !row.Restaurant && GroceryPurchases.LooksLikeGrocery(row.Description);
        if (!eatingOut && !grocery)
        {
            return BudgetMath.DefaultCategory;
        }

        var names = CategoryNames(connection);
        return eatingOut
            ? RestaurantPurchases.FoodCategory(names) ?? BudgetMath.Uncategorized
            : GroceryPurchases.GroceryCategory(names) ?? BudgetMath.DefaultCategory;
    }

    private static List<string> CategoryNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM budget_categories ORDER BY name";
        var names = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }
        }

        return names;
    }

    private static string? MatchCategory(SqliteConnection connection, string title)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT category FROM merchant_rules WHERE instr(lower($title), match) > 0 ORDER BY length(match) DESC LIMIT 1";
        command.Parameters.AddWithValue("$title", title);
        return command.ExecuteScalar() as string;
    }
}
