using System.Globalization;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

/// <summary>
/// How far a capture's steps got: its items saved, which budget changes and assistant actions already ran, and what
/// they said. A capture stays pending until its last step is done, so one cut off by a crash picks up from here
/// instead of sorting again or repeating a step. The text is <see cref="CaptureService"/>'s own.
/// </summary>
public sealed partial class WaypointDatabase
{
    private void CreateCaptureProgressTables() => Execute("""
        CREATE TABLE IF NOT EXISTS capture_progress (
            capture_id INTEGER PRIMARY KEY,
            progress TEXT NOT NULL
        );
        """);

    public string? GetCaptureProgress(long captureId)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT progress FROM capture_progress WHERE capture_id = $id";
        command.Parameters.AddWithValue("$id", captureId);
        return command.ExecuteScalar() as string;
    }

    public void SetCaptureProgress(long captureId, string progress)
    {
        using var lease = Connect(out var connection);
        WriteProgress(connection, captureId, progress);
    }

    public void ClearCaptureProgress(long captureId)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM capture_progress WHERE capture_id = $id";
        command.Parameters.AddWithValue("$id", captureId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Saves a sorted capture's items, changes, and monthly budget in one write. With <paramref name="progress"/>,
    /// the capture stays pending with its steps still to run, given what the changes came to. Without, it's finished.
    /// </summary>
    public IReadOnlyList<AppliedChange> StoreSortedCapture(long captureId, IReadOnlyList<SortedItem> items, IReadOnlyList<ItemChange>? changes, long? monthlyBudgetCents, Func<IReadOnlyList<AppliedChange>, string>? progress)
    {
        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        var applied = CompleteCapture(connection, captureId, items, changes, finish: progress is null);
        if (monthlyBudgetCents is { } budget)
        {
            WritePreference(connection, MonthlyBudgetKey, budget.ToString(CultureInfo.InvariantCulture));
        }

        if (progress is not null)
        {
            WriteProgress(connection, captureId, progress(applied));
        }

        transaction.Commit();
        return applied;
    }

    private static void WriteProgress(SqliteConnection connection, long captureId, string progress)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO capture_progress (capture_id, progress) VALUES ($id, $progress) ON CONFLICT (capture_id) DO UPDATE SET progress = $progress";
        command.Parameters.AddWithValue("$id", captureId);
        command.Parameters.AddWithValue("$progress", progress);
        command.ExecuteNonQuery();
    }
}
