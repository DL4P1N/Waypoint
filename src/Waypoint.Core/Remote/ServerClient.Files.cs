using System.Net;
using System.Net.Http.Headers;

namespace Waypoint.Core.Remote;

/// <summary>What came of sending a file to the server.</summary>
public enum FileSendResult
{
    Sent,

    /// <summary>The server keeps files only up to a size, and this one is bigger.</summary>
    TooLarge,

    /// <summary>The server couldn't be reached, or didn't accept this PC. The file stays where it was.</summary>
    Away,
}

/// <summary>
/// Files kept with notes go up and down whole, which can take longer than a request is allowed to wait, so they
/// use a client of their own with a longer wait.
/// </summary>
public sealed partial class ServerClient
{
    public static readonly TimeSpan FileTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The client for files. Without one, files use the same client as everything else.</summary>
    public HttpClient? FileHttp { get; set; }

    public async Task<FileSendResult> UploadAsync(string path, string filePath, CancellationToken cancellationToken = default)
    {
        if (!IsOnline)
        {
            return FileSendResult.Away;
        }

        try
        {
            await using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
            using var request = FileRequest(HttpMethod.Put, path);
            request.Content = new StreamContent(file, 1 << 20);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentLength = file.Length;
            using var response = await (FileHttp ?? _http).SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            {
                return FileSendResult.TooLarge;
            }

            return Settle(response) ? FileSendResult.Sent : FileSendResult.Away;
        }
        catch (Exception exception) when (IsConnectionProblem(exception, cancellationToken))
        {
            MarkOffline();
            return FileSendResult.Away;
        }
    }

    /// <summary>Saves the file at <paramref name="path"/> to <paramref name="target"/>. False if there's none or the server is away.</summary>
    public async Task<bool> DownloadAsync(string path, string target, CancellationToken cancellationToken = default)
    {
        if (!IsOnline)
        {
            return false;
        }

        var part = target + ".part";
        try
        {
            using var request = FileRequest(HttpMethod.Get, path);
            using var response = await (FileHttp ?? _http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                LastContact = DateTime.Now;
                return false;
            }

            if (!Settle(response))
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            await using (var to = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                await response.Content.CopyToAsync(to, cancellationToken);
            }

            File.Move(part, target, overwrite: true);
            return true;
        }
        catch (Exception exception) when (IsConnectionProblem(exception, cancellationToken))
        {
            MarkOffline();
            return false;
        }
        finally
        {
            if (File.Exists(part))
            {
                File.Delete(part);
            }
        }
    }

    /// <summary>The file client may be a different one, so the pairing token goes on each request.</summary>
    private HttpRequestMessage FileRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = _http.DefaultRequestHeaders.Authorization;
        request.Headers.Add(ClientHeader, ClientId);
        return request;
    }
}
