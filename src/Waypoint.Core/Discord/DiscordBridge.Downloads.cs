namespace Waypoint.Core.Discord;

/// <summary>Downloading a message's files, following only Discord's own file links.</summary>
public sealed partial class DiscordBridge
{
    private static readonly string[] DownloadHosts = ["cdn.discordapp.com", "media.discordapp.net"];

    private const int MaxDownloadRedirects = 3;

    /// <summary>
    /// The client attachments are downloaded with. It shouldn't follow redirects itself, so each hop is checked against
    /// Discord's file hosts first. Without one, the main client is used and where it ended up is checked.
    /// </summary>
    public HttpClient? Downloads { get; init; }

    /// <summary>
    /// Saves an attachment, refusing anything bigger than <paramref name="maxBytes"/>. Only Discord's own file
    /// links are followed. Returns why it couldn't, or null once it's saved.
    /// </summary>
    public async Task<string?> DownloadAsync(DiscordAttachment attachment, string path, long maxBytes, CancellationToken cancellationToken = default)
    {
        if (attachment.Size > maxBytes)
        {
            return $"{attachment.FileName} is too big to read. The limit is {maxBytes / (1024 * 1024)} MB.";
        }

        var notDiscord = $"{attachment.FileName} didn't come from Discord, so it wasn't opened.";
        if (!Uri.TryCreate(attachment.Url, UriKind.Absolute, out var url) || !IsDiscordFile(url))
        {
            return notDiscord;
        }

        try
        {
            // Redirects are followed here, one at a time, so each place it's sent is checked before it's asked.
            // A client that follows them itself is checked by where it ended up.
            var client = Downloads ?? http;
            HttpResponseMessage response;
            for (var hop = 0; ; hop++)
            {
                response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var landed = response.RequestMessage?.RequestUri ?? url;
                if (!IsDiscordFile(landed))
                {
                    response.Dispose();
                    return notDiscord;
                }

                if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is not { } location)
                {
                    break;
                }

                response.Dispose();
                url = location.IsAbsoluteUri ? location : new Uri(landed, location);
                if (!IsDiscordFile(url))
                {
                    return notDiscord;
                }

                if (hop >= MaxDownloadRedirects)
                {
                    return $"Couldn't download {attachment.FileName}. It was sent on too many times.";
                }
            }

            using var owned = response;
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var target = File.Create(path))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        break;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                if (total <= maxBytes)
                {
                    return null;
                }
            }

            File.Delete(path);
            return $"{attachment.FileName} is too big to read. The limit is {maxBytes / (1024 * 1024)} MB.";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            LastError = exception.Message;
            return $"Couldn't download {attachment.FileName}. Try sending it again.";
        }
    }

    private static bool IsDiscordFile(Uri url) =>
        url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && DownloadHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase);
}
