using Waypoint.Core.Email;

namespace Waypoint.Core.Platform;

/// <summary>
/// Keeps a sign-in in a file only its owner can read or write, for hosts without Windows data protection.
/// On Linux the file is created with mode 600, so it should sit in a folder that belongs to the service user.
/// </summary>
public sealed class FileTokenStore(string path) : ITokenStore
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public string? Load()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var text = File.ReadAllText(path).Trim();
        return text.Length == 0 ? null : text;
    }

    public void Save(string token) => WriteOwnerOnly(path, token);

    /// <summary>Writes through a temporary file, so a crash never leaves half a secret, and never widens who can read it.</summary>
    public static void WriteOwnerOnly(string path, string text)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerOnly;
        }

        using (var stream = new FileStream(temporary, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(text);
        }

        File.Move(temporary, path, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, OwnerOnly);
        }
    }
}
