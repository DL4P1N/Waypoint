using System.Globalization;
using Waypoint.Core.Budget;

namespace Waypoint.Core.Data;

/// <summary>
/// Whether scheduled savings transfers and investing purchases happened, from checking exports and brokerage deposit
/// emails already read. Nothing new is fetched for it.
/// </summary>
public sealed partial class WaypointDatabase
{
    private void CreateTransferTables() => Execute("""
        CREATE TABLE IF NOT EXISTS brokerage_deposits (
            id INTEGER PRIMARY KEY,
            received_on TEXT NOT NULL,
            cents INTEGER NOT NULL
        );
        """);

    /// <summary>Keeps a brokerage deposit read from email, so a scheduled transfer can be matched to it later.</summary>
    public void RecordBrokerageDeposit(long cents, DateOnly on)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO brokerage_deposits (received_on, cents) VALUES ($on, $cents)";
        command.Parameters.AddWithValue("$on", on.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$cents", cents);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<TransferStatus> GetTransferStatuses(DateOnly today) =>
        TransferCheck.Check(today, GetSchedule(), GetScheduleAddedOn(), GetTransferSightings(today.AddDays(-TransferCheck.LookBackDays - 7)), CheckingExportThrough());

    public ReportSection GetTransferSection(DateOnly today) =>
        TransferCheck.Section(GetTransferStatuses(today), CheckingExportThrough(), HasBrokerageDeposits());

    /// <summary>When each schedule entry was added, for the ones added since that was kept.</summary>
    private IReadOnlyDictionary<long, DateOnly> GetScheduleAddedOn()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, added_on FROM budget_schedule WHERE added_on IS NOT NULL";

        var added = new Dictionary<long, DateOnly>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (DateOnly.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, out var day))
            {
                added[reader.GetInt64(0)] = day;
            }
        }

        return added;
    }

    /// <summary>Money leaving checking in an export, and brokerage deposits from email. A typed paycheck isn't an export.</summary>
    private IReadOnlyList<TransferSighting> GetTransferSightings(DateOnly from)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT posted_on, -cents, description, 0 FROM checking_activity
            WHERE cents < 0 AND posted_on >= $from AND import_key NOT LIKE 'typed|%'
            UNION ALL
            SELECT received_on, cents, $deposit, 1 FROM brokerage_deposits WHERE received_on >= $from
            """;
        command.Parameters.AddWithValue("$from", from.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$deposit", $"{BankProfiles.Current.Brokerage.Name} deposit");

        var sightings = new List<TransferSighting>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            sightings.Add(new TransferSighting(DateOnly.Parse(reader.GetString(0), CultureInfo.InvariantCulture), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3) != 0));
        }

        return sightings;
    }

    /// <summary>The last day an imported checking export covers, or null if none was ever imported.</summary>
    private DateOnly? CheckingExportThrough()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(posted_on) FROM checking_activity WHERE import_key NOT LIKE 'typed|%'";
        return command.ExecuteScalar() is string day ? DateOnly.Parse(day, CultureInfo.InvariantCulture) : null;
    }

    private bool HasBrokerageDeposits()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM brokerage_deposits)";
        return (long)command.ExecuteScalar()! != 0;
    }
}
