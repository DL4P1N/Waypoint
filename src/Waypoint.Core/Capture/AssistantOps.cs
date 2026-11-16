using System.Text.Json;
using System.Text.RegularExpressions;
using Waypoint.Core.Data;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Capture;

/// <summary>
/// Things the sorter can ask for that aren't items or budget updates. To add one: add a member here,
/// an arm in <see cref="AssistantOps.Apply"/>, and a line under "Assistant ops" in the sorter's prompt.
/// The schema and parser pick up the new name on their own.
/// </summary>
public enum AssistantOpKind
{
    Search,
    ListCreate,
    ListAdd,
    ListShow,
    ListCheck,
    ListUncheck,
    ListRemove,
    ListStart,
    ListDelete,
    ActivationStart,
    ActivationSwitch,
    ActivationSmaller,
    ActivationPause,
    ActivationResume,
    ActivationCancel,
    ActivationLink,
    ActivationStats,
    HandoffSave,
    HandoffNext,
    HandoffShow,
    WorkHoursSet,
    WorkHoursRemove,
    WorkShiftAdd,
    WorkDayOff,
    WorkCommute,
    WorkShow,
    BedtimeCheck,
    BedtimeUncheck,
    BedtimeSnooze,
    BedtimeSkip,
    BedtimeShow,
    PayScheduleSet,
    PayAmountSet,
    PayExpected,
    PayReceived,
    PayShow,
    PayDates,
    PayRateSet,
    AffordCheck,
    BillSet,
    BillRemove,
    BillShow,
    SavingsGoalSet,
    SavingsGoalClear,
    SavingsGoalShow,
    MonthlySummary,
    CreditScoreAdd,
    CreditScoreShow,

    /// <summary>Reads new bank and card emails now, then says the balances. The app does the reading.</summary>
    EmailCheck,
    WaitHoursSet,
    SpendingPatterns,
    SpendingHelped,
    ThemeSet,
    RepaymentMatch,
    RepaymentClear,
    WaitingAdd,
    WaitingStill,
    WaitingDraft,
    WaitingStop,
    WaitingShow,
}

/// <summary>One op as the sorter sent it. Which fields matter depends on the kind.</summary>
public sealed record AssistantOp(
    AssistantOpKind Kind,
    string? Name = null,
    string? Text = null,
    IReadOnlyList<string>? Entries = null,
    long? AmountCents = null,
    DateTime? At = null,
    DateTime? Until = null,
    string? Next = null,
    string? Account = null);

/// <summary>
/// What an op did. An answer replies to a question, so it's shown like the sorter's own answer. ItemId is the item
/// the op added, when it added one, so the page that asked can offer to undo it.
/// </summary>
public sealed record OpReply(string Text, bool IsAnswer = false, long? ItemId = null);

public static partial class AssistantOps
{
    /// <summary>The op names as a JSON array, for the sorter's schema.</summary>
    public static string SchemaNames =>
        JsonSerializer.Serialize(Enum.GetNames<AssistantOpKind>().Select(name => SnakeBoundary().Replace(name, "_$1").ToLowerInvariant()));

    public static bool TryParseKind(string? text, out AssistantOpKind kind) =>
        Enum.TryParse(text?.Replace("_", ""), ignoreCase: true, out kind) && Enum.IsDefined(kind);

    /// <summary>Runs one op. It happens after the capture's items are saved, so it can refer to them.</summary>
    public static OpReply Apply(AssistantOp op, WaypointDatabase database, long captureId, DateTime now, string? note = null, BedtimeSchedule? bedtime = null)
    {
        var entries = op.Entries ?? [];
        return op.Kind switch
        {
            AssistantOpKind.Search => new OpReply(SearchIndex.Reply(database.SearchEverything(new SearchQuery(op.Name, op.Text, op.At, op.Until), now), op.Name), true),
            AssistantOpKind.ListCreate => new OpReply(database.AddToList(op.Name, entries, captureId, now)),
            AssistantOpKind.ListAdd => new OpReply(database.AddToList(op.Name, entries, captureId, now)),
            AssistantOpKind.ListShow => new OpReply(database.DescribeList(op.Name), true),
            AssistantOpKind.ListCheck => new OpReply(string.Join(". ", entries.Select(entry => database.SetEntryDone(op.Name, entry, true, now)))),
            AssistantOpKind.ListUncheck => new OpReply(string.Join(". ", entries.Select(entry => database.SetEntryDone(op.Name, entry, false, now)))),
            AssistantOpKind.ListRemove => new OpReply(string.Join(". ", entries.Select(entry => database.RemoveEntry(op.Name, entry, now)))),
            AssistantOpKind.ListStart => new OpReply(database.StartList(op.Name), true),
            AssistantOpKind.ListDelete => new OpReply(database.DeleteList(op.Name, now)),
            AssistantOpKind.ActivationStart => database.StartActivation(op.Name, op.Text, entries, Activation.TypedMethod, now, note),
            AssistantOpKind.ActivationSwitch => database.StartActivation(op.Name, op.Text, entries, Activation.SwitchMethod, now, note),
            AssistantOpKind.ActivationSmaller => database.MakeSmaller(op.Text, now),
            AssistantOpKind.ActivationPause => database.PauseSession(op.Text, op.At, now),
            AssistantOpKind.ActivationResume => database.ResumeSession(now),
            AssistantOpKind.ActivationCancel => database.CancelSession(now),
            AssistantOpKind.ActivationLink => database.SetSessionResource(op.Text, now),
            AssistantOpKind.ActivationStats => new OpReply(database.DescribeSessionStats(), true),
            AssistantOpKind.HandoffSave => database.SaveHandoff(op.Name, op.Text, op.Next, entries, captureId, now),
            AssistantOpKind.HandoffNext => database.SetHandoffNext(op.Next ?? op.Text, now),
            AssistantOpKind.HandoffShow => new OpReply(database.DescribeHandoff(op.Name), true),
            AssistantOpKind.WorkHoursSet => database.SetWeeklyHours(entries, op.Text),
            AssistantOpKind.WorkHoursRemove => database.RemoveWeeklyHours(entries),
            AssistantOpKind.WorkShiftAdd => database.AddWorkShift(op.At, op.Until),
            AssistantOpKind.WorkDayOff => database.SetDayOff(op.At),
            AssistantOpKind.WorkCommute => database.SetCommute(op.Text),
            AssistantOpKind.WorkShow => new OpReply(database.DescribeWork(DateOnly.FromDateTime(now)), true),
            AssistantOpKind.BedtimeCheck => BedtimeRoutine.Mark(database, bedtime, op.Name, true, now),
            AssistantOpKind.BedtimeUncheck => BedtimeRoutine.Mark(database, bedtime, op.Name, false, now),
            AssistantOpKind.BedtimeSnooze => BedtimeRoutine.Snooze(database, bedtime, now),
            AssistantOpKind.BedtimeSkip => BedtimeRoutine.Dismiss(database, bedtime, now),
            AssistantOpKind.BedtimeShow => BedtimeRoutine.Describe(database, bedtime, now),
            AssistantOpKind.PayScheduleSet => database.SetPaySchedule(op.Text, entries, op.AmountCents, op.At),
            AssistantOpKind.PayAmountSet => database.SetPayAmount(op.AmountCents),
            AssistantOpKind.PayExpected => database.AddExpectedDeposit(op.Name, op.AmountCents, op.At),
            AssistantOpKind.PayReceived => database.ConfirmPayTyped(op.AmountCents, DateOnly.FromDateTime(now)),
            AssistantOpKind.PayDates => database.ProposePayDates(entries, op.AmountCents, now),
            AssistantOpKind.PayRateSet => database.SetHourlyRate(op.AmountCents / 100m),
            AssistantOpKind.PayShow => new OpReply(database.DescribePay(DateOnly.FromDateTime(now)), true),
            AssistantOpKind.AffordCheck => database.CheckAffordable(op.AmountCents, op.Name, DateOnly.FromDateTime(now)),
            AssistantOpKind.BillSet => database.SetBill(op.Name, op.AmountCents, op.Text, entries, op.At, op.Account, DateOnly.FromDateTime(now)),
            AssistantOpKind.BillRemove => database.RemoveBill(op.Name),
            AssistantOpKind.BillShow => new OpReply(database.DescribeBills(DateOnly.FromDateTime(now)), true),
            AssistantOpKind.SavingsGoalSet => database.SetSavingsGoal(op.AmountCents, op.At, DateOnly.FromDateTime(now)),
            AssistantOpKind.SavingsGoalClear => database.ClearSavingsGoal(),
            AssistantOpKind.SavingsGoalShow => new OpReply(database.DescribeSavingsGoal(DateOnly.FromDateTime(now)), true),
            AssistantOpKind.MonthlySummary => database.DescribeMonth(op.At, DateOnly.FromDateTime(now)),
            AssistantOpKind.CreditScoreAdd => database.AddCreditScore(op.Text, op.Name, op.At, entries.FirstOrDefault(), now),
            AssistantOpKind.CreditScoreShow => new OpReply(database.DescribeCreditScores(), true),
            AssistantOpKind.WaitHoursSet => database.SetDefaultWaitHours(op.Text),
            AssistantOpKind.SpendingPatterns => new OpReply(string.Join("\n", database.GetSpendingPatterns(DateOnly.FromDateTime(now))), true),
            AssistantOpKind.SpendingHelped => new OpReply(database.DescribeSpendingOutcomes(DateOnly.FromDateTime(now)), true),
            AssistantOpKind.ThemeSet => database.SetTheme(op.Name),
            AssistantOpKind.EmailCheck => new OpReply("Email isn't connected, so there's nothing to check.", true),
            AssistantOpKind.RepaymentMatch => database.MatchRepayment(op.Text, op.Name, DateOnly.FromDateTime(now)),
            AssistantOpKind.RepaymentClear => database.ClearRepayment(op.Text),
            AssistantOpKind.WaitingAdd => database.MarkWaiting(op.Name, op.Text, op.Next, op.At is { } followUp ? DateOnly.FromDateTime(followUp) : null, entries.FirstOrDefault(entry => entry.Contains('@')), captureId, now),
            AssistantOpKind.WaitingStill => database.FindWaiting(op.Name, DateOnly.FromDateTime(now)) is { } still
                ? database.StillWaiting(still.ItemId, op.At is { } until ? DateOnly.FromDateTime(until) : null, now)
                : new OpReply(database.DescribeWaiting(DateOnly.FromDateTime(now)), true),
            AssistantOpKind.WaitingDraft => new OpReply(database.DraftFollowUp(op.Name, DateOnly.FromDateTime(now)) is { } draft
                ? $"Here's a follow-up to copy into an email:\n\n{draft}"
                : "You're not waiting on anyone right now.", true),
            AssistantOpKind.WaitingStop => database.FindWaiting(op.Name, DateOnly.FromDateTime(now)) is { } stop && database.StopWaiting(stop.ItemId)
                ? new OpReply($"Not waiting on {stop.Who} anymore. {stop.Title} is still on your list.")
                : new OpReply("I couldn't tell which one you meant.", true),
            AssistantOpKind.WaitingShow => new OpReply(database.DescribeWaiting(DateOnly.FromDateTime(now)), true),
            _ => new OpReply(""),
        };
    }

    /// <summary>Whether this op touches a session, so its reply carries the session's buttons.</summary>
    public static bool IsActivation(AssistantOpKind kind) =>
        kind is >= AssistantOpKind.ActivationStart and <= AssistantOpKind.ActivationLink;

    [GeneratedRegex(@"(?<=[a-z])([A-Z])")]
    private static partial Regex SnakeBoundary();
}
