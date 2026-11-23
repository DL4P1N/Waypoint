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

    /// <summary>The file here couldn't be opened, such as one gone or in use. The server wasn't asked.</summary>
    NotRead,
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

        // A file here that can't be opened is a problem here, not the server being away.
        FileStream file;
        try
        {
            file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return FileSendResult.NotRead;
        }

        await using var opened = file;
        using var change = Changing();
        try
        {
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

    /// <summary>
    /// Saves the file at <paramref name="path"/> to <paramref name="target"/>. False if there's none, the server is
    /// away, or it couldn't be saved here, such as on a full disk. Only the server not answering counts as it being away.
    /// </summary>
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

            await using var from = await response.Content.ReadAsStreamAsync(cancellationToken);
            var to = Here(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
                return new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            });
            await using (to)
            {
                // Read from the server and written here in turn, so a problem writing here is told apart from the server.
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await from.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await HereAsync(() => to.WriteAsync(buffer.AsMemory(0, read), cancellationToken).AsTask());
                }

                await HereAsync(() => to.FlushAsync(cancellationToken));
            }

            Here(() =>
            {
                File.Move(part, target, overwrite: true);
                return true;
            });
            return true;
        }
        catch (LocalFileProblem)
        {
            return false;
        }
        catch (Exception exception) when (IsConnectionProblem(exception, cancellationToken))
        {
            MarkOffline();
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(part))
                {
                    File.Delete(part);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left for the next fetch of the same file, which writes over it.
            }
        }
    }

    /// <summary>A file here that couldn't be written, as opposed to the server not answering.</summary>
    private sealed class LocalFileProblem(Exception inner) : Exception(inner.Message, inner);

    private static T Here<T>(Func<T> work)
    {
        try
        {
            return work();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LocalFileProblem(exception);
        }
    }

    private static async Task HereAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LocalFileProblem(exception);
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
