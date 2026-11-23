using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

public sealed partial class WaypointDatabase
{
    /// <summary>Everything that can be found. To make something new searchable, add a method that lists it here.</summary>
    /// <remarks>Notes are read only where a word is, since their text and sources are what grows.</remarks>
    private Func<DateTime, IEnumerable<SearchDoc>>[] SearchSources(IReadOnlyList<string> words) =>
        [OpenItemDocs, HistoryDocs, PastEventDocs, now => NoteDocs(now, words), ListDocs, HandoffDocs, WaitDocs];

    public IReadOnlyList<SearchDoc> SearchEverything(SearchQuery query, DateTime now) =>
        SearchIndex.Find(SearchSources(SearchIndex.Words(query.Words)).SelectMany(source => source(now)), query);

    /// <summary>
    /// A LIKE pattern that finds <paramref name="text"/> anywhere an ordinal search that ignores case would. SQLite's
    /// LIKE only ignores case for ASCII, so any other character matches any one character here. What it finds is
    /// still matched exactly afterwards, so it only narrows what's read, never what's found.
    /// </summary>
    private static string ContainsPattern(string text)
    {
        var pattern = new StringBuilder("%");
        foreach (var rune in text.EnumerateRunes())
        {
            if (!rune.IsAscii)
            {
                pattern.Append('_');
                continue;
            }

            var letter = (char)rune.Value;
            if (letter is '%' or '_' or '\\')
            {
                pattern.Append('\\');
            }

            pattern.Append(letter);
        }

        return pattern.Append('%').ToString();
    }

    /// <summary>More patterns than this, or one longer than <see cref="LongestPattern"/>, and SQLite turns the query down.</summary>
    private const int MostPatterns = 50;

    private const int LongestPattern = 1000;

    /// <summary>
    /// The LIKE patterns to narrow a search by, or none when there are too many or one is too long for SQLite to
    /// take. Then everything is read and matched as before.
    /// </summary>
    private static List<string> Prefilter(IEnumerable<string> texts)
    {
        var patterns = texts.Select(ContainsPattern).ToList();
        return patterns.Count > MostPatterns || patterns.Any(pattern => pattern.Length > LongestPattern) ? [] : patterns;
    }

    /// <summary>
    /// True for a note (items i, with its capture c) holding any of the patterns $p0, $p1, and so on in its title,
    /// its text, its note line, or one of its sources.
    /// </summary>
    private static string NoteHoldsAny(int patterns) => string.Join(" OR ", Enumerable.Range(0, patterns).Select(index => $"""
        (i.title LIKE $p{index} ESCAPE '\' OR COALESCE(i.body, c.text) LIKE $p{index} ESCAPE '\' OR i.about LIKE $p{index} ESCAPE '\'
         OR EXISTS (SELECT 1 FROM note_sources s WHERE s.note_id = i.id AND (s.name LIKE $p{index} ESCAPE '\' OR s.text LIKE $p{index} ESCAPE '\')))
        """));

    private static void AddPatterns(SqliteCommand command, IReadOnlyList<string> patterns)
    {
        for (var index = 0; index < patterns.Count; index++)
        {
            command.Parameters.AddWithValue($"$p{index.ToString(CultureInfo.InvariantCulture)}", patterns[index]);
        }
    }

    private IEnumerable<SearchDoc> WaitDocs(DateTime now) =>
        GetWaitList().Select(item => new SearchDoc(SearchIndex.Shopping, "Wait list", item.Description, item.AddedAt, item.Reason));

    private IEnumerable<SearchDoc> OpenItemDocs(DateTime now) =>
        GetOpenItems()
            .Where(item => item.Kind != ItemKind.Note)
            .Select(item => item.Kind == ItemKind.Shopping
                ? new SearchDoc(SearchIndex.Shopping, ShoppingList, item.Title, item.DueDate?.ToDateTime(TimeOnly.MinValue))
                : new SearchDoc(SearchIndex.Open, item.DueDate is { } date ? $"Due {ShortDate(date)}" : "To do", item.Title, item.DueAt?.LocalDateTime ?? item.DueDate?.ToDateTime(TimeOnly.MinValue), item.Course));

    private IEnumerable<SearchDoc> HistoryDocs(DateTime now) =>
        GetHistory().Select(entry => new SearchDoc(
            entry.Action == HistoryAction.Past ? SearchIndex.Past : SearchIndex.Finished,
            $"{entry.Label} {ShortDate(DateOnly.FromDateTime(entry.At))}",
            entry.Title,
            entry.At,
            entry.Course));

    /// <summary>Feed events that came and went without being checked off.</summary>
    private IEnumerable<SearchDoc> PastEventDocs(DateTime now)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {EventColumns} FROM calendar_events WHERE done = 0 AND {EventDueDate} < $today";
        command.Parameters.AddWithValue("$today", DateOnly.FromDateTime(now).ToString("O", CultureInfo.InvariantCulture));

        var docs = new List<SearchDoc>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var item = ReadEvent(reader);
            docs.Add(new SearchDoc(SearchIndex.Past, $"Past {ShortDate(item.DueDate!.Value)}", item.Title, item.DueDate.Value.ToDateTime(TimeOnly.MinValue), item.Course));
        }

        return docs;
    }

    /// <summary>
    /// A note is found by its own text once edited, and by the capture it came from until then. With words to find,
    /// only notes holding one of them somewhere are read, along with only their sources.
    /// </summary>
    private IEnumerable<SearchDoc> NoteDocs(DateTime now, IReadOnlyList<string> words)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        var patterns = Prefilter(words);
        command.CommandText = $"""
            SELECT i.title, i.about, COALESCE(i.body, c.text), c.created_at, i.body, i.id FROM items i JOIN captures c ON c.id = i.capture_id
            WHERE i.kind = 'Note' AND i.canceled = 0 {(patterns.Count == 0 ? "" : $"AND ({NoteHoldsAny(patterns.Count)})")}
            """;
        AddPatterns(command, patterns);

        var rows = new List<(string Title, string? Detail, string Text, DateTime At, long Id)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((
                    reader.GetString(0),
                    reader.IsDBNull(1) ? reader.IsDBNull(4) ? null : FirstLine(reader.GetString(4)) : reader.GetString(1),
                    reader.GetString(2),
                    DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture).LocalDateTime,
                    reader.GetInt64(5)));
            }
        }

        // Sources are matched too, so a word only in the slides or transcript still finds the note.
        var sources = patterns.Count == 0 ? ReadSourceText(connection) : ReadSourceText(connection, rows.Select(row => row.Id));
        return rows.Select(row => new SearchDoc(
            SearchIndex.Note,
            "Note",
            row.Title,
            row.At,
            row.Detail,
            sources.GetValueOrDefault(row.Id) is { } source ? $"{row.Text}\n{source}" : row.Text)).ToList();
    }

    private IEnumerable<SearchDoc> ListDocs(DateTime now)
    {
        using var lease = Connect(out var connection);
        return ReadEntries(connection, null).Select(entry => new SearchDoc(SearchIndex.List, entry.List, entry.Text, entry.AddedAt));
    }

    private static string ShortDate(DateOnly date) => date.ToString("MMM d", CultureInfo.InvariantCulture);
}
