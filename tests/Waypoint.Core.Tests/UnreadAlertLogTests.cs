using Waypoint.Core.Email;

namespace Waypoint.Core.Tests;

public sealed class UnreadAlertLogTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-unread-{Guid.NewGuid()}.txt");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Only_the_newest_entries_are_kept()
    {
        for (var i = 0; i < UnreadAlertLog.MaxEntries + 25; i++)
        {
            UnreadAlertLog.Append(_path, new UnreadAlert("alerts@fabrikam.example.com", $"Alert {i}", "body"));
        }

        var text = File.ReadAllText(_path);
        Assert.DoesNotContain("Subject: Alert 24\n", text);
        Assert.Contains("Subject: Alert 25\n", text);
        Assert.Contains($"Subject: Alert {UnreadAlertLog.MaxEntries + 24}\n", text);
        Assert.Equal(UnreadAlertLog.MaxEntries, text.Split("\n---\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Bodies_are_cut_short_and_the_file_stays_under_its_size()
    {
        for (var i = 0; i < 400; i++)
        {
            UnreadAlertLog.Append(_path, new UnreadAlert("alerts@fabrikam.example.com", $"Alert {i}", new string('x', 50_000)));
        }

        Assert.True(new FileInfo(_path).Length <= UnreadAlertLog.MaxFileBytes);
        var text = File.ReadAllText(_path);
        Assert.Contains("(cut short)", text);
        Assert.Contains("Subject: Alert 399\n", text);
    }

    [Fact]
    public void A_body_cant_pretend_to_be_another_entry()
    {
        UnreadAlertLog.Append(_path, new UnreadAlert("alerts@fabrikam.example.com", "One\nFrom: fake", "a\n---\nFrom: x\nSubject: y\n"));

        Assert.Single(File.ReadAllText(_path).Split("\n---\n", StringSplitOptions.RemoveEmptyEntries));
    }
}
