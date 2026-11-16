using System.Globalization;
using Waypoint.Core.Budget;

namespace Waypoint.Core.Data;

/// <summary>Reads what the spending outcomes need. Nothing new is stored for them.</summary>
public sealed partial class WaypointDatabase
{
    /// <summary>The wait list's decisions and the months after going over a cap, for the Spending review page and the assistant.</summary>
    public ReportSection GetSpendingOutcomes(DateOnly today)
    {
        var from = new DateOnly(today.Year, today.Month, 1).AddMonths(-(SpendingOutcomes.MonthsBack + 1));
        return SpendingOutcomes.Build(today, GetSettledWaits(), GetCategories(), GetSpending(from, today), GetFirstUsedOn());
    }

    public string DescribeSpendingOutcomes(DateOnly today) => SpendingOutcomes.Describe(GetSpendingOutcomes(today));

    private IReadOnlyList<SettledWait> GetSettledWaits()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT description, price_cents, added_at, decision FROM wait_list WHERE decision IS NOT NULL ORDER BY added_at";

        var settled = new List<SettledWait>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            settled.Add(new SettledWait(reader.GetString(0), reader.GetInt64(1), DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture), reader.GetString(3)));
        }

        return settled;
    }

    /// <summary>The day of the first capture, which is when Waypoint started being used.</summary>
    private DateOnly? GetFirstUsedOn()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(substr(created_at, 1, 10)) FROM captures";
        return command.ExecuteScalar() is string day && DateOnly.TryParse(day, CultureInfo.InvariantCulture, out var on) ? on : null;
    }
}
