using System.IO.Compression;

namespace Waypoint.Core.Remote;

/// <summary>A backup on the server, named by its file alone, so the PC never sends a path for the server to open.</summary>
public sealed record BackupChoice(string Name, DateTime At, bool BeforeRestore);

public sealed record BackupRequest(string Name);

/// <summary>What came of a restore on the server. SafetyCopy names how things were just before, so it can be undone.</summary>
public sealed record BackupRestored(bool Restored, string? SafetyCopy, string? Problem, bool Restarting);

/// <summary>
/// Backups, restores, and the notes export, run where the database is. In server mode that's the server, which
/// keeps the copies in its own backups folder and sends the export back as a zip.
/// </summary>
public sealed class ServerBackups(ServerClient server)
{
    public const string ExportPath = "api/export/notes";

    /// <summary>Makes a copy now and returns its name.</summary>
    public string BackUpNow() => server.SendLong<BackupRequest>(HttpMethod.Post, "api/backups", null, "Restore a backup in the tray menu for a new copy").Name;

    /// <summary>The server's copies, newest first. This is a change-like call, so it says when the server is away.</summary>
    public IReadOnlyList<BackupChoice> GetBackups() => server.Send<List<BackupChoice>>(HttpMethod.Get, "api/backups");

    /// <summary>
    /// Replaces everything on the server with that copy, after saving how things are now. Copying and restarting
    /// can take longer than a page read waits.
    /// </summary>
    public BackupRestored Restore(BackupChoice backup) =>
        server.SendLong<BackupRestored>(HttpMethod.Post, "api/backups/restore", new BackupRequest(backup.Name), "Restore a backup in the tray menu for a new copy marked \"before a restore\"");

    /// <summary>
    /// Every note as Markdown, unpacked into <paramref name="destination"/>, which is made new, so an earlier
    /// export is never written over. Returns how many notes there were.
    /// </summary>
    public int ExportNotes(string destination)
    {
        var zip = server.Download(ExportPath);
        Directory.CreateDirectory(destination);
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        archive.ExtractToDirectory(destination);
        return archive.Entries.Count(entry => entry.FullName.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
    }
}
