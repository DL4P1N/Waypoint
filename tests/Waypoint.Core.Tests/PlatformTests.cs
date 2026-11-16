using Waypoint.Core.Platform;

namespace Waypoint.Core.Tests;

/// <summary>The pieces that differ between the PC and a Linux server.</summary>
public sealed class PlatformTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-platform-{Guid.NewGuid()}");

    public PlatformTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void A_token_file_keeps_what_was_saved()
    {
        var store = new FileTokenStore(Path.Combine(_folder, "outlook.token"));

        Assert.Null(store.Load());
        store.Save("refresh-1");
        store.Save("refresh-2");

        Assert.Equal("refresh-2", store.Load());
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void A_token_file_can_only_be_read_by_its_owner_on_Linux()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(_folder, "outlook.token");
        new FileTokenStore(path).Save("secret");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void The_cli_is_found_on_PATH_as_before_on_Windows()
    {
        Assert.Equal("claude", ClaudeCliPath.Find(_ => null, isWindows: true, _ => false));
    }

    [Fact]
    public void The_cli_can_be_named_outright()
    {
        Assert.Equal("/opt/claude", ClaudeCliPath.Find(name => name == ClaudeCliPath.Variable ? "/opt/claude" : null, isWindows: false, _ => false));
    }

    [Fact]
    public void On_Linux_the_cli_is_found_where_the_installer_puts_it_even_with_a_short_PATH()
    {
        var variables = new Dictionary<string, string> { ["PATH"] = "/usr/bin", ["HOME"] = "/home/waypoint" };
        var expected = Path.Combine("/home/waypoint", ".local", "bin", "claude");

        var found = ClaudeCliPath.Find(name => variables.GetValueOrDefault(name), isWindows: false, path => path == expected);

        Assert.Equal(expected, found);
    }

    private const string Chicago = "America/Chicago";

    [Theory]
    [InlineData("America/Chicago")]
    [InlineData("Central Standard Time")]
    public void A_time_zone_is_found_by_either_kind_of_name(string id)
    {
        var zone = TimeZones.Find(id);

        Assert.NotNull(zone);
        Assert.True(TimeZones.SameZone(zone, Chicago));
        Assert.Null(TimeZones.Warning(zone, Chicago));
    }

    [Fact]
    public void A_different_zone_gets_a_warning()
    {
        var regina = TimeZones.Find("America/Regina")!;

        Assert.False(TimeZones.SameZone(regina, Chicago));
        Assert.Contains("timedatectl", TimeZones.Warning(regina, Chicago));
    }

    /// <summary>
    /// The service sets TZ itself, so its own clock always looks right. The system zone in /etc/localtime is checked
    /// too, since the backup timer and the journal follow it.
    /// </summary>
    [Fact]
    public void A_different_system_zone_gets_a_warning_even_when_the_service_zone_is_right()
    {
        var chicago = TimeZones.Find(Chicago)!;

        Assert.Null(TimeZones.Warning(chicago, Chicago, "America/Chicago"));
        Assert.Null(TimeZones.Warning(chicago, Chicago, null));
        var warning = TimeZones.Warning(chicago, Chicago, "Etc/UTC");
        Assert.Contains("Etc/UTC", warning);
        Assert.Contains("timedatectl set-timezone America/Chicago", warning);
    }

    [Theory]
    [InlineData("/usr/share/zoneinfo/America/Chicago", "America/Chicago")]
    [InlineData("../usr/share/zoneinfo/Etc/UTC", "Etc/UTC")]
    [InlineData("/usr/share/zoneinfo/posix/America/Regina", "America/Regina")]
    [InlineData("/somewhere/else", null)]
    public void The_system_zone_is_read_from_the_localtime_link(string target, string? expected)
    {
        Assert.Equal(expected, TimeZones.ZoneFromLink(target));
    }
}
