using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Waypoint.Core.Notes;

/// <summary>One structured call to Claude. Tests stand in a fake, so nothing real is ever called from them.</summary>
public interface IClaudeRunner
{
    /// <summary>Returns the CLI's JSON output, with the answer under structured_output.</summary>
    Task<string> RunAsync(string systemPrompt, string schema, string input, CancellationToken cancellationToken);
}

/// <summary>
/// Runs <c>claude -p</c> the same way <see cref="Capture.ClaudeCliSorter"/> does: Haiku at low effort, no tools,
/// and with API keys taken out of the environment so the signed-in Claude plan is used and nothing is billed to a key.
/// </summary>
public sealed class ClaudeCliRunner(string workingDirectory, TimeSpan? timeout = null) : IClaudeRunner
{
    public const string Model = "claude-haiku-5-5";

    /// <summary>Notes from a long file take longer than sorting a line, so the default wait is longer too.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(4);

    // Removed so the CLI always uses the signed-in plan and can't bill an API key. CLAUDE_CODE_OAUTH_TOKEN, from
    // claude setup-token, is that plan's sign-in, so it stays for a server that runs unattended.
    private static readonly string[] ClearedEnvironmentVariables =
    [
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
        "CLAUDECODE",
        "CLAUDE_CODE_ENTRYPOINT",
    ];

    public async Task<string> RunAsync(string systemPrompt, string schema, string input, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? DefaultTimeout);

        using var process = Process.Start(CreateStartInfo(systemPrompt, schema))!;
        var output = process.StandardOutput.ReadToEndAsync(limit.Token);
        var errors = process.StandardError.ReadToEndAsync(limit.Token);

        try
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
            await process.WaitForExitAsync(limit.Token);

            var json = await output;
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidOperationException($"Claude Code exited with code {process.ExitCode}. {await errors}".Trim());
            }

            return json;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Claude Code took too long to respond.");
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
    }

    /// <summary>The structured answer from the CLI's JSON output, or null if it finished without one. Reported errors throw.</summary>
    public static JsonElement? ReadStructured(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
        {
            var message = root.TryGetProperty("result", out var result) ? result.GetString() : null;
            throw new InvalidOperationException(message ?? "Claude Code reported an error.");
        }

        return root.TryGetProperty("structured_output", out var structured) && structured.ValueKind == JsonValueKind.Object
            ? structured.Clone()
            : null;
    }

    /// <summary>The arguments and environment for one call, kept apart so tests can check them without running anything.</summary>
    public ProcessStartInfo CreateStartInfo(string systemPrompt, string schema)
    {
        var startInfo = new ProcessStartInfo(Platform.ClaudeCliPath.Find())
        {
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in new[]
        {
            "-p",
            "--model", Model,
            "--effort", "low",
            "--tools", "",
            "--setting-sources", "",
            "--strict-mcp-config",
            "--no-session-persistence",
            "--output-format", "json",
            "--system-prompt", systemPrompt,
            "--json-schema", schema,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var name in ClearedEnvironmentVariables)
        {
            startInfo.Environment.Remove(name);
        }

        return startInfo;
    }
}
