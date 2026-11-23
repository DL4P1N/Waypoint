using System.Globalization;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

/// <summary>Captures as they come in, and storing what the sorter made of each.</summary>
public sealed partial class WaypointDatabase
{
    public long AddCapture(string text, DateTimeOffset? createdAt = null, CaptureOrigin origin = CaptureOrigin.App, string? fileName = null)
    {
        using var lease = Connect(out var connection);
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
        using var lease = Connect(out var connection);
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
        using var lease = Connect(out var connection);
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
        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
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
}
