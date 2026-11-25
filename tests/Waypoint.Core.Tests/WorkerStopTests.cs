using System.Net;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Background;
using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Email;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Tests;

/// <summary>
/// The calendar feed and the nudges planned from it run without being waited for, so a restore stops them first:
/// it waits for them, turns the restore down while they're still going, or cuts short a read that hasn't answered.
/// No network: the feed and the planner are fakes.
/// </summary>
public sealed class WorkerStopTests : IDisposable
{
    private const string FeedLink = "https://example.test/made-up-course.ics";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-stop-{Guid.NewGuid()}");
    private readonly WaypointDatabase _database;
    private readonly Feed _feed = new();
    private readonly HeldPlanner _planner = new();

    public WorkerStopTests()
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

    [Fact]
    public async Task A_restore_waits_for_nudges_being_planned_after_the_feed_is_read_or_is_turned_down()
    {
        var worker = Worker();
        await worker.SyncCalendarAsync();
        await _planner.Asked.WaitAsync(TimeSpan.FromMinutes(1));

        Assert.False(await ExclusiveRestore.StopWritersAsync([limit => worker.StopWorkAsync(limit)], TimeSpan.FromMilliseconds(200)));

        var stopping = ExclusiveRestore.StopWritersAsync([limit => worker.StopWorkAsync(limit)], TimeSpan.FromMinutes(1));
        await Task.Delay(200);
        Assert.False(stopping.IsCompleted);

        _planner.Answer();
        Assert.True(await stopping);

        // Once stopped, the feed isn't read again.
        var asked = _feed.Asked;
        await worker.SyncCalendarAsync();
        Assert.Equal(asked, _feed.Asked);
    }

    [Fact]
    public async Task Stopping_cuts_short_a_feed_read_that_hasnt_answered_and_saves_nothing_from_it()
    {
        _feed.Hold = true;
        var worker = Worker();
        var sync = worker.SyncCalendarAsync();
        Assert.True(await Waits.Until(() => _feed.Asked > 0, every: 25));

        Assert.True(await worker.StopWorkAsync(TimeSpan.FromMinutes(1)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync);
        Assert.DoesNotContain(_database.GetOpenItems(), item => item.Source == ItemSource.Calendar);
        Assert.False(_planner.Asked.IsCompleted);
    }

    /// <summary>A feed with one made up quiz tomorrow. While held, it answers only once the read is cancelled.</summary>
    private sealed class Feed : HttpMessageHandler
    {
        private int _asked;

        public int Asked => Volatile.Read(ref _asked);

        public bool Hold { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _asked);
            if (Hold)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            var tomorrow = DateTime.Today.AddDays(1);
            var ics = $"BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART;VALUE=DATE:{tomorrow:yyyyMMdd}\r\nUID:made-up-quiz\r\nSUMMARY:Made up quiz\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ics) };
        }
    }

    /// <summary>Plans nothing, and only once told to answer, whatever stopping asks of it.</summary>
    private sealed class HeldPlanner : INudgePlanner
    {
        private readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IReadOnlyList<EventNudges>?> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Asked => _asked.Task;

        public void Answer() => _answer.TrySetResult([]);

        public Task<IReadOnlyList<EventNudges>?> PlanAsync(IReadOnlyList<StoredItem> events, DateTime now, ClassSchedule? schedule, CancellationToken cancellationToken = default)
        {
            _asked.TrySetResult();
            return _answer.Task;
        }
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

    private WaypointWorker Worker() =>
        new(_database, new WaypointSettings(CalendarFeedUrl: FeedLink), _folder, new QuietHost(), exception => throw exception, new WorkerParts(
            _ => new NoTokens(),
            Http: () => new HttpClient(_feed),
            CaptureSorter: new NeverSorts(),
            NudgePlanner: _planner,
            ConnectGateway: false));
}
