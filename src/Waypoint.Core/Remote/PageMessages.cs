namespace Waypoint.Core.Remote;

/// <summary>What a page says when a change can't go through because the server is away.</summary>
public static class PageMessages
{
    public const string CantChangeYet = "Can't reach the server, so this can't change until it's back. What's shown is from the last time it answered.";

    public const string CantRunYet = "Can't reach the server, so this has to wait until it's back.";

    /// <summary>A pairing problem says so, since waiting won't fix it. Anything else is the server being away.</summary>
    public static string CantChange(ServerUnavailableException away) => NotPaired(away) ? away.Message : CantChangeYet;

    /// <summary>The same for something run on the server, such as a backup or the notes export.</summary>
    public static string CantRun(ServerUnavailableException away) => NotPaired(away) ? away.Message : CantRunYet;

    private static bool NotPaired(ServerUnavailableException away) => away.Message.Contains("didn't accept this PC", StringComparison.Ordinal);
}
