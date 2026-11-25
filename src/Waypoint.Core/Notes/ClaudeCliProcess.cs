using System.Diagnostics;

namespace Waypoint.Core.Notes;

/// <summary>
/// Runs one Claude CLI process with a single time limit over the whole call: the start, the write to stdin,
/// closing it, reading the output and waiting for exit. The whole process tree is killed on a timeout, a cancel or
/// any failure, so a child that stops reading its input or never exits can't hold a slot forever.
/// </summary>
public static class ClaudeCliProcess
{
    public static Task<string> RunAsync(ProcessStartInfo startInfo, string input, TimeSpan timeout, CancellationToken cancellationToken) =>
        RunAsync(startInfo, input, timeout, TimeProvider.System, cancellationToken);

    /// <param name="time">Where <paramref name="timeout"/> is measured, so a test can say when it runs out.</param>
    internal static async Task<string> RunAsync(ProcessStartInfo startInfo, string input, TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
    {
        using var timer = new CancellationTokenSource(timeout, time);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);

        Process? process = null;
        Task<string>? output = null;
        Task<string>? errors = null;
        try
        {
            limit.Token.ThrowIfCancellationRequested();
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Claude Code could not be started.");

            // A write into a pipe nobody reads can't be cancelled by a token, so the kill is what frees it.
            using var killOnCancel = limit.Token.Register(() => Kill(process));
            output = process.StandardOutput.ReadToEndAsync(limit.Token);
            errors = process.StandardError.ReadToEndAsync(limit.Token);

            await process.StandardInput.WriteAsync(input.AsMemory(), limit.Token);
            await process.StandardInput.FlushAsync(limit.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(limit.Token);

            var json = await output.WaitAsync(limit.Token);
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidOperationException($"Claude Code exited with code {process.ExitCode}. {await errors.WaitAsync(limit.Token)}".Trim());
            }

            return json;
        }
        catch (Exception) when (limit.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            throw new TimeoutException("Claude Code took too long to respond.");
        }
        finally
        {
            if (process is not null)
            {
                Kill(process);
                process.Dispose();
            }

            Observe(output);
            Observe(errors);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone, or already disposed.
        }
    }

    private static void Observe(Task? task) => task?.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
}
