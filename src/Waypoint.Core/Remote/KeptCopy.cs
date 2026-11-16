using System.Globalization;
using System.Text;

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
}
