namespace Waypoint.Core.Platform;

/// <summary>
/// Finds the Claude Code CLI. On Windows it's found on PATH as before. A Linux service often starts with a short
/// PATH that leaves out where the installer puts it, so the usual places are checked too.
/// WAYPOINT_CLAUDE_PATH names it outright on any system.
/// </summary>
public static class ClaudeCliPath
{
    public const string Variable = "WAYPOINT_CLAUDE_PATH";
    private const string Name = "claude";

    public static string Find() => Find(Environment.GetEnvironmentVariable, OperatingSystem.IsWindows(), File.Exists);

    /// <summary>Kept apart from the real environment so tests can check each case.</summary>
    public static string Find(Func<string, string?> variable, bool isWindows, Func<string, bool> exists)
    {
        if (variable(Variable) is { Length: > 0 } chosen)
        {
            return chosen;
        }

        if (isWindows)
        {
            return Name;
        }

        var folders = (variable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (variable("HOME") is { Length: > 0 } home)
        {
            folders.Add(Path.Combine(home, ".local", "bin"));
            folders.Add(Path.Combine(home, ".claude", "local"));
            folders.Add(Path.Combine(home, ".npm-global", "bin"));
        }

        folders.Add("/usr/local/bin");
        folders.Add("/usr/bin");

        return folders.Select(folder => Path.Combine(folder, Name)).FirstOrDefault(exists) ?? Name;
    }
}
