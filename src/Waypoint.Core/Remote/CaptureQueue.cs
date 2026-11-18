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

    /// <summary>
    /// The files of captures found damaged and set aside, then or before a restart, that haven't been opened or had
    /// their notice closed, so the person can be told.
    /// </summary>
    public IReadOnlyList<string> SetAsideFiles
    {
        get
        {
            lock (_gate)
            {
                // Read first, so a damaged file is found as soon as the status line is shown.
                Read();
                Read(RefusedPath);
            }

            return KeptCopy.SetAsideOf([path, RefusedPath], seen: false);
        }
    }

    /// <summary>Opened or closed: the files set aside so far aren't told about again. One set aside later is.</summary>
    public void MarkSetAsideSeen() => KeptCopy.MarkSeen(SetAsideFiles);

    /// <summary>
    /// What to show while any capture has been turned down, or a file of them was found damaged and set aside. Null
    /// when neither. Open shows them.
    /// </summary>
    public string? Refused
    {
        get
        {
            var turnedDown = RefusedCount switch
            {
                0 => null,
                1 => "Something you typed couldn't be saved on the server. It's kept on this PC.",
                var count => $"{count} things you typed couldn't be saved on the server. They're kept on this PC.",
            };
            var damaged = SetAsideFiles switch
            {
                [] => null,
                [var one] => $"Some things you typed couldn't be read, so they weren't saved on the server. They're kept on this PC in {Path.GetFileName(one)}.",
                var files => $"Some things you typed couldn't be read, so they weren't saved on the server. They're kept on this PC in {files.Count} files, starting with {Path.GetFileName(files[0])}.",
            };

            return turnedDown is null && damaged is null ? null : string.Join(" ", new[] { turnedDown, damaged }.OfType<string>());
        }
    }

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

        // Every one still on this PC, opened before or not, so the copy shows all that's kept.
        var aside = KeptCopy.SetAsideOf([path, RefusedPath]);

        if (refused.Count == 0 && aside.Count == 0)
        {
            return null;
        }

        return KeptCopy.Write(
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "", RefusedCopyName),
            refused.Select(capture => (
                $"Typed {KeptCopy.When(capture.WrittenAt.LocalDateTime)}{(capture.FileName is { } file ? $", from {file}" : "")}",
                capture.Text))
            .Concat(aside.Select(file => KeptCopy.SetAsideEntry("Things typed while waiting for the server", file))));
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

    /// <summary>
    /// Reads a kept file. One that can't be read, or that holds an entry that's empty or the wrong shape, is set
    /// aside byte for byte, so whatever was typed can still be found by hand, and is told about. The whole entries
    /// are kept in a new file, so they still go.
    /// </summary>
    private List<CaptureRequest> Read(string file)
    {
        if (!File.Exists(file))
        {
            return [];
        }

        List<CaptureRequest?> entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<CaptureRequest?>>(File.ReadAllText(file), RemoteJson.Options) ?? [];
        }
        catch (JsonException)
        {
            KeptCopy.SetAside(file);
            return [];
        }

        var good = entries.OfType<CaptureRequest>().Where(capture => capture is { Id: not null, Text: not null }).ToList();
        if (good.Count != entries.Count)
        {
            KeptCopy.SetAside(file);
            if (good.Count > 0)
            {
                Write(file, good);
            }
        }

        return good;
    }

    private static void Write(string file, List<CaptureRequest> waiting)
    {
        var temporary = $"{file}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(waiting, RemoteJson.Options));
        File.Move(temporary, file, overwrite: true);
    }
}
