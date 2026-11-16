using System.Text;

namespace Waypoint.Core.Email;

/// <summary>
/// The alerts the parser couldn't read, kept in a text file so the parser can learn their format. Only the newest are
/// kept, each body is cut short, and only mail from a known bank or the payment app ever gets here.
/// </summary>
public static class UnreadAlertLog
{
    public const int MaxEntries = 200;
    public const int MaxFileBytes = 1024 * 1024;
    public const int MaxBodyChars = 4000;

    private const string Separator = "\n---\n";

    public static void Append(string path, UnreadAlert alert)
    {
        var body = alert.Body.Length > MaxBodyChars ? alert.Body[..MaxBodyChars] + "\n(cut short)" : alert.Body;
        var entry = $"From: {OneLine(alert.From)}\nSubject: {OneLine(alert.Subject)}\n{body.Replace("\r\n", "\n").Replace(Separator, "\n- - -\n")}";

        var entries = File.Exists(path)
            ? File.ReadAllText(path).Split(Separator, StringSplitOptions.RemoveEmptyEntries).ToList()
            : [];
        entries.Add(entry);

        // The oldest go first, until both limits are met. The newest is always kept.
        var start = Math.Max(0, entries.Count - MaxEntries);
        var bytes = entries.Skip(start).Sum(Size);
        while (bytes > MaxFileBytes && start < entries.Count - 1)
        {
            bytes -= Size(entries[start]);
            start++;
        }

        var text = new StringBuilder();
        foreach (var kept in entries.Skip(start))
        {
            text.Append(kept).Append(Separator);
        }

        var part = path + ".part";
        File.WriteAllText(part, text.ToString());
        File.Move(part, path, overwrite: true);
    }

    private static int Size(string entry) => Encoding.UTF8.GetByteCount(entry) + Separator.Length;

    private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
}
