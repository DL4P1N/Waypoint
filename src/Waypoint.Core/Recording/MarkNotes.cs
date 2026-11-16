using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Waypoint.Core.Recording;

/// <summary>
/// Turns the moments marked while recording into lines the note writer reads, each with its time and what the
/// lecture said around then, found from Whisper's timestamps. The writer is asked to stress those moments.
/// </summary>
public static class MarkNotes
{
    /// <summary>A mark is usually pressed just after the thing that mattered was said, so more is taken from before it.</summary>
    public static readonly TimeSpan Before = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan After = TimeSpan.FromSeconds(10);

    public const int MaxQuoteChars = 400;

    /// <summary>One line per mark, or null when there are none.</summary>
    public static string? Describe(IReadOnlyList<RecordingMark> marks, string? timestampsJson)
    {
        if (marks.Count == 0)
        {
            return null;
        }

        var lines = ReadLines(timestampsJson);
        var text = new StringBuilder();
        foreach (var mark in marks.OrderBy(mark => mark.Offset))
        {
            text.Append("- At ").Append(Recorder.Clock(mark.Offset));
            text.Append(mark.Text is { } typed
                ? $", the student marked this and wrote: {JsonSerializer.Serialize(typed)}."
                : ", the student marked this as important.");
            if (Around(lines, mark.Offset) is { Length: > 0 } said)
            {
                text.Append($" Around then the lecture said: {JsonSerializer.Serialize(said)}");
            }

            text.Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>What was said from a little before the mark to a little after, cut to a short quote.</summary>
    public static string Around(IReadOnlyList<(TimeSpan From, TimeSpan To, string Text)> lines, TimeSpan offset)
    {
        var start = offset - Before;
        var end = offset + After;
        var said = string.Join(" ", lines.Where(line => line.To > start && line.From < end).Select(line => line.Text.Trim()).Where(line => line.Length > 0));
        if (said.Length <= MaxQuoteChars)
        {
            return said;
        }

        // Keep the end, which is nearest the moment the mark was pressed.
        var cut = said[^MaxQuoteChars..];
        var space = cut.IndexOf(' ');
        return space is > 0 and < 40 ? cut[(space + 1)..] : cut;
    }

    /// <summary>Whisper's JSON lines, with their start and end times. Empty when there's no JSON or it can't be read.</summary>
    public static IReadOnlyList<(TimeSpan From, TimeSpan To, string Text)> ReadLines(string? timestampsJson)
    {
        var lines = new List<(TimeSpan, TimeSpan, string)>();
        if (string.IsNullOrWhiteSpace(timestampsJson))
        {
            return lines;
        }

        try
        {
            using var document = JsonDocument.Parse(timestampsJson);
            if (!document.RootElement.TryGetProperty("transcription", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                return lines;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                var text = entry.TryGetProperty("text", out var said) && said.ValueKind == JsonValueKind.String ? said.GetString() ?? "" : "";
                if (Times(entry) is { } times)
                {
                    lines.Add((times.From, times.To, text));
                }
            }
        }
        catch (JsonException)
        {
            // No quotes then. The times of the marks are still given.
        }

        return lines;
    }

    private static (TimeSpan From, TimeSpan To)? Times(JsonElement entry)
    {
        if (entry.TryGetProperty("offsets", out var offsets)
            && offsets.TryGetProperty("from", out var from) && from.TryGetInt64(out var fromMs)
            && offsets.TryGetProperty("to", out var to) && to.TryGetInt64(out var toMs))
        {
            return (TimeSpan.FromMilliseconds(fromMs), TimeSpan.FromMilliseconds(toMs));
        }

        if (entry.TryGetProperty("timestamps", out var stamps)
            && stamps.TryGetProperty("from", out var fromText) && Parse(fromText.GetString()) is { } start
            && stamps.TryGetProperty("to", out var toText) && Parse(toText.GetString()) is { } end)
        {
            return (start, end);
        }

        return null;
    }

    /// <summary>Whisper writes times as "00:01:02,500".</summary>
    private static TimeSpan? Parse(string? text) =>
        TimeSpan.TryParseExact(text?.Replace(',', '.'), @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out var time) ? time : null;
}
