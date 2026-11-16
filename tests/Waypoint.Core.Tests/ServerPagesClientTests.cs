using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Remote;

namespace Waypoint.Core.Tests;

/// <summary>Lists, History, Settings, and backups in server mode on the PC, while the server is there and while it isn't.</summary>
public sealed class ServerPagesClientTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-pages-client-{Guid.NewGuid()}");
    private readonly FakeServer _fake = new();

    public ServerPagesClientTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>Answers a few page reads, or acts like a server that's switched off.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public bool Up { get; set; } = true;

        public List<string> Lists { get; } = ["Shopping"];

        public List<string> Sent { get; } = [];

        public byte[] Zip { get; set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!Up)
            {
                throw new HttpRequestException("No route to host");
            }

            Sent.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            if (request.Content is not null)
            {
                await request.Content.ReadAsStringAsync(cancellationToken);
            }

            if (request.RequestUri.AbsolutePath == "/api/export/notes")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip) };
            }

            var body = request.RequestUri.AbsolutePath switch
            {
                "/api/lists" when request.Method == HttpMethod.Get => Json(Lists.Select(name => new ListOverview(name, [])).ToList()),
                "/api/history" => Json(new[] { new HistoryRow(Entry("return books"), true) }),
                "/api/settings/rate" => Json(new NumberValue(20.00m)),
                "/api/settings/tonight" => Json(new TextValue("Bed by 11:30 PM")),
                "/api/settings/discord" => Json(new DiscordForm("1", null, null, null, true)),
                _ => "{}",
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        private static string Json<T>(T value) => JsonSerializer.Serialize(value, RemoteJson.Options);
    }

    private static HistoryEntry Entry(string title) =>
        new(1, DateTime.Now, HistoryAction.Done, ItemSource.Capture, 1, ItemKind.Task, title, null, null, null, []);

    private ServerClient Client() => new(new HttpClient(_fake) { BaseAddress = new Uri("http://server/") }, "token");

    [Fact]
    public void While_the_server_is_away_lists_show_what_they_last_had()
    {
        var lists = new ServerListsData(Client());
        Assert.Equal(["Shopping"], lists.GetListOverview().Select(list => list.Name));

        _fake.Up = false;
        _fake.Lists.Add("Packing");

        Assert.Equal(["Shopping"], lists.GetListOverview().Select(list => list.Name));
    }

    [Fact]
    public void While_the_server_is_away_list_changes_say_so_instead_of_pretending()
    {
        var lists = new ServerListsData(Client());
        lists.GetListOverview();
        _fake.Up = false;
        var entry = new ListEntry(1, "Shopping", "milk", false, DateTime.Now);

        Assert.Throws<ServerUnavailableException>(() => lists.AddListEntry("Shopping", "eggs", DateTime.Now));
        Assert.Throws<ServerUnavailableException>(() => lists.SetEntryDone(entry, true, DateTime.Now));
        Assert.Throws<ServerUnavailableException>(() => lists.RenameList("Shopping", "Food", DateTime.Now));
        var away = Assert.Throws<ServerUnavailableException>(() => lists.DeleteList("Packing", DateTime.Now));
        Assert.Equal(PageMessages.CantChangeYet, PageMessages.CantChange(away));
    }

    [Fact]
    public void While_the_server_is_away_history_keeps_its_last_search_and_reopening_waits()
    {
        var history = new ServerHistoryData(Client());
        var row = Assert.Single(history.SearchHistory("books", 100, HistoryFilter.All));
        Assert.True(row.CanReopen);
        Assert.Contains(_fake.Sent, sent => sent.Contains("filter=All") && sent.Contains("words=books"));

        _fake.Up = false;

        Assert.Single(history.SearchHistory("books", 100, HistoryFilter.All));
        Assert.Empty(history.SearchHistory("something new", 100, HistoryFilter.All));
        Assert.Throws<ServerUnavailableException>(() => history.ReopenEntry(row.Entry));
        Assert.Throws<ServerUnavailableException>(() => history.DeleteHistoryItem(row.Entry, DateTime.Now));
    }

    [Fact]
    public async Task While_the_server_is_away_settings_show_what_they_last_had_and_changes_wait()
    {
        var settings = new ServerSettingsData(Client());
        Assert.True(settings.OnServer);
        Assert.Equal(20.00m, settings.GetHourlyRate());
        Assert.Equal("Bed by 11:30 PM", settings.DescribeTonight(DateTime.Now));
        Assert.True(settings.GetDiscord().HasToken);

        _fake.Up = false;

        Assert.Equal(20.00m, settings.GetHourlyRate());
        Assert.Equal("Bed by 11:30 PM", settings.DescribeTonight(DateTime.Now));
        Assert.Throws<ServerUnavailableException>(() => settings.SetHourlyRate(22m));
        Assert.Throws<ServerUnavailableException>(() => settings.SetBedtimeChoices(new Schedule.BedtimeChoices(SleepHours: 8)));
        Assert.Throws<ServerUnavailableException>(() => settings.SaveDiscord(new DiscordForm("1", null, null, null, false), "secret"));
        await Assert.ThrowsAsync<ServerUnavailableException>(settings.TestDiscordAsync);
    }

    [Fact]
    public void With_nothing_read_yet_and_no_server_the_settings_page_still_opens()
    {
        _fake.Up = false;
        var settings = new ServerSettingsData(Client());

        Assert.Empty(settings.GetWeeklyHours());
        Assert.Equal(0, settings.GetCommuteMinutes());
        Assert.Equal(8, settings.GetBedtime().SleepHours);
        Assert.False(settings.GetDiscord().HasToken);
    }

    [Fact]
    public void A_discord_token_is_sent_once_and_only_in_the_save()
    {
        var settings = new ServerSettingsData(Client());

        settings.SaveDiscord(new DiscordForm("1", null, null, null, false), "secret");

        Assert.Equal(["PUT /api/settings/discord"], _fake.Sent);
    }

    [Fact]
    public void While_the_server_is_away_backups_and_the_export_wait()
    {
        var backups = new ServerBackups(Client());
        _fake.Up = false;

        var away = Assert.Throws<ServerUnavailableException>(backups.BackUpNow);
        Assert.Equal(PageMessages.CantRunYet, PageMessages.CantRun(away));
        Assert.Throws<ServerUnavailableException>(() => backups.GetBackups());
        Assert.Throws<ServerUnavailableException>(() => backups.ExportNotes(Path.Combine(_folder, "out")));
        Assert.False(Directory.Exists(Path.Combine(_folder, "out")));
    }

    [Fact]
    public void The_export_unpacks_the_servers_zip_and_counts_the_notes()
    {
        using (var stream = new MemoryStream())
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(zip, "Unfiled/Lecture 1.md", "Recursion");
                Write(zip, "School/Lecture 2.md", "Base cases");
            }

            _fake.Zip = stream.ToArray();
        }

        var destination = Path.Combine(_folder, "Waypoint notes");
        Assert.Equal(2, new ServerBackups(Client()).ExportNotes(destination));
        Assert.Equal("Base cases", File.ReadAllText(Path.Combine(destination, "School", "Lecture 2.md")));

        static void Write(ZipArchive zip, string name, string text)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }
    }
}
