using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

public sealed partial class WaypointDatabase
{
    private const string PayProposalKey = "pay_proposal";
    private const string PayCalendarNoticeKey = "pay_calendar_notice";
    private const int PayCalendarNoticeDays = 30;

    private static readonly TimeSpan PayProposalLife = TimeSpan.FromDays(7);

    /// <summary>Imports a card or checking export, skipping anything already counted.</summary>
    public ImportResult ImportStatement(string csv)
    {
        if (CheckingImport.IsChecking(csv))
        {
            var (rows, unreadable) = CheckingImport.Read(csv);
            return ImportChecking(rows) with { Unreadable = unreadable };
        }

        var profiles = BankProfiles.Current;
        var account = CsvImport.IsTypeColumnExport(csv) ? profiles.RewardsCard.Name : profiles.Card.Name;
        var export = CsvImport.Read(csv);
        var waiting = GetWaitList().ToList();

        // The purchases and the payments are one write, so a failure partway leaves neither and the file can be imported again.
        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        var result = ImportTransactions(connection, account, export.Purchases, waiting);
        AddCardPayments(connection, account, export.Payments);
        transaction.Commit();
        return result with { Unreadable = export.Unreadable };
    }

    /// <summary>Holds pay dates until the user says yes, since they replace what the forecast expects.</summary>
    public OpReply ProposePayDates(PayProposal proposal, DateTime now)
    {
        if (proposal.Dates.Count == 0)
        {
            return new OpReply("I didn't find any pay dates in that.", true);
        }

        var sorted = proposal with { Dates = proposal.Dates.Distinct().Order().ToList() };
        SetPreference(PayProposalKey, JsonSerializer.Serialize(sorted));
        return new OpReply(DescribeProposal(sorted, now), true);
    }

    /// <summary>The sorter's reading of a calendar, for files the local parser couldn't place.</summary>
    public OpReply ProposePayDates(IReadOnlyList<string> entries, long? cents, DateTime now) => ProposePayDates(
        new PayProposal(
            entries.Select(entry => DateOnly.TryParseExact(entry, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : (DateOnly?)null).OfType<DateOnly>().ToList(),
            cents,
            now),
        now);

    public PayProposal? GetPayProposal(DateTime now)
    {
        var saved = GetPreference(PayProposalKey);
        return string.IsNullOrEmpty(saved) || JsonSerializer.Deserialize<PayProposal>(saved) is not { } proposal || now - proposal.CreatedAt > PayProposalLife
            ? null
            : proposal;
    }

    public string DescribeProposal(PayProposal proposal, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var next = proposal.Dates.Where(date => date >= today).Select(date => $" Next: {date.ToString("ddd MMM d", CultureInfo.InvariantCulture)}.").FirstOrDefault() ?? "";
        var amount = proposal.AmountCents is { } cents ? $"Use {Briefing.Money(cents)} per check?"
            : MainPaycheck() is { } pay ? $"Keep amount {Briefing.Money(pay.AmountCents)}?"
            : "About how much is each paycheck?";
        return $"Found {proposal.Dates.Count} pay dates, {LongDate(proposal.Dates[0])} to {LongDate(proposal.Dates[^1])}.{next} {amount}";
    }

    public OpReply CancelPayProposal()
    {
        SetPreference(PayProposalKey, "");
        return new OpReply("Okay, your pay dates stay as they were", true);
    }

    /// <summary>Puts the dates on the main paycheck. Earlier dates stay, so past deposits still match.</summary>
    public OpReply ApplyPayProposal(DateTime now)
    {
        if (GetPayProposal(now) is not { } proposal)
        {
            return new OpReply("I don't have any pay dates waiting.", true);
        }

        var current = MainPaycheck();
        if ((proposal.AmountCents ?? current?.AmountCents) is not { } amount)
        {
            return new OpReply("About how much is each paycheck?", true);
        }

        SavePaycheck((current ?? new ScheduledItem(MainPayLabel, amount, [], true)) with { AmountCents = amount });
        var paycheck = MainPaycheck()!;

        using (Connect(out var connection))
        using (var transaction = Begin(connection))
        {
            using (var clear = connection.CreateCommand())
            {
                clear.CommandText = "DELETE FROM pay_calendar WHERE schedule_id = $id AND pay_on >= $first";
                clear.Parameters.AddWithValue("$id", paycheck.Id);
                clear.Parameters.AddWithValue("$first", proposal.Dates[0].ToString("O", CultureInfo.InvariantCulture));
                clear.ExecuteNonQuery();
            }

            foreach (var date in proposal.Dates)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT OR IGNORE INTO pay_calendar (schedule_id, pay_on) VALUES ($id, $date)";
                insert.Parameters.AddWithValue("$id", paycheck.Id);
                insert.Parameters.AddWithValue("$date", date.ToString("O", CultureInfo.InvariantCulture));
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        SetPreference(PayProposalKey, "");
        var today = DateOnly.FromDateTime(now);
        var next = BudgetMath.NextOccurrence(MainPaycheck()!, today, GetPayDeposits()) is { } day ? $" Next expected {ShortDate(day)}, about {Briefing.Money(amount)}." : "";
        return new OpReply($"Using {proposal.Dates.Count} pay dates through {LongDate(proposal.Dates[^1])}.{next}", true);
    }

    /// <summary>Said once, about a month before the listed dates run out.</summary>
    public string? TakePayCalendarNotice(DateOnly today)
    {
        if (MainPaycheck()?.Dates is not { Count: > 0 } dates)
        {
            return null;
        }

        var last = dates[^1];
        var lastText = last.ToString("O", CultureInfo.InvariantCulture);
        if (last.DayNumber - today.DayNumber > PayCalendarNoticeDays || GetPreference(PayCalendarNoticeKey) == lastText)
        {
            return null;
        }

        SetPreference(PayCalendarNoticeKey, lastText);
        return $"Your pay calendar runs through {LongDate(last)}. Drop next year's into the Ask window when you have it. Until then, forecasts go back to your usual pattern.";
    }

    private Dictionary<long, List<DateOnly>> ReadPayCalendar()
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT schedule_id, pay_on FROM pay_calendar ORDER BY pay_on";

        var dates = new Dictionary<long, List<DateOnly>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            if (!dates.TryGetValue(id, out var list))
            {
                dates[id] = list = [];
            }

            list.Add(DateOnly.Parse(reader.GetString(1), CultureInfo.InvariantCulture));
        }

        return dates;
    }

    private static void ClearPayCalendar(SqliteConnection connection, long scheduleId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM pay_calendar WHERE schedule_id = $id";
        command.Parameters.AddWithValue("$id", scheduleId);
        command.ExecuteNonQuery();
    }

    private static string LongDate(DateOnly date) => date.ToString("MMM d yyyy", CultureInfo.InvariantCulture);
}
