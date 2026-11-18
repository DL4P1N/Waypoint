using Waypoint.Core.Budget;
using Waypoint.Core.Email;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Capture;

public enum ItemKind
{
    Task,
    Deadline,
    Shopping,
    Expense,
    Note,
    Reminder,
    Exam,
    Event,
}

/// <summary>An item the sorter pulled out of a capture, not yet stored.</summary>
public sealed record SortedItem(
    ItemKind Kind,
    string Title,
    DateOnly? DueDate,
    long? AmountCents,
    TimeOnly? DueTime = null,
    IReadOnlyList<PlannedReminder>? Reminders = null,
    int? RepeatMinutes = null,
    IReadOnlyList<PlannedStep>? Steps = null,
    Recurrence? Recurs = null,
    string? Category = null,
    bool OneOff = false,
    string? About = null,
    TimeOnly? EndTime = null,
    string? Course = null,
    bool Dose = false);

/// <summary>One small step of a bigger item. At is when to be reminded, if at all.</summary>
public sealed record PlannedStep(string Title, DateTime? At);

/// <summary>A pop-up planned for an item. Message is null to just show the item.</summary>
public sealed record PlannedReminder(DateTime At, string? Message);

/// <summary>
/// A pop-up that's ready to show. RepeatMinutes is set if it keeps coming back until the item is done.
/// IsFollowUp is set on the one that comes back after "10 more minutes", which doesn't offer more time again.
/// </summary>
public sealed record DueReminder(long Id, StoredItem Item, string? Message, int? RepeatMinutes, bool IsFollowUp = false);

public enum ItemSource
{
    Capture,
    Calendar,

    /// <summary>An entry on a list other than Shopping. Only History uses it, for check-offs.</summary>
    List,
}

/// <summary>
/// An item saved in the database. Body is the whole text of a long capture whose title was shortened, read for the
/// To do list only, and null everywhere else. Dose marks a repeating reminder for taking medicine, answered with Took it.
/// </summary>
public sealed record StoredItem(
    long Id,
    ItemKind Kind,
    string Title,
    DateOnly? DueDate,
    long? AmountCents,
    bool Done,
    ItemSource Source = ItemSource.Capture,
    string? Course = null,
    DateTimeOffset? DueAt = null,
    string? ParentRef = null,
    Recurrence? Recurs = null,
    string? Account = null,
    string? Body = null,
    bool Dose = false);

/// <summary>Raw text that has been saved but not sorted yet.</summary>
public sealed record PendingCapture(long Id, string Text, DateTimeOffset CreatedAt, CaptureOrigin Origin = CaptureOrigin.App, string? FileName = null);

/// <summary>Where a capture was typed, which is where its reply goes.</summary>
public enum CaptureOrigin
{
    App,
    Discord,
}

public enum ChangeAction
{
    Done,
    Cancel,
    Reopen,
    Update,
    StopReminders,
    BreakDown,
    UseFeedDate,
}

/// <summary>A change to an existing item. Null fields on an update keep their current value.</summary>
public sealed record ItemChange(
    ItemSource Source,
    long ItemId,
    ChangeAction Action,
    string? Title = null,
    DateOnly? DueDate = null,
    TimeOnly? DueTime = null,
    IReadOnlyList<PlannedStep>? Steps = null);

/// <summary>
/// A change after it was tried. Applied is false if the item can't be changed that way. Note is said in place of the
/// usual summary, such as when a dose was already marked taken.
/// </summary>
public sealed record AppliedChange(ItemChange Change, string Title, bool Applied = true, string? Note = null);

/// <summary>What the sorter made of a capture: new items, changes to existing ones, and any answer.</summary>
public sealed record CaptureResult(
    IReadOnlyList<SortedItem> Items,
    IReadOnlyList<ItemChange>? Changes = null,
    string? Answer = null,
    long? MonthlyBudgetCents = null,
    IReadOnlyList<BudgetOp>? BudgetOps = null,
    IReadOnlyList<AssistantOp>? Ops = null,
    string? FocusRef = null);

/// <summary>What actually happened to a capture, for the reply.</summary>
public sealed record CaptureOutcome(IReadOnlyList<SortedItem> Items, IReadOnlyList<AppliedChange> Changes, string? Answer, long? MonthlyBudgetCents = null, CaptureOrigin Origin = CaptureOrigin.App, IReadOnlyList<string>? BudgetReplies = null, IReadOnlyList<string>? OpReplies = null, ActivationSession? Activation = null, long? CaptureId = null);

/// <summary>What the sorter can see when reading a capture.</summary>
public sealed record AssistantContext(
    DateTime Now,
    IReadOnlyList<StoredItem> OpenItems,
    IReadOnlyList<StoredItem> RecentlyFinished,
    ClassOccurrence? NextClass = null,
    BedtimePlan? Bedtime = null,
    long? SafeToSpendCents = null,
    IReadOnlyList<string>? Categories = null,
    IReadOnlyList<string>? Accounts = null,
    IReadOnlyList<string>? MoneyNotes = null,
    IReadOnlyList<string>? Lists = null,
    ActivationSession? Activation = null,
    string? WaitingHandoff = null,
    WorkShift? NextShift = null,
    IReadOnlyList<Exchange>? Conversation = null,
    IReadOnlyList<PaymentAppRepayment>? WaitingRepayments = null,
    IReadOnlyList<NoteDetail>? EditedNotes = null,
    IReadOnlyList<string>? Balances = null,
    IReadOnlyList<WaitingItem>? Waiting = null);

/// <summary>One note and its reply. Refs are the items it acted on or suggested, so "that" can find them.</summary>
public sealed record Exchange(DateTime At, string Note, string Reply, IReadOnlyList<string> Refs);

/// <summary>How something left the list. Past means its time went by without anyone closing it.</summary>
public enum HistoryAction
{
    Done,
    Canceled,
    Past,
    Reopened,
}

/// <summary>One close in the log, with its own copy of the details since the item itself can move on or go away.</summary>
public sealed record HistoryEntry(
    long Id,
    DateTime At,
    HistoryAction Action,
    ItemSource Source,
    long ItemId,
    ItemKind Kind,
    string Title,
    string? Course,
    DateOnly? DueDate,
    DateOnly? NextDueDate,
    IReadOnlyList<PlannedReminder> Reminders)
{
    public string Label => Action switch
    {
        HistoryAction.Canceled => "Canceled",
        HistoryAction.Past => "Past",
        HistoryAction.Reopened => "Reopened",
        _ => "Finished",
    };
}

/// <summary>One thing on a named list.</summary>
public sealed record ListEntry(long Id, string List, string Text, bool Done, DateTime AddedAt);

/// <summary>One list as the window shows it, with what's left first.</summary>
public sealed record ListOverview(string Name, IReadOnlyList<ListEntry> Entries)
{
    public int OpenCount => Entries.Count(entry => !entry.Done);
}

/// <summary>A note as the window shows it. FirstLine is what was written, if it adds to the title.</summary>
public sealed record RecentNote(string Title, string? FirstLine, DateTime At);

/// <summary>Reminders the planner chose for one calendar event.</summary>
public sealed record EventNudges(long EventId, IReadOnlyList<PlannedReminder> Reminders);

/// <summary>The short ids that name stored items in the sorter's prompt and replies.</summary>
public static class ItemRef
{
    private const string CapturePrefix = "item:";
    private const string CalendarPrefix = "feed:";

    public static string Format(StoredItem item) => Format(item.Source, item.Id);

    public static string Format(ItemSource source, long id) =>
        (source == ItemSource.Calendar ? CalendarPrefix : CapturePrefix) + id;

    public static bool TryParse(string? text, out ItemSource source, out long id)
    {
        source = text?.StartsWith(CalendarPrefix, StringComparison.Ordinal) == true ? ItemSource.Calendar : ItemSource.Capture;
        var prefix = source == ItemSource.Calendar ? CalendarPrefix : CapturePrefix;
        id = 0;
        return text?.StartsWith(prefix, StringComparison.Ordinal) == true && long.TryParse(text[prefix.Length..], out id);
    }
}
