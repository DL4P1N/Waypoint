using System.Net;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Email;

namespace Waypoint.Core.Tests;

public sealed class WaitingOnTests : IDisposable
{
    // A Monday, so three days on is a Thursday.
    private static readonly DateOnly Today = new(2026, 11, 9);
    private static readonly DateTime Now = new(2026, 11, 9, 14, 0, 0);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public WaitingOnTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static WaitingItem Waiting(string who, string? email = null, string title = "Hear back") =>
        new(1, title, who, null, Now, Today.AddDays(3), email);

    private WaitingItem AddWaiting(string who = "Prof. Quill", string? title = "Hear back from Prof. Quill about the extension", string? email = null, DateOnly? followUp = null)
    {
        var reply = AssistantOps.Apply(new AssistantOp(AssistantOpKind.WaitingAdd, who, title, email is null ? null : [email], At: followUp?.ToDateTime(new TimeOnly(9, 0))), _database, _database.AddCapture("note"), Now);
        Assert.False(reply.IsAnswer, reply.Text);
        return _database.GetWaiting().Last();
    }

    private StoredItem AddTask(string title)
    {
        _database.CompleteCapture(_database.AddCapture(title), [new SortedItem(ItemKind.Task, title, null, null)]);
        return _database.GetOpenItems().First(item => item.Title == title);
    }

    // The fixed rules.

    [Fact]
    public void The_usual_follow_up_is_three_days_on_and_skips_the_weekend()
    {
        Assert.Equal(new DateOnly(2026, 11, 12), WaitingOn.DefaultFollowUp(new DateOnly(2026, 11, 9)));
        Assert.Equal(new DateOnly(2026, 11, 16), WaitingOn.DefaultFollowUp(new DateOnly(2026, 11, 12)));
        Assert.Equal(new DateOnly(2026, 11, 16), WaitingOn.DefaultFollowUp(new DateOnly(2026, 11, 13)));
        Assert.Equal(WaitingOn.DefaultFollowUp(Today), WaitingOn.FollowUpOr(Today.AddDays(-1), Today));
        Assert.Equal(Today.AddDays(10), WaitingOn.FollowUpOr(Today.AddDays(10), Today));
    }

    [Fact]
    public void The_draft_is_short_and_polite_and_names_what_it_is_about()
    {
        var waiting = new WaitingItem(1, "Hear back from Prof. Quill about the extension", "Prof. Quill", "the extension", new DateTime(2026, 11, 6, 9, 0, 0), Today);

        var draft = WaitingOn.Draft(waiting);

        Assert.StartsWith("Hi Prof. Quill,", draft);
        Assert.Contains("follow up on my message from Friday, November 6 about the extension", draft);
        Assert.EndsWith("Thank you,", draft.TrimEnd());
        Assert.DoesNotContain("—", draft);
        Assert.Equal("Hello,", WaitingOn.Greeting("HR"));
        Assert.Equal("Hello,", WaitingOn.Greeting("my professor"));
        Assert.Equal("Hi Jake,", WaitingOn.Greeting("Jake"));
    }

    [Fact]
    public void Topic_comes_from_the_title()
    {
        Assert.Equal("the extension", WaitingOn.TopicOf("Hear back from Prof. Quill about the extension"));
        Assert.Null(WaitingOn.TopicOf("Hear back from HR"));
        Assert.Equal("Hear back from HR", WaitingOn.TitleFor("HR", null));
    }

    [Fact]
    public void A_reply_matches_by_address_or_by_a_word_of_the_name()
    {
        Assert.True(WaitingOn.IsReplyFrom(Waiting("Prof. Quill", "dquill@school.example"), "DQuill@School.example", null));
        Assert.False(WaitingOn.IsReplyFrom(Waiting("Prof. Quill", "dquill@school.example"), "quill.other@school.example", "Quill Other"));
        Assert.True(WaitingOn.IsReplyFrom(Waiting("Prof. Quill"), "dquill@school.example", "Dana Quill"));
        Assert.True(WaitingOn.IsReplyFrom(Waiting("HR"), "hr@company.com", null));
        Assert.True(WaitingOn.IsReplyFrom(Waiting("Jake"), "jake@example.com", null));
        Assert.False(WaitingOn.IsReplyFrom(Waiting("Prof. Quill"), "professor@school.example", "Prof. Vance"));
        Assert.False(WaitingOn.IsReplyFrom(Waiting("my professor"), "professor@school.example", "Professor Vance"));
        Assert.False(WaitingOn.IsReplyFrom(Waiting("Wood"), "alerts@woodgrove-bank.example.com", "Woodgrove Bank"));
    }

    [Theory]
    [InlineData("still waiting", WaitingWord.StillWaiting)]
    [InlineData("Still waiting.", WaitingWord.StillWaiting)]
    [InlineData("no reply yet", WaitingWord.StillWaiting)]
    [InlineData("draft follow-up", WaitingWord.Draft)]
    [InlineData("Draft a follow up", WaitingWord.Draft)]
    [InlineData("still waiting on Prof. Quill about the extension", null)]
    [InlineData("I'm waiting for the bus", null)]
    public void Short_replies_are_read_without_the_sorter(string text, WaitingWord? expected) =>
        Assert.Equal(expected, WaitingOn.ParseWord(text));

    // Stored waits.

    [Fact]
    public void An_op_adds_a_task_that_waits_with_one_nudge_on_its_follow_up_day()
    {
        var waiting = AddWaiting();

        var item = Assert.Single(_database.GetOpenItems());
        Assert.Equal(ItemKind.Task, item.Kind);
        Assert.Equal("Hear back from Prof. Quill about the extension", item.Title);
        Assert.Equal(item.Id, waiting.ItemId);
        Assert.Equal("Prof. Quill", waiting.Who);
        Assert.Equal("the extension", waiting.Topic);
        Assert.Equal(new DateOnly(2026, 11, 12), waiting.FollowUpOn);

        Assert.Empty(_database.GetDueReminders(new DateTime(2026, 11, 12, 9, 59, 0)));
        var nudge = Assert.Single(_database.GetDueReminders(new DateTime(2026, 11, 12, 10, 0, 0)));
        Assert.Equal(item.Id, nudge.Item.Id);
        Assert.StartsWith("Follow up with Prof. Quill?", nudge.Message);
        Assert.Contains("still waiting", nudge.Message);
    }

    [Fact]
    public void Who_is_needed_and_asked_for()
    {
        var reply = AssistantOps.Apply(new AssistantOp(AssistantOpKind.WaitingAdd, null, "Hear back"), _database, _database.AddCapture("note"), Now);

        Assert.True(reply.IsAnswer);
        Assert.Empty(_database.GetOpenItems());
    }

    [Fact]
    public void A_listed_task_can_be_marked_waiting_and_keeps_its_title()
    {
        var task = AddTask("Refund for the headphones");

        AssistantOps.Apply(new AssistantOp(AssistantOpKind.WaitingAdd, "Megamart", null, Next: ItemRef.Format(task)), _database, _database.AddCapture("note"), Now);

        var waiting = Assert.Single(_database.GetWaiting());
        Assert.Equal(task.Id, waiting.ItemId);
        Assert.Equal("Refund for the headphones", waiting.Topic);
        Assert.Single(_database.GetOpenItems());
    }

    [Fact]
    public void A_wait_stays_out_of_picks_until_its_follow_up_day()
    {
        var waiting = AddWaiting();
        var other = AddTask("Read chapter 4");
        var open = _database.GetOpenItems();

        Assert.Equal([other.Id], _database.WithoutQuietWaiting(open, Today).Select(item => item.Id));
        Assert.Contains(waiting.ItemId, _database.WithoutQuietWaiting(open, waiting.FollowUpOn).Select(item => item.Id));

        _database.StartActivation(null, null, null, Activation.TypedMethod, Now);
        Assert.Equal(ItemRef.Format(other), _database.GetActiveSession(Now)!.TaskRef);
    }

    [Fact]
    public void A_wait_can_still_be_found_by_search()
    {
        AddWaiting();

        Assert.Contains("extension", SearchIndex.Reply(_database.SearchEverything(new SearchQuery("extension", null, null, null), Now), "extension"));
    }

    [Fact]
    public void Still_waiting_moves_the_follow_up_and_keeps_one_nudge()
    {
        var waiting = AddWaiting();

        var reply = _database.StillWaiting(waiting.ItemId, null, new DateTime(2026, 11, 12, 10, 5, 0));

        Assert.Equal("Still waiting on Prof. Quill. I'll check in Monday.", reply.Text);
        Assert.Equal(new DateOnly(2026, 11, 16), _database.GetWaiting().Single().FollowUpOn);
        Assert.Empty(_database.GetDueReminders(new DateTime(2026, 11, 15, 23, 0, 0)));
        Assert.Single(_database.GetDueReminders(new DateTime(2026, 11, 16, 10, 0, 0)));
    }

    [Fact]
    public void The_still_waiting_word_answers_the_wait_that_came_due()
    {
        AddWaiting("Jake", "Hear back from Jake about his part", followUp: Today.AddDays(1));
        AddWaiting();

        var reply = _database.ApplyWaitingWord(WaitingWord.StillWaiting, new DateTime(2026, 11, 10, 11, 0, 0));

        Assert.StartsWith("Still waiting on Jake.", reply!.Text);
        Assert.Null(_database.ApplyWaitingWord(WaitingWord.StillWaiting, Now));
    }

    [Fact]
    public void The_draft_word_gives_text_to_copy_and_sends_nothing()
    {
        AddWaiting(email: "dquill@school.example");

        var reply = _database.ApplyWaitingWord(WaitingWord.Draft, new DateTime(2026, 11, 12, 10, 0, 0));

        Assert.True(reply!.IsAnswer);
        Assert.Contains("to copy into an email", reply.Text);
        Assert.Contains("Hi Prof. Quill,", reply.Text);
    }

    [Fact]
    public void Stopping_a_wait_keeps_the_task_and_drops_its_nudge()
    {
        var waiting = AddWaiting();

        Assert.True(_database.StopWaiting(waiting.ItemId));

        Assert.Empty(_database.GetWaiting());
        Assert.Single(_database.GetOpenItems());
        Assert.Empty(_database.GetDueReminders(new DateTime(2026, 11, 20)));
    }

    [Fact]
    public void Done_closes_the_wait_and_logs_it_in_history()
    {
        var waiting = AddWaiting();
        var item = _database.GetOpenItems().Single();

        _database.SetDone(item, true);

        Assert.Empty(_database.GetWaiting());
        Assert.Empty(_database.GetDueReminders(new DateTime(2026, 11, 20)));
        var entry = _database.GetHistory().First();
        Assert.Equal(HistoryAction.Done, entry.Action);
        Assert.Equal(waiting.Title, entry.Title);
    }

    [Fact]
    public void Added_by_hand_it_is_stored_the_same_way()
    {
        var added = _database.AddWaitingByHand("HR", "my start date", null, null, Now);

        Assert.Equal("Hear back from HR about my start date", added.Item!.Title);
        Assert.Equal("HR", _database.GetWaiting().Single().Who);
        Assert.Equal("Say who you're waiting on.", _database.AddWaitingByHand(" ", null, null, null, Now).Problem);
    }

    [Fact]
    public void Show_lists_each_wait_in_plain_words()
    {
        AddWaiting();

        Assert.Equal("Waiting on:\nHear back from Prof. Quill about the extension: follow up Thursday.", _database.DescribeWaiting(Today));
    }

    // Email.

    [Fact]
    public void A_reply_brings_the_wait_back_with_one_nudge_and_leaves_it_open()
    {
        var waiting = AddWaiting();
        var received = new DateTime(2026, 11, 10, 8, 30, 0);

        var answered = _database.NoticeReply("dquill@school.example", "Dana Quill", "Re: Extension request", received);

        Assert.Equal(waiting.ItemId, Assert.Single(answered).ItemId);
        var nudge = Assert.Single(_database.GetDueReminders(received));
        Assert.Equal("Prof. Quill wrote back: \"Re: Extension request\". Mark this done if that settles it.", nudge.Message);
        Assert.False(_database.GetOpenItems().Single().Done);
        Assert.True(_database.GetWaiting().Single().IsDue(Today));
        Assert.Empty(_database.NoticeReply("dquill@school.example", "Dana Quill", "Re: Re: Extension request", received.AddHours(1)));
    }

    [Fact]
    public void Mail_from_before_the_wait_started_is_not_a_reply()
    {
        AddWaiting();

        Assert.Empty(_database.NoticeReply("dquill@school.example", "Dana Quill", "Office hours", Now.AddDays(-1)));
    }

    private sealed class FakeServer(string messages) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.ToString().Contains("/token")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"a","refresh_token":"new"}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(messages) });
    }

    private sealed class MemoryTokens : ITokenStore
    {
        private string? _token = "old";

        public string? Load() => _token;

        public void Save(string token) => _token = token;
    }

    [Fact]
    public async Task Reading_email_notices_the_reply_by_its_sender()
    {
        // Since a real clock reads the mail, the wait starts well before the fake message.
        var reply = AssistantOps.Apply(new AssistantOp(AssistantOpKind.WaitingAdd, "Prof. Quill", "Hear back from Prof. Quill about the extension"), _database, _database.AddCapture("note"), new DateTime(2026, 1, 5, 9, 0, 0));
        Assert.False(reply.IsAnswer);
        const string body = """
            {"value":[{"id":"m1","subject":"Re: Extension","receivedDateTime":"2026-01-06T15:00:00Z","from":{"emailAddress":{"address":"dquill@school.example","name":"Dana Quill"}},"body":{"content":"Yes, Friday is fine."}}]}
            """;
        var mail = new OutlookMail(new HttpClient(new FakeServer(body)), _database, new OutlookSettings("id"), new MemoryTokens());
        var purchases = 0;

        await mail.PollAsync(_ => purchases++, _ => { });

        Assert.Null(mail.LastError);
        Assert.Equal(0, purchases);
        var waiting = _database.GetWaiting().Single();
        Assert.Equal("Dana Quill", waiting.ReplyFrom);
        Assert.Equal("Re: Extension", waiting.ReplySubject);
    }

    // The sorter and the capture path.

    [Fact]
    public void The_sorter_reads_waiting_ops_and_is_shown_what_is_waiting()
    {
        var result = ClaudeCliSorter.ParseResult("""
            {"is_error": false, "structured_output": {"items": [], "changes": [], "answer": null, "monthly_budget_cents": null, "budget_ops": [], "focus_ref": null,
             "ops": [{"op": "waiting_add", "name": "Prof. Quill", "text": "Hear back from Prof. Quill about the extension", "next": null, "entries": null, "amount_cents": null, "at": null, "until": null}]}}
            """);

        var op = Assert.Single(result!.Ops!);
        Assert.Equal(AssistantOpKind.WaitingAdd, op.Kind);
        Assert.Equal("Prof. Quill", op.Name);
        Assert.Contains("\"waiting_add\"", ClaudeCliSorter.Schema);
        Assert.Contains("\"waiting_still\"", ClaudeCliSorter.Schema);

        var waiting = AddWaiting();
        var prompt = ClaudeCliSorter.BuildPrompt("still waiting on Lee", Now, new AssistantContext(Now, [], [], Waiting: [waiting]));
        Assert.Contains($"Waiting on: [{{\"ref\":\"item:{waiting.ItemId}\",\"who\":\"Prof. Quill\",\"follow_up\":\"2026-11-12\",\"replied\":false}}]", prompt);
    }

    private sealed class OpSorter(params AssistantOp[] ops) : ICaptureSorter
    {
        public int Calls { get; private set; }

        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<CaptureResult?>(new CaptureResult([], Ops: ops));
        }
    }

    [Fact]
    public async Task Saying_who_was_emailed_makes_a_wait()
    {
        var service = new CaptureService(_database, new OpSorter(new AssistantOp(AssistantOpKind.WaitingAdd, "Prof. Quill", "Hear back from Prof. Quill about the extension")));
        CaptureOutcome? outcome = null;
        service.Sorted += sorted => outcome = sorted;

        service.Capture("I emailed Prof. Quill about the extension");
        await service.ProcessPendingAsync();

        Assert.Equal("Prof. Quill", _database.GetWaiting().Single().Who);
        Assert.Single(_database.GetOpenItems());
        Assert.StartsWith("Waiting on Prof. Quill", outcome!.OpReplies!.Single());
    }

    [Fact]
    public async Task Still_waiting_works_without_the_sorter_once_a_reply_is_in()
    {
        // Added by hand, so there's no capture left waiting for the sorter.
        _database.AddWaitingByHand("HR", null, null, null, new DateTime(2026, 1, 5, 9, 0, 0));
        _database.NoticeReply("hr@company.com", null, "Your start date", new DateTime(2026, 1, 6, 9, 0, 0));
        var sorter = new OpSorter();
        var service = new CaptureService(_database, sorter);
        CaptureOutcome? outcome = null;
        service.Sorted += sorted => outcome = sorted;

        service.Capture("still waiting");
        await service.ProcessPendingAsync();

        Assert.Equal(0, sorter.Calls);
        Assert.StartsWith("Still waiting on HR.", outcome!.Answer);
        Assert.Null(_database.GetWaiting().Single().RepliedAt);
    }

    [Fact]
    public void A_settled_nudge_never_takes_another_items_reminder_with_it()
    {
        var waiting = AddWaiting();
        var nudge = Assert.Single(_database.GetDueReminders(waiting.FollowUpOn.ToDateTime(new TimeOnly(23, 0))));
        _database.DismissReminder(nudge);

        // The nudge's id is free again, so the next reminder made anywhere can take it.
        var other = _database.AddCapture("Call the bank");
        var later = DateTime.Now.AddYears(1);
        _database.CompleteCapture(other, [new SortedItem(ItemKind.Task, "Call the bank", null, null, Reminders: [new PlannedReminder(later, null)])]);
        var bank = Assert.Single(_database.GetDueReminders(later));
        Assert.Equal(nudge.Id, bank.Id);

        _database.StillWaiting(waiting.ItemId, null, Now);
        _database.StopWaiting(waiting.ItemId);

        Assert.Equal("Call the bank", Assert.Single(_database.GetDueReminders(later)).Item.Title);
    }
}
