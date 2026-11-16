using System.Globalization;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

/// <summary>A notes folder, with the folders inside it in order. NoteCount counts only the notes directly in it.</summary>
public sealed record NoteFolder(
    long Id,
    string Name,
    long? ParentId,
    int Position,
    DateTime CreatedAt,
    string? Course,
    int NoteCount,
    IReadOnlyList<NoteFolder> Children);

/// <summary>What came of naming a folder. Id is 0 and Problem says why when nothing changed.</summary>
public sealed record FolderEdit(long Id, string Name, string? Problem = null);

/// <summary>What happens to a folder's notes when it's deleted. Notes are never deleted along with it.</summary>
public enum NotesInFolder
{
    /// <summary>Only delete the folder if it has no notes.</summary>
    Refuse,
    MoveToUnfiled,
    MoveToParent,
}

public enum FolderDeleteResult
{
    Deleted,
    NotFound,
    HasNotes,
}

public sealed partial class WaypointDatabase
{
    /// <summary>A folder that no longer exists reads as Unfiled.</summary>
    private const string LiveFolder = "(SELECT f.id FROM note_folders f WHERE f.id = i.folder_id)";

    private const string FolderAndInside = """
        (WITH RECURSIVE inside(id) AS (
            SELECT $id UNION SELECT f.id FROM note_folders f JOIN inside ON f.parent_id = inside.id)
         SELECT id FROM inside)
        """;

    private void CreateNoteFolderTables()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS note_folders (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                parent_id INTEGER REFERENCES note_folders(id),
                position INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                course TEXT
            );
            """);
        AddColumnIfMissing("items", "folder_id", "INTEGER");
        CreateNoteSourceTables();
        CreateTranscriptionTables();
        CreateNoteFileTables();
    }

    /// <summary>Every folder as a tree, each level in its saved order. Unfiled isn't a folder, so it isn't listed.</summary>
    public IReadOnlyList<NoteFolder> GetNoteFolders()
    {
        var rows = ReadFolderRows();
        var ids = rows.Select(row => row.Id).ToHashSet();
        var byParent = rows.ToLookup(row => row.ParentId is { } parent && ids.Contains(parent) ? parent : (long?)null);

        List<NoteFolder> Build(long? parent) => byParent[parent]
            .OrderBy(row => row.Position)
            .ThenBy(row => row.Id)
            .Select(row => row with { Children = Build(row.Id) })
            .ToList();

        return Build(null);
    }

    public NoteFolder? GetNoteFolder(long id) => Flatten(GetNoteFolders()).FirstOrDefault(folder => folder.Id == id);

    /// <summary>Notes directly in a folder, or in Unfiled when <paramref name="folderId"/> is null.</summary>
    public int CountNotesInFolder(long? folderId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM items i WHERE i.kind = 'Note' AND i.canceled = 0 AND {LiveFolder} IS $id";
        command.Parameters.AddWithValue("$id", (object?)folderId ?? DBNull.Value);
        return (int)(long)command.ExecuteScalar()!;
    }

    /// <summary>Adds a folder at the end of its level. A null parent puts it at the top level.</summary>
    public FolderEdit CreateNoteFolder(string name, long? parentId, DateTime now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var edit = InsertFolder(connection, name, parentId, null, now);
        transaction.Commit();
        return edit;
    }

    public FolderEdit RenameNoteFolder(long id, string name)
    {
        var clean = name.Trim();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (!FolderExists(connection, id))
        {
            return new FolderEdit(0, clean, "That folder is gone.");
        }

        if (NameProblem(connection, clean, ReadFolderParent(connection, id), id) is { } problem)
        {
            return new FolderEdit(0, clean, problem);
        }

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE note_folders SET name = $name WHERE id = $id";
        command.Parameters.AddWithValue("$name", clean);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        transaction.Commit();
        return new FolderEdit(id, clean);
    }

    /// <summary>
    /// Puts a folder at <paramref name="position"/> among the folders in <paramref name="parentId"/> (null for
    /// the top level), and renumbers both levels so positions stay 0, 1, 2. Returns false if the folder or
    /// parent is gone, or the move would put a folder inside itself.
    /// </summary>
    public bool MoveNoteFolder(long id, long? parentId, int position)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (!FolderExists(connection, id) || (parentId is { } parent && (!FolderExists(connection, parent) || IsInside(connection, parent, id))))
        {
            return false;
        }

        var oldParent = ReadFolderParent(connection, id);
        var siblings = ReadSiblingIds(connection, parentId).Where(sibling => sibling != id).ToList();
        siblings.Insert(Math.Clamp(position, 0, siblings.Count), id);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE note_folders SET parent_id = $parent WHERE id = $id";
            command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }

        Renumber(connection, siblings);
        if (oldParent != parentId)
        {
            Renumber(connection, ReadSiblingIds(connection, oldParent));
        }

        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Deletes a folder. Its notes go where <paramref name="notes"/> says, and with Refuse a folder that still
    /// has notes stays. Folders inside it move up a level in its place, so nothing in them is lost.
    /// </summary>
    public FolderDeleteResult DeleteNoteFolder(long id, NotesInFolder notes)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (!FolderExists(connection, id))
        {
            return FolderDeleteResult.NotFound;
        }

        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM items WHERE kind = 'Note' AND canceled = 0 AND folder_id = $id";
            count.Parameters.AddWithValue("$id", id);
            if (notes == NotesInFolder.Refuse && (long)count.ExecuteScalar()! > 0)
            {
                return FolderDeleteResult.HasNotes;
            }
        }

        var parent = ReadFolderParent(connection, id);
        var children = ReadSiblingIds(connection, id);
        var siblings = ReadSiblingIds(connection, parent);
        var spot = siblings.IndexOf(id);
        siblings.RemoveAt(spot);
        siblings.InsertRange(spot, children);

        // Deleted notes move too, so bringing one back doesn't point at a folder that's gone.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE items SET folder_id = $target WHERE folder_id = $id;
                UPDATE note_folders SET parent_id = $parent WHERE parent_id = $id;
                DELETE FROM note_folders WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$target", notes == NotesInFolder.MoveToParent && parent is { } up ? up : DBNull.Value);
            command.Parameters.AddWithValue("$parent", (object?)parent ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }

        Renumber(connection, siblings);
        transaction.Commit();
        return FolderDeleteResult.Deleted;
    }

    /// <summary>Files a note in a folder, or in Unfiled when <paramref name="folderId"/> is null. False if either is gone.</summary>
    public bool MoveNoteToFolder(long noteId, long? folderId)
    {
        using var connection = Open();
        if (folderId is { } folder && !FolderExists(connection, folder))
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE items SET folder_id = $folder WHERE id = $id AND kind = 'Note' AND canceled = 0";
        command.Parameters.AddWithValue("$folder", (object?)folderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", noteId);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>Notes in a folder, newest first, or Unfiled notes when <paramref name="folderId"/> is null.</summary>
    public IReadOnlyList<NoteDetail> GetNotesInFolder(long? folderId, bool includeSubfolders = false) =>
        folderId is { } id
            ? ReadNotes(includeSubfolders ? $"AND i.folder_id IN {FolderAndInside}" : "AND i.folder_id = $id", id)
            : ReadNotes($"AND {LiveFolder} IS NULL");

    /// <summary>The folder made for a class, if there is one.</summary>
    public long? FindClassFolder(string course)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM note_folders WHERE course = $course COLLATE NOCASE ORDER BY id LIMIT 1";
        command.Parameters.AddWithValue("$course", course.Trim());
        return command.ExecuteScalar() as long?;
    }

    /// <summary>
    /// Makes a top-level folder named after a class, only when asked. If the class already has one, or a
    /// top-level folder already has its name, that folder is used instead of making a second.
    /// </summary>
    public FolderEdit CreateClassFolder(string course, DateTime now)
    {
        var clean = course.Trim();
        if (clean.Length == 0)
        {
            return new FolderEdit(0, clean, "Which class is the folder for?");
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var find = connection.CreateCommand())
        {
            find.CommandText = """
                SELECT id, name FROM note_folders
                WHERE course = $course COLLATE NOCASE OR (parent_id IS NULL AND name = $course COLLATE NOCASE)
                ORDER BY course IS NULL, id LIMIT 1
                """;
            find.Parameters.AddWithValue("$course", clean);
            using var reader = find.ExecuteReader();
            if (reader.Read())
            {
                var existing = new FolderEdit(reader.GetInt64(0), reader.GetString(1));
                reader.Close();
                using var claim = connection.CreateCommand();
                claim.CommandText = "UPDATE note_folders SET course = COALESCE(course, $course) WHERE id = $id";
                claim.Parameters.AddWithValue("$course", clean);
                claim.Parameters.AddWithValue("$id", existing.Id);
                claim.ExecuteNonQuery();
                transaction.Commit();
                return existing;
            }
        }

        var edit = InsertFolder(connection, clean, null, clean, now);
        transaction.Commit();
        return edit;
    }

    /// <summary>Searches every note's title, text, and note line. Best matches first, then newest.</summary>
    public IReadOnlyList<NoteDetail> SearchNotes(string? words) => MatchNotes(ReadNotes(""), words);

    /// <summary>Searches one folder, or Unfiled when <paramref name="folderId"/> is null.</summary>
    public IReadOnlyList<NoteDetail> SearchNotesInFolder(string? words, long? folderId, bool includeSubfolders = true) =>
        MatchNotes(GetNotesInFolder(folderId, includeSubfolders), words);

    /// <summary>
    /// Uses the same matching as Ask's search. A query with no word long enough to match on falls back to
    /// plain text matching. Transcripts join the search by adding their text to the doc's Raw.
    /// </summary>
    private IReadOnlyList<NoteDetail> MatchNotes(IReadOnlyList<NoteDetail> notes, string? words)
    {
        var clean = words?.Trim() ?? "";
        if (clean.Length == 0)
        {
            return notes;
        }

        // A note's sources are searched along with its text, so words only in the slides or transcript still find it.
        var sources = ReadSourceText();
        if (SearchIndex.Words(clean).Count == 0)
        {
            return notes.Where(note => $"{note.Title}\n{note.Body}\n{note.About}\n{sources.GetValueOrDefault(note.Id)}".Contains(clean, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var byDoc = new Dictionary<object, NoteDetail>(ReferenceEqualityComparer.Instance);
        foreach (var note in notes)
        {
            var raw = sources.TryGetValue(note.Id, out var text) ? $"{note.Body}\n{text}" : note.Body;
            byDoc[new SearchDoc(SearchIndex.Note, "Note", note.Title, note.At, note.About, raw)] = note;
        }

        return SearchIndex.Find(byDoc.Keys.Cast<SearchDoc>(), new SearchQuery(clean)).Select(doc => byDoc[doc]).ToList();
    }

    private static FolderEdit InsertFolder(SqliteConnection connection, string name, long? parentId, string? course, DateTime now)
    {
        var clean = name.Trim();
        if (parentId is { } parent && !FolderExists(connection, parent))
        {
            return new FolderEdit(0, clean, "That folder is gone.");
        }

        if (NameProblem(connection, clean, parentId, 0) is { } problem)
        {
            return new FolderEdit(0, clean, problem);
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO note_folders (name, parent_id, position, created_at, course)
            VALUES ($name, $parent, (SELECT COALESCE(MAX(position) + 1, 0) FROM note_folders WHERE parent_id IS $parent), $now, $course)
            RETURNING id
            """;
        command.Parameters.AddWithValue("$name", clean);
        command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", FormatReminder(now));
        command.Parameters.AddWithValue("$course", (object?)course ?? DBNull.Value);
        return new FolderEdit((long)command.ExecuteScalar()!, clean);
    }

    private static string? NameProblem(SqliteConnection connection, string name, long? parentId, long exceptId)
    {
        if (name.Length == 0)
        {
            return "A folder needs a name.";
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM note_folders WHERE parent_id IS $parent AND name = $name COLLATE NOCASE AND id <> $except";
        command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$except", exceptId);
        return command.ExecuteScalar() is null ? null : $"There's already a folder called {name} here.";
    }

    private static bool FolderExists(SqliteConnection connection, long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM note_folders WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is not null;
    }

    private static long? ReadFolderParent(SqliteConnection connection, long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT parent_id FROM note_folders WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as long?;
    }

    /// <summary>Whether <paramref name="folder"/> is <paramref name="container"/> or somewhere inside it.</summary>
    private static bool IsInside(SqliteConnection connection, long folder, long container)
    {
        var seen = new HashSet<long>();
        for (long? at = folder; at is { } current && seen.Add(current); at = ReadFolderParent(connection, current))
        {
            if (current == container)
            {
                return true;
            }
        }

        return false;
    }

    private static List<long> ReadSiblingIds(SqliteConnection connection, long? parentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM note_folders WHERE parent_id IS $parent ORDER BY position, id";
        command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);

        var ids = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private static void Renumber(SqliteConnection connection, IReadOnlyList<long> ids)
    {
        for (var position = 0; position < ids.Count; position++)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE note_folders SET position = $position WHERE id = $id";
            command.Parameters.AddWithValue("$position", position);
            command.Parameters.AddWithValue("$id", ids[position]);
            command.ExecuteNonQuery();
        }
    }

    private List<NoteFolder> ReadFolderRows()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.id, f.name, f.parent_id, f.position, f.created_at, f.course,
                   (SELECT COUNT(*) FROM items i WHERE i.folder_id = f.id AND i.kind = 'Note' AND i.canceled = 0)
            FROM note_folders f
            """;

        var rows = new List<NoteFolder>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new NoteFolder(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetInt32(3),
                DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6),
                []));
        }

        return rows;
    }

    private static IEnumerable<NoteFolder> Flatten(IEnumerable<NoteFolder> folders) =>
        folders.SelectMany(folder => new[] { folder }.Concat(Flatten(folder.Children)));
}
