using System.Collections.Concurrent;
using System.Globalization;
using Waypoint.Core.Capture;
using Waypoint.Core.Discord;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Background;

/// <summary>
/// Buttons on Discord cards that answer on the card itself: a session's buttons and Start turn the pressed card
/// into the next step, and buttons that carry what they act on, such as a wait-list purchase, settle it there.
/// </summary>
public sealed partial class WaypointWorker
{
    /// <summary>Bought and Skip on a wait-list card. The purchase's ID follows.</summary>
    public const string WaitBought = "wait-bought:";
    public const string WaitSkip = "wait-skip:";

    /// <summary>Picks for /done and /start when what was typed could be more than one thing, and Undo on a finish. An item reference follows.</summary>
    public const string FinishPick = "done:";
    public const string StartPick = "start:";
    public const string UndoFinish = "undo-done:";

    /// <summary>Follow up and Still waiting on a waiting-on nudge. The item's ID follows. Done is a finish pick.</summary>
    public const string WaitingFollowUp = "waiting-follow:";
    public const string WaitingStill = "waiting-still:";

    /// <summary>Not now on something brought back from earlier. Its hold is already gone, so this only clears the buttons.</summary>
    public const string AfterThisNotNow = "after-not-now";

    /// <summary>Makes a list /listadd didn't find. A key into <see cref="_newLists"/> follows.</summary>
    public const string MakeList = "new-list:";

    // Kept only while the app runs, since a name and an entry may not fit in a button. A press after a restart says it ran out.
    private readonly ConcurrentDictionary<string, (string List, string Entry)> _newLists = new();

    /// <summary>How long a pressed card waits for its answer before saying it will come in the capture channel.</summary>
    private static readonly TimeSpan PressAnswerWait = TimeSpan.FromSeconds(60);

    private readonly object _pressLock = new();

    // Captures typed by a button, waiting for their answer so it can replace the pressed card.
    private readonly ConcurrentDictionary<long, TaskCompletionSource<CaptureOutcome>> _pressAnswers = new();

    // Captures answered on their card, so ReplyInDiscord doesn't say the same thing again in the capture channel.
    private readonly ConcurrentDictionary<long, bool> _answeredOnCard = new();

    /// <summary>A pressed button that answers on its own card. Null leaves it to <see cref="OnButtonPressed"/>.</summary>
    public async Task<ButtonResult?> OnButtonActedAsync(ButtonPress press)
    {
        _viewsDirty = true;
        if (press.CustomId.StartsWith(DiscordBridge.ActionPrefix, StringComparison.Ordinal))
        {
            var result = OnCarriedAction(press.CustomId[DiscordBridge.ActionPrefix.Length..], press.MessageId);
            _host.Changed();
            ViewsChanged();
            return result;
        }

        var word = press.Kind switch
        {
            AlertActionKind.DidIt => "did it",
            AlertActionKind.Smaller => "smaller",
            AlertActionKind.NotNow => "not now",
            AlertActionKind.Trial => "2 minutes",
            AlertActionKind.EveningStepDone => BedtimeRoutine.DoneText(BedtimeSchedule.Settings.EveningStepLabel()),
            AlertActionKind.WindDownStepDone => BedtimeRoutine.DoneText(BedtimeSchedule.Settings.WindDownStepLabel()),
            AlertActionKind.SnoozeBedtime => BedtimeRoutine.SnoozeWord,
            _ => null,
        };

        if (word is not null)
        {
            return await AnswerOnCardAsync(word);
        }

        if (press.Kind == AlertActionKind.StartNext)
        {
            return Activation.PickTask(_database.WithoutQuietWaiting(_database.GetOpenItems(), DateOnly.FromDateTime(_clock())), null) is { } next
                ? new ButtonResult(StartFromDiscord(next))
                : new ButtonResult(Outcome: CardLabels.NothingToStart);
        }

        if (press.Kind == AlertActionKind.Start)
        {
            // The card, its answer, and the session are one write, so a failure partway leaves the card to press again.
            if (_database.StartFromReminder(press.MessageId, _clock()) is not { } started)
            {
                return null;
            }

            return new ButtonResult(StartedCard(started.Reply));
        }

        return null;
    }

    /// <summary>A button's words go through capture like typing, and the answer replaces the card that was pressed.</summary>
    private async Task<ButtonResult?> AnswerOnCardAsync(string word)
    {
        var wait = new TaskCompletionSource<CaptureOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        long id;
        lock (_pressLock)
        {
            if (Captures.Capture(word, null, CaptureOrigin.Discord) is not { } captured)
            {
                return null;
            }

            id = captured;
            _pressAnswers[id] = wait;
        }

        _ = Captures.ProcessPendingAsync();
        if (await Task.WhenAny(wait.Task, Task.Delay(PressAnswerWait)) != wait.Task)
        {
            _pressAnswers.TryRemove(id, out _);
            return new ButtonResult(Outcome: "Got it. The answer will come in the capture channel.");
        }

        var outcome = await wait.Task;
        _host.Changed();
        var card = SessionCard(OutcomeText(outcome), outcome.Activation);

        // Did it that ends the session is a natural point to bring back something put off earlier, on this same card.
        if (word == "did it" && outcome.Activation is null && TakeAfterThisLine() is { Line: { } line } held)
        {
            card = card with { Fields = [.. card.Fields, DiscordText.HeldField(line)], Actions = held.Actions ?? [] };
        }

        return new ButtonResult(card);
    }

    /// <summary>Hands an answer to the pressed card waiting for it, if there is one.</summary>
    private void OnSortedForCard(CaptureOutcome outcome)
    {
        if (outcome.CaptureId is not { } id)
        {
            return;
        }

        TaskCompletionSource<CaptureOutcome>? wait;
        lock (_pressLock)
        {
            if (!_pressAnswers.TryRemove(id, out wait))
            {
                return;
            }

            _answeredOnCard[id] = true;
        }

        wait.TrySetResult(outcome);
    }

    private static string OutcomeText(CaptureOutcome outcome) =>
        string.Join("\n", new[] { ItemGroups.OutcomeSummary(outcome), outcome.Answer }.Where(text => !string.IsNullOrWhiteSpace(text)));

    /// <summary>The next step with the session's buttons while one is going, or a plain gray card once it's over.</summary>
    private static DiscordCard SessionCard(string text, ActivationSession? session) =>
        session is { } running
            ? DiscordText.Reply(text.Length > 0 ? text : "Saved.") with { HasSessionButtons = true, CanTrial = running.CanTrial, SessionId = running.Id, FeedbackFeature = Feedback.Session, FeedbackRef = running.TaskRef }
            : DiscordText.Reply(text.Length > 0 ? text : "Saved.", CardTone.Finished);

    /// <summary>Starts a session from Discord and hands back its first step with the session's buttons.</summary>
    private DiscordCard StartFromDiscord(StoredItem item) =>
        StartedCard(_database.StartActivation(ItemRef.Format(item), null, null, Activation.ReminderMethod, _clock()));

    /// <summary>The card a start turns into: the session going now with its buttons, or why nothing started.</summary>
    private DiscordCard StartedCard(OpReply reply)
    {
        _viewsDirty = true;
        _host.Changed();
        var session = _database.GetActiveSession(_clock());
        return session is { } running
            ? DiscordText.Reply($"{running.TaskTitle}\n{reply.Text}") with { HasSessionButtons = true, CanTrial = running.CanTrial, SessionId = running.Id, FeedbackFeature = Feedback.Session, FeedbackRef = running.TaskRef }
            : DiscordText.Reply(reply.Text, CardTone.Attention);
    }

    /// <summary>A button that carries what it acts on. Unknown or stale ones say so rather than doing anything.</summary>
    private ButtonResult OnCarriedAction(string action, string messageId)
    {
        // End only ends the session its card was posted for, and is never handed to the sorter.
        if (TakeId(action, DiscordBridge.EndSessionAction) is { } ending)
        {
            return new ButtonResult(Outcome: _database.EndSessionFromCard(ending, _clock()));
        }

        if (OnThreadAction(action) is { } thread)
        {
            return thread;
        }

        if (OnPaymentAppButton(action, messageId) is { } paymentApp)
        {
            return paymentApp;
        }

        if (OnLateNightButton(action) is { } lateNight)
        {
            return lateNight;
        }

        if (OnPhoneAction(action, messageId) is { } phone)
        {
            return phone;
        }

        if (TakeId(action, WaitBought) is { } bought)
        {
            return new ButtonResult(Outcome: _database.ResolveWaitItem(bought, "bought").Text);
        }

        if (TakeId(action, WaitSkip) is { } skipped)
        {
            return new ButtonResult(Outcome: _database.ResolveWaitItem(skipped, "skipped").Text);
        }

        if (TakeId(action, WaitingFollowUp) is { } followUp)
        {
            return _database.GetWaiting().FirstOrDefault(waiting => waiting.ItemId == followUp) is { } waiting
                ? new ButtonResult(DiscordText.Reply(WaitingOn.DraftReply(waiting)) with { Actions = WaitingActions(waiting.ItemId)[1..] })
                : new ButtonResult(Outcome: "That one isn't waiting anymore");
        }

        if (TakeId(action, WaitingStill) is { } still)
        {
            return new ButtonResult(Outcome: _database.StillWaiting(still, null, _clock()).Text);
        }

        if (TakeRef(action, FinishPick) is { } toFinish)
        {
            return toFinish.Done ? new ButtonResult(Outcome: $"{toFinish.Title} was already finished") : new ButtonResult(Finish(toFinish));
        }

        if (TakeRef(action, StartPick) is { } toStart)
        {
            return toStart.Done ? new ButtonResult(Outcome: $"{toStart.Title} is already finished") : new ButtonResult(StartFromDiscord(toStart));
        }

        if (TakeRef(action, UndoFinish) is { } finished)
        {
            if (finished.Done)
            {
                _database.SetDone(finished, false);
            }

            return new ButtonResult(DiscordText.Notice($"{finished.Title} is back on the list", "", CardTone.Reminder));
        }

        if (action == AfterThisNotNow)
        {
            return new ButtonResult(Outcome: "Okay. It's still on your list.");
        }

        if (action.StartsWith(MakeList, StringComparison.Ordinal) && _newLists.TryRemove(action[MakeList.Length..], out var asked))
        {
            // Adding makes the list in the same write, so a failure partway never leaves it made but empty.
            var adding = ListLines(asked.Entry);
            if (adding.Any(entry => entry.Trim().Length > 0))
            {
                _database.AddListEntries(asked.List, adding, _clock());
            }
            else
            {
                _database.CreateList(asked.List, _clock());
            }
            return new ButtonResult(Outcome: $"Started a list called {asked.List}, with {string.Join(", ", adding)} on it");
        }

        return new ButtonResult(Outcome: "That button has run out. Try the command again.");
    }

    /// <summary>
    /// A waiting-on nudge's card. It's about someone else, so it gets Follow up, Still waiting, and Done instead of
    /// Start and 10 more minutes. All three are answered here, without the sorter.
    /// </summary>
    public static DiscordCard WaitingNudgeCard(DueReminder reminder, WaitingItem waiting, DateOnly today) =>
        DiscordText.Reminder(reminder, today, canFinish: false) with { Actions = WaitingActions(waiting.ItemId) };

    private static CardAction[] WaitingActions(long itemId)
    {
        var id = itemId.ToString(CultureInfo.InvariantCulture);
        return
        [
            new CardAction(CardLabels.FollowUp, WaitingFollowUp + id),
            new CardAction(CardLabels.StillWaiting, WaitingStill + id, ButtonRole.Other),
            new CardAction("Done", FinishPick + ItemRef.Format(ItemSource.Capture, itemId), ButtonRole.Other),
        ];
    }

    /// <summary>Finishes an item, with Undo on the reply in case it was the wrong one.</summary>
    private DiscordCard Finish(StoredItem item)
    {
        // A dose is marked taken once for its day. A repeating item has nothing to undo here, so there's no Undo.
        if (item is { Dose: true, Recurs: not null })
        {
            _viewsDirty = true;
            return DiscordText.Notice(_database.TakeDose(item, _clock()).Text, "", CardTone.Finished);
        }

        _database.SetDone(item, true);
        _viewsDirty = true;
        return DiscordText.Notice($"Finished {item.Title}", "", CardTone.Finished) with
        {
            Actions = [new CardAction("Undo", UndoFinish + ItemRef.Format(item), ButtonRole.Other)],
        };
    }

    /// <summary>The list name wasn't found. It says which lists there are, and offers to start the new one.</summary>
    private CommandReply NewListOffer(string name, string entry)
    {
        var lists = _database.GetListNames();
        var key = Guid.NewGuid().ToString("N")[..12];
        _newLists[key] = (name.Trim(), entry);
        var have = lists.Count == 0 ? "There are no lists yet." : $"Your lists: {string.Join(", ", lists)}.";
        var card = DiscordText.Reply($"There's no list called {name} yet\n{have} Pick one from the suggestions, or start this one.", CardTone.Attention) with
        {
            Actions = [new CardAction($"Start a list called {name}", MakeList + key)],
        };
        return new CommandReply(card, true);
    }

    /// <summary>
    /// The one item held from earlier, taken so it's shown only this once, as a line and its Start and Not now for a
    /// card that's already going out. Nothing when there's none. <paramref name="write"/> takes it inside that write.
    /// </summary>
    private (string? Line, IReadOnlyList<CardAction>? Actions) TakeAfterThisLine(Data.WaypointDatabase? write = null)
    {
        if ((write ?? _database).TakeAfterThis(_clock()) is not { } item)
        {
            return default;
        }

        return (AfterThis.Line(item), [new CardAction("Start", StartPick + ItemRef.Format(item)), new CardAction(CardLabels.NotNow, AfterThisNotNow, ButtonRole.NotNow)]);
    }

    /// <summary>Holds startable Discord reminders that got no answer, for <see cref="AfterThis"/>.</summary>
    private void CheckUnanswered() => _database.HoldUnansweredReminders(_clock());

    private StoredItem? TakeRef(string action, string prefix) =>
        action.StartsWith(prefix, StringComparison.Ordinal) ? _database.FindItems([action[prefix.Length..]]).FirstOrDefault() : null;

    private static long? TakeId(string action, string prefix) =>
        action.StartsWith(prefix, StringComparison.Ordinal) && long.TryParse(action[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
}
