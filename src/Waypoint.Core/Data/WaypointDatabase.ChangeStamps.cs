using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Waypoint.Core.Data;

/// <summary>
/// A count the database moves on itself whenever anything the notes folder sync or the Discord notes forum shows
/// changes, so a round that finds it where it was can tell nothing changed without reading every note.
/// </summary>
public sealed partial class WaypointDatabase
{
    private const string NotesStampName = "notes";

    /// <summary>
    /// What changes a note's file or its forum post: the note, its capture's text, its folders, its sources, its
    /// file records, its forum post, and the notes folder's own settings, each PC's own folder in server mode included. Each change, from anywhere, moves the count.
    /// </summary>
    private static readonly (string Table, string? Columns, string? When)[] NotesStampTriggers =
    [
        ("items", null, "{0}.kind = 'Note'"),
        ("captures", "text", null),
        ("note_folders", null, null),
        ("note_sources", null, null),
        ("note_files", null, null),
        ("pc_note_files", null, null),
        ("discord_posts", null, $"{{0}}.kind = '{ThreadNote}'"),
        ("preferences", null, $"{{0}}.key IN ('{NoteFilesRootKey}', '{NotesRestoredKey}', '{NotesRestoreHandledKey}') OR {{0}}.key LIKE '{NotesRestoreHandledKey}:%' OR {{0}}.key LIKE '{NoteFilesRootKey}:%'"),
    ];

    /// <summary>Told apart from the counts of any other copy of the data this one held, such as before a restore.</summary>
    private string _stampEpoch = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Every stamp trigger is named with a hash of all their SQL, so a change to any of them gives new names. On open,
    /// stamp triggers with any other name are dropped and the current ones made, so an older database always ends up
    /// with exactly these. Once they're in place, an open changes nothing.
    /// </summary>
    private void CreateChangeStamps()
    {
        var bodies = new List<(string Name, string Sql)>();
        foreach (var (table, columns, when) in NotesStampTriggers)
        {
            foreach (var (verb, row) in new[] { ("INSERT", "NEW"), ("UPDATE", "NEW"), ("DELETE", "OLD") })
            {
                if (columns is not null && verb != "UPDATE")
                {
                    continue;
                }

                var condition = when is null ? "" : verb == "UPDATE"
                    ? $" WHEN ({string.Format(CultureInfo.InvariantCulture, when, "OLD")}) OR ({string.Format(CultureInfo.InvariantCulture, when, "NEW")})"
                    : $" WHEN {string.Format(CultureInfo.InvariantCulture, when, row)}";
                var of = columns is null ? "" : $" OF {columns}";
                bodies.Add(($"{NotesStampName}_{table}_{verb.ToLowerInvariant()}", $"""
                    AFTER {verb}{of} ON {table}{condition}
                    BEGIN UPDATE change_stamps SET stamp = stamp + 1 WHERE name = '{NotesStampName}'; END
                    """));
            }
        }

        var version = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("\n", bodies.Select(body => $"{body.Name}\n{body.Sql}")))))[..8].ToLowerInvariant();
        var triggers = bodies.Select(body => (Name: $"{StampTriggerPrefix}{version}_{body.Name}", body.Sql)).ToList();

        using var lease = Connect(out var connection);
        using (var create = connection.CreateCommand())
        {
            create.CommandText = $"""
                CREATE TABLE IF NOT EXISTS change_stamps (
                    name TEXT PRIMARY KEY,
                    stamp INTEGER NOT NULL
                );
                INSERT OR IGNORE INTO change_stamps (name, stamp) VALUES ('{NotesStampName}', 0);
                """;
            create.ExecuteNonQuery();
        }

        var wanted = triggers.Select(trigger => trigger.Name).ToHashSet(StringComparer.Ordinal);
        var stale = new List<string>();
        using (var find = connection.CreateCommand())
        {
            find.CommandText = $"SELECT name FROM sqlite_master WHERE type = 'trigger' AND substr(name, 1, {StampTriggerPrefix.Length}) = '{StampTriggerPrefix}'";
            using var reader = find.ExecuteReader();
            while (reader.Read())
            {
                stale.Add(reader.GetString(0));
            }
        }

        var have = stale.ToHashSet(StringComparer.Ordinal);
        stale.RemoveAll(wanted.Contains);
        var missing = triggers.Where(trigger => !have.Contains(trigger.Name)).ToList();
        if (stale.Count == 0 && missing.Count == 0)
        {
            return;
        }

        // Swapped in one write, so the count never goes unmoved by a change made between the old going and the new coming.
        using var transaction = Begin(connection);
        using (var swap = connection.CreateCommand())
        {
            swap.CommandText = string.Concat(stale.Select(name => $"DROP TRIGGER IF EXISTS \"{name}\";\n"))
                + string.Concat(missing.Select(trigger => $"CREATE TRIGGER IF NOT EXISTS {trigger.Name} {trigger.Sql};\n"));
            swap.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>The start of every stamp trigger's name, followed by the version and what it watches.</summary>
    private const string StampTriggerPrefix = "stamp_";

    /// <summary>
    /// Changes whenever a note, its folder, its sources, its file record, its forum post, or the notes folder's
    /// settings change, and is otherwise the same from one call to the next. Null when it can't be read, so nothing
    /// relying on it skips a round.
    /// </summary>
    public string? NotesChangeStamp()
    {
        try
        {
            using var lease = Connect(out var connection);
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT stamp FROM change_stamps WHERE name = '{NotesStampName}'";
            return command.ExecuteScalar() is long stamp ? $"{_root._stampEpoch}:{stamp.ToString(CultureInfo.InvariantCulture)}" : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>After a restore the data is another copy's, so no count read before it can match one read after.</summary>
    private void ForgetChangeStamps() => _root._stampEpoch = Guid.NewGuid().ToString("N");
}
