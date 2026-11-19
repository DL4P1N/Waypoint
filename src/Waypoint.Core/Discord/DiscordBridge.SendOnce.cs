using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Waypoint.Core.Discord;

/// <summary>What looking for an earlier post found: it, nothing, or nothing known because the channel couldn't be read.</summary>
public enum SentLookup
{
    Found,
    NotFound,

    /// <summary>The channel, or who the bot is, couldn't be read, so the post may or may not be there.</summary>
    Unknown,

    /// <summary>
    /// Discord turned the read away for good: the channel is gone, the bot may not see it, or its token no longer
    /// works. A post there would fail too, so whether one is there isn't known either.
    /// </summary>
    Refused,
}

/// <summary>The answer to looking for an earlier post, with the message when it was found.</summary>
public readonly record struct SentSearch(SentLookup Result, string? MessageId)
{
    public static readonly SentSearch NotKnown = new(SentLookup.Unknown, null);

    public static readonly SentSearch TurnedAway = new(SentLookup.Refused, null);

    /// <summary>Unknown or refused: whether the post is there can't be told, so it mustn't be sent again now.</summary>
    public bool CantTell => Result is SentLookup.Unknown or SentLookup.Refused;

    /// <summary>The answer for a read Discord answered with this status instead of the messages.</summary>
    internal static SentSearch Failed(HttpStatusCode status) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound ? TurnedAway : NotKnown;
}

/// <summary>Finding a post that went through before Waypoint could note it, so a post noted beforehand is never made twice.</summary>
public sealed partial class DiscordBridge
{
    /// <summary>
    /// Looks in a channel for a post of this bot's from a try that went through before Waypoint could note it, made
    /// since <paramref name="since"/> and carrying this nonce or showing this card. When the channel couldn't be read
    /// it isn't known, and the post mustn't be sent again until it is.
    /// </summary>
    /// <param name="sameTitleOnly">For a card whose words change, such as a pinned view, the title alone is enough.</param>
    /// <param name="known">Messages already kept as something else, so one that looks the same is never taken.</param>
    public Task<SentSearch> FindSentAsync(string channel, DateTime since, string nonce, DiscordCard card, bool sameTitleOnly = false, IReadOnlySet<string>? known = null) =>
        FindSentAfterAsync(channel, SnowflakeAt(since - SentLookBack), nonce, card, known ?? new HashSet<string>(), sameTitleOnly);

    /// <summary>
    /// The same, among the messages after <paramref name="after"/>, leaving out any already <paramref name="known"/>.
    /// A page at a time, oldest first, up to <see cref="MaxSentPages"/> pages.
    /// </summary>
    public async Task<SentSearch> FindSentAfterAsync(string channel, string after, string nonce, DiscordCard card, IReadOnlySet<string> known, bool sameTitleOnly = false)
    {
        try
        {
            // Only this bot's own posts count, so one from another bot that looks the same is never taken.
            var (self, refused) = await OwnUserIdAsync();
            if (self is null)
            {
                return refused ? SentSearch.TurnedAway : SentSearch.NotKnown;
            }

            for (var page = 0; page < MaxSentPages; page++)
            {
                using var response = await SendWithLimitsAsync(() => NewRequest(HttpMethod.Get, $"{ApiBase}/channels/{channel}/messages?limit={SentPageSize}&after={after}"));
                if (!response.IsSuccessStatusCode)
                {
                    return SentSearch.Failed(response.StatusCode);
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return SentSearch.NotKnown;
                }

                var messages = document.RootElement.EnumerateArray()
                    .Where(message => Text(message, "id") is not null)
                    .OrderBy(message => SnowflakeOf(message) ?? 0)
                    .ToList();
                var found = messages
                    .Where(message => IsFrom(message, self) && (NonceOf(message) == nonce || Shows(message, card, sameTitleOnly)))
                    .Select(message => Text(message, "id")!)
                    .FirstOrDefault(id => !known.Contains(id));
                if (found is not null)
                {
                    return new SentSearch(SentLookup.Found, found);
                }

                var next = messages.Select(SnowflakeOf).Max() is { } newest ? newest.ToString(CultureInfo.InvariantCulture) : after;
                if (document.RootElement.GetArrayLength() < SentPageSize || next == after)
                {
                    return new SentSearch(SentLookup.NotFound, null);
                }

                after = next;
            }

            // Read oldest first from when it was noted, so a post that went through is among the first. One not found
            // in this many isn't there, and a busy channel never keeps it from going out.
            return new SentSearch(SentLookup.NotFound, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            LastError = exception.Message;
            return SentSearch.NotKnown;
        }
    }

    /// <summary>Messages read in one page when looking for a post, the most Discord gives.</summary>
    private const int SentPageSize = 100;

    /// <summary>Pages read at most when looking for a post, so a busy channel can't be read forever.</summary>
    private const int MaxSentPages = 5;

    /// <summary>
    /// This bot's own user ID: the one Discord said on connecting, or else asked for once. Null when it can't be read,
    /// with Refused when Discord says the token no longer works.
    /// </summary>
    private async Task<(string? Id, bool Refused)> OwnUserIdAsync()
    {
        if (BotUserId is { } known)
        {
            return (known, false);
        }

        using var response = await SendWithLimitsAsync(() => NewRequest(HttpMethod.Get, $"{ApiBase}/users/@me"));
        if (!response.IsSuccessStatusCode)
        {
            return (null, response.StatusCode == HttpStatusCode.Unauthorized);
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (document.RootElement.ValueKind != JsonValueKind.Object || Text(document.RootElement, "id") is not { } id)
        {
            return (null, false);
        }

        BotUserId ??= id;
        return (BotUserId, false);
    }

    /// <summary>How far before a post's note to look, in case this PC's clock runs a little ahead of Discord's.</summary>
    private static readonly TimeSpan SentLookBack = TimeSpan.FromMinutes(1);

    private static readonly DateTime DiscordEpoch = new(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The smallest message ID Discord could give at this time, so messages after it are the ones made since.</summary>
    public static string SnowflakeAt(DateTime time)
    {
        var milliseconds = (time.ToUniversalTime() - DiscordEpoch).TotalMilliseconds;
        return milliseconds <= 0 ? "0" : (((ulong)milliseconds) << 22).ToString(CultureInfo.InvariantCulture);
    }

    private static ulong? SnowflakeOf(JsonElement message) =>
        ulong.TryParse(Text(message, "id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    private static bool IsFrom(JsonElement message, string userId) =>
        message.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object && Text(author, "id") == userId;

    private static string? NonceOf(JsonElement message) =>
        message.TryGetProperty("nonce", out var nonce) ? nonce.ValueKind switch
        {
            JsonValueKind.String => nonce.GetString(),
            JsonValueKind.Number => nonce.GetRawText(),
            _ => null,
        } : null;

    /// <summary>Whether a message shows this card's title and words, or only its title, the way Waypoint posts it.</summary>
    private static bool Shows(JsonElement message, DiscordCard card, bool titleOnly) =>
        message.TryGetProperty("embeds", out var embeds) && embeds.ValueKind == JsonValueKind.Array
        && embeds.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } embed
        && (Text(embed, "title") ?? "") == (card.Title ?? "")
        && (titleOnly || (Text(embed, "description") ?? "") == (card.Description ?? ""));
}
