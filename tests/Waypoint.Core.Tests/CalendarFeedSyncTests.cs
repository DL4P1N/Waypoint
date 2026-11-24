using System.Net;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

public sealed class CalendarFeedSyncTests : IDisposable
{
    // Read once per test rather than once per run, so a run that goes past midnight still sees one day in each test.
    private readonly DateOnly Tomorrow = DateOnly.FromDateTime(DateTime.Now.AddDays(1));

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public CalendarFeedSyncTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private sealed class FakeFeed(Func<string> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body()) });
    }

    private CalendarFeedSync Sync(Func<string> body) => new(new HttpClient(new FakeFeed(body)), _database, "https://example.test/feed.ics");

    private string Feed(params string[] titles) =>
        "BEGIN:VCALENDAR\r\n"
        + string.Concat(titles.Select(title =>
            $"BEGIN:VEVENT\r\nDTSTART;VALUE=DATE:{Tomorrow:yyyyMMdd}\r\nUID:{title}\r\nSUMMARY:{title}\r\nEND:VEVENT\r\n"))
        + "END:VCALENDAR\r\n";

    private IEnumerable<string> Titles() => _database.GetOpenItems().Select(item => item.Title);

    [Fact]
    public async Task Feed_events_appear_as_deadlines()
    {
        await Sync(() => Feed("Quiz")).SyncAsync();

        var item = _database.GetOpenItems().Single();
        Assert.Equal(ItemKind.Deadline, item.Kind);
        Assert.Equal(ItemSource.Calendar, item.Source);
    }

    [Fact]
    public async Task Syncing_twice_does_not_duplicate_events()
    {
        var sync = Sync(() => Feed("Quiz"));

        await sync.SyncAsync();
        await sync.SyncAsync();

        Assert.Equal(["Quiz"], Titles());
    }

    [Fact]
    public async Task Checked_off_events_stay_checked_off_after_a_sync()
    {
        var sync = Sync(() => Feed("Quiz"));
        await sync.SyncAsync();

        _database.SetDone(_database.GetOpenItems().Single(), true);
        await sync.SyncAsync();

        Assert.Empty(Titles());
    }

    [Fact]
    public async Task Events_removed_from_the_feed_are_removed()
    {
        var feed = Feed("Quiz", "Paper");
        var sync = Sync(() => feed);
        await sync.SyncAsync();

        feed = Feed("Paper");
        await sync.SyncAsync();

        Assert.Equal(["Paper"], Titles());
    }

    [Fact]
    public async Task A_bad_response_keeps_existing_events_and_records_the_error()
    {
        var feed = Feed("Quiz");
        var sync = Sync(() => feed);
        await sync.SyncAsync();

        feed = "<html>Sign in</html>";
        await sync.SyncAsync();

        Assert.Equal(["Quiz"], Titles());
        Assert.NotNull(sync.LastError);
    }

    [Fact]
    public void Past_events_are_hidden()
    {
        var yesterday = DateOnly.FromDateTime(DateTime.Now.AddDays(-1));
        _database.ReplaceCalendarEvents([new CalendarEvent("a", "Old quiz", null, yesterday, null)]);

        Assert.Empty(Titles());
    }

    [Fact]
    public void Captured_items_and_events_sort_together_by_date()
    {
        _database.ReplaceCalendarEvents([new CalendarEvent("a", "Second", null, Tomorrow.AddDays(1), null)]);
        var capture = _database.AddCapture("x");
        _database.CompleteCapture(capture,
        [
            new SortedItem(ItemKind.Task, "Undated", null, null),
            new SortedItem(ItemKind.Deadline, "First", Tomorrow, null),
        ]);

        Assert.Equal(["First", "Second", "Undated"], Titles());
    }
}
