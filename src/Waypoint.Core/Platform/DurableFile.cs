using System.Text;

namespace Waypoint.Core.Platform;

/// <summary>
/// Writes a whole file in place of an old one, so a crash, a full disk, or power loss leaves either the old file or
/// the new one, never half of either. The new text goes to a file beside it first, is flushed all the way to the
/// disk, and only then is moved over the old one. A write that fails takes its unfinished file with it.
/// </summary>
public static class DurableFile
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/>. <paramref name="temporary"/> is where it goes first,
    /// beside the file, and is <c>.tmp</c> after the file's name when not given.
    /// </summary>
    public static void WriteAllText(string path, string text, Encoding? encoding = null, string? temporary = null) =>
        WriteAllBytes(path, (encoding ?? Utf8).GetBytes(text), temporary);

    public static void WriteAllBytes(string path, byte[] bytes, string? temporary = null)
    {
        temporary ??= path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left for the next write of the same file, which writes over it.
        }
    }
}
