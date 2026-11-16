using System.Text.RegularExpressions;

namespace Waypoint.Server;

/// <summary>
/// Puts sorting failures in the server's log, so `journalctl -u waypoint` shows why notes are waiting. Each distinct
/// error is logged once, not on every retry, and again only after sorting has worked in between. Only the error's
/// first line goes in, cut short and with anything that looks like a key taken out, and never what was typed.
/// </summary>
public sealed partial class SortingFailureLog(ILogger logger)
{
    private const int MaxLength = 200;

    private readonly Lock _gate = new();
    private string? _logged;

    public void Failed(string error)
    {
        var line = Clean(error);
        lock (_gate)
        {
            if (line == _logged)
            {
                return;
            }

            _logged = line;
        }

        logger.LogWarning("Sorting failed: {Error} Notes stay saved and are sorted once it works again.", line);
    }

    /// <summary>A capture was sorted, so the next failure is news again, even if it's the same as before.</summary>
    public void Worked()
    {
        lock (_gate)
        {
            _logged = null;
        }
    }

    /// <summary>The first line, with keys and bearer tokens taken out, cut to a length a log line can carry.</summary>
    public static string Clean(string error)
    {
        var line = error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        line = Secret().Replace(line, "[hidden]");
        if (line.Length > MaxLength)
        {
            line = line[..MaxLength] + "...";
        }

        return line.Length == 0 ? "no message" : line;
    }

    [GeneratedRegex(@"(sk-[A-Za-z0-9_\-]{8,}|(?i:bearer)\s+\S+)")]
    private static partial Regex Secret();
}
