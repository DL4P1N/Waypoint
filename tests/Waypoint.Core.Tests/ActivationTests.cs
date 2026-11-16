using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Tests;

public sealed class ActivationTests : IDisposable
{
    private static readonly DateTime Now = DateTime.Now;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public ActivationTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private sealed class FakeSorter(Func<AssistantContext, CaptureResult?> sort) : ICaptureSorter
    {
        public int Calls { get; private set; }

        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(sort(context));
        }
    }

    private sealed class FailingSorter : ICaptureSorter
    {
        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("out of usage");
    }

    private StoredItem AddItem(string title, ItemKind kind = ItemKind.Deadline, DateOnly? due = null, params string[] steps)
    {
        var plan = steps.Select(step => new PlannedStep(step, null)).ToList();
        _database.CompleteCapture(_database.AddCapture(title), [new SortedItem(kind, title, due ?? DateOnly.FromDateTime(Now.AddDays(2)), null, Steps: plan.Count > 0 ? plan : null)]);
        return _database.GetOpenItems().First(item => item.Title == title);
    }

    private async Task<CaptureOutcome> Say(ICaptureSorter? sorter, string text)
    {
        CaptureOutcome? outcome = null;
        var service = new CaptureService(_database, sorter);
        service.Sorted += result => outcome = result;
        service.Capture(text);
        await service.ProcessPendingAsync();
        return outcome!;
    }

    private OpReply Start(StoredItem item, string? first = null, params string[] then) =>
        _database.StartActivation(ItemRef.Format(item), first, then, Activation.TypedMethod, Now);

    [Fact]
    public async Task I_cant_start_gives_one_tiny_action_not_a_plan()
    {
        var paper = AddItem("Paper #2");
        var sorter = new FakeSorter(_ => new CaptureResult([], Ops: [new AssistantOp(AssistantOpKind.ActivationStart, ItemRef.Format(paper), "Open the course site", ["Find the assignment", "Read question one"])]));

        var outcome = await Say(sorter, "I can't start");

        Assert.Single(outcome.Answer!.Split('\n'));
        Assert.Contains("Open the course site", outcome.Answer);
        Assert.DoesNotContain("Find the assignment", outcome.Answer);
        Assert.Equal("Open the course site", outcome.Activation!.Action);
    }

    [Fact]
    public async Task Starting_works_when_sorting_is_unavailable()
    {
        AddItem("Paper #2");

        var outcome = await Say(new FailingSorter(), "help me begin");

        Assert.Contains(Activation.Fallback(0), outcome.Answer);
        Assert.Equal("Paper #2", _database.GetActiveSession(Now)!.TaskTitle);
        Assert.Empty(_database.GetPendingCaptures());
    }

    [Fact]
    public async Task A_note_that_names_a_task_wins_over_the_soonest_when_picking_locally()
    {
        AddItem("Quiz", due: DateOnly.FromDateTime(Now.AddDays(1)));
        AddItem("Paper #2", due: DateOnly.FromDateTime(Now.AddDays(5)));

        await Say(null, "I can't start the paper");

        Assert.Equal("Paper #2", _database.GetActiveSession(Now)!.TaskTitle);
    }

    [Fact]
    public async Task Nothing_to_start_says_so_kindly()
    {
        var outcome = await Say(null, "I can't start");

        Assert.Equal(Activation.NothingToPick, outcome.Answer);
        Assert.Null(_database.GetActiveSession(Now));
    }

    [Fact]
    public async Task A_ref_the_sorter_was_not_shown_is_ignored()
    {
        var paper = AddItem("Paper #2");
        var sorter = new FakeSorter(_ => new CaptureResult([], Ops: [new AssistantOp(AssistantOpKind.ActivationStart, "item:9999", "Open it")]));

        await Say(sorter, "I can't start");

        Assert.Equal(ItemRef.Format(paper), _database.GetActiveSession(Now)!.TaskRef);
    }

    [Fact]
    public void Steps_are_reused_and_done_advances_through_them()
    {
        var paper = AddItem("Paper #2", steps: ["Open the prompt", "Write one line"]);
        Start(paper, "ignored action");

        Assert.Equal("Open the prompt", _database.GetActiveSession(Now)!.Action);

        var reply = _database.ApplySessionWord(SessionWord.Did, Now)!;

        Assert.Equal("Next: Write one line.", reply.Text);
        Assert.DoesNotContain(_database.GetOpenItems(), item => item.Title == "Open the prompt");
    }

    [Fact]
    public void After_the_last_action_it_offers_a_trial_then_finishes_without_closing_the_task()
    {
        var paper = AddItem("Paper #2");
        Start(paper, "Open the course site");

        Assert.Contains("two minutes", _database.ApplySessionWord(SessionWord.Did, Now)!.Text);
        Assert.True(_database.GetActiveSession(Now)!.CanTrial);

        _database.ApplySessionWord(SessionWord.Did, Now);

        Assert.Null(_database.GetActiveSession(Now));
        Assert.Contains(_database.GetOpenItems(), item => item.Id == paper.Id);
    }

    [Fact]
    public async Task Short_replies_need_no_sorter_call()
    {
        Start(AddItem("Paper #2"), "Open the course site", "Find it");
        var sorter = new FakeSorter(_ => null);

        var outcome = await Say(sorter, "Done.");

        Assert.Equal(0, sorter.Calls);
        Assert.Equal("Next: Find it.", outcome.Answer);
        Assert.Empty(_database.GetPendingCaptures());
    }

    [Fact]
    public async Task Done_without_a_session_still_goes_to_the_sorter()
    {
        var sorter = new FakeSorter(_ => new CaptureResult([]));

        await Say(sorter, "done");

        Assert.Equal(1, sorter.Calls);
    }

    [Fact]
    public async Task Smaller_goes_through_the_sorter_with_the_session_in_context()
    {
        Start(AddItem("Paper #2"), "Read the prompt");
        AssistantContext? seen = null;
        var sorter = new FakeSorter(context =>
        {
            seen = context;
            return new CaptureResult([], Ops: [new AssistantOp(AssistantOpKind.ActivationSmaller, Text: "Open the tab")]);
        });

        var outcome = await Say(sorter, "smaller");

        Assert.Equal("Read the prompt", seen!.Activation!.Action);
        Assert.Contains("Open the tab", outcome.Answer);
        Assert.Contains("\"action\":\"Read the prompt\"", ClaudeCliSorter.BuildPrompt("smaller", Now, seen));

        Assert.Equal("Next: Read the prompt.", _database.ApplySessionWord(SessionWord.Did, Now)!.Text);
    }

    [Fact]
    public async Task Smaller_falls_back_and_keeps_getting_smaller_without_the_sorter()
    {
        Start(AddItem("Paper #2"), "Read the prompt");

        var first = await Say(new FailingSorter(), "make it smaller");
        var second = await Say(new FailingSorter(), "smaller");

        Assert.Contains(Activation.Fallback(0), first.Answer);
        Assert.Contains(Activation.Fallback(1), second.Answer);
    }

    [Fact]
    public void A_session_survives_a_restart_and_resumes_at_the_right_action()
    {
        Start(AddItem("Paper #2"), "Open the course site", "Find it", "Read it");
        _database.ApplySessionWord(SessionWord.Did, Now);
        _database.ApplySessionWord(SessionWord.Pause, Now);

        var restarted = new WaypointDatabase(_path);

        Assert.Null(restarted.GetActiveSession(Now));
        Assert.Contains("Find it", restarted.ApplySessionWord(SessionWord.Resume, Now)!.Text);
        Assert.Equal(["Read it"], restarted.GetActiveSession(Now)!.Queue);
    }

    [Fact]
    public void A_session_left_alone_overnight_counts_as_paused_and_can_be_resumed()
    {
        var paper = AddItem("Paper #2");
        var afternoon = DateTime.Today.AddHours(15);
        _database.StartActivation(ItemRef.Format(paper), "Open the course site", [], Activation.TypedMethod, afternoon.AddHours(-13));

        Assert.Null(_database.GetActiveSession(afternoon));
        Assert.Contains("Open the course site", _database.ResumeSession(afternoon).Text);
        Assert.Equal("Paper #2", _database.GetActiveSession(afternoon)!.TaskTitle);
    }

    [Fact]
    public void A_session_with_recent_activity_stays_going()
    {
        var paper = AddItem("Paper #2");
        var afternoon = DateTime.Today.AddHours(15);
        _database.StartActivation(ItemRef.Format(paper), "Open the course site", ["Find it"], Activation.TypedMethod, afternoon.AddHours(-11));
        _database.ApplySessionWord(SessionWord.Did, afternoon.AddHours(-1));

        Assert.NotNull(_database.GetActiveSession(afternoon));
    }

    [Fact]
    public void Staying_up_past_four_keeps_a_session_going_but_the_next_morning_it_rests()
    {
        var day = DateTime.Today;
        Assert.False(Activation.IsStale(day.AddHours(3).AddMinutes(50), day.AddHours(4).AddMinutes(10)));
        Assert.True(Activation.IsStale(day.AddHours(1), day.AddHours(9)));
        Assert.False(Activation.IsStale(day.AddHours(9), day.AddHours(20)));
        Assert.True(Activation.IsStale(day.AddHours(8), day.AddHours(20).AddMinutes(1)));
    }

    [Fact]
    public void A_session_started_from_a_generic_first_action_opens_on_the_saved_spots_next_step()
    {
        var exam = AddItem("Calc midterm", ItemKind.Exam);
        _database.SaveHandoff("Calc midterm", "Halfway through chapter 3", "Do the chapter 3 practice set", [], 0, Now.AddDays(-1));

        _database.StartActivation(ItemRef.Format(exam), "Open your Calc midterm notes and read the first page", [], Activation.TypedMethod, Now);

        Assert.Equal("Do the chapter 3 practice set", _database.GetActiveSession(Now)!.Action);
    }

    [Theory]
    [InlineData("I have 15 minutes", EnergyLevel.FifteenMinutes)]
    [InlineData("i only have 20 min.", EnergyLevel.FifteenMinutes)]
    [InlineData("15 minutes", EnergyLevel.FifteenMinutes)]
    [InlineData("I'm exhausted", EnergyLevel.LowEnergy)]
    [InlineData("low energy", EnergyLevel.LowEnergy)]
    [InlineData("I can't focus", EnergyLevel.LowEnergy)]
    [InlineData("I have 2 hours", null)]
    [InlineData("I have 15 minutes before class, can you help with the essay outline?", null)]
    public void Energy_requests_are_known_without_the_sorter(string text, EnergyLevel? expected) =>
        Assert.Equal(expected, EnergyPicks.ParseRequest(text));

    [Fact]
    public async Task Typing_I_have_15_minutes_starts_the_same_pick_as_the_button_without_the_sorter()
    {
        AddItem("Paper #2", ItemKind.Deadline, DateOnly.FromDateTime(Now.AddDays(2)));
        AddItem("Email advisor", ItemKind.Task, DateOnly.FromDateTime(Now.AddDays(1)));
        var expected = EnergyPicks.Pick(_database.GetOpenItems(), EnergyLevel.FifteenMinutes, DateOnly.FromDateTime(Now))!;
        var sorter = new FakeSorter(_ => null);

        var outcome = await Say(sorter, "I have 15 minutes");

        Assert.Equal(0, sorter.Calls);
        Assert.StartsWith($"{EnergyPicks.Heading(EnergyLevel.FifteenMinutes)}: {expected.Item.Title}.", outcome.Answer);
        Assert.Equal(ItemRef.Format(expected.Item), _database.GetActiveSession(DateTime.Now)!.TaskRef);
        Assert.NotNull(outcome.Activation);
    }

    [Fact]
    public async Task An_unclear_energy_request_still_goes_to_the_sorter()
    {
        AddItem("Paper #2");
        var sorter = new FakeSorter(_ => new CaptureResult([], Answer: "Open the outline."));

        await Say(sorter, "I have 15 minutes before class, can you help with the essay outline?");

        Assert.Equal(1, sorter.Calls);
    }

    [Fact]
    public void Asking_to_start_a_paused_task_picks_up_where_it_stopped()
    {
        var paper = AddItem("Paper #2");
        Start(paper, "Open the course site", "Find it");
        _database.ApplySessionWord(SessionWord.Did, Now);
        _database.ApplySessionWord(SessionWord.Pause, Now);

        Assert.Contains("Find it", Start(paper, "Something new").Text);
    }

    [Fact]
    public void Starting_something_else_pauses_the_running_session_rather_than_losing_it()
    {
        Start(AddItem("Paper #2"), "Open the course site");
        Start(AddItem("Quiz"), "Open notes");

        Assert.Equal("Quiz", _database.GetActiveSession(Now)!.TaskTitle);
        Assert.Contains("Open the course site", _database.ResumeSession().Text);
    }

    [Fact]
    public void Finishing_the_task_elsewhere_ends_the_session()
    {
        var paper = AddItem("Paper #2");
        Start(paper, "Open the course site");

        _database.SetDone(paper, true);

        Assert.Null(_database.GetActiveSession(Now));
    }

    [Fact]
    public void Checking_off_the_current_step_elsewhere_moves_the_session_on()
    {
        var paper = AddItem("Paper #2", steps: ["One", "Two"]);
        Start(paper);

        _database.SetDone(_database.GetOpenItems().First(item => item.Title == "One"), true);

        Assert.Equal("Two", _database.GetActiveSession(Now)!.Action);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("Yeah!")]
    [InlineData("ok")]
    [InlineData("sure.")]
    public async Task A_yes_to_the_offer_starts_the_trial_without_the_sorter(string text)
    {
        Start(AddItem("Paper #2"), "Open the course site");
        var sorter = new FakeSorter(_ => null);
        await Say(sorter, "done");

        var outcome = await Say(sorter, text);

        Assert.Equal(0, sorter.Calls);
        Assert.StartsWith("Two minutes, starting now", outcome.Answer);
        Assert.Equal(Activation.KeepGoing, _database.GetActiveSession(Now)!.Action);
    }

    [Fact]
    public async Task A_yes_with_no_offer_waiting_goes_to_the_sorter()
    {
        var sorter = new FakeSorter(_ => new CaptureResult([]));
        await Say(sorter, "yes");
        Assert.Equal(1, sorter.Calls);

        Start(AddItem("Paper #2"), "Open the course site", "Find it");
        await Say(sorter, "done");
        await Say(sorter, "yes");

        Assert.Equal(2, sorter.Calls);
        Assert.Null(_database.GetActiveSession(Now)!.PromptAt);
    }

    [Fact]
    public async Task A_yes_after_some_other_reply_goes_to_the_sorter()
    {
        Start(AddItem("Paper #2"), "Open the course site");
        var sorter = new FakeSorter(_ => new CaptureResult([], Answer: "Want me to add milk?"));
        await Say(sorter, "done");
        await Say(sorter, "we're out of milk");

        await Say(sorter, "yes");

        Assert.Equal(2, sorter.Calls);
        Assert.Equal(Activation.TrialOffer, _database.GetActiveSession(Now)!.Action);
    }

    [Fact]
    public void Trial_checks_in_once_and_leaves_the_session_going()
    {
        Start(AddItem("Paper #2"), "Open the course site");
        _database.ApplySessionWord(SessionWord.Did, Now);
        _database.ApplySessionWord(SessionWord.Trial, Now);

        Assert.Null(_database.TakeDuePrompt(Now.AddMinutes(1)));
        Assert.Equal("Two minutes are up", _database.TakeDuePrompt(Now.AddMinutes(3))!.Headline);
        Assert.Null(_database.TakeDuePrompt(Now.AddMinutes(4)));
        Assert.Equal(Activation.KeepGoing, _database.GetActiveSession(Now)!.Action);
    }

    [Fact]
    public void A_trial_missed_while_the_pc_was_off_stays_quiet()
    {
        Start(AddItem("Paper #2"), "Open the course site");
        _database.ApplySessionWord(SessionWord.Trial, Now);

        Assert.Null(_database.TakeDuePrompt(Now.AddHours(5)));
        Assert.Null(_database.TakeDuePrompt(Now.AddHours(6)));
    }

    [Fact]
    public void Later_choices_skip_the_evening_once_it_has_come()
    {
        var afternoon = new DateTime(2026, 11, 3, 14, 30, 0);

        Assert.Equal(
            [("In an hour", afternoon.AddHours(1)), ("This evening", new DateTime(2026, 11, 3, 18, 0, 0)), ("Tomorrow morning", new DateTime(2026, 11, 4, 9, 0, 0))],
            Activation.LaterChoices(afternoon));
        Assert.Equal(["In an hour", "Tomorrow morning"], Activation.LaterChoices(new DateTime(2026, 11, 3, 19, 0, 0)).Select(choice => choice.Label));
    }

    [Fact]
    public void Tomorrow_morning_after_midnight_is_the_morning_still_ahead()
    {
        // Still up at 1:40 AM, "tomorrow morning" is the one after sleeping, not a whole day later.
        var lateNight = new DateTime(2026, 11, 10, 1, 40, 0);

        Assert.Contains(("Tomorrow morning", new DateTime(2026, 11, 10, 9, 0, 0)), Activation.LaterChoices(lateNight));
        Assert.Contains(("Tomorrow morning", new DateTime(2026, 11, 11, 9, 0, 0)), Activation.LaterChoices(new DateTime(2026, 11, 10, 6, 0, 0)));
    }

    [Fact]
    public void A_picked_time_only_counts_if_it_is_still_ahead()
    {
        var now = new DateTime(2026, 11, 3, 14, 30, 0);

        Assert.Equal(new DateTime(2026, 11, 4, 9, 0, 0), Activation.BringBackAt(new DateOnly(2026, 11, 4), null, now));
        Assert.Equal(new DateTime(2026, 11, 3, 16, 0, 0), Activation.BringBackAt(new DateOnly(2026, 11, 3), new TimeOnly(16, 0), now));
        Assert.Null(Activation.BringBackAt(new DateOnly(2026, 11, 3), new TimeOnly(9, 0), now));
    }

    [Fact]
    public void A_later_choice_pauses_the_session_until_then()
    {
        Start(AddItem("Paper #2"), "Open the course site");
        var (_, at) = Activation.LaterChoices(Now)[0];

        Assert.Contains("I'll bring it back", _database.PauseSession(null, at, Now).Text);

        Assert.Null(_database.GetActiveSession(Now));
        Assert.Null(_database.TakeDuePrompt(at.AddMinutes(-1)));
        Assert.Contains("Open the course site", _database.TakeDuePrompt(at.AddMinutes(1))!.Text);
    }

    [Fact]
    public void Pausing_until_another_day_names_the_day()
    {
        Start(AddItem("Paper #2"), "Open the course site");

        var reply = _database.PauseSession(null, Now.Date.AddDays(1).AddHours(9), Now);

        Assert.Contains("I'll bring it back tomorrow at", reply.Text);
    }

    [Fact]
    public void Rescheduling_brings_it_back_once_and_keeps_it_paused()
    {
        Start(AddItem("Paper #2"), "Open the course site");
        _database.PauseSession("after dinner", Now.AddHours(2), Now);

        Assert.Null(_database.TakeDuePrompt(Now.AddHours(1)));
        Assert.Contains("Open the course site", _database.TakeDuePrompt(Now.AddHours(3))!.Text);
        Assert.Null(_database.TakeDuePrompt(Now.AddHours(4)));
        Assert.Null(_database.GetActiveSession(Now));
    }

    [Fact]
    public void Cancel_ends_the_session_and_keeps_the_task()
    {
        var paper = AddItem("Paper #2");
        Start(paper, "Open the course site");

        Assert.Contains("still on your list", _database.ApplySessionWord(SessionWord.Cancel, Now)!.Text);
        Assert.Null(_database.GetActiveSession(Now));
        Assert.Contains("Nothing's paused", _database.ResumeSession().Text);
        Assert.Contains(_database.GetOpenItems(), item => item.Id == paper.Id);
    }

    [Fact]
    public void Starting_from_a_reminder_records_how_it_began_and_stats_are_plain_counts()
    {
        _database.StartActivation(ItemRef.Format(AddItem("Paper #2")), null, null, Activation.ReminderMethod, Now);
        _database.ApplySessionWord(SessionWord.Did, Now.AddMinutes(4));

        var stats = _database.DescribeSessionStats();

        Assert.Contains("reminder: began 1 of 1, about 4 min", stats);
        Assert.DoesNotContain("streak", stats, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://school.example/courses/1", true)]
    [InlineData("http://example.com/a", true)]
    [InlineData("C:\\School\\notes.pdf", true)]
    [InlineData("C:/School/notes.docx", true)]
    [InlineData("C:\\Windows\\System32\\calc.exe", false)]
    [InlineData("C:\\School\\run.bat", false)]
    [InlineData("C:\\School\\run.ps1", false)]
    [InlineData("C:\\School\\link.lnk", false)]
    [InlineData("C:\\School\\notes.pdf.exe", false)]
    [InlineData("C:\\School\\notes.pdf:evil.exe", false)]
    [InlineData("\\\\server\\share\\notes.pdf", false)]
    [InlineData("notes.pdf", false)]
    [InlineData("file:///C:/School/notes.pdf", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("calc", false)]
    [InlineData("", false)]
    public void Only_web_links_and_documents_can_be_opened(string text, bool allowed) =>
        Assert.Equal(allowed, SafeResource.Check(text, _ => true) is not null);

    [Fact]
    public void A_document_that_is_not_there_is_refused() =>
        Assert.Null(SafeResource.Check("C:\\School\\notes.pdf", _ => false));

    [Fact]
    public void An_unsafe_link_is_not_attached_to_the_session()
    {
        Start(AddItem("Paper #2"), "Open the course site");

        Assert.Contains("only open", _database.SetSessionResource("C:\\evil.exe", Now).Text);
        Assert.Null(_database.GetActiveSession(Now)!.Resource);

        _database.SetSessionResource("https://example.com/a", Now);
        Assert.Equal("https://example.com/a", _database.GetActiveSession(Now)!.Resource);
    }

    [Fact]
    public async Task A_handoff_records_the_next_action_and_is_easy_to_find_again()
    {
        var sorter = new FakeSorter(_ => new CaptureResult([], Ops:
        [
            new AssistantOp(AssistantOpKind.HandoffSave, "Waypoint", "Activation UI", ["https://example.com/pr", "Sessions must survive restarts"], Next: "Connect the Start button"),
        ]));
        await Say(sorter, "save my spot: stopped on Activation UI, next connect the Start button");

        var shown = _database.DescribeHandoff("waypoint");

        Assert.Contains("Stopped: Activation UI", shown);
        Assert.Contains("Next: Connect the Start button", shown);
        Assert.Contains("Links: https://example.com/pr", shown);
        Assert.Contains("Remember: Sessions must survive restarts", shown);
        Assert.Contains(_database.SearchEverything(new SearchQuery("start button"), Now), doc => doc.Bucket == SearchIndex.Handoff);
        Assert.Contains(_database.SearchEverything(new SearchQuery("restarts", SearchIndex.Handoff), Now), doc => doc.Text.Contains("Waypoint"));
    }

    [Fact]
    public async Task A_handoff_without_a_next_action_asks_instead_of_inventing_one()
    {
        var outcome = await Say(new FakeSorter(_ => new CaptureResult([], Ops: [new AssistantOp(AssistantOpKind.HandoffSave, "Waypoint", "Activation UI")])), "save my spot on waypoint");

        Assert.Contains("What's the next thing", outcome.Answer);
        Assert.DoesNotContain("Next:", _database.DescribeHandoff("waypoint"));

        AssistantContext? seen = null;
        await Say(
            new FakeSorter(context =>
            {
                seen = context;
                return new CaptureResult([], Ops: [new AssistantOp(AssistantOpKind.HandoffNext, Next: "Wire the button")]);
            }),
            "wire the button");

        Assert.Equal("Waypoint", seen!.WaitingHandoff);
        Assert.Contains("Next: Wire the button", _database.DescribeHandoff("waypoint"));
        Assert.Null(_database.GetWaitingHandoff(DateTime.Now));
    }

    [Fact]
    public void The_handoff_question_expires_so_a_later_note_is_not_mistaken_for_it()
    {
        var capture = _database.AddCapture("spot");
        _database.SaveHandoff("Waypoint", "UI", null, null, capture, Now);

        Assert.Null(_database.GetWaitingHandoff(Now.AddHours(2)));
    }

    [Fact]
    public void Asking_for_an_unknown_project_says_so()
    {
        Assert.Equal("No saved spots yet.", _database.DescribeHandoff(null));
        Assert.Equal("No saved spot for chess.", _database.DescribeHandoff("chess"));
    }

    [Fact]
    public async Task Switching_saves_the_spot_then_starts_on_the_new_task()
    {
        var study = AddItem("Exam 2 study");
        var sorter = new FakeSorter(_ => new CaptureResult([], Ops:
        [
            new AssistantOp(AssistantOpKind.HandoffSave, "Waypoint", "Sessions", Next: "Write tests"),
            new AssistantOp(AssistantOpKind.ActivationSwitch, ItemRef.Format(study), "Open your exam notes"),
        ]));

        var outcome = await Say(sorter, "help me switch to studying, stopped on sessions, next write tests");

        Assert.Contains("Open your exam notes", outcome.Answer);
        Assert.Equal(Activation.SwitchMethod, _database.GetActiveSession(Now)!.Method);
        Assert.Contains("Write tests", _database.DescribeHandoff("waypoint"));
    }

    [Fact]
    public void A_start_only_reminder_cant_be_finished_by_a_stray_done()
    {
        var exam = AddItem("Exam 2", ItemKind.Exam);
        _database.TrackSentReminder("900", exam.Id, startOnly: true);

        Assert.Null(_database.ApplyAlertAction(new AlertAction(null, AlertActionKind.Done), Now, 15));
        Assert.Contains(_database.GetOpenItems(), item => item.Id == exam.Id);
    }

    [Fact]
    public void Start_from_a_sent_reminder_finds_its_item_once_including_feed_events()
    {
        var capture = AddItem("Paper #2");
        _database.TrackSentReminder("901", capture.Id, startOnly: true);

        Assert.Equal(capture.Id, _database.TakeSentReminderItem("901")!.Id);
        Assert.Null(_database.TakeSentReminderItem("901"));
    }

    [Fact]
    public void Reminders_for_things_with_no_work_get_no_start_button()
    {
        Assert.True(Activation.CanStart(new StoredItem(1, ItemKind.Exam, "Exam", null, null, false)));
        Assert.False(Activation.CanStart(new StoredItem(2, ItemKind.Shopping, "Milk", null, null, false)));
        Assert.False(Activation.CanStart(new StoredItem(3, ItemKind.Reminder, "Water the plants", null, null, false, Recurs: new Recurrence(RecurrenceUnit.Day))));
    }

    [Fact]
    public void New_ops_are_in_the_schema()
    {
        foreach (var name in new[] { "activation_start", "activation_smaller", "handoff_save", "handoff_show" })
        {
            Assert.Contains($"\"{name}\"", ClaudeCliSorter.Schema);
        }
    }
}
