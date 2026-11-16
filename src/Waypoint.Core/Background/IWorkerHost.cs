using Waypoint.Core.Capture;

namespace Waypoint.Core.Background;

/// <summary>
/// What the background work needs from whatever runs it. The PC app shows these on screen. A server has no screen,
/// so it logs them and keeps trying instead.
/// </summary>
public interface IWorkerHost
{
    /// <summary>A notification that couldn't go to Discord, because it isn't set up or still can't be reached.</summary>
    void ShowNotice(string title, string text);

    /// <summary>
    /// A reminder that couldn't go to Discord. Call <paramref name="closed"/> once it's dealt with or given up on,
    /// so the same reminder can come up again if it's still due.
    /// </summary>
    void ShowReminder(DueReminder reminder, Action closed);

    /// <summary>The weekly reset, for a host with a window to show it in. It also goes out as a notification.</summary>
    void ShowWeeklyReset(string text);

    /// <summary>Something changed that the status or the pages may show.</summary>
    void Changed();

    /// <summary>Runs work that came in on a background thread, such as a Discord message, where the host does its own work.</summary>
    void Post(Action action);
}

/// <summary>One line of the Connections section. Status only: what's in settings.json is never shown or changed here.</summary>
public sealed record ConnectionStatus(string Name, string State)
{
    /// <summary>What a screen reader says for the line in Settings, rather than the record's fields.</summary>
    public override string ToString() => $"{Name}: {State}";
}
