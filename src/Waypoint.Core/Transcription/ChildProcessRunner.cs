using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace Waypoint.Core.Transcription;

/// <summary>
/// Runs whisper-cli with no window, at below normal priority so the PC stays responsive. Canceling stops the
/// process and anything it started.
/// </summary>
public sealed class ChildProcessRunner : IProcessRunner
{
    private const int TailLines = 12;

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string> onLine, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(spec.FileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var argument in spec.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var tail = new Queue<string>();
        void Take(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (tail)
            {
                tail.Enqueue(line);
                if (tail.Count > TailLines)
                {
                    tail.Dequeue();
                }
            }

            onLine(line);
        }

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Take(e.Data);
        process.ErrorDataReceived += (_, e) => Take(e.Data);
        process.Start();
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            // It may have ended already. Running at normal priority is still fine.
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using (cancellationToken.Register(() => Stop(process)))
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (tail)
        {
            return new ProcessResult(process.ExitCode, string.Join('\n', tail));
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }
}

/// <summary>
/// Downloads with HTTP, carrying on from the end of a part file with a Range request. When the server sends the
/// whole file instead, the part file is started over, so the bytes are never mixed up.
/// </summary>
public sealed class HttpFileDownloader(HttpClient client) : IFileDownloader
{
    public async Task DownloadAsync(Uri url, string partPath, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var have = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0)
        {
            request.Headers.Range = new RangeHeaderValue(have, null);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Already complete, or the part file is bad. The hash check decides which.
            return;
        }

        response.EnsureSuccessStatusCode();
        var resuming = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resuming)
        {
            have = 0;
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = new FileStream(partPath, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        var buffer = new byte[1 << 20];
        var lastReport = 0L;
        int read;
        while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            have += read;
            if (have - lastReport >= 4 << 20)
            {
                lastReport = have;
                progress?.Report(have);
            }
        }

        progress?.Report(have);
    }
}
