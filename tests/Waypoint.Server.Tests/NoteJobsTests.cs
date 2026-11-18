using System.Text.Json;
using Microsoft.Extensions.Logging;
using Waypoint.Core.Notes;
using Waypoint.Core.Remote;

namespace Waypoint.Server.Tests;

/// <summary>
/// The server's note writing jobs on their own: how each one ends, what the PC hears about it, and that each job's
/// stop switch is let go of once, after the job is over, however it ended.
/// </summary>
public sealed class NoteJobsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly ListLogger _log = new();
    private readonly List<CountedStop> _stops = [];
    private DateTime _now = new(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc);

    private NoteJobs Jobs() => new(_log, utcNow: () => _now, newStop: () =>
    {
        var stop = new CountedStop();
        lock (_stops)
        {
            _stops.Add(stop);
        }

        return stop;
    });

    private sealed class CountedStop : CancellationTokenSource
    {
        private int _disposed;

        public int Disposed => Volatile.Read(ref _disposed);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Increment(ref _disposed);
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }

    private static async Task<NoteJobState> Finished(NoteJobs jobs, string id)
    {
        var until = DateTime.UtcNow + Wait;
        NoteJobState state;
        do
        {
            state = jobs.State(id);
            if (state.Kind() != NoteJobOutcome.Running)
            {
                return state;
            }

            await Task.Delay(10);
        }
        while (DateTime.UtcNow < until);

        return state;
    }

    private static async Task Until(Func<bool> done)
    {
        var until = DateTime.UtcNow + Wait;
        while (!done() && DateTime.UtcNow < until)
        {
            await Task.Delay(10);
        }

        Assert.True(done());
    }

    private static NoteJobState Written() =>
        new(Done: true, Notes: new NoteWriteOutcome(NoteWriteStatus.Written, "Notes written for Biology 101."));

    [Fact]
    public async Task A_job_that_finishes_reports_succeeded_with_its_outcome()
    {
        var jobs = Jobs();
        var id = jobs.Start(_ => Task.FromResult(Written()))!.Id;

        var state = await Finished(jobs, id);

        Assert.Equal(NoteJobOutcome.Succeeded, state.Kind());
        Assert.True(state.Done);
        Assert.False(state.Gone);
        Assert.Equal(NoteWriteStatus.Written, state.Notes!.Status);
    }

    [Fact]
    public async Task A_failed_job_reports_failed_with_a_plain_reason_and_is_logged()
    {
        var jobs = Jobs();
        var id = jobs.Start(_ => throw new IOException(@"Could not read 'C:\Users\sam\notes\week 3.md' with key sk-made-up-key-123456 and prompt Summarize cells"))!.Id;

        var state = await Finished(jobs, id);

        Assert.Equal(NoteJobOutcome.Failed, state.Kind());
        Assert.True(state.Done);
        Assert.False(state.Gone);
        Assert.Equal("A file it needed is open in another program or couldn't be reached.", state.Problem);
        Assert.DoesNotContain("sam", state.Problem);
        Assert.DoesNotContain("sk-", state.Problem);
        Assert.DoesNotContain("Summarize", state.Problem);
        await Until(() =>
        {
            lock (_log.Entries)
            {
                return _log.Entries.Any(entry => entry.Level == LogLevel.Error && entry.Exception is IOException);
            }
        });
    }

    [Fact]
    public async Task A_job_failing_because_claude_isnt_signed_in_says_so()
    {
        var jobs = Jobs();
        var id = jobs.Start(_ => throw new InvalidOperationException("Not logged in · Please run /login"))!.Id;

        var state = await Finished(jobs, id);

        Assert.Equal(NoteJobOutcome.Failed, state.Kind());
        Assert.Equal("Claude isn't signed in.", state.Problem);
    }

    [Fact]
    public async Task A_stopped_job_reports_cancelled()
    {
        var jobs = Jobs();
        var id = jobs.Start(async stop =>
        {
            await Task.Delay(Timeout.Infinite, stop);
            return Written();
        })!.Id;

        Assert.Equal(NoteJobOutcome.Running, jobs.State(id).Kind());
        jobs.Stop(id);
        var state = await Finished(jobs, id);

        Assert.Equal(NoteJobOutcome.Cancelled, state.Kind());
        Assert.True(state.Done);
        Assert.False(state.Gone);
        Assert.Null(state.Problem);
    }

    [Fact]
    public void An_unknown_job_reports_lost_the_way_it_always_did()
    {
        var state = Jobs().State("made-up-job");

        Assert.Equal(NoteJobOutcome.Lost, state.Kind());
        Assert.False(state.Done);
        Assert.True(state.Gone);
    }

    [Fact]
    public async Task The_stop_switch_is_let_go_of_once_after_the_job_ends_and_after_it_is_swept()
    {
        var jobs = Jobs();
        var release = new TaskCompletionSource();
        var id = jobs.Start(async _ =>
        {
            await release.Task;
            return Written();
        })!.Id;
        var stop = Assert.Single(_stops);
        Assert.Equal(0, stop.Disposed);

        release.SetResult();
        await Until(() => stop.Disposed == 1);

        // Nobody came back for it, so it's forgotten an hour later, and the switch isn't let go of again.
        _now = _now.AddHours(2);
        Assert.Equal(NoteJobOutcome.Lost, jobs.State(id).Kind());
        Assert.Equal(1, stop.Disposed);
    }

    [Fact]
    public async Task Every_way_a_job_ends_lets_go_of_its_stop_switch_once()
    {
        var jobs = Jobs();
        var read = jobs.Start(_ => Task.FromResult(Written()))!.Id;
        var failed = jobs.Start(_ => throw new TimeoutException("Claude Code took too long to respond."))!.Id;
        await Finished(jobs, read);
        await Finished(jobs, failed);

        var stopped = jobs.Start(async stop =>
        {
            await Task.Delay(Timeout.Infinite, stop);
            return Written();
        })!.Id;
        jobs.Stop(stopped);
        await Finished(jobs, stopped);

        var shutDown = jobs.Start(async stop =>
        {
            await Task.Delay(Timeout.Infinite, stop);
            return Written();
        })!.Id;
        jobs.StopAll();
        Assert.Equal(NoteJobOutcome.Cancelled, (await Finished(jobs, shutDown)).Kind());

        await Until(() =>
        {
            lock (_stops)
            {
                return _stops.Count == 4 && _stops.All(stop => stop.Disposed == 1);
            }
        });
    }

    [Fact]
    public async Task Stopping_a_job_after_it_ended_does_nothing()
    {
        var jobs = Jobs();
        var id = jobs.Start(_ => Task.FromResult(Written()))!.Id;
        var stop = Assert.Single(_stops);
        await Until(() => stop.Disposed == 1);

        // Still kept for the PC to read, with its switch already let go of.
        jobs.Stop(id);
        jobs.StopAll();
        Assert.Equal(NoteJobOutcome.Succeeded, jobs.State(id).Kind());

        // And once read, it's gone.
        jobs.Stop(id);
        Assert.Equal(NoteJobOutcome.Lost, jobs.State(id).Kind());
    }

    [Fact]
    public async Task Stopping_while_a_job_ends_never_trips_over_a_let_go_switch()
    {
        var jobs = new NoteJobs(_log, maxRunning: 1000);
        for (var round = 0; round < 200; round++)
        {
            var release = new TaskCompletionSource();
            var id = jobs.Start(async stop =>
            {
                await release.Task.WaitAsync(stop);
                return Written();
            })!.Id;

            var stopping = Task.Run(() =>
            {
                for (var i = 0; i < 50; i++)
                {
                    jobs.Stop(id);
                }
            });
            release.TrySetResult();
            await stopping;
            var state = await Finished(jobs, id);
            Assert.Contains(state.Kind(), new[] { NoteJobOutcome.Succeeded, NoteJobOutcome.Cancelled });
        }
    }

    [Fact]
    public async Task Only_so_many_run_at_once_and_a_finished_one_makes_room()
    {
        var jobs = new NoteJobs(_log, maxRunning: 1);
        var release = new TaskCompletionSource();
        var first = jobs.Start(async _ =>
        {
            await release.Task;
            return Written();
        });

        Assert.NotNull(first);
        Assert.Null(jobs.Start(_ => Task.FromResult(Written())));

        release.SetResult();
        await Finished(jobs, first.Id);
        NoteJobStarted? second = null;
        await Until(() => (second ??= jobs.Start(_ => Task.FromResult(Written()))) is not null);
    }

    [Theory]
    [InlineData("""{"done":false,"gone":false}""", NoteJobOutcome.Running)]
    [InlineData("""{"done":true,"gone":false}""", NoteJobOutcome.Succeeded)]
    [InlineData("""{"done":false,"gone":true}""", NoteJobOutcome.Lost)]
    public void A_server_from_before_outcomes_is_still_understood(string json, NoteJobOutcome expected)
    {
        var state = JsonSerializer.Deserialize<NoteJobState>(json, RemoteJson.Options)!;

        Assert.Equal(expected, state.Kind());
    }

    [Fact]
    public void A_failed_job_reads_as_finished_to_a_pc_from_before_outcomes()
    {
        var json = JsonSerializer.Serialize(NoteJobState.Failed("It took too long."), RemoteJson.Options);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(root.GetProperty("done").GetBoolean());
        Assert.False(root.GetProperty("gone").GetBoolean());
        Assert.Equal("Failed", root.GetProperty("outcome").GetString());
        Assert.Equal("It took too long.", root.GetProperty("problem").GetString());
    }
}
