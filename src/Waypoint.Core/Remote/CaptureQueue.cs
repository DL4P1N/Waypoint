using System.Text.Json;

namespace Waypoint.Core.Remote;

/// <summary>
/// Captures typed on the PC in server mode wait here, in a file, until the server takes them. Typing is never lost:
/// not when the server is away, and not when the app closes before it's back. They go in the order they were typed.
/// </summary>
public sealed class CaptureQueue(string path)
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sending = new(1, 1);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return Read().Count;
            }
        }
    }

    public CaptureRequest Add(string text, DateTimeOffset writtenAt, string? fileName = null)
    {
        var capture = new CaptureRequest(Guid.NewGuid().ToString("N"), text, writtenAt, fileName);
        lock (_gate)
        {
            var waiting = Read();
            waiting.Add(capture);
            Write(waiting);
        }

        return capture;
    }

    /// <summary>Where captures the server turned down for good are kept, so they can still be found by hand.</summary>
    public string RefusedPath => $"{path}.refused.json";

    public int RefusedCount
    {
        get
        {
            lock (_gate)
            {
                return Read(RefusedPath).Count;
            }
        }
    }

    /// <summary>A plain text copy of what was turned down, for opening in a text editor.</summary>
    public const string RefusedCopyName = "Typed but not saved on the server.txt";

    /// <summary>What to show while any capture has been turned down. Null when none has. Open shows them.</summary>
    public string? Refused => RefusedCount switch
    {
        0 => null,
        1 => "Something you typed couldn't be saved on the server. It's kept on this PC.",
        var count => $"{count} things you typed couldn't be saved on the server. They're kept on this PC.",
    };

    /// <summary>
    /// Writes what was turned down as plain text beside the queue and returns where, so it can be opened and copied
    /// back in. Null when nothing was. The kept file itself stays as it is, so nothing is lost.
    /// </summary>
    public string? WriteRefusedCopy()
    {
        List<CaptureRequest> refused;
        lock (_gate)
        {
            refused = Read(RefusedPath);
        }

        if (refused.Count == 0)
        {
            return null;
        }

        return KeptCopy.Write(
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "", RefusedCopyName),
            refused.Select(capture => (
                $"Typed {KeptCopy.When(capture.WrittenAt.LocalDateTime)}{(capture.FileName is { } file ? $", from {file}" : "")}",
                capture.Text)));
    }

    /// <summary>
    /// Sends what's waiting, oldest first, and stops at the first one that doesn't go so the order holds. One the
    /// server turns down for good is set aside instead, so it can't hold up everything typed after it.
    /// </summary>
    public async Task<int> SendAsync(Func<CaptureRequest, Task<bool>> send, CancellationToken cancellationToken = default)
    {
        await _sending.WaitAsync(cancellationToken);
        try
        {
            var sent = 0;
            while (true)
            {
                CaptureRequest? next;
                lock (_gate)
                {
                    next = Read().FirstOrDefault();
                }

                if (next is null)
                {
                    return sent;
                }

                var refused = false;
                try
                {
                    if (!await send(next))
                    {
                        return sent;
                    }
                }
                catch (CaptureRefusedException)
                {
                    refused = true;
                }

                lock (_gate)
                {
                    if (refused)
                    {
                        var kept = Read(RefusedPath);
                        kept.Add(next);
                        Write(RefusedPath, kept);
                    }

                    Write(Read().Where(capture => capture.Id != next.Id).ToList());
                }

                sent += refused ? 0 : 1;
            }
        }
        finally
        {
            _sending.Release();
        }
    }

    private List<CaptureRequest> Read() => Read(path);

    private void Write(List<CaptureRequest> waiting) => Write(path, waiting);

    private static List<CaptureRequest> Read(string file)
    {
        if (!File.Exists(file))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<CaptureRequest>>(File.ReadAllText(file), RemoteJson.Options) ?? [];
        }
        catch (JsonException)
        {
            // Kept aside rather than thrown away, so whatever was typed can still be found by hand.
            File.Move(file, $"{file}.unreadable-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
            return [];
        }
    }

    private static void Write(string file, List<CaptureRequest> waiting)
    {
        var temporary = $"{file}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(waiting, RemoteJson.Options));
        File.Move(temporary, file, overwrite: true);
    }
}
