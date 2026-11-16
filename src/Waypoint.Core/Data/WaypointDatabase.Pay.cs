using System.Globalization;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

public sealed partial class WaypointDatabase
{
    private const string MainPayLabel = "Paycheck";
    private const string WaitHoursKey = "wait_hours";
    private const string HourlyRateKey = "hourly_rate";
    private const decimal MaxHourlyRate = 1000m;

    // Transfers and investment moves can look like a paycheck, so they never confirm one.
    private static bool IsNotPay(string description) =>
        description.Contains("TRANSFER", StringComparison.OrdinalIgnoreCase)
        || BankProfiles.Current.PaymentApp.Mentions(description)
        || CheckingImport.IsBrokerageDeposit(description);

    public IReadOnlyList<PayDeposit> GetPayDeposits()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT label, due_on, received_on, cents, typed_key FROM pay_deposits ORDER BY received_on";

        var deposits = new List<PayDeposit>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            deposits.Add(new PayDeposit(
                reader.GetString(0),
                DateOnly.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                DateOnly.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                reader.GetInt64(3),
                !reader.IsDBNull(4)));
        }

        return deposits;
    }

    /// <summary>
    /// The schedule plus any open recurring item that already carries an amount, at its next date. A to-do with the
    /// same name as a bill on the schedule is that bill, so it isn't counted twice. The main paycheck is estimated
    /// from the shifts in each pay period, and keeps its flat amount for a period with none.
    /// </summary>
    public IReadOnlyList<ScheduledItem> GetForecastSchedule(DateOnly? today = null)
    {
        var day = today ?? DateOnly.FromDateTime(DateTime.Now);
        var saved = GetSchedule();
        var main = MainPaycheck(saved);
        var rate = GetHourlyRate();
        var schedule = saved
            .Select(item => main is not null && item.Id == main.Id ? PayEstimate.WithEstimates(item, day, BudgetMath.ForecastDays, rate, GetShifts) : item)
            .ToList();
        var billNames = schedule.Where(item => item.IsBill).Select(item => item.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bills = GetOpenItems()
            .Where(item => item is { Recurs: not null, DueDate: not null, AmountCents: > 0 } && item.Kind != ItemKind.Expense && !billNames.Contains(item.Title))
            .Select(item => new ScheduledItem(item.Title, -item.AmountCents!.Value, [], Pattern: PayPattern.Once, Anchor: item.DueDate));
        return schedule.Concat(bills).ToList();
    }

    public OpReply SetPaySchedule(string? pattern, IReadOnlyList<string> days, long? cents, DateTime? next)
    {
        var current = MainPaycheck();
        var anchor = next is { } date ? DateOnly.FromDateTime(date) : (DateOnly?)null;
        var dayNumbers = days.Select(day => int.TryParse(day, out var number) ? number : 0).Where(day => day is >= 1 and <= 31).ToArray();

        PayPattern kind;
        switch (pattern?.Trim().ToLowerInvariant())
        {
            case "weekly":
            case "biweekly":
                kind = pattern.Trim().Equals("weekly", StringComparison.OrdinalIgnoreCase) ? PayPattern.Weekly : PayPattern.Biweekly;
                if (anchor is null)
                {
                    return new OpReply("What date is your next payday?", true);
                }

                dayNumbers = [];
                break;
            case "twice_monthly":
                kind = PayPattern.MonthDays;
                if (dayNumbers.Length != 2)
                {
                    return new OpReply("Which two days of the month do you get paid?", true);
                }

                anchor = null;
                break;
            case "monthly":
                kind = PayPattern.MonthDays;
                dayNumbers = dayNumbers.Length == 1 ? dayNumbers : anchor is { } first ? [first.Day] : [];
                if (dayNumbers.Length != 1)
                {
                    return new OpReply("Which day of the month do you get paid?", true);
                }

                anchor = null;
                break;
            default:
                return new OpReply("How often do you get paid: weekly, every other week, twice a month, or monthly?", true);
        }

        if ((cents ?? current?.AmountCents) is not { } amount)
        {
            return new OpReply("About how much is each paycheck?", true);
        }

        var item = new ScheduledItem(current?.Label ?? MainPayLabel, amount, dayNumbers, current?.EarlierOnWeekend ?? true, kind, anchor, current?.Id ?? 0);
        SavePaycheck(item);
        ClearPayCalendar(item.Id);
        var today = DateOnly.FromDateTime(DateTime.Now);
        return new OpReply($"{item.Label} is now {Briefing.Money(amount)} {Cadence(item)}, next expected {ShortDate(BudgetMath.NextOccurrence(item, today) ?? today)}");
    }

    public OpReply SetPayAmount(long? cents)
    {
        if (cents is not { } amount)
        {
            return new OpReply("About how much is each paycheck?", true);
        }

        if (MainPaycheck() is not { } current)
        {
            // A calendar waiting on its amount is the schedule this answers.
            if (GetPayProposal(DateTime.Now) is { } proposal)
            {
                return ProposePayDates(proposal with { AmountCents = amount }, DateTime.Now);
            }

            return new OpReply("How often do you get paid: weekly, every other week, twice a month, or monthly?", true);
        }

        SavePaycheck(current with { AmountCents = amount });
        return new OpReply($"{current.Label} is now {Briefing.Money(amount)}");
    }

    /// <summary>An irregular deposit, expected once.</summary>
    public OpReply AddExpectedDeposit(string? name, long? cents, DateTime? at)
    {
        if (cents is not { } amount || at is not { } date)
        {
            return new OpReply("How much is coming, and on what date?", true);
        }

        var day = DateOnly.FromDateTime(date);
        var label = name ?? "Deposit";
        AddScheduledItem(new ScheduledItem(label, amount, [], Pattern: PayPattern.Once, Anchor: day));
        return new OpReply($"Expecting {Briefing.Money(amount)} from {label} on {ShortDate(day)}");
    }

    /// <summary>
    /// "I got paid" with no export yet. It raises checking through a typed row, which the real
    /// deposit replaces when it's imported, so the money only counts once.
    /// </summary>
    public OpReply ConfirmPayTyped(long? cents, DateOnly today)
    {
        var match = IncomeNear(GetSchedule(), today)
            .Select(item => (Item: item, Due: BudgetMath.ExpectedNear(item, today, null)))
            .Where(entry => entry.Due is not null)
            .OrderBy(entry => Math.Abs(entry.Due!.Value.DayNumber - today.DayNumber))
            .FirstOrDefault();
        if (match.Item is not { } item)
        {
            return new OpReply("I don't see a paycheck expected around now. If it's other income, tell me where it came from.", true);
        }

        var due = match.Due!.Value;
        if (GetPayDeposits().Any(deposit => deposit.Label == item.Label && deposit.DueOn == due))
        {
            return new OpReply($"Your {ShortDate(due)} {item.Label.ToLowerInvariant()} is already counted");
        }

        var amount = cents ?? item.AmountOn(due);
        var key = $"typed|{item.Label}|{due:O}";

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var activity = connection.CreateCommand())
        {
            activity.CommandText = "INSERT OR IGNORE INTO checking_activity (import_key, posted_on, description, cents) VALUES ($key, $date, $description, $cents)";
            activity.Parameters.AddWithValue("$key", key);
            activity.Parameters.AddWithValue("$date", today.ToString("O", CultureInfo.InvariantCulture));
            activity.Parameters.AddWithValue("$description", $"{item.Label} (typed)");
            activity.Parameters.AddWithValue("$cents", amount);
            activity.ExecuteNonQuery();
        }

        RecordDeposit(connection, item.Label, due, today, amount, key);
        transaction.Commit();
        return new OpReply($"Got it, {Briefing.Money(amount)} {item.Label.ToLowerInvariant()} counted");
    }

    /// <summary>What Until payday and "when do I get paid" say before there's a schedule, with the two ways to add one.</summary>
    public const string NoPaySchedule = "No pay schedule yet. Add one with + and Pay schedule on Budget, or type something like \"I get paid on the 1st and 15th, about $850\" in Ask.";

    /// <summary>
    /// The next paycheck first, with where its amount comes from, then how paydays are worked out and the last one
    /// that arrived.
    /// </summary>
    public string DescribePay(DateOnly today)
    {
        if (MainPaycheck() is not { } pay)
        {
            return NoPaySchedule;
        }

        var deposits = GetPayDeposits();
        var estimated = GetForecastSchedule(today).FirstOrDefault(item => item.Id == pay.Id) ?? pay;
        // One short line to read, then how it's worked out after a blank line, which the page shows smaller.
        string next;
        var how = "";
        if (BudgetMath.NextOccurrence(pay, today.AddDays(1), deposits) is not { } date)
        {
            next = $"{pay.Label}: about {Briefing.Money(pay.AmountCents)}.";
        }
        else if (estimated.Amounts?.ContainsKey(date) == true)
        {
            next = $"Next pay: about {WholeDollars(estimated.AmountOn(date))} on {date.ToString("ddd, MMM d", CultureInfo.InvariantCulture)}.";
            how = $"How this is worked out: from your shifts at {Briefing.Money((long)(GetHourlyRate() * 100))} an hour."
                + (PayEstimate.Explain(pay, date, GetHourlyRate(), GetShifts) is { } explained ? $" {explained}" : "") + " ";
        }
        else
        {
            next = $"Next pay: about {Briefing.Money(pay.AmountCents)} on {date.ToString("ddd, MMM d", CultureInfo.InvariantCulture)}.";
        }

        var cadence = pay.Dates is { Count: > 0 } dates && dates[^1] >= today
            ? $"Paydays are from your pay calendar through {ShortDate(dates[^1])}."
            : $"Paid {Cadence(pay)}.";
        var last = deposits.LastOrDefault(deposit => deposit.Label == pay.Label) is { } deposit
            ? $" Last confirmed {ShortDate(deposit.ReceivedOn)}, {Briefing.Money(deposit.Cents)}."
            : "";
        return $"{next}\n\n{how}{cadence}{last}";
    }

    /// <summary>
    /// The main paycheck as the pay schedule form starts out, or null with none yet. The rate is set when the next
    /// paycheck is estimated from shifts, so the form can say the amount is only the fallback.
    /// </summary>
    public PayScheduleForm? GetPayScheduleForm(DateOnly today)
    {
        if (MainPaycheck() is not { } pay)
        {
            return null;
        }

        var deposits = GetPayDeposits();
        var next = BudgetMath.NextOccurrence(pay, today.AddDays(1), deposits);
        var pattern = pay.Pattern switch
        {
            PayPattern.Weekly => "weekly",
            PayPattern.Biweekly => "biweekly",
            PayPattern.MonthDays when pay.Days.Length == 2 => "twice_monthly",
            PayPattern.MonthDays when pay.Days.Length == 1 => "monthly",
            _ => null,
        };

        // A monthly payday that moved earlier for a weekend still means its usual day of the month.
        // One moved back past the start of the month, like the 1st paid on the Friday before, belongs to the next month.
        if (pattern == "monthly" && next is { } moved)
        {
            var month = moved.Day > pay.Days[0] ? moved.AddMonths(1) : moved;
            var day = Math.Min(pay.Days[0], DateTime.DaysInMonth(month.Year, month.Month));
            next = new DateOnly(month.Year, month.Month, day);
        }

        var estimated = next is { } date && GetForecastSchedule(today).FirstOrDefault(item => item.Id == pay.Id)?.Amounts?.ContainsKey(date) == true;
        var calendar = pay.Dates is { Count: > 0 } dates && dates[^1] >= today ? dates[^1] : (DateOnly?)null;
        return new PayScheduleForm(pattern, pay.Days, next, pay.AmountCents, estimated ? GetHourlyRate() : null, calendar);
    }

    public OpReply CheckAffordable(long? cents, string? card, DateOnly today)
    {
        if (cents is not { } price)
        {
            return new OpReply("How much is it?", true);
        }

        var accounts = GetCurrentAccounts(today);
        var checking = accounts.FirstOrDefault(account => account.Kind == AccountKind.Checking);
        var answer = AffordCheck.Check(new AffordRequest(
            price,
            today,
            checking,
            checking is null ? null : LastCheckingSeen(checking),
            GetForecastSchedule(today),
            accounts.Where(account => account.Kind == AccountKind.Card).ToList(),
            GetPayDeposits(),
            card,
            accounts.FirstOrDefault(account => account.Kind == AccountKind.Cushion),
            GetSafeToSpend(today),
            GetSavingsGoalStatus(today)));
        return new OpReply(answer.Text, true);
    }

    /// <summary>An estimate to the nearest dollar, since its cents would claim more than is known.</summary>
    private static string WholeDollars(long cents) => Math.Round(cents / 100m, MidpointRounding.AwayFromZero).ToString("$#,##0", CultureInfo.InvariantCulture);

    /// <summary>Dollars an hour, which paychecks are estimated from when there are shifts to go on.</summary>
    public decimal GetHourlyRate() =>
        GetPreference(HourlyRateKey) is { } text && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate) && rate > 0
            ? rate
            : PayEstimate.DefaultRate;

    public OpReply SetHourlyRate(decimal? dollars)
    {
        if (dollars is not > 0 || dollars > MaxHourlyRate)
        {
            return new OpReply("How much do you make an hour?", true);
        }

        var rate = Math.Round(dollars.Value, 2);
        SetPreference(HourlyRateKey, rate.ToString(CultureInfo.InvariantCulture));
        return new OpReply($"Hourly rate is now {Briefing.Money((long)(rate * 100))}. Paychecks are estimated from your shifts at that rate.");
    }

    public int GetDefaultWaitHours() =>
        GetPreference(WaitHoursKey) is { } text && int.TryParse(text, CultureInfo.InvariantCulture, out var hours) ? hours : BudgetMath.WaitHours;

    public OpReply SetDefaultWaitHours(string? text)
    {
        if (!int.TryParse(text, CultureInfo.InvariantCulture, out var hours) || hours <= 0)
        {
            return new OpReply("How many hours should the wait list wait?", true);
        }

        SetPreference(WaitHoursKey, hours.ToString(CultureInfo.InvariantCulture));
        return new OpReply($"New wait list items will wait {hours} hours");
    }

    /// <summary>Waits that just ended, each returned once, whether or not anything comes of it.</summary>
    public IReadOnlyList<WaitItem> TakeWaitCheckIns(DateTime now)
    {
        var ready = GetWaitList().Where(item => item.ReadyAt <= now).ToList();
        if (ready.Count == 0)
        {
            return ready;
        }

        var unchecked_ = new List<WaitItem>();
        using var connection = Open();
        foreach (var item in ready)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE wait_list SET checked_in = 1 WHERE id = $id AND checked_in = 0";
            command.Parameters.AddWithValue("$id", item.Id);
            if (command.ExecuteNonQuery() > 0)
            {
                unchecked_.Add(item);
            }
        }

        return unchecked_;
    }

    public IReadOnlyList<string> GetSpendingPatterns(DateOnly today)
    {
        var month = new DateOnly(today.Year, today.Month, 1);
        var recent = today.AddDays(-31);
        return BudgetCoach.Patterns(today, GetCategories(), GetSpending(recent < month ? recent : month, today));
    }

    private static string Cadence(ScheduledItem item) => item.Pattern switch
    {
        PayPattern.Weekly => "every week",
        PayPattern.Biweekly => "every other week",
        PayPattern.Once => "once",
        _ when item.Days.Length == 0 => "every month",
        _ => $"on the {string.Join(" and ", item.Days.Select(Ordinal))} of each month",
    };

    /// <summary>A day of the month as it's said: 1st, 2nd, 3rd, 11th, 22nd.</summary>
    internal static string Ordinal(int day) =>
        day + (day % 100 is 11 or 12 or 13 ? "th" : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    private static void SettleWait(SqliteConnection connection, long id, string decision)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE wait_list SET decision = $decision WHERE id = $id";
        command.Parameters.AddWithValue("$decision", decision);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static void RecordDeposit(SqliteConnection connection, string label, DateOnly due, DateOnly received, long cents, string? typedKey)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pay_deposits (label, due_on, received_on, cents, typed_key) VALUES ($label, $due, $received, $cents, $typed)
            ON CONFLICT (label, due_on) DO UPDATE SET received_on = excluded.received_on, cents = excluded.cents, typed_key = excluded.typed_key
            """;
        command.Parameters.AddWithValue("$label", label);
        command.Parameters.AddWithValue("$due", due.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$received", received.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$cents", cents);
        command.Parameters.AddWithValue("$typed", (object?)typedKey ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private ScheduledItem? MainPaycheck() => MainPaycheck(GetSchedule());

    private static ScheduledItem? MainPaycheck(IReadOnlyList<ScheduledItem> schedule)
    {
        var income = schedule.Where(item => item.AmountCents > 0 && item.Pattern != PayPattern.Once).ToList();
        return income.FirstOrDefault(item => item.Label.Contains("pay", StringComparison.OrdinalIgnoreCase)) ?? income.FirstOrDefault();
    }

    /// <summary>Changes the main paycheck in place, so there's never a second one.</summary>
    private void SavePaycheck(ScheduledItem item)
    {
        if (item.Id == 0)
        {
            AddScheduledItem(item);
            return;
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE budget_schedule SET amount_cents = $amount, days = $days, pattern = $pattern, anchor = $anchor WHERE id = $id";
        command.Parameters.AddWithValue("$amount", item.AmountCents);
        command.Parameters.AddWithValue("$days", string.Join(',', item.Days));
        command.Parameters.AddWithValue("$pattern", item.Pattern.ToString());
        command.Parameters.AddWithValue("$anchor", (object?)item.Anchor?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", item.Id);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Ties real deposits to what was expected, so the forecast doesn't count them again. Rows are matched
    /// by date and amount, and the key on each expected date makes a repeated export change nothing.
    /// </summary>
    private void ConfirmDeposits(IReadOnlyList<CheckingRow> rows)
    {
        var schedule = GetSchedule();
        var deposits = rows
            .Where(row => row.Kind == CheckingKind.Other && row.Cents > 0)
            .Where(row => !IsNotPay(row.Description))
            .OrderBy(row => row.Date);

        using var connection = Open();
        foreach (var row in deposits)
        {
            var typed = FindTyped(connection, row);
            var match = typed is not null
                ? typed
                : IncomeNear(schedule, row.Date)
                    .Select(item => (item.Label, Due: BudgetMath.ExpectedNear(item, row.Date, row.Cents)))
                    .Where(entry => entry.Due is not null)
                    .OrderBy(entry => Math.Abs(entry.Due!.Value.DayNumber - row.Date.DayNumber))
                    .Select(entry => ((string Label, DateOnly Due, string? Key)?)(entry.Label, entry.Due!.Value, null))
                    .FirstOrDefault();
            if (match is not { } found || (found.Key is null && DepositKnown(connection, found.Label, found.Due)))
            {
                continue;
            }

            if (found.Key is { } key)
            {
                using var remove = connection.CreateCommand();
                remove.CommandText = "DELETE FROM checking_activity WHERE import_key = $key";
                remove.Parameters.AddWithValue("$key", key);
                remove.ExecuteNonQuery();
            }

            RecordDeposit(connection, found.Label, found.Due, row.Date, row.Cents, null);
        }
    }

    /// <summary>
    /// The income on the schedule, with the main paycheck's estimates for paydays near a day, so a deposit is matched
    /// against what that check was expected to be and not only the flat amount.
    /// </summary>
    private List<ScheduledItem> IncomeNear(IReadOnlyList<ScheduledItem> schedule, DateOnly day)
    {
        var main = MainPaycheck(schedule);
        var rate = GetHourlyRate();
        return schedule
            .Where(item => item.AmountCents > 0)
            .Select(item => main is not null && item.Id == main.Id
                ? PayEstimate.WithEstimates(item, day.AddDays(-BudgetMath.PayMatchDays), BudgetMath.PayMatchDays * 2, rate, GetShifts)
                : item)
            .ToList();
    }

    /// <summary>A typed paycheck this deposit is the real version of. Typed amounts are rough, so only a very different one rules it out.</summary>
    private static (string Label, DateOnly Due, string? Key)? FindTyped(SqliteConnection connection, CheckingRow row)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT label, due_on, typed_key FROM pay_deposits
            WHERE typed_key IS NOT NULL AND received_on BETWEEN $from AND $through
              AND cents BETWEEN $cents / $ratio AND $cents * $ratio
            ORDER BY received_on
            """;
        command.Parameters.AddWithValue("$cents", row.Cents);
        command.Parameters.AddWithValue("$ratio", BudgetMath.TypedPayRatio);
        command.Parameters.AddWithValue("$from", row.Date.AddDays(-BudgetMath.PayMatchDays).ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$through", row.Date.AddDays(BudgetMath.PayMatchDays).ToString("O", CultureInfo.InvariantCulture));

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (reader.GetString(0), DateOnly.Parse(reader.GetString(1), CultureInfo.InvariantCulture), reader.GetString(2))
            : null;
    }

    private static bool DepositKnown(SqliteConnection connection, string label, DateOnly due)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pay_deposits WHERE label = $label AND due_on = $due AND typed_key IS NULL";
        command.Parameters.AddWithValue("$label", label);
        command.Parameters.AddWithValue("$due", due.ToString("O", CultureInfo.InvariantCulture));
        return (long)command.ExecuteScalar()! > 0;
    }

    private DateOnly LastCheckingSeen(BudgetAccount checking)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(posted_on) FROM checking_activity WHERE import_key NOT LIKE 'typed|%'";
        return command.ExecuteScalar() is string posted && DateOnly.Parse(posted, CultureInfo.InvariantCulture) is var last && last > checking.UpdatedOn
            ? last
            : checking.UpdatedOn;
    }
}
