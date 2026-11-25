using Microsoft.Data.Sqlite;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Email;

namespace Waypoint.Core.Tests;

/// <summary>
/// The PC runs each round of alert checks, with the daily backup, off its window thread. A round still running when
/// the next is due isn't joined by a second, and the one running can be waited for.
/// </summary>
public sealed class AlertRoundTests : IDisposable
{
    private static readonly DateTime Now = new(2030, 5, 14, 13, 0, 0);

    /// <summary>
    /// Only so a hung test fails rather than hangs. Everything here waits for a signal, so a slow machine only makes
    /// a test later, never wrong. A held round is let go on its own only after twice this, so a stop that waited for
    /// it would run out first.
    /// </summary>
    private static readonly TimeSpan Hung = TimeSpan.FromSeconds(60);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-alert-round-{Guid.NewGuid():N}");
    private readonly WaypointDatabase _database;

    public AlertRoundTests()
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
    public async Task A_round_runs_off_the_calling_thread_and_one_still_running_is_not_joined_by_a_second()
    {
        var caller = Environment.CurrentManagedThreadId;
        var armed = false;
        var roundThread = 0;
        var entered = 0;
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        DateTime Clock()
        {
            if (Volatile.Read(ref armed) && Environment.CurrentManagedThreadId != caller && Interlocked.Increment(ref entered) == 1)
            {
                roundThread = Environment.CurrentManagedThreadId;
                inside.Set();
                release.Wait(Hung * 2);
            }

            return Now;
        }

        var worker = new WaypointWorker(_database, new WaypointSettings(), _folder, new Host(), exception => throw exception, new WorkerParts(
            _ => new NoTokens(),
            Clock: Clock,
            CaptureSorter: new NullSorter(),
            ConnectGateway: false));
        Assert.True(worker.AlertRoundInFlight().IsCompleted);
        Volatile.Write(ref armed, true);

        var first = worker.StartAlertRound();
        Assert.True(inside.Wait(Hung));
        var second = worker.StartAlertRound();

        Assert.Same(first, second);
        Assert.Same(first, worker.AlertRoundInFlight());
        Assert.False(first.IsCompleted);
        Assert.NotEqual(caller, roundThread);

        release.Set();
        await first.WaitAsync(Hung);

        // The round made the daily backup, and the next round is a new one.
        Assert.Single(_database.GetBackups());
        var third = worker.StartAlertRound();
        Assert.NotSame(first, third);
        await third.WaitAsync(Hung);
        Assert.Single(_database.GetBackups());
    }

    [Fact]
    public async Task Stopping_waits_for_a_round_still_running_within_its_limit_and_finishes_once_it_ends()
    {
        var caller = Environment.CurrentManagedThreadId;
        var armed = false;
        var entered = 0;
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        DateTime Clock()
        {
            if (Volatile.Read(ref armed) && Environment.CurrentManagedThreadId != caller && Interlocked.Increment(ref entered) == 1)
            {
                inside.Set();
                release.Wait(Hung * 2);
            }

            return Now;
        }

        var worker = new WaypointWorker(_database, new WaypointSettings(), _folder, new Host(), exception => throw exception, new WorkerParts(
            _ => new NoTokens(),
            Clock: Clock,
            CaptureSorter: new NullSorter(),
            ConnectGateway: false));
        Volatile.Write(ref armed, true);
        var round = worker.StartAlertRound();
        Assert.True(inside.Wait(Hung));

        // Held, so a short limit runs out and says so, while the round is still held, so it never waited for it.
        Assert.False(await worker.StopWorkAsync(TimeSpan.FromMilliseconds(200)).WaitAsync(Hung));
        Assert.False(round.IsCompleted);

        // A longer limit waits for it and finishes once it's let go.
        var stopping = worker.StopWorkAsync(TimeSpan.FromMinutes(1));
        await Task.Delay(200);
        Assert.False(stopping.IsCompleted);

        release.Set();
        Assert.True(await stopping.WaitAsync(Hung));
        Assert.True(round.IsCompleted);
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

    private sealed class NoTokens : ITokenStore
    {
        public string? Load() => null;

        public void Save(string token)
        {
        }
    }

    private sealed class NullSorter : ICaptureSorter
    {
        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<CaptureResult?>(null);
    }
}
