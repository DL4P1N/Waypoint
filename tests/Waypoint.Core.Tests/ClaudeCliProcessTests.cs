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

    private static async Task AssertGoneAsync(string pidFile)
    {
        var pid = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());
        for (var i = 0; i < 50; i++)
        {
            try
            {
                using var still = Process.GetProcessById(pid);
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
        var input = new string('x', 8_000_000);

        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => ClaudeCliProcess.RunAsync(start, input, TimeSpan.FromSeconds(4), CancellationToken.None));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed}.");
        await AssertGoneAsync(pidFile);
    }

    [Fact]
    public async Task A_child_that_reads_everything_but_never_exits_times_out_and_is_killed()
    {
        var start = Child("[Console]::In.ReadToEnd() | Out-Null; Start-Sleep -Seconds 120", out var pidFile);

        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => ClaudeCliProcess.RunAsync(start, "hello", TimeSpan.FromSeconds(4), CancellationToken.None));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed}.");
        await AssertGoneAsync(pidFile);
    }

    [Fact]
    public async Task Cancelling_while_the_child_is_not_reading_stops_it_and_is_not_a_timeout()
    {
        var start = Child("Start-Sleep -Seconds 120", out var pidFile);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClaudeCliProcess.RunAsync(start, new string('x', 8_000_000), TimeSpan.FromMinutes(2), cancel.Token));

        await AssertGoneAsync(pidFile);
    }

    [Fact]
    public async Task A_child_that_answers_returns_its_output()
    {
        var start = Child("$in = [Console]::In.ReadToEnd(); Write-Output \"{`\"got`\":$($in.Length)}\"", out _);

        var json = await ClaudeCliProcess.RunAsync(start, "hello", TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Contains("\"got\":5", json);
    }
}
