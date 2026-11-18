using System.Globalization;
using System.Text.Json;

namespace Waypoint.Core.Discord;

/// <summary>Reading Discord's JSON for a message: its text, files, card, sender and time.</summary>
public sealed partial class DiscordBridge
{
    private static JsonElement WithChannel(JsonElement message, string channel)
    {
        var fields = message.EnumerateObject().ToDictionary(field => field.Name, field => (object)field.Value.Clone());
        fields["channel_id"] = channel;
        return JsonSerializer.SerializeToElement(fields);
    }

    private static DiscordMessage? ReadMessage(JsonElement message)
    {
        var text = Text(message, "content") ?? "";
        var attachments = new List<DiscordAttachment>();
        if (message.TryGetProperty("attachments", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in files.EnumerateArray())
            {
                if (Text(file, "id") is { } id && Text(file, "filename") is { } name && Text(file, "url") is { } url)
                {
                    var size = file.TryGetProperty("size", out var bytes) && bytes.ValueKind == JsonValueKind.Number ? bytes.GetInt64() : 0;
                    var seconds = file.TryGetProperty("duration_secs", out var duration) && duration.ValueKind == JsonValueKind.Number ? duration.GetDouble() : (double?)null;
                    attachments.Add(new DiscordAttachment(id, name, size, url, Text(file, "content_type"), seconds));
                }
            }
        }

        if (text.Trim().Length == 0 && attachments.Count == 0)
        {
            return null;
        }

        var replyTo = message.TryGetProperty("message_reference", out var reference) ? Text(reference, "message_id") : null;
        var replyToText = replyTo is not null && message.TryGetProperty("referenced_message", out var referenced) ? CardText(referenced) : null;
        return new DiscordMessage(Text(message, "id")!, text, SentAt(message), attachments, replyTo, replyToText);
    }

    /// <summary>Everything a message's card says, title, text and fields, one per line.</summary>
    private static string? CardText(JsonElement message)
    {
        if (FirstEmbed(message) is not { } embed)
        {
            return Text(message, "content");
        }

        var lines = new List<string?> { Text(embed, "title"), Text(embed, "description") };
        if (embed.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
        {
            lines.AddRange(fields.EnumerateArray().Select(field => $"{Text(field, "name")}: {Text(field, "value")}"));
        }

        return string.Join('\n', lines.Where(line => !string.IsNullOrEmpty(line)));
    }

    /// <summary>
    /// Something the owner typed: an ordinary message or a reply (types 0 and 19). Discord's own notices, such as
    /// "changed the post title", carry the owner as their author too, but aren't anything they wrote.
    /// </summary>
    private bool IsFromOwner(JsonElement message) =>
        message.TryGetProperty("author", out var author)
        && Text(author, "id") == settings.OwnerUserId
        && !(author.TryGetProperty("bot", out var bot) && bot.ValueKind == JsonValueKind.True)
        && (!message.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.Number || type.GetInt32() is 0 or 19);

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset SentAt(JsonElement message) =>
        DateTimeOffset.TryParse(Text(message, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var sent) ? sent : DateTimeOffset.Now;
}
