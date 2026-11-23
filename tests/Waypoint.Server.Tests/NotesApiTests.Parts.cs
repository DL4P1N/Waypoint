namespace Waypoint.Server.Tests;

/// <summary>Uploads cut off when the server last stopped suddenly don't stay in the attachments folder for good.</summary>
public sealed partial class NotesApiTests
{
    [Fact]
    public void An_upload_cut_off_before_a_restart_is_swept_from_the_attachments_folder()
    {
        var attachments = Path.Combine(_folder, "attachments");
        Directory.CreateDirectory(attachments);
        var stale = Path.Combine(attachments, "source-7.m4a.0123456789abcdef.part");
        var kept = Path.Combine(attachments, "source-7.m4a");
        File.WriteAllText(stale, "half an upload");
        File.WriteAllText(kept, "a whole upload");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-1));
        File.SetLastWriteTimeUtc(kept, DateTime.UtcNow.AddDays(-1));

        using var server = StartServer();
        using var client = server.CreateClient();

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(kept));
    }
}
