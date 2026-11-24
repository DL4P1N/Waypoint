using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Data;
using Waypoint.Core.Files;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Capture;

/// <summary>
/// Saves captures first and sorts them afterwards, so nothing is lost if sorting
/// is unavailable. Unsorted captures stay pending and are retried later.
/// </summary>
public sealed class CaptureService(
    WaypointDatabase database,
    ICaptureSorter? sorter,
    ClassSchedule? schedule = null,
    BedtimeSchedule? bedtime = null,
    Func<DateTime>? clock = null)
{
    private static readonly TimeSpan UndoWindow = TimeSpan.FromHours(24);
    private const int MaxFileItems = 30;

    private static readonly TimeSpan RescueTimeout = TimeSpan.FromSeconds(25);
    private static readonly string[] NextStepWords = ["what do i do", "what should i", "what now", "what's next", "whats next", "work on"];

    private readonly SemaphoreSlim _processing = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _stopGate = new();
    private Task<bool>? _stopped;

    /// <summary>The time as the service sees it. Tests pass a fixed clock so they don't depend on the hour.</summary>
    private DateTime Now => clock?.Invoke() ?? DateTime.Now;

    /// <summary>Raised after stored data changes, possibly on a background thread.</summary>
    public event Action? Changed;

    /// <summary>Raised with what came of a capture, possibly on a background thread.</summary>
    public event Action<CaptureOutcome>? Sorted;

    public string? LastError { get; private set; }

    /// <summary>Raised with the error when sorting fails, possibly on a background thread. The captures stay pending.</summary>
    public event Action<string>? Failed;

    /// <summary>Reads new bank and card emails now and says what came in. Null when email isn't connected.</summary>
    public Func<CancellationToken, Task<string>>? CheckEmail { get; set; }

    /// <summary><paramref name="createdAt"/> is when it was written, if that wasn't just now. Returns the capture's ID, or null if it was empty.</summary>
    public long? Capture(string text, DateTimeOffset? createdAt = null, CaptureOrigin origin = CaptureOrigin.App)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var id = database.AddCapture(trimmed, createdAt, origin);
        Changed?.Invoke();
        return id;
    }

    /// <summary>
    /// The same, saved under an ID of its own, such as the Discord message it came from, so the same message handed
    /// over again saves nothing more. Null when it's empty or was already saved.
    /// </summary>
    public long? CaptureOnce(string clientId, string text, DateTimeOffset? createdAt, CaptureOrigin origin)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || database.AddCaptureOnce(clientId, trimmed, createdAt, origin) is not { } id)
        {
            return null;
        }

        Changed?.Invoke();
        return id;
    }

    /// <summary>
    /// A dropped file waits in line like anything typed, so it's kept if sorting is unavailable. A file sent from
    /// Discord is treated the same, and its reply goes back there. With <paramref name="clientId"/>, it's saved once
    /// however often it's handed over, and the times after give null.
    /// </summary>
    public long? CaptureFile(FileText file, CaptureOrigin origin = CaptureOrigin.App, string? clientId = null)
    {
        if (file.Text is not { } text)
        {
            return null;
        }

        var id = clientId is null ? database.AddCapture(text, origin: origin, fileName: file.Name) : database.AddCaptureOnce(clientId, text, null, origin, file.Name);
        if (id is not null)
        {
            Changed?.Invoke();
        }

        return id;
    }

    /// <summary>
    /// A capture or file sent from the PC to the server under the PC's own ID. It's saved once however often it's
    /// sent, and the times after give null.
    /// </summary>
    public long? CaptureSent(string clientId, string text, DateTimeOffset? createdAt, string? fileName)
    {
        var id = fileName is null
            ? database.AddSentCapture(clientId, text.Trim(), createdAt, null)
            : database.AddSentCapture(clientId, text, null, fileName);
        if (id is not null)
        {
            Changed?.Invoke();
        }

        return id;
    }

    public async Task ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        try
        {
            await _processing.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return;
        }

        cancellationToken = linked.Token;
        try
        {
            if (_stop.IsCancellationRequested)
            {
                return;
            }

            LastError = null;
            var round = new SortRound();
            foreach (var capture in InTurn(database.GetPendingCaptures()))
            {
                // One that kept failing is let go as a note, so it can't hold up the ones after it round after round.
                if (_failures.GetValueOrDefault(capture.Id) >= MaxFailures)
                {
                    TryPark(capture);
                    continue;
                }

                // Each capture is its own try, so one that throws doesn't stop the ones after it, such as a done
                // or snooze word, from being handled this round.
                try
                {
                    await ProcessOneAsync(capture, round, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Trouble with the database, or with reading what the sorter is shown, is the same for every
                    // capture, so it isn't held against this one.
                    RecordError(exception.Message);
                    if (!round.BuildingContext && exception is not SqliteException)
                    {
                        CountFailure(capture.Id);
                    }

                    round.BuildingContext = false;
                }
            }

            _startAfter = round.LastFailed;

            if (LastError is not null)
            {
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Stopped for a restore. One cut off partway picks up where it stopped next start.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastError = exception.Message;
            Changed?.Invoke();
            Failed?.Invoke(exception.Message);
        }
        finally
        {
            _processing.Release();
        }
    }

    /// <summary>How many times a capture can fail before it's kept as a note instead of being tried again.</summary>
    public const int MaxFailures = 3;

    /// <summary>Said when a capture that kept failing is kept as a note just as it was written.</summary>
    public const string Parked = "I couldn't sort this after a few tries, so I kept it as a note just as you wrote it.";

    /// <summary>Said when a capture whose steps had begun kept failing to finish.</summary>
    public const string ParkedPartway = "This kept stopping partway, so I stopped trying. Check that it went through.";

    /// <summary>How many times each capture has failed in this run, kept until it's sorted or let go.</summary>
    private readonly Dictionary<long, int> _failures = [];

    /// <summary>
    /// Where the next round starts: after the last capture the sorter failed on, so the ones behind a failing pair
    /// get their turn first instead of waiting behind it every round. Null starts from the oldest, as usual.
    /// </summary>
    private long? _startAfter;

    /// <summary>The captures in the order this round tries them.</summary>
    private IEnumerable<PendingCapture> InTurn(IReadOnlyList<PendingCapture> pending) =>
        _startAfter is { } after ? pending.Where(capture => capture.Id > after).Concat(pending.Where(capture => capture.Id <= after)) : pending;

    /// <summary>
    /// One round's view of the sorter, fresh each round. A sort that throws counts against its capture. A second
    /// throw straight after means the sorter itself is down: that one isn't counted, and the rest of the round leaves
    /// sorting alone, with those captures pending as they were. The next round starts after the last one that failed.
    /// </summary>
    private sealed class SortRound
    {
        public bool SorterDown { get; set; }

        public bool LastSortFailed { get; set; }

        public long? LastFailed { get; set; }

        /// <summary>Set while what the sorter is shown is read, so a failure there isn't held against the capture.</summary>
        public bool BuildingContext { get; set; }
    }

    private void RecordError(string message)
    {
        LastError = message;
        Failed?.Invoke(message);
    }

    private void CountFailure(long captureId) => _failures[captureId] = _failures.GetValueOrDefault(captureId) + 1;

    /// <summary>Asks the sorter, keeping a throw to this capture and this round.</summary>
    private async Task<CaptureResult?> SortAsync(PendingCapture capture, string text, AssistantContext context, SortRound round, CancellationToken cancellationToken)
    {
        if (sorter is null || round.SorterDown)
        {
            return null;
        }

        try
        {
            var result = await sorter.SortAsync(text, capture.CreatedAt.ToLocalTime().DateTime, context, cancellationToken);
            round.LastSortFailed = false;
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RecordError(exception.Message);
            round.LastFailed = capture.Id;
            if (round.LastSortFailed)
            {
                round.SorterDown = true;
            }
            else
            {
                CountFailure(capture.Id);
                round.LastSortFailed = true;
            }

            return null;
        }
    }

    /// <summary>
    /// Lets go of a capture that kept failing. One not yet sorted is kept as a note of its text, or of its file's
    /// name, and one whose steps had begun is finished as it stands. Either way the reply says so plainly. If even
    /// that can't be saved, it stays pending and is tried again next round.
    /// </summary>
    private void TryPark(PendingCapture capture)
    {
        try
        {
            var begun = database.GetCaptureProgress(capture.Id) is not null;
            IReadOnlyList<SortedItem> items = begun
                ? []
                : [new SortedItem(ItemKind.Note, capture.FileName is { } file ? FileNote.Title(file) : capture.Text, null, null)];
            var outcome = new CaptureOutcome(items, [], begun ? ParkedPartway : Parked, Origin: capture.Origin);
            database.InOneWrite(write =>
            {
                write.CompleteCapture(capture.Id, items);
                Keep(write, capture, outcome);
            });
            _failures.Remove(capture.Id);
            Announce(capture, outcome);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RecordError(exception.Message);
        }
    }

    private async Task ProcessOneAsync(PendingCapture capture, SortRound round, CancellationToken cancellationToken)
    {
        // One cut off partway picks up where it stopped, without sorting again or repeating a step.
        if (database.GetCaptureProgress(capture.Id) is { } saved)
        {
            await ResumeAsync(capture, saved, cancellationToken);
            _failures.Remove(capture.Id);
            return;
        }

        if (capture.FileName is { } fileName)
        {
            await ProcessFileAsync(capture, fileName, round, cancellationToken);
            return;
        }

        if (TryApplyPayWord(capture) || TryApplySessionWord(capture) || TryApplyRoutineWord(capture) || TryApplyWaitingWord(capture) || TryApplyEnergyWord(capture) || TryApplyDoseQuestion(capture))
        {
            _failures.Remove(capture.Id);
            return;
        }

        // Built fresh each time, since the capture before this one may have changed things.
        round.BuildingContext = true;
        var context = BuildContext(Now, capture.Text, capture.Origin);
        round.BuildingContext = false;
        var wantsSmaller = context.Activation is not null && Activation.ParseWord(capture.Text) == SessionWord.Smaller;
        var wantsStart = !wantsSmaller && Activation.AsksToStart(capture.Text);
        var result = wantsSmaller || wantsStart
            ? await SortWithLimitAsync(capture, context, cancellationToken)
            : await SortAsync(capture, capture.Text, context, round, cancellationToken);

        // Getting going can't wait for a retry, so these fall back to the fixed actions.
        if (wantsSmaller && result?.Ops?.Any(op => op.Kind == AssistantOpKind.ActivationSmaller) != true)
        {
            Act(capture, write => write.MakeSmaller(null, Now));
            return;
        }

        if (wantsStart && !Responds(result))
        {
            Act(capture, write => write.StartActivation(null, null, null, Activation.TypedMethod, Now, capture.Text));
            return;
        }

        if (result is null)
        {
            return;
        }

        result = result with { Ops = KnownOps(result, context) };
        var changes = KnownChanges(result, context);
        var hasContent = result.Items.Count > 0 || changes.Count > 0 || !string.IsNullOrWhiteSpace(result.Answer) || result.MonthlyBudgetCents is not null || result.BudgetOps is { Count: > 0 } || result.Ops is { Count: > 0 };
        // A capture sorted late plans from now, so no study session lands on a day that's already gone.
        var writtenAt = capture.CreatedAt.ToLocalTime().DateTime;
        var planFrom = writtenAt > Now ? writtenAt : Now;
        IReadOnlyList<SortedItem> stored = hasContent
            ? result.Items.Select(item => ReminderPlan.WithStudySessions(item, planFrom)).ToList()
            : [new SortedItem(ItemKind.Note, capture.Text, null, null)];

        // The items, changes, and budget are saved together. Budget changes and actions run after, one at a
        // time, each noted as it's done, so a crash partway never repeats one or loses the ones after it.
        var progress = new StepProgress(
            StepProgress.Sorted,
            Stored: [.. stored],
            Answer: result.Answer,
            MonthlyBudget: result.MonthlyBudgetCents,
            BudgetOps: [.. result.BudgetOps ?? []],
            Ops: [.. result.Ops ?? []],
            FocusRef: result.FocusRef);
        var steps = progress.BudgetOps!.Count + progress.Ops!.Count;
        var applied = database.StoreSortedCapture(capture.Id, stored, changes, result.MonthlyBudgetCents, steps == 0 ? null : changed => (progress with { Applied = [.. changed] }).Save());
        await RunStepsAsync(capture, progress with { Applied = [.. applied] }, context, cancellationToken);
        _failures.Remove(capture.Id);
    }

    /// <summary>
    /// Stops sorting for good, as before a restore: a sort under way is told to stop, and none starts after. True once
    /// none is running, within <paramref name="limit"/>. Calling it again gives the same answer.
    /// </summary>
    public Task<bool> StopAsync(TimeSpan limit)
    {
        lock (_stopGate)
        {
            return _stopped ??= StopCoreAsync(limit);
        }
    }

    private async Task<bool> StopCoreAsync(TimeSpan limit)
    {
        _stop.Cancel();

        // Taken and never given back, so nothing sorts again in this run.
        return await _processing.WaitAsync(limit);
    }

    /// <summary>Said for a step that was running when Waypoint stopped, since it can't be told whether it went through.</summary>
    public const string CutOff = "Waypoint closed before this finished, so check that it went through.";

    /// <summary>Said for a step that failed, so the rest still run and the reply still comes.</summary>
    public const string StepFailed = "Part of this didn't go through, so try that part again.";

    /// <summary>
    /// How far a capture got, kept with it until it's finished. A short reply saves its action and its finished
    /// capture as one write, so it notes nothing now, but a note one left from before is still read. A sorted capture keeps what's needed to run its remaining steps and reply: Next is the step to run, and
    /// Started says it was begun, so it may have gone through.
    /// </summary>
    private sealed record StepProgress(
        string Kind,
        int Next = 0,
        bool Started = false,
        List<SortedItem>? Stored = null,
        List<AppliedChange>? Applied = null,
        string? Answer = null,
        long? MonthlyBudget = null,
        List<BudgetOp>? BudgetOps = null,
        List<AssistantOp>? Ops = null,
        string? FocusRef = null,
        List<string>? BudgetReplies = null,
        List<OpReply>? OpReplies = null)
    {
        public const string Word = "word";
        public const string Sorted = "sorted";

        public string Save() => System.Text.Json.JsonSerializer.Serialize(this);

        public static StepProgress? Read(string text)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<StepProgress>(text);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Runs a short reply's action, such as "done" on a session, and finishes the capture, all as one write. So a
    /// crash partway keeps the action and the finished capture together, or neither, and the capture comes round
    /// again to be done once. False, with the capture left as it was, when the action had nothing to act on and the
    /// capture should go on to the sorter. An action that fails, such as on a busy database, saves nothing, and the
    /// capture is tried again as usual.
    /// </summary>
    private bool Act(PendingCapture capture, Func<WaypointDatabase, OpReply?> action)
    {
        if (database.InOneWrite(write => action(write) is { } reply ? Finish(write, capture, reply) : null) is not { } outcome)
        {
            return false;
        }

        Announce(capture, outcome);
        return true;
    }

    /// <summary>A capture cut off partway. A short reply is answered, and a sorted one runs whatever steps are left.</summary>
    private async Task ResumeAsync(PendingCapture capture, string saved, CancellationToken cancellationToken)
    {
        if (StepProgress.Read(saved) is { Kind: StepProgress.Sorted } progress)
        {
            await RunStepsAsync(capture, progress, null, cancellationToken);
            return;
        }

        // Only a short reply begun before its action and capture were saved as one write leaves this note.
        Reply(capture, new OpReply(CutOff, true));
    }

    /// <summary>
    /// Runs a sorted capture's budget changes, then its actions, from where they got to. Each one that only changes
    /// what's saved here runs in one write with the note that it's done, so a crash keeps both or neither and it's
    /// done once. Reading email reaches outside, so it's noted before it starts and is read again if cut off, since
    /// that's safe. A step noted as begun by an older copy is skipped with <see cref="CutOff"/>, as it can't be told
    /// whether it went through. Then the capture is finished with its reply kept, and the reply goes out.
    /// No write is held open while email is read.
    /// </summary>
    private async Task RunStepsAsync(PendingCapture capture, StepProgress progress, AssistantContext? context, CancellationToken cancellationToken)
    {
        var budgetOps = progress.BudgetOps ?? [];
        var ops = progress.Ops ?? [];
        var total = budgetOps.Count + ops.Count;
        var budgetReplies = progress.BudgetReplies ?? [];
        var opReplies = progress.OpReplies ?? [];
        for (var step = progress.Next; step < total; step++)
        {
            var op = step < budgetOps.Count ? null : ops[step - budgetOps.Count];
            var emailCheck = op?.Kind == AssistantOpKind.EmailCheck && CheckEmail is not null;
            if (step == progress.Next && progress.Started && !emailCheck)
            {
                AddReply(op is null ? null : new OpReply(CutOff, true), CutOff);
            }
            else if (!emailCheck)
            {
                try
                {
                    var current = step;
                    var (budgetReply, opReply) = database.InOneWrite(write =>
                    {
                        var replies = op is null
                            ? (write.ApplyBudgetOp(budgetOps[current], DateOnly.FromDateTime(Now)), (OpReply?)null)
                            : ((string?)null, AssistantOps.Apply(op, write, capture.Id, Now, capture.Text, bedtime));
                        write.SetCaptureProgress(capture.Id, (progress with
                        {
                            Next = current + 1,
                            Started = false,
                            BudgetReplies = [.. budgetReplies, .. replies.Item1 is null ? [] : new[] { replies.Item1 }],
                            OpReplies = [.. opReplies, .. replies.Item2 is null ? [] : new[] { replies.Item2 }],
                        }).Save());
                        return replies;
                    });
                    AddReply(opReply, budgetReply!);
                    continue;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The rest still run and the reply still comes. The error itself is for the log and the status line.
                    LastError = exception.Message;
                    AddReply(op is null ? null : new OpReply(StepFailed, true), StepFailed);
                }
            }
            else
            {
                database.SetCaptureProgress(capture.Id, (progress with { Next = step, Started = true, BudgetReplies = budgetReplies, OpReplies = opReplies }).Save());
                try
                {
                    opReplies.Add(await CheckEmailAsync(CheckEmail!, cancellationToken));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LastError = exception.Message;
                    opReplies.Add(new OpReply(StepFailed, true));
                }
            }

            database.SetCaptureProgress(capture.Id, (progress with { Next = step + 1, Started = false, BudgetReplies = budgetReplies, OpReplies = opReplies }).Save());
        }

        var answers = new[] { progress.Answer }.Concat(opReplies.Where(reply => reply.IsAnswer).Select(reply => reply.Text)).Where(text => !string.IsNullOrWhiteSpace(text));
        var answer = string.Join("\n", answers);
        var focus = FocusOf(progress.FocusRef, answer, Shown(context));

        // Finished and kept together, so a crash between can't leave the reply kept for a capture that comes round again.
        var outcome = database.InOneWrite(write =>
        {
            if (total > 0)
            {
                write.CompleteCapture(capture.Id, []);
            }

            var finished = new CaptureOutcome(
                progress.Stored ?? [],
                progress.Applied ?? [],
                answer.Length > 0 ? answer : null,
                progress.MonthlyBudget,
                capture.Origin,
                budgetReplies,
                opReplies.Where(reply => !reply.IsAnswer).Select(reply => reply.Text).ToList(),
                ops.Any(op => AssistantOps.IsActivation(op.Kind)) ? write.GetActiveSession(Now) : null);
            Keep(write, capture, finished, focus);
            return finished;
        });

        Announce(capture, outcome);

        void AddReply(OpReply? forOp, string forBudget)
        {
            if (forOp is null)
            {
                budgetReplies.Add(forBudget);
            }
            else
            {
                opReplies.Add(forOp);
            }
        }
    }

    /// <summary>Starts on an item directly, as the Start button on a reminder does.</summary>
    public void StartOn(StoredItem item, CaptureOrigin origin = CaptureOrigin.App) =>
        Started(database.StartActivation(ItemRef.Format(item), null, null, Activation.ReminderMethod, Now), origin);

    /// <summary>Says a session was started elsewhere, such as by a reminder's Start saved with the card in one write.</summary>
    public void Started(OpReply reply, CaptureOrigin origin = CaptureOrigin.App)
    {
        Changed?.Invoke();
        Sorted?.Invoke(new CaptureOutcome([], [], reply.Text, Origin: origin, Activation: database.GetActiveSession(Now)));
    }

    /// <summary>
    /// A file can add items and suggest pay dates, and nothing else, so text inside one can't finish,
    /// cancel, or change what's already here. Its spending stays out too, or a statement would count twice.
    /// </summary>
    private async Task ProcessFileAsync(PendingCapture capture, string fileName, SortRound round, CancellationToken cancellationToken)
    {
        var now = Now;
        var note = new SortedItem(ItemKind.Note, FileNote.Title(fileName), null, null);

        if (PayCalendar.TryParse(capture.Text, now) is { } calendar)
        {
            FinishFile(capture, [note], database.ProposePayDates(calendar, now).Text, asked: true);
            return;
        }

        if (sorter is null)
        {
            return;
        }

        round.BuildingContext = true;
        var context = BuildContext(now, fileName);
        round.BuildingContext = false;
        var result = await SortAsync(capture, FileNote.Wrap(fileName, capture.Text), context, round, cancellationToken);
        if (result is null)
        {
            return;
        }

        var items = result.Items.Where(item => item.Kind != ItemKind.Expense).Take(MaxFileItems)
            .Select(item => ReminderPlan.WithStudySessions(item, now))
            .Append(note).ToList();
        var pay = (result.Ops ?? []).Where(op => op.Kind == AssistantOpKind.PayDates).Select(op => AssistantOps.Apply(op, database, capture.Id, now).Text).ToList();
        var cut = FileNote.WasCut(capture.Text) ? $"{fileName} is long, so I only read the first part." : null;
        FinishFile(capture, items, string.Join("\n", new[] { result.Answer }.Concat(pay).Append(cut).Where(text => !string.IsNullOrWhiteSpace(text))), asked: pay.Count > 0);
    }

    /// <summary>The reply that's kept is a plain one, since words from a file must never be what a later yes answers.</summary>
    private void FinishFile(PendingCapture capture, IReadOnlyList<SortedItem> items, string answer, bool asked = false)
    {
        var added = items.Count(item => item.Kind != ItemKind.Note);
        var outcome = new CaptureOutcome(items.Where(item => item.Kind != ItemKind.Note).ToList(), [], answer.Length > 0 ? answer : null, Origin: capture.Origin);
        database.InOneWrite(write =>
        {
            write.CompleteCapture(capture.Id, items);
            Keep(write, capture, outcome, kept: asked ? PayCalendar.AskedNote : $"Read the file and added {added}.");
        });
        _failures.Remove(capture.Id);
        Announce(capture, outcome);
    }

    /// <summary>A yes or no to waiting pay dates, if that's what was just asked.</summary>
    private bool TryApplyPayWord(PendingCapture capture)
    {
        var now = Now;
        if (PayCalendar.ParseWord(capture.Text) is not { } yes || database.GetPayProposal(now) is null)
        {
            return false;
        }

        if (!PayCalendar.NamesProposal(capture.Text) && database.GetRecentExchanges(capture.Origin, now).LastOrDefault()?.Reply != PayCalendar.AskedNote)
        {
            return false;
        }

        return Act(capture, write => yes ? write.ApplyPayProposal(now) : write.CancelPayProposal());
    }

    /// <summary>Short replies to a running session need no sorter, so they work offline and straight away.</summary>
    private bool TryApplySessionWord(PendingCapture capture)
    {
        if ((Activation.ParseWord(capture.Text) ?? (IsYesToTrial(capture) ? SessionWord.Trial : null)) is not { } word || word == SessionWord.Smaller)
        {
            return false;
        }

        return Act(capture, write => write.ApplySessionWord(word, Now));
    }

    /// <summary>A yes starts the trial only while it's on offer and the last reply here was the offer.</summary>
    private bool IsYesToTrial(PendingCapture capture) =>
        Activation.IsYes(capture.Text)
        && database.GetActiveSession(Now)?.Action == Activation.TrialOffer
        && Activation.OffersTrial(database.GetRecentExchanges(capture.Origin, Now).LastOrDefault()?.Reply);

    /// <summary>The plain bedtime phrases need no sorter either, but only mean something during the wind-down.</summary>
    private bool TryApplyRoutineWord(PendingCapture capture)
    {
        var now = Now;
        if (bedtime is null)
        {
            return false;
        }

        if (BedtimeRoutine.IsSnoozeWord(capture.Text))
        {
            return Act(capture, write => BedtimeRoutine.Snooze(write, bedtime, now));
        }

        if (BedtimeRoutine.ParseDoneWord(capture.Text, bedtime.Settings) is not { } step || bedtime.GetWindDown(now) is null)
        {
            return false;
        }

        return Act(capture, write => BedtimeRoutine.Mark(write, bedtime, step, true, now));
    }

    /// <summary>
    /// "I have 15 minutes" or "I'm exhausted" typed alone picks with the same local rules as the To do buttons and
    /// starts on it, so the two never disagree. Anything less clear goes on to the sorter.
    /// </summary>
    private bool TryApplyEnergyWord(PendingCapture capture)
    {
        if (EnergyPicks.ParseRequest(capture.Text) is not { } level)
        {
            return false;
        }

        var now = Now;
        var today = DateOnly.FromDateTime(now);
        var declined = NotNowDeclines.Read(database.GetPreference(NotNowDeclines.Preference), now);
        if (EnergyPicks.Pick(database.WithoutQuietWaiting(database.GetOpenItems(), today), level, today, declined) is not { } pick)
        {
            Reply(capture, new OpReply(Activation.NothingToPick, true));
            return true;
        }

        return Act(capture, write =>
        {
            var started = write.StartActivation(ItemRef.Format(pick.Item), null, null, Activation.EnergyMethod, now);
            return new OpReply($"{EnergyPicks.Heading(level)}: {pick.Item.Title}. {pick.Reason} {started.Text}", true);
        });
    }

    /// <summary>
    /// "Did I take my meds?" is answered from the taken times kept, with no sorter, so it only ever says what was
    /// marked. A question that names nothing marked as a dose goes on to the sorter.
    /// </summary>
    private bool TryApplyDoseQuestion(PendingCapture capture)
    {
        if (Doses.ParseQuestion(capture.Text) is null || Doses.Reply(capture.Text, database.GetDoses(), Now) is not { } answer)
        {
            return false;
        }

        Reply(capture, new OpReply(answer, true));
        return true;
    }

    /// <summary>"still waiting" and "draft follow up" answer the last follow-up nudge, with no sorter. Anything else goes on.</summary>
    private bool TryApplyWaitingWord(PendingCapture capture)
    {
        return WaitingOn.ParseWord(capture.Text) is { } word && Act(capture, write => write.ApplyWaitingWord(word, DateTime.Now));
    }

    /// <summary>A reply with nothing to do but answer, saved with the finished capture as one write.</summary>
    private void Reply(PendingCapture capture, OpReply reply) =>
        Announce(capture, database.InOneWrite(write => Finish(write, capture, reply)));

    /// <summary>Finishes a short reply's capture and keeps the exchange, inside the caller's one write.</summary>
    private CaptureOutcome Finish(WaypointDatabase write, PendingCapture capture, OpReply reply)
    {
        write.CompleteCapture(capture.Id, []);
        var outcome = new CaptureOutcome([], [], reply.Text, Origin: capture.Origin, Activation: write.GetActiveSession(Now));
        Keep(write, capture, outcome);
        return outcome;
    }

    /// <summary>Says what came of a capture once it's saved.</summary>
    private void Announce(PendingCapture capture, CaptureOutcome outcome)
    {
        Changed?.Invoke();
        Sorted?.Invoke(outcome with { CaptureId = capture.Id });
    }

    /// <summary>Keeps the exchange, in the same write that finishes the capture, so the next note can follow on from this one.</summary>
    private void Keep(WaypointDatabase write, PendingCapture capture, CaptureOutcome outcome, string? focus = null, string? kept = null)
    {
        var acted = outcome.Changes.Where(change => change.Applied).Select(change => ItemRef.Format(change.Change.Source, change.Change.ItemId));
        var session = outcome.Activation?.TaskRef;
        var refs = acted.Concat(new[] { focus, session }.OfType<string>()).Distinct().ToList();
        var reply = string.Join("\n", new[] { ItemGroups.OutcomeSummary(outcome), outcome.Answer }.Where(text => !string.IsNullOrWhiteSpace(text)));

        // A file's text stays out, since it would go back to the sorter on every note after it.
        write.AddExchange(capture.Origin, capture.FileName is { } file ? FileNote.Title(file) : capture.Text, kept ?? reply, refs, Now);
    }

    /// <summary>What an answer was about: the sorter's ref if it was shown that item, or the item the answer names.</summary>
    private static string? FocusOf(string? focusRef, string answer, IReadOnlyList<StoredItem> shown)
    {
        if (shown.FirstOrDefault(item => ItemRef.Format(item) == focusRef) is { } named)
        {
            return ItemRef.Format(named);
        }

        var mentioned = shown.Where(item => answer.Contains(item.Title, StringComparison.OrdinalIgnoreCase)).MaxBy(item => item.Title.Length);
        return mentioned is null ? null : ItemRef.Format(mentioned);
    }

    /// <summary>What the sorter was shown, or for a capture picked up after a restart, what it would be shown now.</summary>
    private IReadOnlyList<StoredItem> Shown(AssistantContext? context) =>
        context is not null
            ? context.OpenItems.Concat(context.RecentlyFinished).ToList()
            : database.GetOpenItems().Concat(database.GetRecentlyFinished(Now - UndoWindow)).ToList();

    private async Task<CaptureResult?> SortWithLimitAsync(PendingCapture capture, AssistantContext context, CancellationToken cancellationToken)
    {
        if (sorter is null)
        {
            return null;
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(RescueTimeout);
        try
        {
            return await sorter.SortAsync(capture.Text, capture.CreatedAt.ToLocalTime().DateTime, context, limit.Token);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>What reading email found, then every balance as it stands after it.</summary>
    private async Task<OpReply> CheckEmailAsync(Func<CancellationToken, Task<string>> check, CancellationToken cancellationToken)
    {
        var heard = database.GetWaiting().Where(waiting => waiting.RepliedAt is not null).Select(waiting => waiting.ItemId).ToHashSet();
        var found = await check(cancellationToken);
        var replies = database.GetWaiting().Where(waiting => waiting.RepliedAt is not null && !heard.Contains(waiting.ItemId)).Select(waiting => $"{waiting.Who} wrote back about {waiting.Title}.");
        var balances = BudgetReport.BalanceLines(database.GetCurrentAccounts(DateOnly.FromDateTime(Now)));
        return new OpReply(string.Join("\n", replies.Prepend(found).Concat(balances)), true);
    }

    /// <summary>Whether the sorter did something with a request to start, even if only asking which task.</summary>
    private static bool Responds(CaptureResult? result) =>
        result is not null
        && (!string.IsNullOrWhiteSpace(result.Answer) || result.Changes is { Count: > 0 } || result.Ops?.Any(op => AssistantOps.IsActivation(op.Kind)) == true);

    /// <summary>A session can only start on an item the sorter was shown. Anything else gets picked locally.</summary>
    private static List<AssistantOp> KnownOps(CaptureResult result, AssistantContext context)
    {
        var open = context.OpenItems.Select(ItemRef.Format).ToHashSet();
        return (result.Ops ?? [])
            .Select(op => op.Kind is AssistantOpKind.ActivationStart or AssistantOpKind.ActivationSwitch && !open.Contains(op.Name ?? "") ? op with { Name = null } : op)
            .ToList();
    }

    /// <summary>Money figures only go along when the note is asking what to do next.</summary>
    private static bool AsksWhatToDo(string text) =>
        NextStepWords.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Notes and older finished items only go along when the capture names them, or when the conversation
    /// just touched them. That also makes them items the sorter was shown, so it can change them.
    /// </summary>
    private AssistantContext BuildContext(DateTime now, string text, CaptureOrigin? origin = null)
    {
        var conversation = origin is { } from ? database.GetRecentExchanges(from, now) : [];
        var discussed = database.FindItems(conversation.SelectMany(exchange => exchange.Refs).Distinct());

        return new AssistantContext(
            now,
            database.GetOpenItems().Where(item => item.Kind != ItemKind.Note || SearchIndex.Mentions(item.Title, text)).Concat(discussed.Where(item => !item.Done)).DistinctBy(item => (item.Source, item.Id)).ToList(),
            database.GetRecentlyFinished(now - UndoWindow).Concat(database.GetFinishedMatching(text)).Concat(discussed.Where(item => item.Done)).DistinctBy(item => (item.Source, item.Id)).ToList(),
            schedule?.GetNextClass(now),
            bedtime?.GetPlan(now),
            database.GetSafeToSpend(DateOnly.FromDateTime(now)),
            database.GetCategories().Select(category => category.Name).ToList(),
            database.GetBudgetAccounts().Select(account => account.Name).ToList(),
            AsksWhatToDo(text) ? BudgetReport.MoneyNotes(database.GetBudgetReport(DateOnly.FromDateTime(now))) : null,
            database.GetListNames(),
            database.GetActiveSession(now),
            database.GetWaitingHandoff(now),
            database.GetNextShift(now),
            conversation,
            database.GetWaitingRepayments(),
            database.GetEditedNotesMentioning(text),
            BudgetReport.BalanceLines(database.GetCurrentAccounts(DateOnly.FromDateTime(now))),
            database.GetWaiting());
    }

    /// <summary>Keeps only changes to items the sorter was shown, so a bad reply can't touch anything else.</summary>
    private static List<ItemChange> KnownChanges(CaptureResult result, AssistantContext context)
    {
        var open = context.OpenItems.Select(item => (item.Source, item.Id)).ToHashSet();
        var finished = context.RecentlyFinished.Select(item => (item.Source, item.Id)).ToHashSet();

        return (result.Changes ?? [])
            .Where(change => (change.Action == ChangeAction.Reopen ? finished : open).Contains((change.Source, change.ItemId)))
            .ToList();
    }
}
