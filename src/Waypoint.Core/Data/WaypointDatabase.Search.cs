using System.Globalization;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

public sealed partial class WaypointDatabase
{
    /// <summary>Everything that can be found. To make something new searchable, add a method that lists it here.</summary>
    private Func<DateTime, IEnumerable<SearchDoc>>[] SearchSources => [OpenItemDocs, HistoryDocs, PastEventDocs, NoteDocs, ListDocs, HandoffDocs, WaitDocs];

    public IReadOnlyList<SearchDoc> SearchEverything(SearchQuery query, DateTime now) =>
        SearchIndex.Find(SearchSources.SelectMany(source => source(now)), query);

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
        using var connection = Open();
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

    /// <summary>A note is found by its own text once edited, and by the capture it came from until then.</summary>
    private IEnumerable<SearchDoc> NoteDocs(DateTime now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.title, i.about, COALESCE(i.body, c.text), c.created_at, i.body, i.id FROM items i JOIN captures c ON c.id = i.capture_id
            WHERE i.kind = 'Note' AND i.canceled = 0
            """;

        // Sources are matched too, so a word only in the slides or transcript still finds the note.
        var sources = ReadSourceText(connection);
        var docs = new List<SearchDoc>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var source = sources.GetValueOrDefault(reader.GetInt64(5));
            docs.Add(new SearchDoc(
                SearchIndex.Note,
                "Note",
                reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture).LocalDateTime,
                reader.IsDBNull(1) ? reader.IsDBNull(4) ? null : FirstLine(reader.GetString(4)) : reader.GetString(1),
                source is null ? reader.GetString(2) : $"{reader.GetString(2)}\n{source}"));
        }

        return docs;
    }

    private IEnumerable<SearchDoc> ListDocs(DateTime now)
    {
        using var connection = Open();
        return ReadEntries(connection, null).Select(entry => new SearchDoc(SearchIndex.List, entry.List, entry.Text, entry.AddedAt));
    }

    private static string ShortDate(DateOnly date) => date.ToString("MMM d", CultureInfo.InvariantCulture);
}
