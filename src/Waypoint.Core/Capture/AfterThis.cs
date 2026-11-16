namespace Waypoint.Core.Capture;

/// <summary>
/// "I'll do that after this." A startable reminder that gets no answer while its card is up is held once, and comes
/// back once at a point the app already shows something: To do or Ask opening, right after a session ends with Did
/// it, tonight's heads-up, or the next morning's briefing. Whichever comes first takes it, and then it's gone,
/// whatever the answer. Nothing new is sent for it, and only the newest is ever kept, so older ones never trickle back.
/// </summary>
public static class AfterThis
{
    /// <summary>How long a reminder card can go unanswered before its item is held.</summary>
    public static readonly TimeSpan Unanswered = TimeSpan.FromMinutes(30);

    /// <summary>A hold that hasn't come back by then is dropped, so nothing piles up.</summary>
    public static readonly TimeSpan Kept = TimeSpan.FromHours(24);

    public const string Lead = "Earlier you meant to:";

    /// <summary>The one line it comes back as.</summary>
    public static string Line(StoredItem item) => $"{Lead} {item.Title}";
}
