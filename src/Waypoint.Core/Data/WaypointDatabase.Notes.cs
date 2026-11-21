using System.Globalization;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

public sealed partial class WaypointDatabase
{
    private const string UntitledNote = "Untitled note";
    private const int MaxNotesForSorter = 5;
    private const int MaxNoteForSorter = 600;

    private const string NoteColumns = $"i.id, i.title, COALESCE(i.body, c.text), i.about, c.created_at, {LiveFolder}";

    /// <summary>Notes newest first, in the order they were made, so editing one doesn't move it.</summary>
    public IReadOnlyList<NoteDetail> GetNotes(string? filter = null)
    {
        var clean = filter?.Trim() ?? "";
        return ReadNotes("").Where(note => clean.Length == 0 || $"{note.Title}\n{note.Body}\n{note.About}".Contains(clean, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public NoteDetail? GetNote(long id) => ReadNotes("AND i.id = $id", id).FirstOrDefault();

    /// <summary>Hand-edited notes the sorter should know about. Their text only lives on the note, not in any capture.</summary>
    public IReadOnlyList<NoteDetail> GetEditedNotesMentioning(string text) =>
        ReadNotes("AND i.body IS NOT NULL")
            .Where(note => SearchIndex.Mentions($"{note.Title} {note.Body}", text))
            .Take(MaxNotesForSorter)
            .Select(note => note.Body.Length > MaxNoteForSorter ? note with { Body = note.Body[..MaxNoteForSorter] } : note)
            .ToList();

    /// <summary>
    /// Saves a new note straight from the Notes page. The sorter never sees it being made. It goes in
    /// <paramref name="folderId"/>, or Unfiled if that's null or gone.
    /// </summary>
    public long CreateNote(string title, string body, DateTime now, long? folderId = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var id = CreateNote(connection, title, body, now, folderId);
        transaction.Commit();
        return id;
    }

    /// <summary>The capture, the note, its text, and its folder, in the caller's write, so a note is never left half made.</summary>
    private static long CreateNote(SqliteConnection connection, string title, string body, DateTime now, long? folderId)
    {
        var name = NoteTitle(title, body);
        var captureId = InsertHandTyped(connection, name, now);
        var id = InsertItem(connection, captureId, new SortedItem(ItemKind.Note, name, null, null), null, null);
        WriteNote(connection, id, name, body, now);
        if (folderId is { } folder && FolderExists(connection, folder))
        {
            using var file = connection.CreateCommand();
            file.CommandText = "UPDATE items SET folder_id = $folder WHERE id = $id";
            file.Parameters.AddWithValue("$folder", folder);
            file.Parameters.AddWithValue("$id", id);
            file.ExecuteNonQuery();
        }

        return id;
    }

    private readonly Lock _sentOnce = new();

    /// <summary>
    /// A new note sent from a PC with an id of its own. Sent again with the same id, such as after the answer was
    /// lost, it gives the note already made rather than making another. A note that's since been deleted doesn't
    /// count, so sending it again then makes it anew rather than losing it.
    /// </summary>
    public long CreateNoteOnce(string clientId, string title, string body, DateTime now, long? folderId)
    {
        lock (_sentOnce)
        {
            // The note and the id it was sent under are one write, so a failure between can't make it twice.
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using (var find = connection.CreateCommand())
            {
                find.CommandText = "SELECT id FROM items WHERE client_id = $client AND kind = 'Note' AND canceled = 0";
                find.Parameters.AddWithValue("$client", clientId);
                if (find.ExecuteScalar() is long made)
                {
                    return made;
                }
            }

            using (var forget = connection.CreateCommand())
            {
                forget.CommandText = "UPDATE items SET client_id = NULL WHERE client_id = $client";
                forget.Parameters.AddWithValue("$client", clientId);
                forget.ExecuteNonQuery();
            }

            var id = CreateNote(connection, title, body, now, folderId);
            using (var mark = connection.CreateCommand())
            {
                mark.CommandText = "UPDATE items SET client_id = $client WHERE id = $id";
                mark.Parameters.AddWithValue("$client", clientId);
                mark.Parameters.AddWithValue("$id", id);
                mark.ExecuteNonQuery();
            }

            transaction.Commit();
            return id;
        }
    }

    /// <summary>Returns false if the note is gone, so whoever was typing can keep their text as a new one.</summary>
    public bool SaveNote(long id, string title, string body, DateTime now)
    {
        using var connection = Open();
        return WriteNote(connection, id, NoteTitle(title, body), body, now);
    }

    /// <summary>Counts as a cancel, so it shows in History and typing can bring it back too.</summary>
    public void DeleteNote(long id, DateTime now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (FindItem(connection, ItemSource.Capture, id) is { Kind: ItemKind.Note, Done: false } note)
        {
            CancelItem(connection, note, now);
        }

        transaction.Commit();
    }

    public void RestoreNote(long id, DateTime now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RestoreItem(connection, id, now);
        transaction.Commit();
    }

    private static string NoteTitle(string title, string body)
    {
        var clean = title.Trim();
        return clean.Length > 0 ? clean : FirstLine(body) is { Length: > 0 } line ? line : UntitledNote;
    }

    private static bool WriteNote(SqliteConnection connection, long id, string title, string body, DateTime now)
    {
        // The capture is left alone, so the original words are always still there.
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE items SET title = $title, body = $body, edited_at = $now WHERE id = $id AND kind = 'Note' AND canceled = 0";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$now", FormatReminder(now));
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    /// <param name="patterns">The values of $p0, $p1, and so on in <paramref name="condition"/>, if it has any.</param>
    private List<NoteDetail> ReadNotes(string condition, long id = 0, IReadOnlyList<string>? patterns = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {NoteColumns} FROM items i JOIN captures c ON c.id = i.capture_id
            WHERE i.kind = 'Note' AND i.canceled = 0 {condition}
            ORDER BY i.id DESC
            """;
        command.Parameters.AddWithValue("$id", id);
        AddPatterns(command, patterns ?? []);

        var notes = new List<NoteDetail>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            notes.Add(new NoteDetail(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture).LocalDateTime,
                reader.IsDBNull(5) ? null : reader.GetInt64(5)));
        }

        return notes;
    }
}
