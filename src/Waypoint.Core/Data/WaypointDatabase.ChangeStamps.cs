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
    /// file records, its forum post, and the notes folder's own settings. Each change, from anywhere, moves the count.
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
        ("preferences", null, $"{{0}}.key IN ('{NoteFilesRootKey}', '{NotesRestoredKey}', '{NotesRestoreHandledKey}') OR {{0}}.key LIKE '{NotesRestoreHandledKey}:%'"),
    ];

    /// <summary>Told apart from the counts of any other copy of the data this one held, such as before a restore.</summary>
    private string _stampEpoch = Guid.NewGuid().ToString("N");

    private void CreateChangeStamps()
    {
        var sql = new System.Text.StringBuilder("""
            CREATE TABLE IF NOT EXISTS change_stamps (
                name TEXT PRIMARY KEY,
                stamp INTEGER NOT NULL
            );
            """);
        sql.Append(CultureInfo.InvariantCulture, $"INSERT OR IGNORE INTO change_stamps (name, stamp) VALUES ('{NotesStampName}', 0);\n");
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
                sql.Append(CultureInfo.InvariantCulture, $"""
                    CREATE TRIGGER IF NOT EXISTS stamp_{NotesStampName}_{table}_{verb.ToLowerInvariant()} AFTER {verb}{of} ON {table}{condition}
                    BEGIN UPDATE change_stamps SET stamp = stamp + 1 WHERE name = '{NotesStampName}'; END;

                    """);
            }
        }

        Execute(sql.ToString());
    }

    /// <summary>
    /// Changes whenever a note, its folder, its sources, its file record, its forum post, or the notes folder's
    /// settings change, and is otherwise the same from one call to the next. Null when it can't be read, so nothing
    /// relying on it skips a round.
    /// </summary>
    public string? NotesChangeStamp()
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT stamp FROM change_stamps WHERE name = '{NotesStampName}'";
            return command.ExecuteScalar() is long stamp ? $"{_stampEpoch}:{stamp.ToString(CultureInfo.InvariantCulture)}" : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>After a restore the data is another copy's, so no count read before it can match one read after.</summary>
    private void ForgetChangeStamps() => _stampEpoch = Guid.NewGuid().ToString("N");
}
