using Waypoint.Core.Background;

namespace Waypoint.Core.Tests;

/// <summary>A trial run beside the installed copy starts nothing that reaches out or speaks up on its own.</summary>
public sealed class TrialRunTests
{
    private static Func<string, string?> Environment(params (string Name, string Value)[] set) =>
        name => set.FirstOrDefault(variable => variable.Name == name).Value;

    [Fact]
    public void A_trial_run_starts_no_alerts_connections_or_recording_offer()
    {
        var trial = TrialRun.From(@"C:\Trial", Environment((TrialRun.TrialVariable, "1")));

        Assert.True(trial.Active);
        Assert.False(trial.StartsAlerts);
        Assert.False(trial.StartsConnections);
        Assert.False(trial.OffersRecording);
        Assert.Equal("Trial run: alerts and connections are off.", trial.Notice);
    }

    [Fact]
    public void Alerts_can_be_turned_on_for_testing_but_connections_stay_off()
    {
        var trial = TrialRun.From(@"C:\Trial", Environment((TrialRun.TrialVariable, "1"), (TrialRun.AlertsVariable, "1")));

        Assert.True(trial.StartsAlerts);
        Assert.False(trial.StartsConnections);
        Assert.False(trial.OffersRecording);
        Assert.Contains("Alerts are on for testing", trial.Notice);
    }

    [Theory]
    [InlineData(null, "1")]
    [InlineData(@"C:\Trial", null)]
    [InlineData(@"C:\Trial", "0")]
    public void Without_both_a_data_folder_and_the_trial_flag_everything_starts(string? folder, string? flag)
    {
        var trial = TrialRun.From(folder, name => name == TrialRun.TrialVariable ? flag : "1");

        Assert.False(trial.Active);
        Assert.True(trial.StartsAlerts);
        Assert.True(trial.StartsConnections);
        Assert.True(trial.OffersRecording);
        Assert.Null(trial.Notice);
    }

    [Fact]
    public void The_connections_list_says_it_is_a_trial_run_first()
    {
        IReadOnlyList<ConnectionStatus> connections = [new ConnectionStatus("Discord", "Not set up.")];

        var shown = TrialRun.From(@"C:\Trial", Environment((TrialRun.TrialVariable, "1"))).Describe(connections);

        Assert.Equal(["Trial run", "Discord"], shown.Select(connection => connection.Name));

        // The row is read as "Name: State", so the state mustn't say "Trial run:" again.
        Assert.Equal("Trial run: Alerts and connections are off.", shown[0].ToString());
        Assert.Same(connections, TrialRun.Off.Describe(connections));
    }

    [Fact]
    public void The_tray_tip_says_trial_in_front_of_every_status()
    {
        var trial = TrialRun.From(@"C:\Trial", Environment((TrialRun.TrialVariable, "1")));

        Assert.Equal("Waypoint (trial): 2 notes waiting for a response", trial.TrayTip("Waypoint: 2 notes waiting for a response", 127));
        Assert.Equal("Waypoint (trial): sorting failed. claude exited", trial.TrayTip("Waypoint: sorting failed. claude exited", 127));
        Assert.Equal("Waypoint (trial): recording 0:42", trial.TrayTip("Waypoint: recording 0:42", 127));
        Assert.Equal("Waypoint (trial). Alerts and connections are off.", trial.TrayTip("Waypoint", 127));

        // A long status is cut at the end, so the label stays and the tip fits the tray's limit.
        var cut = trial.TrayTip("Waypoint: sorting failed. " + new string('x', 300), 127);
        Assert.Equal(127, cut.Length);
        Assert.StartsWith("Waypoint (trial): sorting failed.", cut);
    }

    [Fact]
    public void The_installed_copy_keeps_its_tray_tip_as_it_was()
    {
        Assert.Equal("Waypoint", TrialRun.Off.TrayTip("Waypoint", 127));
        Assert.Equal("Waypoint: 1 note waiting for a response", TrialRun.Off.TrayTip("Waypoint: 1 note waiting for a response", 127));
        Assert.Equal(127, TrialRun.Off.TrayTip(new string('x', 300), 127).Length);
    }
}
