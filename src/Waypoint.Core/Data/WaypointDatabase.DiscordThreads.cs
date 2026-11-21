using Microsoft.Data.Sqlite;
using System.Text.Json;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

/// <summary>One message Waypoint sent in a forum post, with a hash of what it says, so it's only edited when that changes.</summary>
public sealed record DiscordThreadPart(string Message, string Hash);

/// <summary>
/// One forum post Waypoint knows about. Kind is "note" or "list", and Key is the note's ID or the list's name (lists
/// are matched without regard to case). State says what the post is now:
/// - <see cref="WaypointDatabase.PostLive"/>: it shows the note or list, and only one post per note or list is live.
/// - <see cref="WaypointDatabase.PostPending"/>: the owner started it and it hasn't become a note or list yet, such
///   as when its first message couldn't be read. It's tried again.
/// - <see cref="WaypointDatabase.PostRetired"/>: it no longer shows anything, because what it showed was deleted on
///   the PC or it was replaced by a newer post (MovedTo). Kept so it's never taken for a new post again.
/// - <see cref="WaypointDatabase.PostGone"/>: Discord deleted it.
/// UserStarter is true when the owner started the post, so its first message is theirs. MetaHash covers the post's
/// name and tags as last set. Cursor is the newest message in it that's been dealt with: everything after it is
/// still to be read, and it only moves once what a message said is saved.
/// </summary>
public sealed record DiscordThreadRow(
    string Thread,
    string Kind,
    string Key,
    string Forum,
    string State,
    bool UserStarter,
    string? MetaHash,
    IReadOnlyList<DiscordThreadPart> Parts,
    string? Cursor = null,
    string? MovedTo = null)
{
    public bool Gone => State == WaypointDatabase.PostGone;

    public bool Live => State == WaypointDatabase.PostLive;
}

/// <summary>
/// Which forum post shows which note or list, the messages in it, and how far it's been read. Added on its own, so
/// nothing older changes. Nothing here holds note text: the note itself stays the only copy that counts. Rows are
/// never deleted, so a post Waypoint has seen is never mistaken for a new one.
/// </summary>
public sealed partial class WaypointDatabase
{
    public const string ThreadNote = "note";
    public const string ThreadList = "list";

    public const string PostLive = "live";
    public const string PostPending = "pending";
    public const string PostRetired = "retired";
    public const string PostGone = "gone";

    private const string PostColumns = "thread_id, kind, item_key, forum_id, state, user_starter, meta_hash, parts, read_to, moved_to";

    private void CreateDiscordThreadTables() => Execute("""
        CREATE TABLE IF NOT EXISTS discord_posts (
            thread_id TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            item_key TEXT NOT NULL COLLATE NOCASE,
            forum_id TEXT NOT NULL,
            state TEXT NOT NULL,
            user_starter INTEGER NOT NULL DEFAULT 0,
            meta_hash TEXT,
            parts TEXT NOT NULL DEFAULT '[]',
            read_to TEXT,
            moved_to TEXT
        );
        CREATE INDEX IF NOT EXISTS discord_posts_item ON discord_posts (kind, item_key, state);
        CREATE TABLE IF NOT EXISTS discord_post_notices (
            message_id TEXT PRIMARY KEY,
            thread_id TEXT NOT NULL,
            sent_at TEXT NOT NULL
        );
        """);

    /// <summary>A notice Waypoint sent in a post, such as a file that couldn't be added, to be cleared away later.</summary>
    public void AddDiscordNotice(string threadId, string messageId, DateTime sentAt)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO discord_post_notices (message_id, thread_id, sent_at) VALUES ($message, $thread, $at)";
        command.Parameters.AddWithValue("$message", messageId);
        command.Parameters.AddWithValue("$thread", threadId);
        command.Parameters.AddWithValue("$at", sentAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <summary>The notices still in a post, oldest first.</summary>
    public IReadOnlyList<(string Message, DateTime SentAt)> GetDiscordNotices(string threadId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT message_id, sent_at FROM discord_post_notices WHERE thread_id = $thread ORDER BY rowid";
        command.Parameters.AddWithValue("$thread", threadId);
        var notices = new List<(string, DateTime)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            notices.Add((reader.GetString(0), DateTime.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return notices;
    }

    public void RemoveDiscordNotice(string messageId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM discord_post_notices WHERE message_id = $message";
        command.Parameters.AddWithValue("$message", messageId);
        command.ExecuteNonQuery();
    }

    /// <summary>A list was renamed, so its posts follow it rather than a new post being made under the new name.</summary>
    private static void RenameListPosts(SqliteConnection connection, string oldName, string newName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE discord_posts SET item_key = $new WHERE kind = 'list' AND item_key = $old";
        command.Parameters.AddWithValue("$old", oldName);
        command.Parameters.AddWithValue("$new", newName);
        command.ExecuteNonQuery();
    }

    /// <summary>The live posts of one kind, oldest first.</summary>
    public IReadOnlyList<DiscordThreadRow> GetDiscordThreads(string kind) =>
        ReadPosts("WHERE kind = $kind AND state = 'live'", ("$kind", kind));

    /// <summary>Posts in one state, of any kind, oldest first.</summary>
    public IReadOnlyList<DiscordThreadRow> GetDiscordThreadsIn(string state) =>
        ReadPosts("WHERE state = $state", ("$state", state));

    /// <summary>The live post for a note or a list, or null. Lists are matched without regard to case.</summary>
    public DiscordThreadRow? GetDiscordThread(string kind, string key) =>
        ReadPosts("WHERE kind = $kind AND item_key = $key AND state = 'live'", ("$kind", kind), ("$key", key)).LastOrDefault();

    /// <summary>The post with this thread ID, in whatever state, or null when Waypoint has never known it.</summary>
    public DiscordThreadRow? FindDiscordThread(string threadId) =>
        ReadPosts("WHERE thread_id = $thread", ("$thread", threadId)).FirstOrDefault();

    /// <summary>The live post one of Waypoint's messages is in, or null.</summary>
    public DiscordThreadRow? FindDiscordThreadByMessage(string messageId) =>
        ReadPosts("WHERE state = 'live' AND parts LIKE $message", ("$message", $"%\"{messageId}\"%")).FirstOrDefault(row => row.Parts.Any(part => part.Message == messageId));

    /// <summary>
    /// Adds a post, or changes everything about one already known. A live one takes over from any other live post for
    /// the same note or list, which is retired with MovedTo pointing here, so there's only ever one. Returns the
    /// posts it took over from.
    /// </summary>
    public IReadOnlyList<DiscordThreadRow> AddDiscordThread(DiscordThreadRow row)
    {
        var replaced = row.Live
            ? ReadPosts("WHERE kind = $kind AND item_key = $key AND state = 'live' AND thread_id <> $thread", ("$kind", row.Kind), ("$key", row.Key), ("$thread", row.Thread))
            : [];
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var old in replaced)
        {
            using var retire = connection.CreateCommand();
            retire.CommandText = "UPDATE discord_posts SET state = 'retired', moved_to = $to WHERE thread_id = $thread";
            retire.Parameters.AddWithValue("$to", row.Thread);
            retire.Parameters.AddWithValue("$thread", old.Thread);
            retire.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO discord_posts ({PostColumns}) VALUES ($thread, $kind, $key, $forum, $state, $user, $meta, $parts, $read, $moved)
            ON CONFLICT (thread_id) DO UPDATE SET kind = excluded.kind, item_key = excluded.item_key, forum_id = excluded.forum_id,
                state = excluded.state, user_starter = excluded.user_starter, meta_hash = excluded.meta_hash, parts = excluded.parts,
                read_to = COALESCE(excluded.read_to, discord_posts.read_to), moved_to = excluded.moved_to
            """;
        command.Parameters.AddWithValue("$thread", row.Thread);
        command.Parameters.AddWithValue("$kind", row.Kind);
        command.Parameters.AddWithValue("$key", row.Key);
        command.Parameters.AddWithValue("$forum", row.Forum);
        command.Parameters.AddWithValue("$state", row.State);
        command.Parameters.AddWithValue("$user", row.UserStarter ? 1 : 0);
        command.Parameters.AddWithValue("$meta", (object?)row.MetaHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$parts", JsonSerializer.Serialize(row.Parts));
        command.Parameters.AddWithValue("$read", (object?)row.Cursor ?? DBNull.Value);
        command.Parameters.AddWithValue("$moved", (object?)row.MovedTo ?? DBNull.Value);
        command.ExecuteNonQuery();
        transaction.Commit();
        return replaced;
    }

    /// <summary>Waypoint's messages in a post, as they now are. Nothing else about the post changes.</summary>
    public void SetDiscordThreadParts(string threadId, IReadOnlyList<DiscordThreadPart> parts) =>
        UpdatePost(threadId, "parts = $value", JsonSerializer.Serialize(parts));

    /// <summary>The name and tags a post was last given.</summary>
    public void SetDiscordThreadMeta(string threadId, string meta) => UpdatePost(threadId, "meta_hash = $value", meta);

    public void SetDiscordThreadState(string threadId, string state) => UpdatePost(threadId, "state = $value", state);

    /// <summary>A post the owner started now shows this note or list.</summary>
    public void SetDiscordThreadKey(string threadId, string key, string state)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE discord_posts SET item_key = $key, state = $state WHERE thread_id = $thread";
        command.Parameters.AddWithValue("$thread", threadId);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$state", state);
        command.ExecuteNonQuery();
    }

    /// <summary>Discord said a post was deleted. Only the record of it changes: the note or list is never touched.</summary>
    public bool MarkDiscordThreadGone(string threadId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE discord_posts SET state = 'gone' WHERE thread_id = $thread";
        command.Parameters.AddWithValue("$thread", threadId);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>Moves how far a post has been read, only ever forward, once what a message said is dealt with.</summary>
    public void AdvanceDiscordThread(string threadId, string messageId)
    {
        using var connection = Open();
        Advance(connection, threadId, messageId);
    }

    private static void Advance(SqliteConnection connection, string threadId, string messageId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE discord_posts SET read_to = $message
            WHERE thread_id = $thread AND (read_to IS NULL OR LENGTH(read_to) < LENGTH($message) OR (LENGTH(read_to) = LENGTH($message) AND read_to < $message))
            """;
        command.Parameters.AddWithValue("$thread", threadId);
        command.Parameters.AddWithValue("$message", messageId);
        command.ExecuteNonQuery();
    }

    private void UpdatePost(string threadId, string set, string value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE discord_posts SET {set} WHERE thread_id = $thread";
        command.Parameters.AddWithValue("$thread", threadId);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private List<DiscordThreadRow> ReadPosts(string condition, params (string Name, string Value)[] values)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {PostColumns} FROM discord_posts {condition} ORDER BY rowid";
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<DiscordThreadRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new DiscordThreadRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5) == 1,
                reader.IsDBNull(6) ? null : reader.GetString(6),
                Parts(reader.GetString(7)),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return rows;
    }

    private static List<DiscordThreadPart> Parts(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<DiscordThreadPart>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Saves a note only if its text is still <paramref name="expectedBody"/>, with the same write the Notes page
    /// makes, so text added from Discord never overwrites a change saved meanwhile. False when it changed or is gone.
    /// With <paramref name="readTo"/>, the post the text came from is marked read up to that message in the same
    /// write, so a crash in between can never add the same text twice. With <paramref name="expectedTitle"/>, the
    /// title it had then, a note renamed meanwhile keeps its new title when this save leaves the title as it was,
    /// and is turned down as changed when this save renames it too.
    /// </summary>
    public bool SaveNoteIfUnchanged(long id, string title, string body, string expectedBody, DateTime now, (string Thread, string Message)? readTo = null, string? expectedTitle = null) =>
        SaveNoteIfUnchanged(id, title, body, expectedBody, now, out _, readTo, expectedTitle);

    /// <summary>The same, with <paramref name="saved"/> the note as this save wrote it, with the title it kept.</summary>
    public bool SaveNoteIfUnchanged(long id, string title, string body, string expectedBody, DateTime now, out NoteDetail? saved, (string Thread, string Message)? readTo = null, string? expectedTitle = null)
    {
        saved = null;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        NoteDetail stored;
        using (var check = connection.CreateCommand())
        {
            check.CommandText = $"SELECT {NoteColumns} FROM items i JOIN captures c ON c.id = i.capture_id WHERE i.id = $id AND i.kind = 'Note' AND i.canceled = 0";
            check.Parameters.AddWithValue("$id", id);
            using var reader = check.ExecuteReader();
            if (!reader.Read() || (stored = ReadNote(reader)).Body != expectedBody)
            {
                return false;
            }
        }

        if (expectedTitle is not null && stored.Title != NoteTitle(expectedTitle, expectedBody))
        {
            if (title != expectedTitle)
            {
                return false;
            }

            title = stored.Title;
        }

        var written = NoteTitle(title, body);
        var done = WriteNote(connection, id, written, body, now);
        if (done && readTo is { } read)
        {
            Advance(connection, read.Thread, read.Message);
        }

        transaction.Commit();
        saved = done ? stored with { Title = written, Body = body } : null;
        return done;
    }

    /// <summary>
    /// Adds several things to a list. Ones already on it are left alone, and checked-off ones go back on, so the
    /// same lines sent again, such as after a failure partway, never end up on it twice.
    /// </summary>
    public void AddListEntries(string name, IReadOnlyList<string> entries, DateTime now)
    {
        var clean = entries.Select(entry => entry.Trim()).Where(entry => entry.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (clean.Count == 0)
        {
            return;
        }

        var current = GetListEntries(name);

        // One write, so the lines are on the list whole or not at all.
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var finished in current.Where(entry => entry.Done && clean.Any(text => Same(text, entry.Text))))
        {
            SetEntryDone(connection, finished, false, now);
        }

        var added = clean.Where(text => !current.Any(entry => Same(entry.Text, text))).ToList();
        if (added.Count > 0)
        {
            AddToList(connection, name, added, null, now);
        }

        transaction.Commit();
    }
}
