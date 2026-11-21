using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>
/// Searches read only the notes, History rows, and purchases that can match. What's found, and its order, is what
/// matching everything by hand finds.
/// </summary>
public sealed class SearchReadsTests : IDisposable
{
    private static readonly DateTime Now = new(2030, 6, 3, 9, 0, 0);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-search-reads-{Guid.NewGuid():N}.db");
    private readonly WaypointDatabase _database;

    public SearchReadsTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("cells")]
    [InlineData("CELLS")]
    [InlineData("mitochondria energy")]
    [InlineData("ÉCOLE")]
    [InlineData("école")]
    [InlineData("100%")]
    [InlineData("under_score")]
    [InlineData(@"back\slash")]
    [InlineData("ab")]
    [InlineData("a b")]
    [InlineData("slide")]
    [InlineData("nothing like it")]
    [InlineData("💡 idea")]
    public void Notes_found_in_the_database_are_the_ones_matching_every_note_finds_in_the_same_order(string words)
    {
        var biology = _database.CreateNoteFolder("Made up biology", null, Now).Id;
        var cells = _database.CreateNote("Cells", "The made up nucleus holds the DNA.", Now, biology);
        _database.CreateNote("Made up energy", "Mitochondria make energy.", Now.AddMinutes(1), biology);
        _database.CreateNote("École notes", "Made up french class.", Now.AddMinutes(2));
        _database.CreateNote("Percent", "Scored 100% on the made up quiz", Now.AddMinutes(3));
        _database.CreateNote("Snake", "an under_score and a back\\slash", Now.AddMinutes(4));
        _database.CreateNote("Short", "a b c", Now.AddMinutes(5));
        _database.CreateNote("Light bulb", "💡 idea for the made up project", Now.AddMinutes(6));
        _database.CreateNote("Plain", "Nothing to see", Now.AddMinutes(7));
        _database.AddNoteSource(cells, "pdf", "slides.pdf", "Slide text about CELLS and more", Now);

        Assert.Equal(Ids(ByHand(_database.GetNotes(), words)), Ids(_database.SearchNotes(words)));
        Assert.Equal(
            Ids(ByHand(_database.GetNotesInFolder(biology, includeSubfolders: true), words)),
            Ids(_database.SearchNotesInFolder(words, biology)));
        Assert.Equal(
            Ids(ByHand(_database.GetNotesInFolder(null), words)),
            Ids(_database.SearchNotesInFolder(words, null)));
    }

    [Fact]
    public void Asks_search_finds_notes_by_their_sources_and_only_what_matches()
    {
        var cells = _database.CreateNote("Cells", "The made up nucleus", Now);
        _database.CreateNote("Other", "Nothing here", Now);
        _database.AddNoteSource(cells, "transcript", "lecture", "We talked about ribosomes", Now);

        var found = _database.SearchEverything(new SearchQuery("ribosomes", SearchIndex.Note), Now);

        var doc = Assert.Single(found);
        Assert.Equal("Cells", doc.Text);
        Assert.Contains("ribosomes", doc.Raw);
        Assert.Equal(2, _database.SearchEverything(new SearchQuery(null, SearchIndex.Note), Now).Count);
    }

    [Theory]
    [InlineData("dentist")]
    [InlineData("DENTIST math")]
    [InlineData("cleaning math")]
    [InlineData("é")]
    [InlineData("%")]
    public void History_found_in_the_database_is_what_matching_every_row_finds(string words)
    {
        foreach (var (title, course) in new[] { ("Book dentist cleaning", "Math"), ("Return made up books", null), ("Résumé draft", null), ("Call dentist", null), ("Save 5% more", "Math") })
        {
            _database.SetDone(Store(new SortedItem(ItemKind.Task, title, DateOnly.FromDateTime(Now), null, Course: course)), true);
        }

        foreach (var filter in Enum.GetValues<HistoryFilter>())
        {
            var all = _database.SearchHistory(null, 1000, filter);
            foreach (var limit in new[] { 0, 1, 2, 100 })
            {
                var byHand = all.Where(entry => $"{entry.Title} {entry.Course}".Contains(words.Trim(), StringComparison.OrdinalIgnoreCase)).Take(limit);
                Assert.Equal(byHand.Select(entry => entry.Id), _database.SearchHistory(words, limit, filter).Select(entry => entry.Id));
            }
        }
    }

    /// <summary>Too many words, or one too long, for SQLite to narrow by, and everything is read and matched as before.</summary>
    [Theory]
    [InlineData(1000, 6)]
    [InlineData(1, 60000)]
    [InlineData(51, 4)]
    [InlineData(1, 1001)]
    public void A_very_long_search_still_finds_what_matching_every_note_and_row_finds(int count, int length)
    {
        var cells = _database.CreateNote("Cells", "The made up nucleus", Now);
        _database.CreateNote("Other", "Nothing here", Now.AddMinutes(1));
        _database.AddNoteSource(cells, "pdf", "slides.pdf", "Slide text", Now);
        _database.SetDone(Store(new SortedItem(ItemKind.Task, "Made up nucleus errand", null, null)), true);
        var words = string.Join(" ", Enumerable.Range(0, count).Select(index => index == count - 1 ? "nucleus" : new string((char)('a' + (index % 26)), length) + index));

        Assert.Equal(Ids(ByHand(_database.GetNotes(), words)), Ids(_database.SearchNotes(words)));
        Assert.Equal(Ids(ByHand(_database.GetNotesInFolder(null), words)), Ids(_database.SearchNotesInFolder(words, null)));
        Assert.Contains(_database.SearchEverything(new SearchQuery(words), Now), doc => doc.Text == "Cells");
        foreach (var filter in new[] { HistoryFilter.All, HistoryFilter.Completed })
        {
            var byHand = _database.SearchHistory(null, 100, filter).Where(entry => $"{entry.Title} {entry.Course}".Contains(words, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(byHand.Select(entry => entry.Id), _database.SearchHistory(words, 100, filter).Select(entry => entry.Id));
        }
    }

    [Fact]
    public void Purchases_found_are_the_newest_matching_ones_in_order()
    {
        for (var day = 1; day <= 12; day++)
        {
            Store(new SortedItem(ItemKind.Expense, day % 3 == 0 ? $"MADE UP CAFE {day}" : $"Made up grocer {day}", new DateOnly(2030, 5, day), 100 * day));
        }

        Store(new SortedItem(ItemKind.Expense, "Made up cafe again", new DateOnly(2030, 5, 12), 450));

        var all = _database.GetSpending(DateOnly.MinValue, DateOnly.MaxValue);
        foreach (var limit in new[] { 1, 3, 100 })
        {
            var byHand = all.Where(row => row.Title.Contains("cafe", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(row => row.Date).ThenByDescending(row => row.Id).Take(limit);
            Assert.Equal(byHand, _database.SearchPurchases("cafe", limit));
        }
    }

    /// <summary>The item as stored, found by its title, or null for one that isn't open, such as an expense.</summary>
    private StoredItem Store(SortedItem item)
    {
        _database.CompleteCapture(_database.AddCapture(item.Title), [item]);
        return _database.GetOpenItems().SingleOrDefault(stored => stored.Title == item.Title)!;
    }

    private IReadOnlyList<NoteDetail> ByHand(IReadOnlyList<NoteDetail> notes, string words)
    {
        var clean = words.Trim();
        var sources = notes.ToDictionary(note => note.Id, note => string.Concat(_database.GetNoteSources(note.Id).Select(source => $"\n{source.Name}\n{source.Text}")));
        if (SearchIndex.Words(clean).Count == 0)
        {
            return notes.Where(note => $"{note.Title}\n{note.Body}\n{note.About}\n{sources[note.Id]}".Contains(clean, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var byDoc = notes.ToDictionary(
            note => new SearchDoc(SearchIndex.Note, "Note", note.Title, note.At, note.About, sources[note.Id].Length > 0 ? $"{note.Body}\n{sources[note.Id]}" : note.Body),
            note => note,
            ReferenceEqualityComparer.Instance);
        return SearchIndex.Find(byDoc.Keys.Cast<SearchDoc>(), new SearchQuery(clean)).Select(doc => byDoc[doc]).ToList();
    }

    private static List<long> Ids(IEnumerable<NoteDetail> notes) => notes.Select(note => note.Id).ToList();
}
