using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Waypoint.Core.Remote;

/// <summary>
/// A plain text copy of what the server couldn't save, written beside the file that keeps it, so it can be opened in
/// a text editor and copied back in. The kept file itself is left as it is.
/// </summary>
public static class KeptCopy
{
    public const string Intro = "The server couldn't save these, so they're kept on this PC. Copy anything you still need back into Waypoint.";

    public static string When(DateTime at) => at.ToString("ddd, MMM d 'at' h:mm tt", CultureInfo.InvariantCulture);

    /// <summary>Writes each entry under its heading and returns the file's path.</summary>
    public static string Write(string file, IEnumerable<(string Heading, string Text)> entries)
    {
        var text = new StringBuilder().AppendLine(Intro);
        foreach (var (heading, body) in entries)
        {
            text.AppendLine().AppendLine($"{heading}:").AppendLine(body.TrimEnd());
        }

        File.WriteAllText(file, text.ToString());
        return file;
    }

    /// <summary>
    /// Moves a file that couldn't be read out of the way, byte for byte, so whatever was typed in it can still be
    /// found by hand, and returns where it went. One set aside before is never overwritten.
    /// </summary>
    public static string SetAside(string file)
    {
        var aside = $"{file}.unreadable-{DateTime.Now:yyyyMMddHHmmss}";
        for (var next = 2; File.Exists(aside); next++)
        {
            aside = $"{file}.unreadable-{DateTime.Now:yyyyMMddHHmmss}-{next}";
        }

        File.Move(file, aside);
        return aside;
    }

    /// <summary>
    /// Beside the files kept on this PC: the names of files set aside that the person opened, or whose notice they
    /// closed, so it isn't told again. One set aside later is told about as usual.
    /// </summary>
    public const string SeenName = "set-aside-seen.json";

    private static readonly object SeenGate = new();

    /// <summary>
    /// Every file set aside from these that's still on this PC, oldest first by when it was set aside, read from the folder so one set aside
    /// before a restart is still found. Leaving out <paramref name="seen"/> ones gives those still to be told about.
    /// </summary>
    public static IReadOnlyList<string> SetAsideOf(IEnumerable<string> files, bool seen = true)
    {
        var found = new List<string>();
        foreach (var file in files)
        {
            var full = Path.GetFullPath(file);
            var folder = Path.GetDirectoryName(full) ?? "";
            if (!Directory.Exists(folder))
            {
                continue;
            }

            var told = seen ? [] : ReadSeen(folder);
            found.AddRange(Directory.GetFiles(folder, Path.GetFileName(full) + ".unreadable-*")
                .Where(aside => !told.Contains(Path.GetFileName(aside))));
        }

        return found.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(aside => aside[(aside.LastIndexOf(".unreadable-", StringComparison.Ordinal) + 1)..], StringComparer.Ordinal)
            .ThenBy(aside => aside, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Remembers these as opened or closed, each by its own name, so their notice isn't shown again.</summary>
    public static void MarkSeen(IEnumerable<string> asides)
    {
        foreach (var group in asides.GroupBy(aside => Path.GetDirectoryName(Path.GetFullPath(aside)) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            lock (SeenGate)
            {
                var seen = ReadSeen(group.Key);
                var before = seen.Count;
                seen.UnionWith(group.Select(Path.GetFileName).OfType<string>());
                if (seen.Count == before)
                {
                    continue;
                }

                // Only names still on this PC are kept, so the list doesn't grow once a file is deleted by hand.
                var kept = seen.Where(name => File.Exists(Path.Combine(group.Key, name))).Order(StringComparer.Ordinal).ToList();
                var file = Path.Combine(group.Key, SeenName);
                File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(kept));
                File.Move(file + ".tmp", file, overwrite: true);
            }
        }
    }

    private static HashSet<string> ReadSeen(string folder)
    {
        var file = Path.Combine(folder, SeenName);
        lock (SeenGate)
        {
            try
            {
                return File.Exists(file)
                    ? new HashSet<string>(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file)) ?? [], StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                // A damaged list only means a notice is shown again, which is the safe way round.
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>The heading and text for a file set aside as unreadable, so a kept copy points to it and shows what it held.</summary>
    public static (string Heading, string Text) SetAsideEntry(string what, string aside)
    {
        string text;
        try
        {
            text = File.Exists(aside) ? File.ReadAllText(aside) : "";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            text = "";
        }

        return ($"{what} couldn't be read. The file is kept as it was at {aside}", text);
    }
}
