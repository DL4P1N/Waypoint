using System.Globalization;
using System.Text.Json;

namespace Waypoint.Core.Transcription;

/// <summary>One line of a transcript, with when it was said in the recording.</summary>
public sealed record TranscriptLine(TimeSpan Start, TimeSpan End, string Text)
{
    /// <summary>"4:05", or "1:02:03" past an hour.</summary>
    public string Stamp => TranscriptLines.Stamp(Start);
}

/// <summary>
/// Reads the timestamps whisper-cli writes with <c>-oj</c>, so a transcript can be shown line by line with times,
/// and works out where in the audio a line starts and which line is playing.
/// </summary>
public static class TranscriptLines
{
    /// <summary>
    /// The lines in whisper's JSON, in order. Offsets in milliseconds are used when they're there, else the
    /// "00:01:02,500" timestamps. Empty lines are skipped. Anything that can't be read gives no lines, never an error.
    /// </summary>
    public static IReadOnlyList<TranscriptLine> Parse(string? whisperJson)
    {
        if (string.IsNullOrWhiteSpace(whisperJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(whisperJson, new JsonDocumentOptions { AllowTrailingCommas = true });
            if (!document.RootElement.TryGetProperty("transcription", out var segments) || segments.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var lines = new List<TranscriptLine>();
            foreach (var segment in segments.EnumerateArray())
            {
                var text = segment.TryGetProperty("text", out var said) && said.ValueKind == JsonValueKind.String ? said.GetString()!.Trim() : "";
                if (text.Length == 0 || ReadTimes(segment) is not { } times)
                {
                    continue;
                }

                lines.Add(new TranscriptLine(times.Start, times.End < times.Start ? times.Start : times.End, text));
            }

            return lines.OrderBy(line => line.Start).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Stamp(TimeSpan at)
    {
        if (at < TimeSpan.Zero)
        {
            at = TimeSpan.Zero;
        }

        return at.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)at.TotalHours}:{at.Minutes:00}:{at.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{at.Minutes}:{at.Seconds:00}");
    }

    /// <summary>
    /// Where to start playing for a line: its start, kept inside the recording. Whisper's last line can end a
    /// little past the audio, so a start at or past the end goes back a moment.
    /// </summary>
    public static TimeSpan SeekTarget(TranscriptLine line, TimeSpan? audioLength)
    {
        var at = line.Start < TimeSpan.Zero ? TimeSpan.Zero : line.Start;
        if (audioLength is { } length && length > TimeSpan.Zero && at >= length)
        {
            at = length - TimeSpan.FromSeconds(1);
            if (at < TimeSpan.Zero)
            {
                at = TimeSpan.Zero;
            }
        }

        return at;
    }

    /// <summary>The line being said at <paramref name="position"/>: the last one started by then. -1 before the first.</summary>
    public static int LineAt(IReadOnlyList<TranscriptLine> lines, TimeSpan position)
    {
        int low = 0, high = lines.Count - 1, found = -1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (lines[middle].Start <= position)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found;
    }

    private static (TimeSpan Start, TimeSpan End)? ReadTimes(JsonElement segment)
    {
        if (segment.TryGetProperty("offsets", out var offsets)
            && ReadMilliseconds(offsets, "from") is { } from)
        {
            return (from, ReadMilliseconds(offsets, "to") ?? from);
        }

        if (segment.TryGetProperty("timestamps", out var stamps)
            && ReadClock(stamps, "from") is { } start)
        {
            return (start, ReadClock(stamps, "to") ?? start);
        }

        return null;
    }

    private static TimeSpan? ReadMilliseconds(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var ms) && ms >= 0
            ? TimeSpan.FromMilliseconds(ms)
            : null;

    /// <summary>Whisper writes "00:01:02,500", with a comma before the milliseconds.</summary>
    private static TimeSpan? ReadClock(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && TimeSpan.TryParseExact(value.GetString()!.Replace(',', '.'), @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out var at)
            ? at
            : null;
}

/// <summary>
/// Whether a transcript can be trusted enough to let its audio go: it has words, and its timestamps reach most of
/// the way through the audio. A transcript that stops well before the end may have missed part of the lecture.
/// </summary>
public static class TranscriptCheck
{
    /// <summary>How far through the audio the last line must reach.</summary>
    public const double MostOfTheAudio = 0.75;

    public static bool Passes(string transcript, string? whisperJson, double? audioSeconds)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return false;
        }

        // With no length or no timestamps there's nothing to measure, so words are enough.
        var lines = TranscriptLines.Parse(whisperJson);
        if (audioSeconds is not { } length || length <= 0 || lines.Count == 0)
        {
            return true;
        }

        return lines.Max(line => line.End).TotalSeconds >= length * MostOfTheAudio;
    }
}
