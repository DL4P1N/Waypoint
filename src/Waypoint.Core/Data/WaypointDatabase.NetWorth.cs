using System.Globalization;
using System.Text.Json;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

/// <summary>
/// Net worth kept once a day, and the monthly summary built from it. A snapshot is taken whenever today's budget
/// is worked out, so a day the budget isn't looked at is simply missing.
/// </summary>
public sealed partial class WaypointDatabase
{
    private void CreateNetWorthTables() => Execute("""
        CREATE TABLE IF NOT EXISTS net_worth_snapshots (
            day TEXT PRIMARY KEY,
            assets_cents INTEGER NOT NULL,
            owed_cents INTEGER NOT NULL,
            parts TEXT NOT NULL
        );
        """);

    /// <summary>Keeps today's net worth and its parts. A later one the same day replaces it, so there's one a day at most.</summary>
    public void RecordNetWorthSnapshot(DateOnly day) => RecordNetWorthSnapshot(day, GetCurrentAccounts(day));

    private void RecordNetWorthSnapshot(DateOnly day, IReadOnlyList<BudgetAccount> accounts)
    {
        if (accounts.Count == 0)
        {
            return;
        }

        var snapshot = NetWorthSnapshot.From(day, accounts);
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO net_worth_snapshots (day, assets_cents, owed_cents, parts) VALUES ($day, $assets, $owed, $parts)
            ON CONFLICT (day) DO UPDATE SET assets_cents = excluded.assets_cents, owed_cents = excluded.owed_cents, parts = excluded.parts
            WHERE assets_cents <> excluded.assets_cents OR owed_cents <> excluded.owed_cents OR parts <> excluded.parts
            """;
        command.Parameters.AddWithValue("$day", day.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$assets", snapshot.AssetsCents);
        command.Parameters.AddWithValue("$owed", snapshot.OwedCents);
        command.Parameters.AddWithValue("$parts", JsonSerializer.Serialize(snapshot.Parts));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<NetWorthSnapshot> GetNetWorthSnapshots(DateOnly from, DateOnly through)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT day, assets_cents, owed_cents, parts FROM net_worth_snapshots WHERE day BETWEEN $from AND $through ORDER BY day";
        command.Parameters.AddWithValue("$from", from.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$through", through.ToString("O", CultureInfo.InvariantCulture));

        var snapshots = new List<NetWorthSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            snapshots.Add(new NetWorthSnapshot(
                DateOnly.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                reader.GetInt64(1),
                reader.GetInt64(2),
                JsonSerializer.Deserialize<Dictionary<string, long>>(reader.GetString(3)) ?? []));
        }

        return snapshots;
    }

    /// <summary>Net worth at the end of each recent month, for the Balances page.</summary>
    public ReportSection GetNetWorthByMonth(DateOnly today) =>
        NetWorthTrend.ByMonth(GetNetWorthSnapshots(new DateOnly(today.Year, today.Month, 1).AddMonths(-NetWorthTrend.MonthsShown), today));

    /// <summary>A month's spending against the month before, its income, and its net worth change.</summary>
    public IReadOnlyList<ReportSection> GetMonthlySummary(DateOnly month, DateOnly today)
    {
        var start = new DateOnly(month.Year, month.Month, 1);
        var last = start.AddMonths(1).AddDays(-1);
        var before = start.AddMonths(-1);
        var otherIncome = start.Year == before.Year ? GetOtherIncome(start.Year) : GetOtherIncome(before.Year).Concat(GetOtherIncome(start.Year)).ToList();
        return MonthlySummary.Build(
            start,
            today,
            GetCategories(),
            GetSpending(before, last),
            GetPayDeposits(),
            otherIncome,
            GetNetWorthSnapshots(before.AddMonths(-1), last));
    }

    /// <summary>"How did I do last month?" A date picks the month, and without one it's last month.</summary>
    public OpReply DescribeMonth(DateTime? at, DateOnly today)
    {
        var day = at is { } given ? DateOnly.FromDateTime(given) : today.AddMonths(-1);
        var month = new DateOnly(day.Year, day.Month, 1);
        if (month > today)
        {
            return new OpReply("That month hasn't happened yet.", true);
        }

        return new OpReply(MonthlySummary.Text(month, GetMonthlySummary(month, today)), true);
    }
}
