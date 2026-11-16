namespace Waypoint.Core.Capture;

/// <summary>
/// One searchable thing. Bucket says what sort of thing it is, Label is how it reads in a reply, and
/// Raw holds any longer original text that's matched against but not shown.
/// </summary>
public sealed record SearchDoc(string Bucket, string Label, string Text, DateTime? At, string? Detail = null, string? Raw = null);

/// <summary>Words to look for, optionally narrowed to one bucket and a time range.</summary>
public sealed record SearchQuery(string? Words, string? Bucket = null, DateTime? From = null, DateTime? Until = null);

/// <summary>Plain local matching, so nothing stored needs to go to the sorter to be found.</summary>
public static class SearchIndex
{
    public const string Open = "open";
    public const string Finished = "finished";
    public const string Past = "past";
    public const string Note = "note";
    public const string Shopping = "shopping";
    public const string List = "list";
    public const string Handoff = "handoff";

    private const int MaxShown = 8;
    private const int MinWordLength = 3;

    private static readonly string[] Buckets = [Open, Finished, Past, Note, Shopping, List, Handoff];

    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "that", "this", "with", "was", "are", "did", "what", "when", "where", "which", "who", "how",
        "about", "from", "have", "had", "has", "need", "needed", "write", "wrote", "find", "show", "look", "undo",
        "again", "back", "reopen", "not", "you", "your", "his", "her", "their", "can", "could", "should", "would", "want", "all",
    };

    /// <summary>The words worth matching on. A plural matches its singular, since matching is by substring.</summary>
    public static IReadOnlyList<string> Words(string? text) =>
        (text ?? "")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim('.', ',', '!', '?', ':', ';', '"', '\'', '(', ')'))
            .Where(word => word.Length >= MinWordLength && !Ignored.Contains(word))
            .Select(word => word.Length > MinWordLength && word.EndsWith('s') ? word[..^1] : word)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Whether any word of <paramref name="note"/> appears in <paramref name="text"/>.</summary>
    public static bool Mentions(string text, string note) =>
        Words(note).Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>The docs that match, best first, then newest. Without words, everything in range matches.</summary>
    public static IReadOnlyList<SearchDoc> Find(IEnumerable<SearchDoc> docs, SearchQuery query)
    {
        var words = Words(query.Words);
        var bucket = Buckets.FirstOrDefault(known => string.Equals(known, query.Bucket?.Trim(), StringComparison.OrdinalIgnoreCase));

        return docs
            .Where(doc => bucket is null || doc.Bucket == bucket)
            .Where(doc => (query.From is null && query.Until is null) || (doc.At is { } at && !(at < query.From) && !(at > query.Until)))
            .Select(doc => (Doc: doc, Score: words.Count(word => Haystack(doc).Contains(word, StringComparison.OrdinalIgnoreCase))))
            .Where(hit => words.Count == 0 || hit.Score > 0)
            .OrderByDescending(hit => hit.Score)
            .ThenByDescending(hit => hit.Doc.At ?? DateTime.MinValue)
            .Select(hit => hit.Doc)
            .ToList();
    }

    public static string Reply(IReadOnlyList<SearchDoc> hits, string? words)
    {
        if (hits.Count == 0)
        {
            return string.IsNullOrWhiteSpace(words) ? "Nothing found." : $"Nothing found for {words}.";
        }

        var lines = hits.Take(MaxShown).Select(hit => $"- {hit.Label}: {hit.Text}{(hit.Detail is { } detail ? $" ({detail})" : "")}");
        var more = hits.Count > MaxShown ? $"\nand {hits.Count - MaxShown} more" : "";
        return string.Join("\n", lines) + more;
    }

    private static string Haystack(SearchDoc doc) => string.Join(' ', doc.Text, doc.Detail, doc.Raw);
}
