using Microsoft.Data.Sqlite;

namespace Waypoint.Core.Data;

/// <summary>
/// What a note's .md file and the note itself looked like the last time they matched. Path is relative to the
/// notes folder. Size and Modified let a sync skip reading a file that hasn't been touched.
/// </summary>
public sealed record NoteFileRecord(long NoteId, string Path, string FileHash, long Size, long ModifiedTicks, string Title, long? FolderId, string BodyHash);

/// <summary>A live note as the notes folder sees it.</summary>
public sealed record NoteFileState(long Id, string Title, string Body, long? FolderId);

public sealed partial class WaypointDatabase
{
    private void CreateNoteFileTables() => Execute("""
        CREATE TABLE IF NOT EXISTS note_files (
            note_id INTEGER PRIMARY KEY,
            path TEXT NOT NULL,
            file_hash TEXT NOT NULL,
            size INTEGER NOT NULL,
            modified_ticks INTEGER NOT NULL,
            title TEXT NOT NULL,
            folder_id INTEGER,
            body_hash TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS pc_note_files (
            client_id TEXT NOT NULL,
            note_id INTEGER NOT NULL,
            path TEXT NOT NULL,
            file_hash TEXT NOT NULL,
            size INTEGER NOT NULL,
            modified_ticks INTEGER NOT NULL,
            title TEXT NOT NULL,
            folder_id INTEGER,
            body_hash TEXT NOT NULL,
            PRIMARY KEY (client_id, note_id)
        );
        """);

    // On the server, each PC in server mode keeps a notes folder of its own, so what its files were when they last
    // matched is kept for that PC alone, by the id it sends. A PC with no record of a note never wrote its file, so
    // a file missing there says nothing about the note. Records kept before this, in note_files, go to the first PC
    // that asks for its own.

    public IReadOnlyList<NoteFileRecord> GetNoteFileRecords(string clientId)
    {
        using var connection = Open();
        using (var transaction = connection.BeginTransaction())
        {
            using var mine = connection.CreateCommand();
            mine.Transaction = transaction;
            mine.CommandText = "SELECT EXISTS (SELECT 1 FROM pc_note_files WHERE client_id = $client)";
            mine.Parameters.AddWithValue("$client", clientId);
            if ((long)mine.ExecuteScalar()! == 0)
            {
                using var adopt = connection.CreateCommand();
                adopt.Transaction = transaction;
                adopt.CommandText = """
                    INSERT OR IGNORE INTO pc_note_files (client_id, note_id, path, file_hash, size, modified_ticks, title, folder_id, body_hash)
                    SELECT $client, note_id, path, file_hash, size, modified_ticks, title, folder_id, body_hash FROM note_files;
                    DELETE FROM note_files;
                    """;
                adopt.Parameters.AddWithValue("$client", clientId);
                adopt.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT note_id, path, file_hash, size, modified_ticks, title, folder_id, body_hash FROM pc_note_files WHERE client_id = $client";
        command.Parameters.AddWithValue("$client", clientId);
        return ReadNoteFileRecords(command);
    }

    public void SetNoteFileRecord(string clientId, NoteFileRecord record)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pc_note_files (client_id, note_id, path, file_hash, size, modified_ticks, title, folder_id, body_hash)
            VALUES ($client, $id, $path, $hash, $size, $modified, $title, $folder, $body)
            ON CONFLICT(client_id, note_id) DO UPDATE SET path = $path, file_hash = $hash, size = $size, modified_ticks = $modified,
                title = $title, folder_id = $folder, body_hash = $body
            """;
        command.Parameters.AddWithValue("$client", clientId);
        AddRecordValues(command, record);
        command.ExecuteNonQuery();
    }

    public void RemoveNoteFileRecord(string clientId, long noteId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM pc_note_files WHERE client_id = $client AND note_id = $id";
        command.Parameters.AddWithValue("$client", clientId);
        command.Parameters.AddWithValue("$id", noteId);
        command.ExecuteNonQuery();
    }

    public void ClearNoteFileRecords(string clientId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM pc_note_files WHERE client_id = $client";
        command.Parameters.AddWithValue("$client", clientId);
        command.ExecuteNonQuery();
    }

    private static List<NoteFileRecord> ReadNoteFileRecords(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var records = new List<NoteFileRecord>();
        while (reader.Read())
        {
            records.Add(new NoteFileRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.GetString(7)));
        }

        return records;
    }

    private static void AddRecordValues(SqliteCommand command, NoteFileRecord record)
    {
        command.Parameters.AddWithValue("$id", record.NoteId);
        command.Parameters.AddWithValue("$path", record.Path);
        command.Parameters.AddWithValue("$hash", record.FileHash);
        command.Parameters.AddWithValue("$size", record.Size);
        command.Parameters.AddWithValue("$modified", record.ModifiedTicks);
        command.Parameters.AddWithValue("$title", record.Title);
        command.Parameters.AddWithValue("$folder", (object?)record.FolderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$body", record.BodyHash);
    }

    public IReadOnlyList<NoteFileRecord> GetNoteFileRecords()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT note_id, path, file_hash, size, modified_ticks, title, folder_id, body_hash FROM note_files";
        using var reader = command.ExecuteReader();
        var records = new List<NoteFileRecord>();
        while (reader.Read())
        {
            records.Add(new NoteFileRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.GetString(7)));
        }

        return records;
    }

    public void SetNoteFileRecord(NoteFileRecord record)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO note_files (note_id, path, file_hash, size, modified_ticks, title, folder_id, body_hash)
            VALUES ($id, $path, $hash, $size, $modified, $title, $folder, $body)
            ON CONFLICT(note_id) DO UPDATE SET path = $path, file_hash = $hash, size = $size, modified_ticks = $modified,
                title = $title, folder_id = $folder, body_hash = $body
            """;
        command.Parameters.AddWithValue("$id", record.NoteId);
        command.Parameters.AddWithValue("$path", record.Path);
        command.Parameters.AddWithValue("$hash", record.FileHash);
        command.Parameters.AddWithValue("$size", record.Size);
        command.Parameters.AddWithValue("$modified", record.ModifiedTicks);
        command.Parameters.AddWithValue("$title", record.Title);
        command.Parameters.AddWithValue("$folder", (object?)record.FolderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$body", record.BodyHash);
        command.ExecuteNonQuery();
    }

    public void RemoveNoteFileRecord(long noteId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM note_files WHERE note_id = $id";
        command.Parameters.AddWithValue("$id", noteId);
        command.ExecuteNonQuery();
    }

    /// <summary>Forgets every file, for when the notes folder moves. The next sync writes them all again.</summary>
    public void ClearNoteFileRecords() => Execute("DELETE FROM note_files");

    private const string NoteFilesRootKey = "note_files_synced_root";

    public string? NoteFilesRoot() => GetPreference(NoteFilesRootKey);

    public void SetNoteFilesRoot(string root) => SetPreference(NoteFilesRootKey, root);

    // A restore is marked with a new stamp, and each notes folder, this PC's own or each PC's in server mode, says
    // when it has been rewritten from the restored notes. Until then its next sync does that instead of comparing.

    private const string NotesRestoredKey = "notes_restored";
    private const string NotesRestoreHandledKey = "notes_restore_handled";

    /// <summary>Marks the notes as just restored, so every notes folder is rewritten from them.</summary>
    private void MarkNotesRestored() => SetPreference(NotesRestoredKey, Guid.NewGuid().ToString("N"));

    public string? PendingNotesRestore() => Pending(NotesRestoreHandledKey);

    public void NotesRestoreHandled(string stamp) => SetPreference(NotesRestoreHandledKey, stamp);

    public string? PendingNotesRestore(string clientId) => Pending($"{NotesRestoreHandledKey}:{clientId}");

    public void NotesRestoreHandled(string clientId, string stamp) => SetPreference($"{NotesRestoreHandledKey}:{clientId}", stamp);

    private string? Pending(string handledKey) =>
        GetPreference(NotesRestoredKey) is { } stamp && GetPreference(handledKey) != stamp ? stamp : null;

    /// <summary>Every live note, oldest first, with the folder it's really in (a folder that's gone reads as none).</summary>
    public IReadOnlyList<NoteFileState> GetNoteFileStates() =>
        ReadNotes("").OrderBy(note => note.Id).Select(note => new NoteFileState(note.Id, note.Title, note.Body, note.FolderId)).ToList();
}
