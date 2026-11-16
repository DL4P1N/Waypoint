using System.IO;
using Waypoint.App;
using Waypoint.Core.Remote;
using Waypoint.Core.Schedule;

namespace Waypoint.App.Tests;

public sealed class IdleSenderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-idle-{Guid.NewGuid():N}");

    public IdleSenderTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static IdleReporter Reporter() => new(new BedtimeSchedule(new BedtimeSettings(), null), new NeverRead());

    private sealed class NeverRead : IUserIdle
    {
        public TimeSpan? IdleFor(DateTime now) => null;
    }

    [Theory]
    [InlineData("192.168.1.20:5000")]
    [InlineData("server.local")]
    [InlineData("ftp://192.168.1.20")]
    [InlineData("")]
    [InlineData(null)]
    public void An_address_that_isnt_http_or_https_is_skipped_without_stopping_anything(string? address) =>
        Assert.Null(IdleSender.ForAddress(address, _folder, Reporter()));

    [Fact]
    public async Task A_good_address_sends_and_makes_no_PC_id_just_for_this()
    {
        var sender = IdleSender.ForAddress("http://192.168.1.20:5000", _folder, Reporter());

        Assert.NotNull(sender);
        await sender.TickAsync(DateTime.Now);
        Assert.False(File.Exists(Path.Combine(_folder, "server-pc.id")));
    }
}
