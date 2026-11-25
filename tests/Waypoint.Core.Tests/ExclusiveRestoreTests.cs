using Microsoft.Data.Sqlite;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>Everything that writes is stopped, in order and within a time limit, before a restore replaces the database.</summary>
public sealed class ExclusiveRestoreTests : IDisposable
{
    /// <summary>
    /// Only so a hung test fails rather than hangs, and as the limit where running out isn't what's checked. A slow
    /// machine then only makes a test later, never wrong.
    /// </summary>
    private static readonly TimeSpan Hung = TimeSpan.FromSeconds(60);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-restore-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public ExclusiveRestoreTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public async Task Every_writer_is_stopped_in_order_before_it_says_yes()
    {
        var stopped = new List<string>();

        var ready = await ExclusiveRestore.StopWritersAsync(
        [
            _ => Stop(stopped, "loops"),
            async _ =>
            {
                await Task.Delay(20);
                return await Stop(stopped, "sorting");
            },
            _ => Stop(stopped, "note jobs"),
        ], Hung);

        Assert.True(ready);
        Assert.Equal(["loops", "sorting", "note jobs"], stopped);
    }

    [Fact]
    public async Task A_writer_that_does_not_stop_in_time_means_no_restore_and_the_rest_are_not_waited_on()
    {
        var reached = false;

        // The first never stops, so coming back at all means the limit ran out rather than it being waited on.
        var ready = await ExclusiveRestore.StopWritersAsync(
        [
            _ => new TaskCompletionSource<bool>().Task,
            _ =>
            {
                reached = true;
                return Task.FromResult(true);
            },
        ], TimeSpan.FromMilliseconds(200)).WaitAsync(Hung);

        Assert.False(ready);
        Assert.False(reached);
    }

    [Fact]
    public async Task A_writer_that_says_it_did_not_stop_or_fails_means_no_restore()
    {
        var failures = new List<Exception>();

        Assert.False(await ExclusiveRestore.StopWritersAsync([_ => Task.FromResult(false)], Hung));
        Assert.False(await ExclusiveRestore.StopWritersAsync([_ => throw new InvalidOperationException("made up")], Hung, failures.Add));
        Assert.Single(failures);
    }

    [Fact]
    public async Task Sorting_stopped_for_a_restore_finishes_what_it_was_doing_and_never_sorts_again()
    {
        var sorting = new TaskCompletionSource();
        var sorter = new WaitingSorter(sorting);
        var service = new CaptureService(_database, sorter);
        service.Capture("buy stamps");
        var running = service.ProcessPendingAsync();
        await sorting.Task.WaitAsync(Hung);

        Assert.True(await service.StopAsync(Hung));
        await running.WaitAsync(Hung);
        Assert.True(sorter.Cancelled);

        service.Capture("buy envelopes");
        await service.ProcessPendingAsync().WaitAsync(Hung);

        Assert.Equal(1, sorter.Calls);
        Assert.Equal(2, _database.GetPendingCaptures().Count);
        Assert.True(await service.StopAsync(Hung));
    }

    [Fact]
    public void A_wrong_file_is_turned_down_before_anything_is_stopped()
    {
        Assert.Equal("That backup file is gone.", _database.RestoreProblem(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid()}.db")));
        Assert.Contains("using now", _database.RestoreProblem(_path));
    }

    private static Task<bool> Stop(List<string> stopped, string name)
    {
        stopped.Add(name);
        return Task.FromResult(true);
    }

    /// <summary>Holds the first sort until it's told to stop.</summary>
    private sealed class WaitingSorter(TaskCompletionSource sorting) : ICaptureSorter
    {
        public int Calls { get; private set; }

        public bool Cancelled { get; private set; }

        public async Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            sorting.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }

            return null;
        }
    }
}
