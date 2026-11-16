namespace Waypoint.App;

/// <summary>
/// The words for messages typed in Ask that are still waiting, for the line above the box and the tray's tip. A
/// failure is said plainly, never with the error, which goes to errors.log instead.
/// </summary>
public static class SortStatus
{
    public static string Messages(int count) => count == 1 ? "1 message" : $"{count} messages";

    /// <summary>The line in Ask, or null when there's nothing to say.</summary>
    public static string? Notice(int pending, bool failed) => (pending, failed) switch
    {
        (> 0, true) => $"Sorting failed for now. {(pending == 1 ? "Your message is" : $"Your {pending} messages are")} saved and will be sorted once Claude can be reached.",
        (_, true) => "Sorting failed for now. Waypoint will try again on its own.",
        (> 0, false) => $"{Messages(pending)} waiting for a response",
        _ => null,
    };

    /// <summary>The tray's tip, or null when there's nothing to say.</summary>
    public static string? TrayTip(int pending, bool failed) => (pending, failed) switch
    {
        (> 0, true) => $"Waypoint: sorting failed, {Messages(pending)} saved",
        (_, true) => "Waypoint: sorting failed for now",
        (> 0, false) => $"Waypoint: {Messages(pending)} waiting for a response",
        _ => null,
    };
}
