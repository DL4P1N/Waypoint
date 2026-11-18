using System.Globalization;
using System.Text.Json;

namespace Waypoint.Core.Discord;

/// <summary>Finding a post that went through before Waypoint could note it, so a post noted beforehand is never made twice.</summary>
public sealed partial class DiscordBridge
{
    /// <summary>
    /// Looks in a channel for a post of the bot's from a try that went through before Waypoint could note it, made
    /// since <paramref name="since"/> and carrying this nonce or showing this card. Read is false when the channel
    /// couldn't be read, so it isn't known.
    /// </summary>
    /// <param name="sameTitleOnly">For a card whose words change, such as a pinned view, the title alone is enough.</param>
    /// <param name="known">Messages already kept as something else, so one that looks the same is never taken.</param>
    public Task<(bool Read, string? MessageId)> FindSentAsync(string channel, DateTime since, string nonce, DiscordCard card, bool sameTitleOnly = false, IReadOnlySet<string>? known = null) =>
        FindSentAfterAsync(channel, SnowflakeAt(since - SentLookBack), nonce, card, known ?? new HashSet<string>(), sameTitleOnly);

    /// <summary>
    /// The same, among the messages after <paramref name="after"/>, leaving out any already <paramref name="known"/>.
    /// </summary>
    public async Task<(bool Read, string? MessageId)> FindSentAfterAsync(string channel, string after, string nonce, DiscordCard card, IReadOnlySet<string> known, bool sameTitleOnly = false)
    {
        try
        {
            using var response = await SendWithLimitsAsync(() => NewRequest(HttpMethod.Get, $"{ApiBase}/channels/{channel}/messages?limit={MaxFetched}&after={after}"));
            if (!response.IsSuccessStatusCode)
            {
                return (false, null);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return (false, null);
            }

            var found = document.RootElement.EnumerateArray()
                .Where(message => IsFromBot(message) && (NonceOf(message) == nonce || Shows(message, card, sameTitleOnly)))
                .Select(message => Text(message, "id"))
                .FirstOrDefault(id => id is not null && !known.Contains(id));
            return (true, found);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            LastError = exception.Message;
            return (false, null);
        }
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

    private static bool IsFromBot(JsonElement message) =>
        message.TryGetProperty("author", out var author) && author.TryGetProperty("bot", out var bot) && bot.ValueKind == JsonValueKind.True;

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
