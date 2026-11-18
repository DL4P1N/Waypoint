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
