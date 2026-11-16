using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Waypoint.Core.Discord;

/// <summary>What kind of copy runs Discord. The server wins over a PC, so a PC left in Local mode steps aside.</summary>
public enum DiscordRunner
{
    Pc,
    Server,
}

/// <summary>One copy's note, pinned in the alerts channel. CheckedAt is when that copy last said it's still running.</summary>
public sealed record ClaimNote(string MessageId, string Host, DiscordRunner Runner, DateTimeOffset CheckedAt);

public enum ClaimState
{
    /// <summary>This copy runs Discord and the alerts.</summary>
    Here,

    /// <summary>Another copy does, named in RunningOn, so this one sends nothing.</summary>
    Elsewhere,

    /// <summary>Discord couldn't be asked, so nothing changes.</summary>
    Unknown,
}

public sealed record ClaimCheck(ClaimState State, string? RunningOn = null, string? Problem = null);

/// <summary>
/// Makes sure only one copy of Waypoint uses the bot at a time, so nothing is sent twice. The copy that runs Discord
/// keeps a pinned note in the alerts channel saying where it runs, and updates it every few minutes. Every copy reads
/// the pins first: one that finds a fresh note from another copy sends nothing until that note goes stale. The server
/// wins over a PC, and otherwise the copy that was there first keeps it. A note from this same machine and kind is
/// this copy's own, from before a restart, so a restart never locks it out.
/// </summary>
public sealed class DiscordClaim(HttpClient http, DiscordSettings settings, string host, DiscordRunner runner, Func<DateTimeOffset> clock)
{
    /// <summary>How often the note is read and updated.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(2);

    /// <summary>A note not updated for this long belongs to a copy that has stopped, so another may take over.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(7);

    private const string ApiBase = "https://discord.com/api/v10";
    private const string Marker = "waypoint-claim";
    private const string UserAgent = "DiscordBot (waypoint, 1.0)";

    // Sent without a sound on the phone, and nothing in it may ping anyone.
    private const int SilentMessage = 4096;

    private readonly SemaphoreSlim _checking = new(1, 1);

    // This copy's note, remembered in case it couldn't be pinned and so can't be found among the pins.
    private string? _lastId;

    public string Host => host;

    public DiscordRunner Runner => runner;

    /// <summary>Reads the pinned notes, then updates this copy's own note or steps aside. Checks never overlap.</summary>
    public async Task<ClaimCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        if ((settings.AlertsChannelId ?? settings.CaptureChannelId) is not { } channel)
        {
            return new ClaimCheck(ClaimState.Here);
        }

        await _checking.WaitAsync(cancellationToken);
        try
        {
            var notes = await ReadNotesAsync(channel, cancellationToken);
            if (notes is null)
            {
                return new ClaimCheck(ClaimState.Unknown, Problem: "Waypoint couldn't read the pinned messages in the alerts channel, so it can't tell whether another copy is sending too. Give the bot the Read Message History and Pin Messages permissions there.");
            }

            var now = clock();
            var mine = notes.Where(IsMine).OrderBy(note => Id(note.MessageId)).ToList();
            if (RunsElsewhere(notes, host, runner, now) is { } other)
            {
                foreach (var id in mine.Select(note => note.MessageId).Append(_lastId).OfType<string>().Distinct())
                {
                    await DeleteAsync(channel, id, cancellationToken);
                }

                _lastId = null;

                return new ClaimCheck(ClaimState.Elsewhere, other);
            }

            // Old notes from copies that stopped, and any spare of this copy's own, are tidied away.
            foreach (var note in notes.Where(note => (!IsMine(note) && !IsFresh(note, now)) || (mine.Count > 1 && IsMine(note) && note != mine[0])))
            {
                await DeleteAsync(channel, note.MessageId, cancellationToken);
            }

            var problem = await WriteNoteAsync(channel, mine.FirstOrDefault()?.MessageId, now, cancellationToken);
            return new ClaimCheck(ClaimState.Here, Problem: problem);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new ClaimCheck(ClaimState.Unknown, Problem: $"Waypoint couldn't check whether another copy is sending: {exception.Message}");
        }
        finally
        {
            _checking.Release();
        }
    }

    /// <summary>
    /// The host of the copy that should run Discord instead of this one, or null when this one should. Each copy is
    /// ranked: the server before a PC, then the oldest note first. This copy, with no note yet, comes last of its kind.
    /// </summary>
    public static string? RunsElsewhere(IReadOnlyList<ClaimNote> notes, string host, DiscordRunner runner, DateTimeOffset now)
    {
        bool Mine(ClaimNote note) => note.Runner == runner && string.Equals(note.Host, host, StringComparison.OrdinalIgnoreCase);
        static (int, ulong) Rank(DiscordRunner kind, ulong id) => (kind == DiscordRunner.Server ? 0 : 1, id);

        var mine = notes.Where(Mine).Select(note => Id(note.MessageId)).DefaultIfEmpty(ulong.MaxValue).Min();
        var best = notes
            .Where(note => !Mine(note) && IsFresh(note, now))
            .OrderBy(note => Rank(note.Runner, Id(note.MessageId)))
            .FirstOrDefault();
        return best is not null && Rank(best.Runner, Id(best.MessageId)).CompareTo(Rank(runner, mine)) < 0 ? best.Host : null;
    }

    /// <summary>The note's text. The last line is what copies read, and the rest is for whoever opens the pins.</summary>
    public static string NoteText(string host, DiscordRunner runner, DateTimeOffset checkedAt) =>
        $"Waypoint is running on {host}, {(runner == DiscordRunner.Server ? "the server" : "a PC")}. Only this copy sends alerts and reads messages here. "
        + "If it stops for a few minutes, another copy can take over.\n"
        + $"-# {Marker} runner={(runner == DiscordRunner.Server ? "server" : "pc")} host={Uri.EscapeDataString(host)} at={checkedAt.ToUnixTimeSeconds()}";

    /// <summary>Reads a note back from a message's text, or null when the message isn't one.</summary>
    public static ClaimNote? ReadNote(string messageId, string? content)
    {
        var line = content?.Split('\n').Select(part => part.Trim()).LastOrDefault(part => part.Contains(Marker + " ", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var values = line[(line.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .GroupBy(pair => pair[0])
            .ToDictionary(group => group.Key, group => group.First()[1]);
        if (!values.TryGetValue("host", out var name) || !values.TryGetValue("at", out var at) || !values.TryGetValue("runner", out var kind)
            || !long.TryParse(at, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || kind is not ("server" or "pc"))
        {
            return null;
        }

        return new ClaimNote(messageId, Uri.UnescapeDataString(name), kind == "server" ? DiscordRunner.Server : DiscordRunner.Pc, DateTimeOffset.FromUnixTimeSeconds(seconds));
    }

    private bool IsMine(ClaimNote note) => note.Runner == runner && string.Equals(note.Host, host, StringComparison.OrdinalIgnoreCase);

    private static bool IsFresh(ClaimNote note, DateTimeOffset now) => (now - note.CheckedAt).Duration() < Stale;

    private static ulong Id(string messageId) => ulong.TryParse(messageId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : ulong.MaxValue;

    /// <summary>The notes among the channel's pins, or null when the pins can't be read. The older address is tried if the newer one isn't there.</summary>
    private async Task<IReadOnlyList<ClaimNote>?> ReadNotesAsync(string channel, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(Request(HttpMethod.Get, $"{ApiBase}/channels/{channel}/messages/pins?limit=50"), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            using var older = await http.SendAsync(Request(HttpMethod.Get, $"{ApiBase}/channels/{channel}/pins"), cancellationToken);
            return older.IsSuccessStatusCode ? Notes(await older.Content.ReadAsStringAsync(cancellationToken)) : null;
        }

        return response.IsSuccessStatusCode ? Notes(await response.Content.ReadAsStringAsync(cancellationToken)) : null;
    }

    /// <summary>The newer address answers with items that each hold a message, and the older one with the messages themselves.</summary>
    private static List<ClaimNote> Notes(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var messages = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var items)
            ? items.EnumerateArray().Select(item => item.TryGetProperty("message", out var message) ? message : item)
            : root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : Enumerable.Empty<JsonElement>();
        return messages
            .Select(message => ReadNote(
                message.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString() : null))
            .OfType<ClaimNote>()
            .ToList();
    }

    /// <summary>
    /// Updates this copy's note, or sends a new one, and pins it if it isn't pinned yet. A note that couldn't be
    /// pinned is kept and updated, and pinning is tried again each time. Returns why it couldn't, or null.
    /// </summary>
    private async Task<string?> WriteNoteAsync(string channel, string? pinnedId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var text = NoteText(host, runner, now);
        var id = pinnedId ?? _lastId;
        if (id is not null)
        {
            var edit = Request(HttpMethod.Patch, $"{ApiBase}/channels/{channel}/messages/{id}");
            edit.Content = JsonContent.Create(new { content = text, allowed_mentions = new { parse = Array.Empty<string>() } });
            using var edited = await http.SendAsync(edit, cancellationToken);
            if (edited.StatusCode == HttpStatusCode.NotFound)
            {
                id = null;
            }
            else if (!edited.IsSuccessStatusCode)
            {
                return $"Waypoint couldn't update its note in the alerts channel ({(int)edited.StatusCode}).";
            }
        }

        if (id is null)
        {
            var send = Request(HttpMethod.Post, $"{ApiBase}/channels/{channel}/messages");
            send.Content = JsonContent.Create(new { content = text, flags = SilentMessage, allowed_mentions = new { parse = Array.Empty<string>() } });
            using var sent = await http.SendAsync(send, cancellationToken);
            if (!sent.IsSuccessStatusCode)
            {
                return $"Waypoint couldn't post its note in the alerts channel ({(int)sent.StatusCode}).";
            }

            using var document = JsonDocument.Parse(await sent.Content.ReadAsStringAsync(cancellationToken));
            id = document.RootElement.GetProperty("id").GetString()!;
        }

        _lastId = id;
        if (pinnedId is not null)
        {
            return null;
        }

        foreach (var url in new[] { $"{ApiBase}/channels/{channel}/messages/pins/{id}", $"{ApiBase}/channels/{channel}/pins/{id}" })
        {
            using var pinned = await http.SendAsync(Request(HttpMethod.Put, url), cancellationToken);
            if (pinned.IsSuccessStatusCode)
            {
                return null;
            }

            if (pinned.StatusCode != HttpStatusCode.NotFound)
            {
                break;
            }
        }

        // Unpinned, other copies can't find the note, so the check doesn't work until the bot may pin.
        return "Waypoint couldn't pin its note in the alerts channel, so it can't make sure only one copy sends. Give the bot the Pin Messages permission there.";
    }

    private async Task DeleteAsync(string channel, string messageId, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(Request(HttpMethod.Delete, $"{ApiBase}/channels/{channel}/messages/{messageId}"), cancellationToken);
    }

    private HttpRequestMessage Request(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", settings.BotToken);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        return request;
    }
}
