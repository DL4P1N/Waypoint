using System.IO;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Waypoint.App;
using Waypoint.Core;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Email;

namespace Waypoint.App.Tests;

/// <summary>
/// Closing the app the way App.OnExit does, without starting Waypoint: drafts are saved first, then a round of
/// reminder checks still running is waited for, and only then does closing end and the lock go. The worker is a real
/// one on a temp database, with no Discord, and its round is held on a gate until the test lets it go.
/// </summary>
public sealed class AppClosingTests : IDisposable
{
    private static readonly DateTime Now = new(2030, 5, 14, 13, 0, 0);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-closing-{Guid.NewGuid():N}");
    private readonly WaypointDatabase _database;

    public AppClosingTests()
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
    public void Closing_saves_drafts_then_waits_for_the_reminder_round_still_running_before_it_ends() => Ui.Run(ui =>
    {
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var worker = HeldWorker(inside, release);
        var round = worker.StartAlertRound();
        Assert.True(inside.Wait(TimeSpan.FromSeconds(30)));

        var steps = new List<string>();
        var problems = new List<Exception>();
        using var closingSaid = new EventWaitHandle(false, EventResetMode.ManualReset);

        // Let go from another thread a moment after closing starts waiting, once the drafts are already saved.
        var letGo = Task.Run(() =>
        {
            Assert.True(closingSaid.WaitOne(TimeSpan.FromSeconds(30)));
            Thread.Sleep(300);
            lock (steps)
            {
                steps.Add("round let go");
            }

            release.Set();
        });

        void Did(string step)
        {
            lock (steps)
            {
                steps.Add(step);
            }
        }

        new AppClosing
        {
            Closing = closingSaid,
            FinishPageWork = _ =>
            {
                Did("page work");
                return [];
            },
            SaveDrafts = () =>
            {
                _database.SetPreference("made_up_draft", "Half typed made up note");
                Did("drafts saved");
            },
            SavePlace = () => Did("place saved"),
            Worker = worker,
            StopIntake = () => Did("intake stopped"),
            Release = () => Did("released"),
            DrainLimit = TimeSpan.FromSeconds(30),
            LogError = problems.Add,
            LogText = text => problems.Add(new InvalidOperationException(text)),
        }.Run(Dispatcher.CurrentDispatcher);

        // What comes after Run in OnExit is the process ending, so by now the round is over and everything is saved.
        Did("closing done");
        Assert.True(round.IsCompleted);
        Assert.True(letGo.Wait(TimeSpan.FromSeconds(30)));
        Assert.Empty(problems);
        Assert.Equal("Half typed made up note", _database.GetPreference("made_up_draft"));
        Assert.Equal(["page work", "drafts saved", "place saved", "intake stopped", "round let go", "released", "closing done"], steps);
        ui.Pump();
    });

    [Fact]
    public void Closing_ends_once_its_limit_is_up_when_the_reminder_round_doesnt_finish() => Ui.Run(ui =>
    {
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var worker = HeldWorker(inside, release);
        var round = worker.StartAlertRound();
        Assert.True(inside.Wait(TimeSpan.FromSeconds(30)));
        try
        {
            var released = false;
            var started = System.Diagnostics.Stopwatch.StartNew();
            new AppClosing
            {
                Worker = worker,
                Release = () => released = true,
                DrainLimit = TimeSpan.FromMilliseconds(300),
                LogError = exception => throw exception,
                LogText = _ => { },
            }.Run(Dispatcher.CurrentDispatcher);

            Assert.True(released);
            Assert.False(round.IsCompleted);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(20));
        }
        finally
        {
            release.Set();
            Assert.True(round.Wait(TimeSpan.FromSeconds(30)));
        }

        ui.Pump();
    });

    /// <summary>A worker whose next round of reminder checks waits inside it until <paramref name="release"/> is set.</summary>
    private WaypointWorker HeldWorker(ManualResetEventSlim inside, ManualResetEventSlim release)
    {
        var caller = Environment.CurrentManagedThreadId;
        var entered = 0;
        var armed = false;
        DateTime Clock()
        {
            if (Volatile.Read(ref armed) && Environment.CurrentManagedThreadId != caller && Interlocked.Increment(ref entered) == 1)
            {
                inside.Set();
                release.Wait(TimeSpan.FromSeconds(60));
            }

            return Now;
        }

        var worker = new WaypointWorker(_database, new WaypointSettings(), _folder, new QuietHost(), exception => throw exception, new WorkerParts(
            _ => new NoTokens(),
            Clock: Clock,
            CaptureSorter: new NeverSorts(),
            ConnectGateway: false));
        Volatile.Write(ref armed, true);
        return worker;
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

    private sealed class NoTokens : ITokenStore
    {
        public string? Load() => null;

        public void Save(string token)
        {
        }
    }

    private sealed class NeverSorts : ICaptureSorter
    {
        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<CaptureResult?>(null);
    }
}
