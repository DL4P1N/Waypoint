using System.Net;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Email;
using Waypoint.Core.Remote;

namespace Waypoint.Core.Tests;

/// <summary>Which mode the PC runs in, and what happens when it goes back to Local after server mode.</summary>
public sealed class PcModeTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-mode-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public PcModeTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void Server_mode_without_an_address_sends_nothing_instead_of_quietly_running_local()
    {
        Assert.Equal(PcRun.Local, PcMode.Choose(null));
        Assert.Equal(PcRun.Local, PcMode.Choose(new ServerSettings("http://100.101.102.103:5280")));
        Assert.Equal(PcRun.Server, PcMode.Choose(new ServerSettings("http://100.101.102.103:5280", DataMode.Server)));
        Assert.Equal(PcRun.NeedsAddress, PcMode.Choose(new ServerSettings(null, DataMode.Server)));
        Assert.Equal(PcRun.NeedsAddress, PcMode.Choose(new ServerSettings("  ", DataMode.Server)));

        Assert.False(PcMode.Sends(PcRun.NeedsAddress));
        Assert.Contains("address", PcMode.NeedsAddressNotice);
        Assert.DoesNotContain("—", PcMode.NeedsAddressNotice);
        Assert.True(PcMode.NeedsAddressTray.Length <= 63);
    }

    [Fact]
    public void Going_back_to_local_after_server_mode_starts_Discord_from_now_so_old_captures_are_not_replayed()
    {
        _database.SetPreference(DiscordBridge.LastMessageKey, "1000");
        _database.SetPreference(DiscordBridge.LastAlertKey, "1000");
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

        Assert.False(PcMode.Started(_database, PcRun.Server, now));
        Assert.Equal("1000", _database.GetPreference(DiscordBridge.LastMessageKey));
        Assert.True(PcMode.Started(_database, PcRun.Local, now));

        var expected = PcMode.Snowflake(now).ToString();
        Assert.Equal(expected, _database.GetPreference(DiscordBridge.LastMessageKey));
        Assert.Equal(expected, _database.GetPreference(DiscordBridge.LastAlertKey));
        Assert.False(PcMode.Started(_database, PcRun.Local, now.AddDays(1)));
        Assert.Equal(expected, _database.GetPreference(DiscordBridge.LastMessageKey));
    }

    [Fact]
    public void A_PC_that_was_never_in_server_mode_keeps_its_place_in_Discord()
    {
        _database.SetPreference(DiscordBridge.LastMessageKey, "1000");

        Assert.False(PcMode.Started(_database, PcRun.Local, DateTimeOffset.Now));
        Assert.Equal("1000", _database.GetPreference(DiscordBridge.LastMessageKey));
    }

    [Fact]
    public void A_snowflake_is_Discords_id_for_a_moment()
    {
        // Discord's own example: 175928847299117063 was made at 2016-04-30 11:18:25.796 UTC.
        Assert.Equal(175928847299117063UL >> 22, PcMode.Snowflake(DateTimeOffset.Parse("2016-04-30T11:18:25.796Z")) >> 22);
    }

    private sealed class Host : IWorkerHost
    {
        public void ShowNotice(string title, string text)
        {
        }

        public void ShowReminder(DueReminder reminder, Action closed)
        {
        }

        public void ShowWeeklyReset(string text)
        {
        }

        public void Changed()
        {
        }

        public void Post(Action action) => action();
    }

    private sealed class KeptToken : ITokenStore
    {
        public string? Token { get; private set; }

        public string? Load() => Token;

        public void Save(string token) => Token = token;
    }

    private sealed class CountsCalls : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        }
    }

    private sealed class NullSorter : ICaptureSorter
    {
        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<CaptureResult?>(null);
    }

    [Fact]
    public async Task Without_an_address_saving_Discord_keeps_it_for_the_server_and_connects_nothing()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"waypoint-mode-{Guid.NewGuid()}");
        Directory.CreateDirectory(folder);
        try
        {
            var token = new KeptToken();
            var http = new CountsCalls();
            var worker = new WaypointWorker(_database, new WaypointSettings(), folder, new Host(), exception => throw exception, new WorkerParts(
                _ => token, Http: () => new HttpClient(http), CaptureSorter: new NullSorter(), ConnectGateway: false, Runner: DiscordRunner.Pc))
            {
                HoldsConnections = true,
            };

            worker.StartDiscord();
            var reply = worker.SaveDiscord("123456789012345678", "223456789012345678", "323456789012345678", null, "secret");
            await worker.CheckDiscordClaimAsync();

            Assert.Null(worker.Discord);
            Assert.Equal(0, http.Calls);
            Assert.Contains("once the server address is set", reply);
            Assert.DoesNotContain("\u2014", reply);

            var sent = new List<(DiscordForm Form, string? Token)>();
            string Save(DiscordForm form, string? kept)
            {
                sent.Add((form, kept));
                return "Saved. Connecting to Discord now.";
            }

            Assert.Equal("Saved. Connecting to Discord now.", PcMode.SendHeldDiscord(_database, token.Load(), Save));
            Assert.Null(PcMode.SendHeldDiscord(_database, token.Load(), Save));
            var (form, sentToken) = Assert.Single(sent);
            Assert.Equal(new DiscordForm("123456789012345678", "223456789012345678", "323456789012345678", null, true), form);
            Assert.Equal("secret", sentToken);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Without_an_address_typing_waits_in_the_server_queue_rather_than_the_PCs_database()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"waypoint-mode-{Guid.NewGuid()}");
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Null(PcMode.HeldCaptures(PcRun.Local, folder));
            Assert.Null(PcMode.HeldCaptures(PcRun.Server, folder));

            PcMode.HeldCaptures(PcRun.NeedsAddress, folder)!.Add("buy milk", DateTimeOffset.Now);

            // The same file server mode sends from, so it goes out once the address is set.
            var later = new CaptureQueue(Path.Combine(folder, PcMode.QueueFileName));
            Assert.Equal(1, later.Count);
            Assert.Equal("server-queue.json", PcMode.QueueFileName);
            Assert.Empty(_database.GetPendingCaptures());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
