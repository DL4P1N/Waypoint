namespace Waypoint.Core.Background;

/// <summary>
/// A rehearsal beside the installed copy: WAYPOINT_TRIAL=1 with a data folder of its own. Its folder is often a copy
/// of the real one, so it starts nothing that reaches out or speaks up on its own: no reminders or alerts, no
/// Discord, email, or calendar, and no offer to record at class start. The pages and anything done by hand still
/// work. WAYPOINT_TRIAL_ALERTS=1 turns the reminders and alerts back on for testing them, and nothing else.
/// </summary>
public sealed record TrialRun(bool Active, bool Alerts)
{
    public const string TrialVariable = "WAYPOINT_TRIAL";
    public const string AlertsVariable = "WAYPOINT_TRIAL_ALERTS";

    public static readonly TrialRun Off = new(false, false);

    /// <summary>Only with a data folder of its own, so the installed copy can never be turned into a trial run.</summary>
    public static TrialRun From(string? dataDirectory, Func<string, string?> environment) =>
        !string.IsNullOrWhiteSpace(dataDirectory) && environment(TrialVariable) == "1"
            ? new TrialRun(true, environment(AlertsVariable) == "1")
            : Off;

    /// <summary>Discord, email, and the calendar feed.</summary>
    public bool StartsConnections => !Active;

    /// <summary>Reminders, class alerts, the wind-down, and notices the server left for this PC.</summary>
    public bool StartsAlerts => !Active || Alerts;

    /// <summary>The optional card at class start, which also stays off when alerts are turned on for testing.</summary>
    public bool OffersRecording => !Active;

    /// <summary>The line for Settings and the status, or null when this isn't a trial run.</summary>
    public string? Notice => !Active ? null
        : Alerts ? "Trial run: connections are off. Alerts are on for testing."
        : "Trial run: alerts and connections are off.";

    /// <summary>
    /// The tray's tip for a status such as "Waypoint: 2 notes waiting...". A trial run says "Waypoint (trial)" in front
    /// of every status, not only the idle one, so a trial copy is never mistaken for the installed one. The tip is cut
    /// to <paramref name="maxLength"/>, which keeps the label since only the end is cut.
    /// </summary>
    public string TrayTip(string status, int maxLength)
    {
        var tip = status;
        if (Active)
        {
            const string Name = "Waypoint";
            const string TrialName = "Waypoint (trial)";
            tip = status == Name ? $"{TrialName}. {IdleNotice}"
                : status.StartsWith(TrialName, StringComparison.Ordinal) ? status
                : status.StartsWith(Name, StringComparison.Ordinal) ? TrialName + status[Name.Length..]
                : $"{TrialName}: {status}";
        }

        return tip.Length > maxLength ? tip[..maxLength] : tip;
    }

    /// <summary>The notice without its "Trial run:" lead, for the tip and the Connections row, which already say it's a trial.</summary>
    private string IdleNotice => Alerts ? "Connections are off. Alerts are on for testing." : "Alerts and connections are off.";

    /// <summary>The Connections list with the trial run said first, so it's clear why nothing is connected.</summary>
    public IReadOnlyList<ConnectionStatus> Describe(IReadOnlyList<ConnectionStatus> connections) =>
        Active ? [new ConnectionStatus("Trial run", IdleNotice), .. connections] : connections;
}
