namespace Waypoint.Core.Capture;

/// <summary>
/// The words on card buttons, on the PC and on Discord alike, so both always read the same. Every card keeps one
/// order: the main action first, then the one that brings it back later, then Skip, which means nothing more to
/// come. Not now brings a plain reminder back later. On a reminder that can be started, the one follow-up says what it
/// does, 10 more minutes. On the session card the place is kept and nothing comes back, so it reads Pause. Skip these
/// is Skip for all of an assignment's reminders at once, so it never shares a card with a lone Skip under the same word.
/// </summary>
public static class CardLabels
{
    public const string NotNow = "Not now";

    /// <summary>The main action on a dose's card. It keeps the time, once for that day.</summary>
    public const string TookIt = "Took it";

    /// <summary>The one follow-up on a reminder that can be started. It matches <see cref="TransitionAssist.FollowUpMinutes"/>.</summary>
    public const string MoreTime = "10 more minutes";

    /// <summary>Stops a session with its place kept. Nothing comes back on its own.</summary>
    public const string Pause = "Pause";

    /// <summary>The session card's buttons, the same on the PC and Discord. End stops it without marking anything done.</summary>
    public const string DidIt = "Did it";
    public const string Smaller = "Make it smaller";
    public const string Trial = "Try 2 minutes";
    public const string End = "End";

    /// <summary>Opens the short Save my spot form.</summary>
    public const string SaveSpot = "Save my spot";

    /// <summary>A waiting-on nudge: Follow up gives a short note to copy, and Still waiting checks back in a few days.</summary>
    public const string FollowUp = "Follow up";
    public const string StillWaiting = "Still waiting";
    public const string Skip = "Skip";
    public const string SkipAll = "Skip these";

    /// <summary>
    /// Keeps a click, and an optional line, saying a card or menu didn't help. It sits apart from a card's own buttons,
    /// small and last, so it's never mistaken for one of them.
    /// </summary>
    public const string DidntHelp = "This didn't help";

    /// <summary>Skip for a Tailspin Pay payment: it wasn't paying anything back, so nothing more is asked about it.</summary>
    public const string NotARepayment = "Not a repayment";

    /// <summary>What's said when a typed time or date can't be used, the same wherever one is typed.</summary>
    public const string TimeHint = "Try a time like 3pm or 15:30.";
    public const string DateHint = "Try a day like today, tomorrow, friday, 11/14, or 2026-11-14.";

    /// <summary>The empty states that read the same on the PC and Discord.</summary>
    public const string NothingToStart = Activation.NothingToPick;
    public const string EmptyList = "Nothing on this list yet.";
    public const string NothingYet = "Nothing here yet.";

    /// <summary>"Nothing in History matches "essay"." A search that found nothing, named for where it looked.</summary>
    public static string NothingMatches(string where, string words) => $"Nothing {where} matches \"{words}\".";
}
