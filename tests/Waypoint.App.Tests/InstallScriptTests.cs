using System.Diagnostics;
using System.IO;

namespace Waypoint.App.Tests;

/// <summary>
/// Runs install.ps1 as a trial on a temp -InstallRoot with made-up builds. Each build's Waypoint.exe is a few bytes of
/// text, so Windows refuses to start it and nothing ever opens: that is how a version that can't be started (held back
/// by antivirus, say) looks to the installer.
/// </summary>
public sealed class InstallScriptTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-install-test-{Guid.NewGuid():N}");
    private readonly string _root;

    public InstallScriptTests()
    {
        _root = Path.Combine(_folder, "root");
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_new_version_that_cant_be_started_is_taken_out_and_the_one_before_put_back()
    {
        var first = MakeBuild("one", ("extra.dll", "made-up library"));
        var second = MakeBuild("two");

        Assert.NotEqual(0, RunInstall("-PublishedFolder", first).ExitCode);
        var firstVersion = File.ReadAllText(Path.Combine(_root, "current.txt")).Trim();

        var (exitCode, output) = RunInstall("-PublishedFolder", second);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("put back", output);
        Assert.Equal(firstVersion, File.ReadAllText(Path.Combine(_root, "current.txt")).Trim());
        Assert.Equal("made-up build one", File.ReadAllText(Path.Combine(_root, "Waypoint.exe")));
        Assert.True(File.Exists(Path.Combine(_root, "extra.dll")));
        Assert.Equal([firstVersion], Versions());
        Assert.False(File.Exists(Path.Combine(_root, "previous.txt")));
    }

    [Fact]
    public void Rollback_goes_to_the_version_that_ran_before_and_back_again_never_to_a_newer_build()
    {
        MakeInstalled(current: "20300101-000002-b", previous: "20300101-000001-a", "20300101-000001-a", "20300101-000003-c");

        RunInstall("-Rollback");

        Assert.Equal("20300101-000001-a", File.ReadAllText(Path.Combine(_root, "current.txt")).Trim());
        Assert.Equal("made-up build 20300101-000001-a", File.ReadAllText(Path.Combine(_root, "Waypoint.exe")));
        Assert.Equal("20300101-000002-b", File.ReadAllText(Path.Combine(_root, "previous.txt")).Trim());

        RunInstall("-Rollback");

        Assert.Equal("20300101-000002-b", File.ReadAllText(Path.Combine(_root, "current.txt")).Trim());
    }

    [Fact]
    public void Rollback_with_nothing_noted_goes_to_the_newest_older_version()
    {
        MakeInstalled(current: "20300101-000002-b", previous: null, "20300101-000001-a", "20300101-000003-c");

        RunInstall("-Rollback");

        Assert.Equal("20300101-000001-a", File.ReadAllText(Path.Combine(_root, "current.txt")).Trim());
    }

    private string MakeBuild(string text, params (string Name, string Text)[] extra)
    {
        var folder = Path.Combine(_folder, $"build-{text}");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Waypoint.exe"), $"made-up build {text}");
        foreach (var (name, content) in extra)
        {
            File.WriteAllText(Path.Combine(folder, name), content);
        }

        return folder;
    }

    private void MakeInstalled(string current, string? previous, params string[] others)
    {
        foreach (var version in others.Append(current))
        {
            var folder = Path.Combine(_root, "versions", version);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Waypoint.exe"), $"made-up build {version}");
        }

        File.WriteAllText(Path.Combine(_root, "Waypoint.exe"), $"made-up build {current}");
        File.WriteAllText(Path.Combine(_root, "current.txt"), current);
        if (previous is not null)
        {
            File.WriteAllText(Path.Combine(_root, "previous.txt"), previous);
        }
    }

    private string[] Versions() =>
        Directory.GetDirectories(Path.Combine(_root, "versions")).Select(Path.GetFileName).OfType<string>().ToArray();

    private (int ExitCode, string Output) RunInstall(params string[] arguments)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script(),
                     "-InstallRoot", _root, "-DataDir", Path.Combine(_folder, "data"), "-StartCheckSeconds", "1" }.Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Remove("WAYPOINT_DATA_DIR");
        start.Environment.Remove("WAYPOINT_TRIAL");
        var error = new System.Text.StringBuilder();
        using var process = Process.Start(start)!;
        process.ErrorDataReceived += (_, line) => { lock (error) { error.AppendLine(line.Data); } };
        process.BeginErrorReadLine();
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(2)), "install.ps1 didn't finish");
        process.WaitForExit();
        lock (error)
        {
            return (process.ExitCode, output + error);
        }
    }

    private static string Script()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Waypoint.slnx")))
        {
            folder = folder.Parent;
        }

        Assert.NotNull(folder);
        return Path.Combine(folder.FullName, "install.ps1");
    }
}
