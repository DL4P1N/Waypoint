using Waypoint.Core.Remote;

namespace Waypoint.Core.Tests;

/// <summary>A new note kept offline under an id of its own is kept once however often it's made again.</summary>
public sealed class OfflineNotesOnceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-offline-{Guid.NewGuid()}.json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void A_new_note_made_twice_offline_with_the_same_id_is_kept_once()
    {
        var offline = new OfflineNotes(_path);
        var now = new DateTime(2026, 11, 15, 10, 0, 0);

        var first = offline.AddNew("Made up idea", "Paint the made up shelf", null, now, "discord-post:900");
        var again = offline.AddNew("Made up idea", "Paint the made up shelf", null, now, "discord-post:900");
        var other = offline.AddNew("Made up idea", "Paint the made up shelf", null, now);

        Assert.Equal(first.Id, again.Id);
        Assert.NotEqual(first.Id, other.Id);
        Assert.Equal(2, offline.All.Count);
    }
}
