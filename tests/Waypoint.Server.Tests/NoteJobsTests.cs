using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;
using Waypoint.Core.Remote;

namespace Waypoint.Server.Tests;

/// <summary>
/// The server's note writing jobs on their own: how each one ends, what the PC hears about it, and that each job's
/// stop switch is let go of once, after the job is over, however it ended. Jobs are kept in a database in a temporary
/// folder, and a restart is a new set of jobs over the same file.
/// </summary>
public sealed class NoteJobsTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly ListLogger _log = new();
    private readonly List<CountedStop> _stops = [];
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-jobs-{Guid.NewGuid()}");
    private readonly WaypointDatabase _database;
    private DateTime _now = new(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc);

    public NoteJobsTests()
    {
        Directory.CreateDirectory(_folder);
        _database = new WaypointDatabase(DatabasePath);
    }

    private string DatabasePath => Path.Combine(_folder, "waypoint.db");

    /// <summary>A job a test didn't wait for may still be keeping how it ended, so the folder is let go of a moment later.</summary>
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

    /// <summary>The jobs as the server holds them after a restart: nothing in memory, the same database file.</summary>
    private NoteJobs Restarted() => new(_log, new WaypointDatabase(DatabasePath), utcNow: () => _now);

    private NoteJobs Jobs() => new(_log, _database, utcNow: () => _now, newStop: () =>
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
        var id = jobs.Start(NoteJobKind.Notes, 7, _ => Task.FromResult(Written()))!.Id;

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
        var id = jobs.Start(NoteJobKind.Notes, 7, _ => throw new IOException(@"Could not read 'C:\Users\sam\notes\week 3.md' with key sk-made-up-key-123456 and prompt Summarize cells"))!.Id;

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
        var id = jobs.Start(NoteJobKind.Notes, 7, _ => throw new InvalidOperationException("Not logged in · Please run /login"))!.Id;

        var state = await Finished(jobs, id);

        Assert.Equal(NoteJobOutcome.Failed, state.Kind());
        Assert.Equal("Claude isn't signed in.", state.Problem);
    }

    [Fact]
    public async Task A_stopped_job_reports_cancelled()
    {
        var jobs = Jobs();
        var id = jobs.Start(NoteJobKind.Notes, 7, async stop =>
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
    public async Task A_start_sent_again_with_the_same_key_gives_back_the_same_job_and_runs_it_once()
    {
        var jobs = Jobs();
        var runs = 0;
        var release = new TaskCompletionSource();
        async Task<NoteJobState> Work(CancellationToken stop)
        {
            Interlocked.Increment(ref runs);
            await release.Task;
            return Written();
        }

        var first = jobs.Start(NoteJobKind.Notes, 7, Work, once: "pc-1:write")!.Id;
        var again = jobs.Start(NoteJobKind.Notes, 7, Work, once: "pc-1:write")!.Id;
        var other = jobs.Start(NoteJobKind.Notes, 7, Work, once: "pc-1:other")!.Id;

        // Two are running, so a new start is turned away, but the same one sent again is still given back.
        Assert.Null(jobs.Start(NoteJobKind.Notes, 7, Work, once: "pc-1:third"));
        Assert.Equal(first, jobs.Start(NoteJobKind.Notes, 7, Work, once: "pc-1:write")!.Id);

        release.SetResult();
        Assert.Equal(NoteJobOutcome.Succeeded, (await Finished(jobs, first)).Kind());
        await Finished(jobs, other);

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.Equal(first, jobs.Start(NoteJobKind.Notes, 7, Work, once: "pc-1:write")!.Id);
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task A_key_whose_job_failed_starts_a_new_one()
    {
        var jobs = Jobs();
        var failed = jobs.Start(NoteJobKind.Notes, 7, _ => throw new IOException("made-up failure"), once: "pc-1:write")!.Id;
        Assert.Equal(NoteJobOutcome.Failed, (await Finished(jobs, failed)).Kind());

        var again = jobs.Start(NoteJobKind.Notes, 7, _ => Task.FromResult(Written()), once: "pc-1:write", out var given)!.Id;

        Assert.False(given);
        Assert.NotEqual(failed, again);
        Assert.Equal(NoteJobOutcome.Succeeded, (await Finished(jobs, again)).Kind());
        Assert.Equal(again, jobs.Start(NoteJobKind.Notes, 7, _ => Task.FromResult(Written()), once: "pc-1:write", out var givenBack)!.Id);
        Assert.True(givenBack);
    }

    [Fact]
    public async Task A_key_whose_job_was_forgotten_starts_a_new_one()
    {
        var jobs = Jobs();
        var first = jobs.Start(NoteJobKind.Notes, 7, _ => Task.FromResult(Written()), once: "pc-1:write")!.Id;
        await Finished(jobs, first);

        _now = _now.AddHours(2);
        var later = jobs.Start(NoteJobKind.Notes, 7, _ => Task.FromResult(Written()), once: "pc-1:write")!.Id;

        Assert.NotEqual(first, later);
        Assert.Equal(NoteJobOutcome.Succeeded, (await Finished(jobs, later)).Kind());
    }

    [Fact]
    public async Task A_job_whose_ending_cant_be_kept_reads_as_lost_not_running()
    {
        var jobs = Jobs();
        using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            connection.Open();
            using var refuse = connection.CreateCommand();
            refuse.CommandText = "CREATE TRIGGER no_endings BEFORE UPDATE ON note_jobs BEGIN SELECT RAISE(ABORT, 'made-up full disk'); END;";
            refuse.ExecuteNonQuery();
        }

        var id = jobs.Start(NoteJobKind.Notes, 7, _ => Task.FromResult(Written()))!.Id;

        var state = await Finished(jobs, id);

        Assert.Equal(NoteJobOutcome.Lost, state.Kind());
        Assert.Equal(NoteJobOutcome.Running, _database.GetNoteJob(id)!.Outcome);
        Assert.Contains(_log.Entries, entry => entry.Message.Contains("couldn't be kept"));
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
        var id = jobs.Start(NoteJobKind.Notes, 7, async _ =>
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
        var read = jobs.Start(NoteJobKind.Notes, 7, _ => Task.FromResult(Written()))!.Id;
        var failed = jobs.Start(NoteJobKind.Notes, 7, _ => throw new TimeoutException("Claude Code took too long to respond."))!.Id;
        await Finished(jobs, read);
        await Finished(jobs, failed);

        var stopped = jobs.Start(NoteJobKind.Notes, 7, async stop =>
        {
            await Task.Delay(Timeout.Infinite, stop);
            return Written();
        })!.Id;
        jobs.Stop(stopped);
        await Finished(jobs, stopped);

        var shutDown = jobs.Start(NoteJobKind.Notes, 7, async stop =>
        {
            await Task.Delay(Timeout.Infinite, stop);
            return Written();
        })!.Id;
        jobs.StopAll();
        var cut = await Finished(jobs, shutDown);
        Assert.Equal(NoteJobOutcome.Lost, cut.Kind());
        Assert.True(cut.Interrupted);

        await Until(() =>
        {
            lock (_stops)
            {
                return _stops.Count == 4 && _stops.All(stop => stop.Disposed == 1);
            }
        });
    }

    [Theory]
    [InlineData(NoteJobKind.Notes)]
    [InlineData(NoteJobKind.Study)]
    public async Task A_job_that_tells_its_own_stop_is_kept_as_interrupted_when_the_server_shuts_down(NoteJobKind kind)
    {
        var jobs = Jobs();

        // As writing notes and questions do: a stop is caught and told as its own outcome, and the job returns.
        Func<CancellationToken, Task<NoteJobState>> work = async stop =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, stop);
                return Written();
            }
            catch (OperationCanceledException)
            {
                return kind == NoteJobKind.Notes
                    ? new NoteJobState(Done: true, Notes: NoteJobText.NotesStopped())
                    : new NoteJobState(Done: true, Study: NoteJobText.StudyStopped() with { NoteId = 7 });
            }
        };

        var byPerson = jobs.Start(kind, 7, work)!.Id;
        jobs.Stop(byPerson);
        var stopped = await Finished(jobs, byPerson);
        Assert.Equal(NoteJobOutcome.Succeeded, stopped.Kind());
        Assert.Equal(
            kind == NoteJobKind.Notes ? NoteJobText.NotesStopped().Message : NoteJobText.StudyStopped().Message,
            stopped.Notes?.Message ?? stopped.Study?.Message);

        var byShutdown = jobs.Start(kind, 7, work)!.Id;
        jobs.StopAll();
        var cut = await Finished(jobs, byShutdown);
        Assert.Equal(NoteJobOutcome.Lost, cut.Kind());
        Assert.True(cut.Interrupted);
        Assert.Null(cut.Notes);
        Assert.Null(cut.Study);
    }

    [Fact]
    public async Task Stopping_a_job_after_it_ended_does_nothing()
    {
        var jobs = Jobs();
        var id = jobs.Start(NoteJobKind.Notes, 7, _ => Task.FromResult(Written()))!.Id;
        var stop = Assert.Single(_stops);
        await Until(() => stop.Disposed == 1);

        // Still kept for the PC to read, with its switch already let go of.
        jobs.Stop(id);
        jobs.StopAll();
        Assert.Equal(NoteJobOutcome.Succeeded, jobs.State(id).Kind());

        // And it's still told the same when asked again, as when an answer was lost on the way.
        jobs.Stop(id);
        Assert.Equal(NoteJobOutcome.Succeeded, jobs.State(id).Kind());
    }

    [Fact]
    public async Task Stopping_while_a_job_ends_never_trips_over_a_let_go_switch()
    {
        var jobs = new NoteJobs(_log, _database, maxRunning: 1000);
        for (var round = 0; round < 200; round++)
        {
            var release = new TaskCompletionSource();
            var id = jobs.Start(NoteJobKind.Notes, 7, async stop =>
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
        var jobs = new NoteJobs(_log, _database, maxRunning: 1);
        var release = new TaskCompletionSource();
        var first = jobs.Start(NoteJobKind.Notes, 7, async _ =>
        {
            await release.Task;
            return Written();
        });

        Assert.NotNull(first);
        Assert.Null(jobs.Start(NoteJobKind.Notes, 7, _ => Task.FromResult(Written())));

        release.SetResult();
        await Finished(jobs, first.Id);
        NoteJobStarted? second = null;
        await Until(() => (second ??= jobs.Start(NoteJobKind.Notes, 7, _ => Task.FromResult(Written()))) is not null);
    }

    [Fact]
    public async Task A_job_finished_before_a_restart_still_reports_how_it_ended()
    {
        var jobs = Jobs();
        var written = jobs.Start(NoteJobKind.Notes, 7, _ => Task.FromResult(Written()))!.Id;
        var failed = jobs.Start(NoteJobKind.Study, null, _ => throw new IOException(@"Could not read 'C:\Users\sam\notes\week 3.md' with prompt Summarize cells"))!.Id;
        await Finished(jobs, written);
        await Finished(jobs, failed);
        var stopped = jobs.Start(NoteJobKind.Notes, 8, async stop =>
        {
            await Task.Delay(Timeout.Infinite, stop);
            return Written();
        })!.Id;
        jobs.Stop(stopped);
        await Finished(jobs, stopped);

        var after = Restarted();

        var succeeded = after.State(written);
        Assert.Equal(NoteJobOutcome.Succeeded, succeeded.Kind());
        Assert.True(succeeded.Done);
        Assert.Equal(NoteWriteStatus.Written, succeeded.Notes!.Status);
        Assert.Equal("Notes written for Biology 101.", succeeded.Notes.Message);
        var broke = after.State(failed);
        Assert.Equal(NoteJobOutcome.Failed, broke.Kind());
        Assert.Equal("A file it needed is open in another program or couldn't be reached.", broke.Problem);
        Assert.Equal(NoteJobOutcome.Cancelled, after.State(stopped).Kind());

        // Only the plain reason is kept, never the exception's own text.
        var kept = _database.GetNoteJob(failed)!;
        Assert.Equal(NoteJobKind.Study, kept.Kind);
        Assert.Null(kept.Result);
        Assert.DoesNotContain("sam", kept.Problem);
        Assert.DoesNotContain("Summarize", kept.Problem);
        Assert.Equal(7, _database.GetNoteJob(written)!.NoteId);
    }

    [Fact]
    public async Task A_job_running_at_a_restart_is_told_as_interrupted_and_never_run_again()
    {
        var jobs = Jobs();
        var release = new TaskCompletionSource();
        var runs = 0;
        var id = jobs.Start(NoteJobKind.Notes, 7, async _ =>
        {
            Interlocked.Increment(ref runs);
            await release.Task;
            return Written();
        })!.Id;
        await Until(() => Volatile.Read(ref runs) == 1);

        var after = Restarted();
        var state = after.State(id);

        Assert.Equal(NoteJobOutcome.Lost, state.Kind());
        Assert.True(state.Interrupted);
        Assert.True(state.Gone);
        Assert.False(state.Done);
        Assert.Equal(NoteJobOutcome.Running, NoteJobState.Running().Kind());

        // The PC reads it as it reads any lost job, and tells the person the server restarted while writing them.
        var json = JsonSerializer.Serialize(state, RemoteJson.Options);
        var read = JsonSerializer.Deserialize<NoteJobState>(json, RemoteJson.Options)!;
        Assert.Equal(NoteJobOutcome.Lost, read.Kind());
        Assert.True(read.Interrupted);
        Assert.Contains("server restarted while writing them", NoteJobText.NotesLost().Message);

        // A job cut off that way never ends up told as anything else, even if the old one were to finish.
        release.SetResult();
        var stop = Assert.Single(_stops);
        await Until(() => stop.Disposed == 1);
        Assert.True(after.State(id).Interrupted);
        Assert.Equal(1, Volatile.Read(ref runs));
    }

    [Fact]
    public void An_unknown_job_is_still_lost_after_a_restart_and_not_interrupted()
    {
        var state = Restarted().State("made-up-job");

        Assert.Equal(NoteJobOutcome.Lost, state.Kind());
        Assert.True(state.Gone);
        Assert.False(state.Interrupted);
    }

    [Fact]
    public async Task Finished_jobs_are_pruned_an_hour_after_they_end_and_running_ones_are_kept()
    {
        var jobs = Jobs();
        var done = jobs.Start(NoteJobKind.Notes, 7, _ => Task.FromResult(Written()))!.Id;
        await Finished(jobs, done);
        var release = new TaskCompletionSource();
        var running = jobs.Start(NoteJobKind.Notes, 8, async _ =>
        {
            await release.Task;
            return Written();
        })!.Id;

        _now = _now.AddMinutes(59);
        Assert.Equal(NoteJobOutcome.Succeeded, jobs.State(done).Kind());

        _now = _now.AddMinutes(2);
        Assert.Equal(NoteJobOutcome.Lost, jobs.State(done).Kind());

        // Asking only reads. The sweep comes with the next start.
        Assert.NotNull(_database.GetNoteJob(done));
        var next = jobs.Start(NoteJobKind.Notes, 9, _ => Task.FromResult(Written()))!.Id;
        Assert.Null(_database.GetNoteJob(done));
        Assert.Equal(NoteJobOutcome.Running, jobs.State(running).Kind());
        Assert.NotNull(_database.GetNoteJob(running));
        await Finished(jobs, next);

        release.SetResult();
        Assert.Equal(NoteJobOutcome.Succeeded, (await Finished(jobs, running)).Kind());
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
