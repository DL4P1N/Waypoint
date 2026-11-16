using System.Globalization;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

/// <summary>"This didn't help" clicks, kept for the user to read on Settings and clear when they like.</summary>
public sealed partial class WaypointDatabase
{
    private void CreateFeedbackTables() => Execute("""
        CREATE TABLE IF NOT EXISTS feedback (
            id INTEGER PRIMARY KEY,
            at TEXT NOT NULL,
            feature TEXT NOT NULL,
            item_ref TEXT,
            item_title TEXT,
            note TEXT
        );
        """);

    /// <summary>Keeps one click. The item's title is kept beside its reference, so it still reads after the item is gone.</summary>
    public long AddFeedback(string feature, string? itemRef, string? note, DateTime now)
    {
        using var connection = Open();
        string? title = null;
        if (ItemRef.TryParse(itemRef, out var source, out var id))
        {
            title = FindItem(connection, source, id)?.Title;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO feedback (at, feature, item_ref, item_title, note) VALUES ($at, $feature, $ref, $title, $note); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$at", FormatReminder(now));
        command.Parameters.AddWithValue("$feature", feature);
        command.Parameters.AddWithValue("$ref", (object?)itemRef ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("$note", (object?)Feedback.CleanNote(note) ?? DBNull.Value);
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Adds the optional note to a click already kept. Returns false if it was cleared meanwhile.</summary>
    public bool NoteFeedback(long id, string? note)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE feedback SET note = $note WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$note", (object?)Feedback.CleanNote(note) ?? DBNull.Value);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<FeedbackEntry> GetFeedback()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, at, feature, item_ref, item_title, note FROM feedback ORDER BY at DESC, id DESC";
        var entries = new List<FeedbackEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new FeedbackEntry(
                reader.GetInt64(0),
                DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return entries;
    }

    /// <summary>Puts cleared entries back as they were, for Undo. One that's somehow still there is left alone.</summary>
    public void RestoreFeedback(IEnumerable<FeedbackEntry> entries)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var entry in entries)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO feedback (id, at, feature, item_ref, item_title, note) VALUES ($id, $at, $feature, $ref, $title, $note)";
            command.Parameters.AddWithValue("$id", entry.Id);
            command.Parameters.AddWithValue("$at", FormatReminder(entry.At));
            command.Parameters.AddWithValue("$feature", entry.Feature);
            command.Parameters.AddWithValue("$ref", (object?)entry.ItemRef ?? DBNull.Value);
            command.Parameters.AddWithValue("$title", (object?)entry.ItemTitle ?? DBNull.Value);
            command.Parameters.AddWithValue("$note", (object?)Feedback.CleanNote(entry.Note) ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Forgets all of it. Returns how many there were.</summary>
    public int ClearFeedback()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM feedback";
        return command.ExecuteNonQuery();
    }
}
