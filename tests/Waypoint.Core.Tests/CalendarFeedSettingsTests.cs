using System.Net;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Background;
using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Email;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Tests;

/// <summary>The calendar feed link typed on the Settings page. No network: the feed is a fake that records what was asked for.</summary>
public sealed class CalendarFeedSettingsTests : IDisposable
{
    private const string FileLink = "https://example.test/from-settings-json.ics";
    private const string PageLink = "https://example.test/from-the-page.ics";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-feed-{Guid.NewGuid()}");
    private readonly WaypointDatabase _database;
    private readonly FakeFeed _feed = new();

    public CalendarFeedSettingsTests()
    {
        Directory.CreateDirectory(_folder);
        _database = new WaypointDatabase(Path.Combine(_folder, "waypoint.db"));
    }

    public void Dispose()
    {
        for (var attempt = 0; ; attempt++)
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_folder, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(100);
            }
        }
    }

    private sealed class FakeFeed : HttpMessageHandler
    {
        private readonly List<string> _asked = [];

        public List<string> Asked
        {
            get
            {
                lock (_asked)
                {
                    return _asked.ToList();
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_asked)
            {
                _asked.Add(request.RequestUri!.AbsoluteUri);
            }

            var tomorrow = DateTime.Today.AddDays(1);
            var ics = $"BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART;VALUE=DATE:{tomorrow:yyyyMMdd}\r\nUID:quiz\r\nSUMMARY:Quiz\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ics) });
        }
    }

    /// <summary>Plans nothing, so the real sorter is never asked.</summary>
    private sealed class NoPlans : INudgePlanner
    {
        public Task<IReadOnlyList<EventNudges>?> PlanAsync(IReadOnlyList<StoredItem> events, DateTime now, ClassSchedule? schedule, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EventNudges>?>([]);
    }

    private sealed class NeverSorts : ICaptureSorter
    {
        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<CaptureResult?>(null);
    }

    private sealed class NoTokens : ITokenStore
    {
        public string? Load() => null;

        public void Save(string token)
        {
        }
    }

    private sealed class QuietHost : IWorkerHost
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

    private WaypointWorker Worker(string? fileLink = null) =>
        new(_database, new WaypointSettings(CalendarFeedUrl: fileLink), _folder, new QuietHost(), exception => throw exception, new WorkerParts(
            _ => new NoTokens(),
            Http: () => new HttpClient(_feed),
            CaptureSorter: new NeverSorts(),
            NudgePlanner: new NoPlans(),
            ConnectGateway: false));

    private static string CalendarLine(WaypointWorker worker) =>
        worker.DescribeConnections().Single(line => line.Name == "Calendar feed").State;

    private void WaitForAsked(int count)
    {
        for (var tries = 0; _feed.Asked.Count < count && tries < 100; tries++)
        {
            Thread.Sleep(20);
        }
    }

    [Theory]
    [InlineData("https://example.test/feed.ics", "https://example.test/feed.ics")]
    [InlineData("  webcal://example.test/feed.ics  ", "https://example.test/feed.ics")]
    [InlineData("WEBCAL://example.test/a?b=c", "https://example.test/a?b=c")]
    [InlineData("http://example.test/feed.ics", "http://example.test/feed.ics")]
    [InlineData("example.test/feed.ics", null)]
    [InlineData("ftp://example.test/feed.ics", null)]
    [InlineData("not a link", null)]
    [InlineData("", null)]
    public void A_pasted_link_is_tidied_or_turned_down(string pasted, string? expected) =>
        Assert.Equal(expected, CalendarFeedSetup.Clean(pasted));

    [Fact]
    public void With_no_feed_the_page_says_where_to_paste_one()
    {
        var worker = Worker();

        Assert.Null(worker.Calendar);
        Assert.Equal(new CalendarFeedForm(false, false), worker.GetCalendarFeed());
        Assert.Equal("Not set up. Paste its link below.", CalendarLine(worker));
        Assert.DoesNotContain("settings.json", CalendarLine(worker));
    }

    [Fact]
    public void A_broken_or_blank_link_changes_nothing()
    {
        var worker = Worker();

        Assert.Equal(CalendarFeedSetup.NotALink, worker.SaveCalendarFeed("my course calendar"));
        Assert.Equal("Paste the feed link first.", worker.SaveCalendarFeed("  "));

        Assert.Null(worker.Calendar);
        Assert.Null(CalendarFeedSetup.Saved(_database));
    }

    [Fact]
    public async Task A_saved_link_is_read_straight_away_without_a_restart()
    {
        var worker = Worker();
        worker.SyncsCalendar = true;

        var reply = worker.SaveCalendarFeed("webcal://example.test/from-the-page.ics");

        Assert.Equal("Saved. Reading the feed now, then every hour.", reply);
        WaitForAsked(1);
        Assert.Equal([PageLink], _feed.Asked);
        Assert.Equal(new CalendarFeedForm(true, true), worker.GetCalendarFeed());
        Assert.StartsWith("Set up.", CalendarLine(worker));
        Assert.DoesNotContain("example.test", CalendarLine(worker));

        await worker.SyncCalendarAsync();
        Assert.Contains(_database.GetOpenItems(), item => item.Title == "Quiz" && item.Source == ItemSource.Calendar);
    }

    [Fact]
    public void A_trial_run_saves_the_link_but_reads_nothing()
    {
        var worker = Worker();

        Assert.Equal("Saved. The feed is read every hour while Waypoint runs.", worker.SaveCalendarFeed(PageLink));

        Thread.Sleep(100);
        Assert.Empty(_feed.Asked);
        Assert.NotNull(worker.Calendar);
    }

    [Fact]
    public async Task The_pages_link_is_used_over_settings_json_and_removing_it_goes_back()
    {
        Worker(FileLink).SaveCalendarFeed(PageLink);

        // Saved on the page, so it's still used after a restart.
        var worker = Worker(FileLink);
        Assert.Equal(new CalendarFeedForm(true, true), worker.GetCalendarFeed());
        await worker.SyncCalendarAsync();
        Assert.Equal([PageLink], _feed.Asked);

        Assert.Equal("Removed. The calendar link from the setup file is used instead.", worker.RemoveCalendarFeed());
        Assert.Equal(new CalendarFeedForm(true, false), worker.GetCalendarFeed());
        await worker.SyncCalendarAsync();
        Assert.Equal([PageLink, FileLink], _feed.Asked);
    }

    [Fact]
    public async Task Removing_the_only_link_stops_reading_and_keeps_what_it_added()
    {
        var worker = Worker();
        worker.SaveCalendarFeed(PageLink);
        await worker.SyncCalendarAsync();

        Assert.Equal("Removed. Nothing more is read from the feed, and what it already added stays.", worker.RemoveCalendarFeed());

        Assert.Null(worker.Calendar);
        Assert.Equal(new CalendarFeedForm(false, false), worker.GetCalendarFeed());
        await worker.SyncCalendarAsync();
        Assert.Single(_feed.Asked);
        Assert.Contains(_database.GetOpenItems(), item => item.Title == "Quiz");
    }

    [Fact]
    public void Undo_after_remove_puts_the_same_link_back_but_never_over_a_newer_one()
    {
        var worker = Worker();
        Assert.StartsWith("There's no removed link", worker.PutBackCalendarFeed());
        worker.SaveCalendarFeed(PageLink);
        worker.RemoveCalendarFeed();

        Assert.Equal("Put the feed link back.", worker.PutBackCalendarFeed());
        Assert.Equal(new CalendarFeedForm(true, true), worker.GetCalendarFeed());
        Assert.NotNull(worker.Calendar);

        // A link saved after the remove is the one kept.
        worker.RemoveCalendarFeed();
        worker.SaveCalendarFeed("https://example.test/newer.ics");
        Assert.StartsWith("A feed link is already saved", worker.PutBackCalendarFeed());
        Assert.Equal("https://example.test/newer.ics", CalendarFeedSetup.Saved(_database));
    }
}
