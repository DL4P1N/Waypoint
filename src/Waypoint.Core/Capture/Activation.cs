namespace Waypoint.Core.Capture;

public enum SessionStatus
{
    Active,
    Paused,
    Finished,
    Canceled,
}

/// <summary>What a short reply means while a session is going. Every button sends one of these as text.</summary>
public enum SessionWord
{
    Did,
    Smaller,
    Pause,
    Cancel,
    Trial,
    Resume,
}

/// <summary>
/// One attempt at getting going on a task. Action is the only thing to do right now, and Queue is what
/// follows it. StepId is set while the action stands for one of the task's own steps. The timestamps and
/// Method are also the local record of what helped.
/// </summary>
public sealed record ActivationSession(
    long Id,
    string? TaskRef,
    string TaskTitle,
    string Action,
    SessionStatus Status,
    DateTime StartedAt,
    DateTime? BegunAt,
    string? StoppingNote,
    string? Resource,
    string Method,
    long? StepId,
    IReadOnlyList<string> Queue,
    DateTime? PromptAt,
    int SmallerCount)
{
    /// <summary>The two-minute offer only makes sense once something has been done.</summary>
    public bool CanTrial => BegunAt is not null && PromptAt is null && Action != Activation.KeepGoing;
}

/// <summary>A saved stopping point, so stopping is a pause and not losing the thread.</summary>
public sealed record Handoff(long Id, string Project, string? Stopped, string? Next, IReadOnlyList<string> Links, IReadOnlyList<string> Thoughts, DateTime CreatedAt);

/// <summary>A message a session has waiting, such as the end of a trial.</summary>
public sealed record SessionPrompt(string Headline, string Text);

/// <summary>The rules that need no sorter, so getting started never depends on a reply from it.</summary>
public static class Activation
{
    public const string TrialOffer = "Try it for two minutes";
    public const string KeepGoing = "Keep going, or stop here";
    public const string TypedMethod = "typed";
    public const string ReminderMethod = "reminder";
    public const string SwitchMethod = "switch";
    public const string StudyMethod = "study";
    public const string EnergyMethod = "energy";

    /// <summary>
    /// What shows while Make it smaller or Break down waits on Claude. Make it smaller falls back to a ready step if
    /// Claude takes too long, so it always ends with something to do.
    /// </summary>
    public const string SmallerWorking = "Working on a smaller step...";
    public const string BreakDownWorking = "Working on smaller steps...";

    /// <summary>The line to show while a typed request waits, when it's one of the two that ask Claude for steps.</summary>
    public static string? WorkingLine(string text, bool sessionGoing)
    {
        var clean = Clean(text);
        if (clean.StartsWith("break down ", StringComparison.Ordinal))
        {
            return BreakDownWorking;
        }

        return sessionGoing && ParseWord(text) == SessionWord.Smaller ? SmallerWorking : null;
    }

    /// <summary>The answer when there's nothing open to start on.</summary>
    public const string NothingToPick = "Nothing to pick yet. Add a task first.";

    private static readonly string[] Fallbacks =
    [
        "Open it and look at it for ten seconds",
        "Get it on your screen or in your hands, and do nothing else yet",
        "Say out loud what the very first bit is",
    ];

    private static readonly string[] StartWords =
    [
        "can't start", "cant start", "can not start", "cannot start", "can't get started", "cant get started",
        "help me begin", "help me start", "help me get started", "start with me",
    ];

    /// <summary>Only whole replies count, so "done with the paper" still goes to the sorter.</summary>
    private static readonly Dictionary<string, SessionWord> Words = new()
    {
        ["done"] = SessionWord.Did,
        ["did it"] = SessionWord.Did,
        ["next"] = SessionWord.Did,
        ["smaller"] = SessionWord.Smaller,
        ["make it smaller"] = SessionWord.Smaller,
        ["stuck"] = SessionWord.Smaller,
        ["i'm stuck"] = SessionWord.Smaller,
        ["im stuck"] = SessionWord.Smaller,
        ["not now"] = SessionWord.Pause,
        ["pause"] = SessionWord.Pause,
        ["later"] = SessionWord.Pause,
        ["stop"] = SessionWord.Pause,
        ["cancel"] = SessionWord.Cancel,
        ["cancel this"] = SessionWord.Cancel,
        ["never mind"] = SessionWord.Cancel,
        ["nevermind"] = SessionWord.Cancel,
        ["2 minutes"] = SessionWord.Trial,
        ["two minutes"] = SessionWord.Trial,
        ["2 min"] = SessionWord.Trial,
        ["trial"] = SessionWord.Trial,
        ["resume"] = SessionWord.Resume,
        ["continue"] = SessionWord.Resume,
        ["where was i"] = SessionWord.Resume,
    };

    /// <summary>A plain yes, which only means the trial when the trial was just offered.</summary>
    private static readonly HashSet<string> YesWords = ["yes", "yeah", "yep", "yup", "ok", "okay", "sure", "sounds good", "let's do it", "lets do it"];

    public static SessionWord? ParseWord(string text) =>
        Words.TryGetValue(Clean(text), out var word) ? word : null;

    public static bool IsYes(string text) => YesWords.Contains(Clean(text));

    /// <summary>Whether a reply was the two-minute offer, so a yes after it can mean the trial.</summary>
    public static bool OffersTrial(string? reply) => reply?.Contains("two minutes", StringComparison.OrdinalIgnoreCase) == true;

    private static string Clean(string text) => text.Trim().TrimEnd('.', '!', '?').Trim().Replace('’', '\'').ToLowerInvariant();

    public static bool AsksToStart(string text) =>
        StartWords.Any(word => text.Replace('’', '\'').Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>The quick picks for bringing a session back later. Ones that have already gone by today are left out.</summary>
    private const int NightEndsHour = 5;

    public static IReadOnlyList<(string Label, DateTime At)> LaterChoices(DateTime now)
    {
        var today = now.Date;
        var choices = new List<(string Label, DateTime At)> { ("In an hour", now.AddHours(1)) };
        if (now < today.AddHours(18))
        {
            choices.Add(("This evening", today.AddHours(18)));
        }

        // In the small hours the night isn't over yet, so tomorrow morning is the one still ahead today.
        var morning = now.Hour < NightEndsHour ? today.AddHours(9) : today.AddDays(1).AddHours(9);
        choices.Add(("Tomorrow morning", morning));
        return choices;
    }

    /// <summary>A picked day and time to bring a session back, or null if it has already gone by. No time means 9 AM.</summary>
    public static DateTime? BringBackAt(DateOnly date, TimeOnly? time, DateTime now)
    {
        var at = date.ToDateTime(time ?? new TimeOnly(9, 0));
        return at > now ? at : null;
    }

    /// <summary>A session with nothing done on it for this long counts as paused.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(12);

    /// <summary>A late night still counts as the day before until this hour.</summary>
    private static readonly TimeSpan DayTurnsOver = TimeSpan.FromHours(4);

    /// <summary>Once its day is over, a session left alone this long counts as paused, so working past 4 AM isn't cut off.</summary>
    private static readonly TimeSpan OvernightGap = TimeSpan.FromHours(3);

    /// <summary>
    /// Whether a session left alone since then is past picking up as it stands: half a day with nothing done, or its
    /// day is over and it's been a few hours. It's paused, not ended, so its place is kept for resume.
    /// </summary>
    public static bool IsStale(DateTime lastActivity, DateTime now) =>
        now - lastActivity > StaleAfter
        || (now - lastActivity > OvernightGap && (now - DayTurnsOver).Date > (lastActivity - DayTurnsOver).Date);

    /// <summary>A fixed action for when no better one can be had. Each ask goes smaller, then stays there.</summary>
    public static string Fallback(int smallerCount) => Fallbacks[Math.Min(smallerCount, Fallbacks.Length - 1)];

    public static string JobReply(string action) => $"Your only job right now: {action}. You don't have to do the rest yet.";

    /// <summary>Whether a reminder for this item is worth a Start button.</summary>
    public static bool CanStart(StoredItem item) =>
        item.Recurs is null && item.Kind is ItemKind.Task or ItemKind.Deadline or ItemKind.Exam or ItemKind.Reminder;

    /// <summary>
    /// Whether a reminder card, on the PC or Discord, offers Start and Save my spot: only for tasks, deadlines and
    /// exams. A plain reminder is something to do or put off, so its card keeps to Done and Not now.
    /// </summary>
    public static bool CardOffersStart(StoredItem item) => CanStart(item) && item.Kind is not ItemKind.Reminder;

    /// <summary>
    /// Whether a reminder card, on the PC or Discord, offers Done for its item: reminders, tasks and deadlines,
    /// including an assignment's nudge from the calendar. An exam's alert comes before the exam, so it's only an alert.
    /// </summary>
    public static bool CardFinishesKind(ItemKind kind) => kind is ItemKind.Reminder or ItemKind.Task or ItemKind.Deadline;

    /// <summary>
    /// Whether a reminder's card offers Done, the same on the PC and Discord: a reminder set to repeat, or an item of a
    /// kind that can be finished. On an item that repeats, Done is done for this time and moves it to its next date.
    /// </summary>
    public static bool CardFinishes(DueReminder reminder) => reminder.RepeatMinutes is not null || CardFinishesKind(reminder.Item.Kind);

    /// <summary>What Done means on a repeating item's card, said where there's room for it.</summary>
    public const string DoneThisTime = "Done for this time. It comes back on its next date.";

    /// <summary>The soonest thing that could be worked on, preferring whatever the note names.</summary>
    public static StoredItem? PickTask(IReadOnlyList<StoredItem> open, string? note)
    {
        var candidates = open.Where(item => item.ParentRef is null && CanStart(item)).ToList();
        return candidates.FirstOrDefault(item => note is not null && SearchIndex.Mentions(item.Title, note)) ?? candidates.FirstOrDefault();
    }
}

/// <summary>Decides what "Open the work" may open. Web pages and document files only, never anything that runs.</summary>
public static class SafeResource
{
    private static readonly HashSet<string> DocumentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".ppt", ".pptx", ".xls", ".xlsx", ".csv", ".txt", ".md", ".rtf", ".odt", ".png", ".jpg", ".jpeg",
    };

    /// <summary>Returns the text to hand to the shell, or null if it isn't safe to open.</summary>
    public static string? Check(string? text, Func<string, bool>? fileExists = null)
    {
        var candidate = text?.Trim().Trim('"');
        if (string.IsNullOrEmpty(candidate))
        {
            return null;
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            return uri.AbsoluteUri;
        }

        // Rooted on a local drive, with no share or alternate data stream, and of a type that can't run.
        var isLocal = candidate.Length > 3 && char.IsAsciiLetter(candidate[0]) && candidate[1] == ':' && candidate[2] is '\\' or '/';
        if (!isLocal || candidate.IndexOf(':', 2) >= 0 || !DocumentTypes.Contains(Path.GetExtension(candidate)))
        {
            return null;
        }

        return (fileExists ?? File.Exists)(candidate) ? candidate : null;
    }
}
