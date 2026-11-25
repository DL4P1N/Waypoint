using System.Diagnostics;
using Waypoint.Core.Notes;

namespace Waypoint.Core.Tests;

public class ClaudeCliProcessTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "wp-cli-process-" + Guid.NewGuid().ToString("N"));

    public ClaudeCliProcessTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private ProcessStartInfo Child(string body, out string pidFile)
    {
        pidFile = Path.Combine(_folder, "pid.txt");
        var start = new ProcessStartInfo("powershell.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", $"$PID | Set-Content -LiteralPath '{pidFile}'; {body}" })
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    /// <summary>
    /// Only so a hung test fails rather than hangs. Nothing here waits on the wall clock otherwise: the time limit
    /// runs out when the test says, so a slow machine only makes a test later, never wrong.
    /// </summary>
    private static readonly TimeSpan Hung = TimeSpan.FromSeconds(60);

    /// <summary>A clock whose timers go off only when the test calls <see cref="RunOut"/>, never on their own.</summary>
    private sealed class HandClock : TimeProvider
    {
        private readonly List<(TimerCallback Callback, object? State)> _timers = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_timers)
            {
                _timers.Add((callback, state));
            }

            return new StillTimer();
        }

        public void RunOut()
        {
            List<(TimerCallback Callback, object? State)> due;
            lock (_timers)
            {
                due = [.. _timers];
            }

            Assert.NotEmpty(due);
            foreach (var (callback, state) in due)
            {
                callback(state);
            }
        }

        private sealed class StillTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>Waits for <paramref name="file"/> to be written by the child, while the call is still going.</summary>
    private static async Task<string> WrittenAsync(string file, Task running)
    {
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < Hung)
        {
            if (running.IsCompleted)
            {
                await running;
                Assert.Fail("The call ended before the child was ready.");
            }

            try
            {
                if (File.Exists(file) && (await File.ReadAllTextAsync(file)).Trim() is { Length: > 0 } text)
                {
                    return text;
                }
            }
            catch (IOException)
            {
                // Still being written.
            }

            await Task.Delay(50);
        }

        Assert.Fail("The child never got that far.");
        return "";
    }

    /// <summary>The call's own exception, after waiting for it to end. A test that hangs fails here instead.</summary>
    private static async Task<Exception?> EndedAsync(Task running)
    {
        await Task.WhenAny(running, Task.Delay(Hung));
        Assert.True(running.IsCompleted, "The call never ended.");
        return await Record.ExceptionAsync(() => running);
    }

    private static async Task AssertGoneAsync(string pid)
    {
        var id = int.Parse(pid);
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < Hung)
        {
            try
            {
                using var still = Process.GetProcessById(id);
                if (still.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("The child process was still running.");
    }

    [Fact]
    public async Task A_child_that_never_reads_its_input_times_out_and_is_killed()
    {
        var start = Child("Start-Sleep -Seconds 120", out var pidFile);
        var clock = new HandClock();
        var running = Task.Run(() => ClaudeCliProcess.RunAsync(start, new string('x', 8_000_000), TimeSpan.FromSeconds(4), clock, CancellationToken.None));

        // The child is up and the input is stuck in a pipe it never reads, so only the time limit can end this.
        var pid = await WrittenAsync(pidFile, running);
        Assert.False(running.IsCompleted);
        clock.RunOut();

        Assert.IsType<TimeoutException>(await EndedAsync(running));
        await AssertGoneAsync(pid);
    }

    [Fact]
    public async Task A_child_that_reads_everything_but_never_exits_times_out_and_is_killed()
    {
        var readFile = Path.Combine(_folder, "read.txt");
        var start = Child($"[Console]::In.ReadToEnd() | Out-Null; 'read' | Set-Content -LiteralPath '{readFile}'; Start-Sleep -Seconds 120", out var pidFile);
        var clock = new HandClock();
        var running = Task.Run(() => ClaudeCliProcess.RunAsync(start, "hello", TimeSpan.FromSeconds(4), clock, CancellationToken.None));

        // All of the input was read, so the call is only waiting for an exit that never comes.
        var pid = await WrittenAsync(pidFile, running);
        await WrittenAsync(readFile, running);
        Assert.False(running.IsCompleted);
        clock.RunOut();

        Assert.IsType<TimeoutException>(await EndedAsync(running));
        await AssertGoneAsync(pid);
    }

    [Fact]
    public async Task Cancelling_while_the_child_is_not_reading_stops_it_and_is_not_a_timeout()
    {
        var start = Child("Start-Sleep -Seconds 120", out var pidFile);
        using var cancel = new CancellationTokenSource();

        // The time limit never runs out here, so only the cancel can end it.
        var running = Task.Run(() => ClaudeCliProcess.RunAsync(start, new string('x', 8_000_000), TimeSpan.FromMinutes(2), new HandClock(), cancel.Token));
        var pid = await WrittenAsync(pidFile, running);
        Assert.False(running.IsCompleted);
        await cancel.CancelAsync();

        Assert.IsAssignableFrom<OperationCanceledException>(await EndedAsync(running));
        await AssertGoneAsync(pid);
    }

    [Fact]
    public async Task A_child_that_answers_returns_its_output()
    {
        var start = Child("$in = [Console]::In.ReadToEnd(); Write-Output \"{`\"got`\":$($in.Length)}\"", out _);

        var running = Task.Run(() => ClaudeCliProcess.RunAsync(start, "hello", TimeSpan.FromSeconds(30), new HandClock(), CancellationToken.None));

        Assert.Null(await EndedAsync(running));
        Assert.Contains("\"got\":5", await running);
    }
}
