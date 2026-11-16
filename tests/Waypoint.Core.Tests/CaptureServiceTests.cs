using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

public sealed class CaptureServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public CaptureServiceTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private sealed class FakeSorter(Func<string, IReadOnlyList<SortedItem>?> sort) : ICaptureSorter
    {
        public DateOnly? LastToday { get; private set; }

        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default)
        {
            LastToday = DateOnly.FromDateTime(writtenAt);
            return Task.FromResult(sort(text) is { } items ? new CaptureResult(items) : null);
        }
    }

    [Fact]
    public void Blank_text_is_ignored()
    {
        new CaptureService(_database, null).Capture("   ");

        Assert.Empty(_database.GetPendingCaptures());
    }

    [Fact]
    public void A_capture_sent_under_the_same_id_is_saved_once_even_by_a_fresh_database()
    {
        var service = new CaptureService(_database, null);

        Assert.NotNull(service.CaptureSent("pc-1", " buy stamps ", DateTimeOffset.Now, null));
        Assert.Null(new CaptureService(new WaypointDatabase(_path), null).CaptureSent("pc-1", "buy stamps", DateTimeOffset.Now, null));
        Assert.NotNull(service.CaptureSent("pc-2", "notes.txt text", null, "notes.txt"));

        Assert.Equal(["buy stamps", "notes.txt text"], _database.GetPendingCaptures().Select(capture => capture.Text));
    }

    [Fact]
    public async Task Capture_stays_pending_without_a_sorter()
    {
        var service = new CaptureService(_database, null);

        service.Capture("buy shampoo");
        await service.ProcessPendingAsync();

        Assert.Equal("buy shampoo", _database.GetPendingCaptures().Single().Text);
    }

    [Fact]
    public async Task Sorted_capture_becomes_items_and_stops_being_pending()
    {
        var service = new CaptureService(_database, new FakeSorter(_ => [new SortedItem(ItemKind.Shopping, "shampoo", null, null)]));

        service.Capture("buy shampoo");
        await service.ProcessPendingAsync();

        Assert.Empty(_database.GetPendingCaptures());
        Assert.Equal("shampoo", _database.GetOpenItems().Single().Title);
    }

    [Fact]
    public async Task Failed_sort_keeps_the_capture_and_records_the_error()
    {
        var service = new CaptureService(_database, new FakeSorter(_ => throw new InvalidOperationException("offline")));

        service.Capture("email advisor");
        await service.ProcessPendingAsync();

        Assert.Single(_database.GetPendingCaptures());
        Assert.Equal("offline", service.LastError);
    }

    [Fact]
    public async Task Declined_sort_keeps_the_capture_pending()
    {
        var service = new CaptureService(_database, new FakeSorter(_ => null));

        service.Capture("something");
        await service.ProcessPendingAsync();

        Assert.Single(_database.GetPendingCaptures());
    }

    [Fact]
    public async Task Empty_sort_result_keeps_the_text_as_a_note()
    {
        var service = new CaptureService(_database, new FakeSorter(_ => []));

        service.Capture("hmm");
        await service.ProcessPendingAsync();

        var item = _database.GetOpenItems().Single();
        Assert.Equal(ItemKind.Note, item.Kind);
        Assert.Equal("hmm", item.Title);
    }

    [Fact]
    public async Task Sorter_gets_the_day_the_capture_was_written()
    {
        var sorter = new FakeSorter(_ => []);
        var service = new CaptureService(_database, sorter);

        service.Capture("paper due friday");
        await service.ProcessPendingAsync();

        Assert.Equal(DateOnly.FromDateTime(DateTime.Now), sorter.LastToday);
    }

    [Fact]
    public void Done_items_leave_the_open_list_and_dated_items_sort_first()
    {
        var id = _database.AddCapture("x");
        _database.CompleteCapture(id,
        [
            new SortedItem(ItemKind.Task, "undated", null, null),
            new SortedItem(ItemKind.Deadline, "later", new DateOnly(2026, 12, 1), null),
            new SortedItem(ItemKind.Deadline, "sooner", new DateOnly(2026, 11, 2), null),
        ]);

        Assert.Equal(["sooner", "later", "undated"], _database.GetOpenItems().Select(item => item.Title));

        _database.SetDone(_database.GetOpenItems()[0], true);

        Assert.Equal(["later", "undated"], _database.GetOpenItems().Select(item => item.Title));
    }
}
