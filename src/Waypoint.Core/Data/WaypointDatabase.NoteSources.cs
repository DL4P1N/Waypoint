using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Notes;

namespace Waypoint.Core.Data;

public enum GeneratedSaveResult
{
    Saved,

    /// <summary>The note's text changed since it was read, so nothing was replaced.</summary>
    Changed,
    Gone,
}

/// <summary>What came of saving written notes. Before is the text they replaced, for undo.</summary>
public sealed record GeneratedSave(GeneratedSaveResult Result, NoteBodyState? Before = null);

/// <summary>
/// A note's sources, the record of when its text was written from them, and the dates and tasks they suggest.
/// Sources are only ever added, never edited, so the original text is always there to write from again.
/// </summary>
public sealed partial class WaypointDatabase
{
    private const string OpenSuggestion = "open";
    private const string AddedSuggestion = "added";
    private const string SkippedSuggestion = "skipped";

    private void CreateNoteSourceTables()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS note_sources (
                id INTEGER PRIMARY KEY,
                note_id INTEGER NOT NULL REFERENCES items(id),
                kind TEXT NOT NULL,
                name TEXT NOT NULL,
                text TEXT NOT NULL,
                file_path TEXT,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS note_sources_note ON note_sources(note_id);
            CREATE TABLE IF NOT EXISTS note_suggestions (
                id INTEGER PRIMARY KEY,
                note_id INTEGER NOT NULL REFERENCES items(id),
                kind TEXT NOT NULL,
                title TEXT NOT NULL,
                due_date TEXT,
                due_time TEXT,
                status TEXT NOT NULL DEFAULT 'open',
                item_id INTEGER,
                created_at TEXT NOT NULL
            );
            """);
        AddColumnIfMissing("items", "generated_at", "TEXT");
        AddColumnIfMissing("items", "generated_hash", "TEXT");

        // Set by a PC sending a new note or a transcript to the server, so sending it again after an answer that
        // was lost doesn't make a second one. Empty for everything else.
        AddColumnIfMissing("items", "client_id", "TEXT");
        Execute("CREATE UNIQUE INDEX IF NOT EXISTS items_client_id ON items (client_id)");
        AddColumnIfMissing("note_sources", "client_id", "TEXT");
        Execute("CREATE UNIQUE INDEX IF NOT EXISTS note_sources_client_id ON note_sources (client_id)");
    }

    /// <summary>Adds a source to a note. Returns its id, or 0 if the note is gone.</summary>
    public long AddNoteSource(long noteId, string kind, string name, string text, DateTime now, string? filePath = null)
    {
        using var lease = ConnectForWrite(out var connection);
        return AddNoteSource(connection, noteId, kind, name, text, now, filePath);
    }

    private static long AddNoteSource(SqliteConnection connection, long noteId, string kind, string name, string text, DateTime now, string? filePath)
    {
        if (!NoteExists(connection, noteId))
        {
            return 0;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO note_sources (note_id, kind, name, text, file_path, created_at)
            VALUES ($note, $kind, $name, $text, $path, $now)
            RETURNING id
            """;
        command.Parameters.AddWithValue("$note", noteId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$name", name.Trim().Length > 0 ? name.Trim() : NoteSourceKind.Label(kind));
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$path", (object?)filePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", FormatReminder(now));
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>How many sources each note has, for every note with any, counted in one read.</summary>
    public IReadOnlyDictionary<long, int> CountNoteSources()
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT note_id, COUNT(*) FROM note_sources GROUP BY note_id";

        var counts = new Dictionary<long, int>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            counts[reader.GetInt64(0)] = reader.GetInt32(1);
        }

        return counts;
    }

    /// <summary>A note's sources in the order they were added.</summary>
    public IReadOnlyList<NoteSource> GetNoteSources(long noteId)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, note_id, kind, name, text, file_path, created_at FROM note_sources WHERE note_id = $note ORDER BY id";
        command.Parameters.AddWithValue("$note", noteId);

        var sources = new List<NoteSource>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            sources.Add(new NoteSource(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture)));
        }

        return sources;
    }

    /// <summary>Whether the note's text came from its sources and has been changed since. Null if the note is gone.</summary>
    public NoteGeneration? GetNoteGeneration(long noteId)
    {
        using var lease = Connect(out var connection);
        if (ReadBodyState(connection, noteId) is not { } state)
        {
            return null;
        }

        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM note_sources WHERE note_id = $note";
        count.Parameters.AddWithValue("$note", noteId);
        var sources = (int)(long)count.ExecuteScalar()!;

        var edited = state.GeneratedHash is { } hash ? BodyHash(state.Body) != hash : !string.IsNullOrWhiteSpace(state.Body);
        return new NoteGeneration(state.GeneratedAt, edited, sources);
    }

    /// <summary>
    /// Replaces a note's text with notes written from its sources, and remembers what was written so later edits
    /// can be told apart. Only saves when the text is still <paramref name="expectedBody"/>, so anything typed while
    /// the notes were being written is never replaced without asking.
    /// </summary>
    public GeneratedSave SaveGeneratedNotes(long noteId, string markdown, string expectedBody, DateTime now)
    {
        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        if (ReadBodyState(connection, noteId) is not { } before)
        {
            return new GeneratedSave(GeneratedSaveResult.Gone);
        }

        if (Normalize(before.Body) != Normalize(expectedBody))
        {
            return new GeneratedSave(GeneratedSaveResult.Changed);
        }

        WriteBody(connection, noteId, markdown, now, now, BodyHash(markdown));
        transaction.Commit();
        return new GeneratedSave(GeneratedSaveResult.Saved, before);
    }

    /// <summary>
    /// Puts back the text written notes replaced, as undo. Only while the note is still just what was written, so
    /// anything added since, such as from Discord or the note's .md file, is never undone with it. False, with
    /// nothing changed, when it was changed since or is gone. True with nothing to do when the text is already back,
    /// as when undo is sent again after an answer that was lost.
    /// </summary>
    public bool RestoreNoteBody(long noteId, NoteBodyState state, DateTime now)
    {
        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        if (ReadBodyState(connection, noteId) is not { } current)
        {
            return false;
        }

        if (Normalize(current.Body) == Normalize(state.Body))
        {
            return true;
        }

        if (current.GeneratedHash is not { } written || BodyHash(current.Body) != written)
        {
            return false;
        }

        WriteBody(connection, noteId, state.Body, now, state.GeneratedAt, state.GeneratedHash);
        transaction.Commit();
        return true;
    }

    /// <summary>The class a note is for: the course of the class folder it's in, if it's in one.</summary>
    public string? GetNoteCourse(long noteId)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT f.course FROM items i JOIN note_folders f ON f.id = i.folder_id WHERE i.id = $id";
        command.Parameters.AddWithValue("$id", noteId);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// Swaps a note's waiting suggestions for new ones. Ones already added or skipped stay as they were, and
    /// aren't suggested again.
    /// </summary>
    public void ReplaceNoteSuggestions(long noteId, IEnumerable<SuggestedItem> items, DateTime now)
    {
        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM note_suggestions WHERE note_id = $note AND status = $open";
            clear.Parameters.AddWithValue("$note", noteId);
            clear.Parameters.AddWithValue("$open", OpenSuggestion);
            clear.ExecuteNonQuery();
        }

        var decided = ReadSuggestions(connection, noteId, null).Select(found => Key(found.Item)).ToHashSet();
        foreach (var item in items.Where(item => decided.Add(Key(item))))
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO note_suggestions (note_id, kind, title, due_date, due_time, status, created_at)
                VALUES ($note, $kind, $title, $date, $time, $open, $now)
                """;
            insert.Parameters.AddWithValue("$note", noteId);
            insert.Parameters.AddWithValue("$kind", item.Kind.ToString());
            insert.Parameters.AddWithValue("$title", item.Title);
            insert.Parameters.AddWithValue("$date", item.Date is { } date ? date.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
            insert.Parameters.AddWithValue("$time", item.Time is { } time ? time.ToString("HH:mm", CultureInfo.InvariantCulture) : DBNull.Value);
            insert.Parameters.AddWithValue("$open", OpenSuggestion);
            insert.Parameters.AddWithValue("$now", FormatReminder(now));
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Suggestions still waiting on a yes or no, soonest first.</summary>
    public IReadOnlyList<NoteSuggestion> GetNoteSuggestions(long noteId)
    {
        using var lease = Connect(out var connection);
        return ReadSuggestions(connection, noteId, OpenSuggestion)
            .OrderBy(found => found.Item.Date ?? DateOnly.MaxValue)
            .ThenBy(found => found.Id)
            .ToList();
    }

    /// <summary>
    /// Adds a suggestion to To do, only because the user said yes. It's added as a new item, the same as the
    /// To do form, and never changes anything already there.
    /// </summary>
    public HandAddResult AcceptNoteSuggestion(long suggestionId, DateTime now)
    {
        NoteSuggestion? found;
        using (Connect(out var connection))
        {
            found = ReadSuggestions(connection, null, OpenSuggestion, suggestionId).FirstOrDefault();
        }

        if (found is null)
        {
            return new HandAddResult(null, "That suggestion is gone.");
        }

        var course = GetNoteCourse(found.NoteId);

        // The item and the suggestion's mark are one write, so a failure between can't leave it to be added twice.
        using var lease = Connect(out var write);
        using var transaction = Begin(write);
        if (ReadSuggestions(write, null, OpenSuggestion, suggestionId).Count == 0)
        {
            return new HandAddResult(null, "That suggestion is gone.");
        }

        var item = found.Item;
        var added = AddByHand(write, new HandAddedItem(item.Kind, item.Title, item.Date, item.Time, Course: course), now);
        if (added.Item is { } stored)
        {
            using var command = write.CreateCommand();
            command.CommandText = "UPDATE note_suggestions SET status = $added, item_id = $item WHERE id = $id";
            command.Parameters.AddWithValue("$added", AddedSuggestion);
            command.Parameters.AddWithValue("$item", stored.Id);
            command.Parameters.AddWithValue("$id", suggestionId);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return added;
    }

    /// <summary>Says no to a suggestion, so it isn't shown or suggested again for this note.</summary>
    public bool SkipNoteSuggestion(long suggestionId)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE note_suggestions SET status = $skipped WHERE id = $id AND status = $open";
        command.Parameters.AddWithValue("$skipped", SkippedSuggestion);
        command.Parameters.AddWithValue("$open", OpenSuggestion);
        command.Parameters.AddWithValue("$id", suggestionId);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Every note's source text, joined, so search can reach it while only the notes show. Given <paramref name="only"/>,
    /// just those notes' sources are read.
    /// </summary>
    private static Dictionary<long, string> ReadSourceText(SqliteConnection connection, IEnumerable<long>? only = null)
    {
        var ids = only?.Distinct().ToList();
        if (ids is { Count: 0 })
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.CommandText = ids is null
            ? "SELECT note_id, name, text FROM note_sources ORDER BY id"
            : $"SELECT note_id, name, text FROM note_sources WHERE note_id IN ({string.Join(", ", ids.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)))}) ORDER BY id";

        var texts = new Dictionary<long, StringBuilder>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            if (!texts.TryGetValue(id, out var text))
            {
                texts[id] = text = new StringBuilder();
            }

            text.Append('\n').Append(reader.GetString(1)).Append('\n').Append(reader.GetString(2));
        }

        return texts.ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
    }

    private Dictionary<long, string> ReadSourceText(IEnumerable<long> only)
    {
        using var lease = Connect(out var connection);
        return ReadSourceText(connection, only);
    }

    public static string BodyHash(string body) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(body))));

    private static string Normalize(string body) => body.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    private static bool NoteExists(SqliteConnection connection, long noteId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM items WHERE id = $id AND kind = 'Note' AND canceled = 0";
        command.Parameters.AddWithValue("$id", noteId);
        return command.ExecuteScalar() is not null;
    }

    private static NoteBodyState? ReadBodyState(SqliteConnection connection, long noteId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(i.body, c.text), i.generated_at, i.generated_hash FROM items i JOIN captures c ON c.id = i.capture_id
            WHERE i.id = $id AND i.kind = 'Note' AND i.canceled = 0
            """;
        command.Parameters.AddWithValue("$id", noteId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new NoteBodyState(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                reader.IsDBNull(2) ? null : reader.GetString(2))
            : null;
    }

    private static void WriteBody(SqliteConnection connection, long noteId, string body, DateTime now, DateTime? generatedAt, string? hash)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE items SET body = $body, edited_at = $now, generated_at = $generated, generated_hash = $hash
            WHERE id = $id AND kind = 'Note' AND canceled = 0
            """;
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$now", FormatReminder(now));
        command.Parameters.AddWithValue("$generated", generatedAt is { } at ? FormatReminder(at) : DBNull.Value);
        command.Parameters.AddWithValue("$hash", (object?)hash ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", noteId);
        command.ExecuteNonQuery();
    }

    private static List<NoteSuggestion> ReadSuggestions(SqliteConnection connection, long? noteId, string? status, long? id = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, note_id, kind, title, due_date, due_time FROM note_suggestions
            WHERE ($note IS NULL OR note_id = $note) AND ($status IS NULL OR status = $status) AND ($id IS NULL OR id = $id)
            """;
        command.Parameters.AddWithValue("$note", (object?)noteId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);

        var found = new List<NoteSuggestion>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!Enum.TryParse<ItemKind>(reader.GetString(2), out var kind))
            {
                continue;
            }

            found.Add(new NoteSuggestion(
                reader.GetInt64(0),
                reader.GetInt64(1),
                new SuggestedItem(
                    kind,
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : DateOnly.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                    reader.IsDBNull(5) ? null : TimeOnly.Parse(reader.GetString(5), CultureInfo.InvariantCulture))));
        }

        return found;
    }

    private static (ItemKind, string, DateOnly?) Key(SuggestedItem item) => (item.Kind, item.Title.Trim().ToLowerInvariant(), item.Date);
}
