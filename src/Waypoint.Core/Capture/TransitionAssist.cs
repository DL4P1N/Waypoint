namespace Waypoint.Core.Capture;

/// <summary>
/// The rules for getting from what's on screen to what was planned. A reminder that can be started offers
/// Start, 10 more minutes, and Skip. The extra minutes come back once, and the follow-up only offers
/// Start and Skip, so it can never turn into a chain. Skip ends it, and nothing counts as done.
/// </summary>
public static class TransitionAssist
{
    /// <summary>Fixed for now, so it needs no setup.</summary>
    public const int FollowUpMinutes = 10;

    // The words come from CardLabels, the same as every other card: the ten minutes say so, and clearing it is
    // Skip, since nothing more comes. Skip is still kept as declining.
    public const string SaveSpotLabel = "Save my spot first";

    /// <summary>A spot saved this recently counts as saved, so it isn't offered again.</summary>
    public static readonly TimeSpan RecentSpot = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether a reminder gets the transition buttons. Reminders the user set to repeat keep their own
    /// rhythm, and things that can't be started keep the buttons they had.
    /// </summary>
    public static bool Applies(DueReminder reminder) => reminder.RepeatMinutes is null && Activation.CanStart(reminder.Item);

    /// <summary>Only the first reminder offers more time. The follow-up is the last one.</summary>
    public static bool OffersMoreTime(DueReminder reminder) => Applies(reminder) && !reminder.IsFollowUp;

    /// <summary>Offered only when no spot was saved in the last hour, so there's never a second form to fill in.</summary>
    public static bool OffersSaveSpot(DateTime? lastSaved, DateTime now) => lastSaved is not { } saved || now - saved > RecentSpot;

    public static DateTime FollowUpAt(DateTime now) => now.AddMinutes(FollowUpMinutes);

    /// <summary>A spot older than this is from another time, so starting again doesn't open on it.</summary>
    public static readonly TimeSpan SpotLifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// The latest saved spot for a task from the last month, or null. It matches when every word of one name is a
    /// whole word of the other, ignoring case and punctuation, so "Art" can't match "Start".
    /// </summary>
    public static Handoff? SpotFor(StoredItem task, IEnumerable<Handoff> newestFirst, DateTime now)
    {
        var title = Words(task.Title);
        return newestFirst.FirstOrDefault(handoff =>
        {
            if (now - handoff.CreatedAt > SpotLifetime || string.Concat(Words(handoff.Project)).Length < 3 || title.Count == 0)
            {
                return false;
            }

            var project = Words(handoff.Project);
            return project.IsSubsetOf(title) || title.IsSubsetOf(project);
        });
    }

    private static HashSet<string> Words(string text) =>
        new(new string(text.Select(letter => char.IsLetterOrDigit(letter) ? char.ToLowerInvariant(letter) : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    /// <summary>One line on where things were left, to show when starting again.</summary>
    public static string? Describe(Handoff? spot) =>
        spot?.Stopped is { Length: > 0 } stopped ? $"Last time you stopped at: {stopped}." : null;
}
