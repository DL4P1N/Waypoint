using System.Net;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Tests;

public sealed class LifeFeaturesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public LifeFeaturesTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private void Store(params SortedItem[] items) => _database.CompleteCapture(_database.AddCapture("x"), items);

    private sealed class FakeSorter(CaptureResult result) : ICaptureSorter
    {
        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<CaptureResult?>(result);
    }

    private sealed class FakeDiscord(string reply, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<(string Url, string Body, string? Auth)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString()));
            return new HttpResponseMessage(status) { Content = new StringContent(reply) };
        }
    }

    private DiscordBridge Bridge(FakeDiscord fake) =>
        new(new HttpClient(fake), _database, new DiscordSettings("token", "owner", "100", "200", "300"));

    [Theory]
    [InlineData(RecurrenceUnit.Day, 1, null, "2026-11-01", "2026-11-02")]
    [InlineData(RecurrenceUnit.Week, 2, null, "2026-11-01", "2026-11-15")]
    [InlineData(RecurrenceUnit.Month, 1, 31, "2027-01-31", "2027-02-28")]
    [InlineData(RecurrenceUnit.Month, 1, 31, "2027-02-28", "2027-03-31")]
    public void Recurrence_steps_to_the_next_date(RecurrenceUnit unit, int interval, int? day, string from, string expected)
    {
        var start = DateOnly.Parse(from);

        Assert.Equal(DateOnly.Parse(expected), new Recurrence(unit, interval, day).Next(start, start));
    }

    [Fact]
    public void Missed_occurrences_do_not_pile_up()
    {
        var next = new Recurrence(RecurrenceUnit.Day).Next(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 20));

        Assert.Equal(new DateOnly(2026, 11, 21), next);
    }

    [Fact]
    public void Recurrence_survives_storage_text()
    {
        var recurrence = new Recurrence(RecurrenceUnit.Month, 2, 15);

        Assert.Equal(recurrence, Recurrence.Parse(recurrence.Format()));
        Assert.Equal("Every 2 months", recurrence.Label);
    }

    [Fact]
    public void Finishing_a_recurring_item_brings_it_back_with_a_new_reminder()
    {
        Store(new SortedItem(ItemKind.Task, "water the plants", null, null, new TimeOnly(9, 0), Recurs: new Recurrence(RecurrenceUnit.Day)));
        var plants = _database.GetOpenItems().Single();
        Assert.Equal(ItemKind.Reminder, plants.Kind);

        _database.SetDone(plants, true);

        var next = _database.GetOpenItems().Single();
        Assert.Equal(Today.AddDays(1), next.DueDate);
        Assert.Equal(new Recurrence(RecurrenceUnit.Day), next.Recurs);
        Assert.Empty(_database.GetDueReminders(DateTime.Now));
        Assert.Single(_database.GetDueReminders(Today.AddDays(1).ToDateTime(new TimeOnly(9, 0))));
    }

    [Fact]
    public async Task Canceling_a_recurring_item_ends_it()
    {
        Store(new SortedItem(ItemKind.Reminder, "water the plants", Today, null, Recurs: new Recurrence(RecurrenceUnit.Day)));
        var plants = _database.GetOpenItems().Single();
        var service = new CaptureService(_database, new FakeSorter(new CaptureResult([], [new ItemChange(ItemSource.Capture, plants.Id, ChangeAction.Cancel)])));

        service.Capture("stop the watering reminder");
        await service.ProcessPendingAsync();

        Assert.Empty(_database.GetOpenItems());
    }

    [Fact]
    public async Task Checking_off_by_typing_also_brings_it_back()
    {
        Store(new SortedItem(ItemKind.Reminder, "water the plants", Today, null, Recurs: new Recurrence(RecurrenceUnit.Day)));
        var plants = _database.GetOpenItems().Single();
        var service = new CaptureService(_database, new FakeSorter(new CaptureResult([], [new ItemChange(ItemSource.Capture, plants.Id, ChangeAction.Done)])));

        service.Capture("watered the plants");
        await service.ProcessPendingAsync();

        Assert.Equal(Today.AddDays(1), _database.GetOpenItems().Single().DueDate);
    }

    [Fact]
    public void Safe_to_spend_spreads_what_is_left_over_the_month()
    {
        // 30 days in November, so the 21st has 10 days left, counting today.
        var day = new DateOnly(2026, 11, 21);

        Assert.Equal(1000, Briefing.SafeToSpendToday(40000, 30000, 0, day));
        Assert.Equal(400, Briefing.SafeToSpendToday(40000, 30000, 600, day));
        Assert.Equal(0, Briefing.SafeToSpendToday(40000, 30000, 5000, day));
        Assert.Equal(0, Briefing.SafeToSpendToday(40000, 50000, 0, day));
    }

    [Fact]
    public void Zero_safe_to_spend_says_why()
    {
        // Nov 9 with $215.00 spent before it out of $700: $22.04 a day for the 22 days left, and a $84.20 charge today.
        var day = new DateOnly(2026, 11, 9);

        Assert.Equal(0, Briefing.SafeToSpendToday(70000, 21500, 8420, day));
        Assert.Equal("$84.20 spent today, more than today's $22.04 share of the month.", Briefing.SafeToSpendReason(70000, 21500, 8420, day));
        Assert.Equal("This month's $700.00 budget is used up.", Briefing.SafeToSpendReason(70000, 75000, 0, day));
        Assert.Null(Briefing.SafeToSpendReason(70000, 21500, 0, day));
    }

    [Fact]
    public async Task Typing_a_budget_turns_on_safe_to_spend_and_canceled_expenses_do_not_count()
    {
        Assert.Null(_database.GetSafeToSpend(Today));

        var service = new CaptureService(_database, new FakeSorter(new CaptureResult([], MonthlyBudgetCents: 3000000)));
        CaptureOutcome? outcome = null;
        service.Sorted += result => outcome = result;
        service.Capture("my budget is 30000 a month");
        await service.ProcessPendingAsync();

        Assert.Equal("Monthly budget set to $30,000.00", ItemGroups.OutcomeSummary(outcome!));
        Assert.Empty(_database.GetOpenItems());
        var full = _database.GetSafeToSpend(Today);

        Store(new SortedItem(ItemKind.Expense, "lunch", null, 1200), new SortedItem(ItemKind.Expense, "mistake", null, 500000));
        var mistake = _database.GetOpenItems().Single(item => item.Title == "mistake");
        var lunch = _database.GetOpenItems().Single(item => item.Title == "lunch");
        var cancel = new CaptureService(_database, new FakeSorter(new CaptureResult([], [new ItemChange(ItemSource.Capture, mistake.Id, ChangeAction.Cancel)])));
        cancel.Capture("cancel that");
        await cancel.ProcessPendingAsync();

        Assert.Equal(full - 1200, _database.GetSafeToSpend(Today));

        // Checking an expense off tidies the list but it was still spent.
        _database.SetDone(lunch, true);
        Assert.Equal(full - 1200, _database.GetSafeToSpend(Today));
    }

    [Fact]
    public void Briefing_lists_the_day_and_leaves_out_steps()
    {
        var now = new DateTime(2026, 11, 3, 7, 30, 0);
        var day = DateOnly.FromDateTime(now);
        var schedule = new ClassSchedule(
            new DateOnly(2026, 9, 14), new DateOnly(2026, 12, 18),
            [
                new ClassMeeting("CS 101", "A", [DayOfWeek.Tuesday], new TimeOnly(10, 50), new TimeOnly(12, 10)),
                new ClassMeeting("BIO", "B", [DayOfWeek.Tuesday], new TimeOnly(9, 0), new TimeOnly(10, 20)),
            ],
            []);
        StoredItem Due(string title, int days, string? parent = null) => new(1, ItemKind.Deadline, title, day.AddDays(days), null, false, ParentRef: parent);

        var text = Briefing.Build(now, [Due("Quiz", 0), Due("Step", 0, "item:1"), Due("Paper", 1)], schedule, 1250);

        Assert.Equal("Class: BIO at 9:00 AM, CS 101 at 10:50 AM\nDue today: Quiz\nDue tomorrow: Paper\nSafe to spend today: $12.50", text);
        Assert.Equal("Nothing on the schedule today.", Briefing.Build(now, [], null, null));
    }

    [Fact]
    public void Weekly_reset_covers_the_week_ahead()
    {
        var sunday = new DateTime(2026, 11, 1, 18, 0, 0);
        var day = DateOnly.FromDateTime(sunday);
        StoredItem Due(string title, int days, ItemKind kind = ItemKind.Deadline, string? parent = null) =>
            new(1, kind, title, day.AddDays(days), null, false, ParentRef: parent);

        var text = Briefing.BuildWeek(sunday,
        [
            Due("Sunday thing", 0),
            Due("Exam 2", 1, ItemKind.Exam),
            Due("Quiz", 4),
            Due("Paper", 4),
            Due("A step", 4, ItemKind.Task, "item:1"),
            Due("Too far", 8),
            Due("Groceries", 2, ItemKind.Shopping),
        ]);

        Assert.Equal("Exams: Exam 2 (Mon)\nDeadlines: 2\nBusiest day: Thursday (2 due)\nGet ahead on: Quiz", text);
    }

    [Fact]
    public void Weekly_reset_says_so_when_nothing_is_due()
    {
        Assert.Equal("Nothing due in the week ahead.", Briefing.BuildWeek(new DateTime(2026, 11, 1, 18, 0, 0), []));
    }

    [Fact]
    public void Briefing_keeps_scheduled_things_apart_from_what_is_due()
    {
        var now = new DateTime(2026, 11, 3, 7, 30, 0);
        var day = DateOnly.FromDateTime(now);
        StoredItem Item(string title, ItemKind kind, int days, ItemSource source = ItemSource.Capture, TimeOnly? time = null) =>
            new(1, kind, title, day.AddDays(days), null, false, source, DueAt: time is { } clock ? new DateTimeOffset(day.AddDays(days).ToDateTime(clock)) : null);

        var text = Briefing.Build(now,
        [
            Item("Zoom office hours", ItemKind.Reminder, 0, time: new TimeOnly(19, 0)),
            Item("Paper", ItemKind.Deadline, 0, ItemSource.Calendar),
            Item("Review session", ItemKind.Deadline, 1, ItemSource.Calendar, new TimeOnly(17, 0)),
            Item("Sign up for review session", ItemKind.Deadline, 1, ItemSource.Calendar),
        ], null, null);

        Assert.Equal("Scheduled today: Zoom office hours at 7:00 PM\nDue today: Paper\nScheduled tomorrow: Review session at 5:00 PM\nDue tomorrow: Sign up for review session", text);
    }

    [Fact]
    public async Task Discord_first_poll_only_marks_the_place()
    {
        var fake = new FakeDiscord("""[{"id":"5","content":"old message","author":{"id":"owner"}}]""");
        var received = new List<string>();

        await Bridge(fake).PollAsync(message => received.Add(message.Text));

        Assert.Empty(received);
        Assert.Equal("5", _database.GetPreference("discord_last_message"));
        Assert.Equal("Bot token", fake.Requests.Single().Auth);
    }

    [Fact]
    public async Task Discord_takes_only_the_owners_messages_in_order_and_moves_on()
    {
        _database.SetPreference("discord_last_message", "5");
        var fake = new FakeDiscord("""
            [{"id":"9","content":"second","author":{"id":"owner"}},
             {"id":"8","content":"from someone else","author":{"id":"stranger"}},
             {"id":"7","content":"a bot","author":{"id":"owner","bot":true}},
             {"id":"6","content":"first","author":{"id":"owner"}}]
            """);
        var received = new List<string>();

        await Bridge(fake).PollAsync(message => received.Add(message.Text));

        Assert.Equal(["first", "second"], received);
        Assert.Equal("9", _database.GetPreference("discord_last_message"));
        Assert.Contains("after=5", fake.Requests.Single().Url);
    }

    [Fact]
    public async Task Discord_empty_channel_still_catches_its_first_message()
    {
        await Bridge(new FakeDiscord("[]")).PollAsync(_ => { });
        var received = new List<string>();

        await Bridge(new FakeDiscord("""[{"id":"1","content":"first","author":{"id":"owner"}}]""")).PollAsync(message => received.Add(message.Text));

        Assert.Equal(["first"], received);
    }

    [Fact]
    public async Task Discord_passes_when_a_message_was_sent()
    {
        _database.SetPreference("discord_last_message", "5");
        var fake = new FakeDiscord("""[{"id":"6","content":"x","timestamp":"2026-10-30T15:00:00+00:00","author":{"id":"owner"}}]""");
        DateTimeOffset? sent = null;

        await Bridge(fake).PollAsync(message => sent = message.SentAt);

        Assert.Equal(new DateTimeOffset(2026, 10, 30, 15, 0, 0, TimeSpan.Zero), sent);
    }

    [Fact]
    public async Task Discord_skips_a_bad_message_and_reports_a_bad_reply()
    {
        _database.SetPreference("discord_last_message", "5");
        var bad = new FakeDiscord("""[{"id":"6"},{"id":"7","content":"good","author":{"id":"owner"}}]""");
        var received = new List<string>();

        await Bridge(bad).PollAsync(message => received.Add(message.Text));

        Assert.Equal(["good"], received);

        var notList = Bridge(new FakeDiscord("{}"));
        await notList.PollAsync(_ => { });

        Assert.NotNull(notList.LastError);
    }

    [Fact]
    public async Task A_slow_poll_keeps_the_next_from_repeating_it()
    {
        _database.SetPreference("discord_last_message", "5");
        var gate = new TaskCompletionSource();
        var slow = new SlowDiscord(gate.Task, """[{"id":"6","content":"once","author":{"id":"owner"}}]""");
        var bridge = new DiscordBridge(new HttpClient(slow), _database, new DiscordSettings("token", "owner", "100"));
        var received = new List<string>();

        var first = bridge.PollAsync(message => received.Add(message.Text));
        await bridge.PollAsync(message => received.Add(message.Text));
        gate.SetResult();
        await first;

        Assert.Equal(["once"], received);
    }

    private sealed class SlowDiscord(Task gate, string reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await gate;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply) };
        }
    }

    [Fact]
    public void Finishing_yesterdays_late_leaves_todays_if_it_is_still_ahead()
    {
        Store(new SortedItem(ItemKind.Reminder, "water the plants", Today.AddDays(-1), null, new TimeOnly(23, 59), Recurs: new Recurrence(RecurrenceUnit.Day)));

        _database.SetDone(_database.GetOpenItems().Single(), true);

        Assert.Equal(Today, _database.GetOpenItems().Single().DueDate);
    }

    [Fact]
    public async Task A_capture_remembers_where_it_was_typed()
    {
        var service = new CaptureService(_database, new FakeSorter(new CaptureResult([], Answer: "Start small.")));
        var origins = new List<CaptureOrigin>();
        service.Sorted += outcome => origins.Add(outcome.Origin);

        service.Capture("from the app");
        service.Capture("from my phone", null, CaptureOrigin.Discord);
        await service.ProcessPendingAsync();

        Assert.Equal([CaptureOrigin.App, CaptureOrigin.Discord], origins);
    }

    [Fact]
    public void A_capture_can_keep_when_it_was_written()
    {
        var written = new DateTimeOffset(2026, 10, 30, 15, 0, 0, TimeSpan.Zero);

        new CaptureService(_database, null).Capture("hello", written);

        Assert.Equal(written, _database.GetPendingCaptures().Single().CreatedAt);
    }

    private (long Id, string Title) StoreReminder(string title, int? repeat = null)
    {
        Store(new SortedItem(ItemKind.Reminder, title, Today, null, RepeatMinutes: repeat));
        var item = _database.GetOpenItems().Single(stored => stored.Title == title);
        return (item.Id, item.Title);
    }

    [Fact]
    public void A_reaction_finishes_the_item_once()
    {
        var call = StoreReminder("call");
        _database.TrackSentReminder("700", call.Id);

        Assert.Equal("Done: call", _database.ApplyAlertAction(new AlertAction("700", AlertActionKind.Done), DateTime.Now, 15));
        Assert.Null(_database.ApplyAlertAction(new AlertAction("700", AlertActionKind.Done), DateTime.Now, 15));
        Assert.Empty(_database.GetOpenItems());
    }

    [Fact]
    public void A_bare_done_means_the_latest_reminder()
    {
        var first = StoreReminder("first");
        var second = StoreReminder("second");
        _database.TrackSentReminder("701", first.Id);
        _database.TrackSentReminder("702", second.Id);

        Assert.Equal("Done: second", _database.ApplyAlertAction(new AlertAction(null, AlertActionKind.Done), DateTime.Now, 15));
        Assert.Equal("first", _database.GetOpenItems().Single().Title);
    }

    [Fact]
    public void Snooze_brings_a_reminder_back_and_unknown_messages_do_nothing()
    {
        var call = StoreReminder("call");
        var now = DateTime.Now;
        foreach (var due in _database.GetDueReminders(now.AddDays(1)))
        {
            _database.DismissReminder(due);
        }

        _database.TrackSentReminder("703", call.Id);

        Assert.Null(_database.ApplyAlertAction(new AlertAction("999", AlertActionKind.Done), now, 15));
        Assert.Equal("Back in 15 minutes: call", _database.ApplyAlertAction(new AlertAction("703", AlertActionKind.Snooze), now, 15));
        Assert.Single(_database.GetDueReminders(now.AddMinutes(16)));
    }

    [Fact]
    public void A_button_and_a_reaction_at_the_same_moment_only_apply_once()
    {
        var call = StoreReminder("call");
        var now = DateTime.Now;
        foreach (var due in _database.GetDueReminders(now.AddDays(1)))
        {
            _database.DismissReminder(due);
        }

        // Many tries at once, so a check that isn't also the claim gets caught between them.
        for (var round = 0; round < 10; round++)
        {
            var message = $"race-{round}";
            _database.TrackSentReminder(message, call.Id);
            var more = $"more-{round}";
            _database.TrackSentReminder(more, call.Id);
            using var start = new Barrier(8);
            var replies = Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            {
                start.SignalAndWait();
                return index % 2 == 0
                    ? _database.ApplyAlertAction(new AlertAction(message, AlertActionKind.Snooze), now, 15)
                    : _database.ApplyTransitionAction(more, AlertActionKind.MoreTime, now);
            })).ToArray();
            Task.WaitAll(replies);

            Assert.Single(replies, reply => reply.Result?.StartsWith("Back in 15") == true);
            Assert.Single(replies, reply => reply.Result?.StartsWith("Back once") == true);
        }
    }

    [Fact]
    public async Task Discord_reads_reactions_and_the_owners_replies()
    {
        _database.SetPreference("discord_last_alert", "5");
        var fake = new FakeDiscord("""
            [{"id":"8","content":"done","author":{"id":"stranger"}},
             {"id":"7","content":"Snooze","author":{"id":"owner"},"message_reference":{"message_id":"3"}},
             {"id":"6","content":"Done!","author":{"id":"owner"}},
             {"id":"4","content":"done","author":{"id":"owner"}},
             {"id":"2","content":"call","author":{"id":"bot"},"reactions":[{"emoji":{"name":"\u2705"}},{"emoji":{"name":"\uD83D\uDE00"}}]}]
            """);

        var actions = await Bridge(fake).ReadAlertActionsAsync();

        Assert.Equal(
            [
                new AlertAction("2", AlertActionKind.Done),
                new AlertAction(null, AlertActionKind.Done),
                new AlertAction("3", AlertActionKind.Snooze),
            ],
            actions);
        Assert.Equal("8", _database.GetPreference("discord_last_alert"));
    }

    [Fact]
    public void Discord_cards_turn_labels_into_fields()
    {
        var card = DiscordText.Notice("Today", "Class: BIO at 9:00 AM\nDue today: A very long list of assignments that goes on for a while\nAlmost out of paper", CardTone.Attention);

        Assert.Equal("Today", card.Title);
        Assert.Equal("Almost out of paper", card.Description);
        Assert.Equal([new CardField("Class", "BIO at 9:00 AM", true), new CardField("Due today", "A very long list of assignments that goes on for a while", false)], card.Fields);
        Assert.Equal(CardTone.Attention, card.Tone);
        Assert.Equal(@"$12.00 at Streamco\*Online", DiscordText.Notice("$12.00 at Streamco*Online", "").Title);
    }

    [Fact]
    public void Discord_reminders_show_what_when_and_buttons_only_if_finishable()
    {
        var exam = new StoredItem(1, ItemKind.Exam, "CS 210 Exam 2", Today.AddDays(1), null, false, DueAt: new DateTimeOffset(Today.AddDays(1).ToDateTime(new TimeOnly(13, 0))));
        var card = DiscordText.Reminder(new DueReminder(1, exam, "Do a practice exam", null), Today, false);

        Assert.Equal("Do a practice exam", card.Title);
        Assert.Equal([new CardField("For", "CS 210 Exam 2", true), new CardField("When", "Tomorrow 1:00 PM", true)], card.Fields);
        Assert.False(card.HasButtons);

        var call = new StoredItem(2, ItemKind.Reminder, "Call mom", null, null, false);
        Assert.True(DiscordText.Reminder(new DueReminder(2, call, null, null), Today, true).HasButtons);
    }

    [Fact]
    public async Task Sent_cards_are_embeds_with_buttons()
    {
        var fake = new FakeDiscord("{}");

        await Bridge(fake).SendAlertAsync(new DiscordCard("Call mom", null, [], CardTone.Reminder, true));

        Assert.Contains("\"embeds\"", fake.Requests[0].Body);
        Assert.Contains("\"waypoint:done\"", fake.Requests[0].Body);
        Assert.Contains("\"waypoint:snooze\"", fake.Requests[0].Body);
    }

    /// <summary>The buttons' labels and whether each is green, in the order they were sent.</summary>
    private static List<(string Label, bool Green)> Buttons(string body)
    {
        using var document = System.Text.Json.JsonDocument.Parse(body);
        return document.RootElement.GetProperty("components").EnumerateArray()
            .SelectMany(row => row.GetProperty("components").EnumerateArray())
            .Where(component => component.GetProperty("type").GetInt32() == 2)
            .Select(button => (button.GetProperty("label").GetString()!, button.GetProperty("style").GetInt32() == 3))
            .ToList();
    }

    [Fact]
    public async Task Every_card_puts_the_main_action_first_then_not_now_then_skip()
    {
        var fake = new FakeDiscord("{}");
        var bridge = Bridge(fake);

        await bridge.SendAlertAsync(new DiscordCard("Call mom", null, [], CardTone.Reminder, HasButtons: true));
        await bridge.SendAlertAsync(new DiscordCard("Paper", null, [], CardTone.Reminder, HasButtons: true, HasStart: true, Transition: true, MoreTime: true));
        await bridge.SendAlertAsync(new DiscordCard("Open the course site", null, [], CardTone.Reminder, HasSessionButtons: true, CanTrial: true));
        await bridge.SendAlertAsync(new DiscordCard("Bed", null, [], CardTone.Reminder, BedtimeButton: AlertActionKind.SnoozeBedtime));
        await bridge.SendAlertAsync(new DiscordCard("Wait's over", null, [], CardTone.Money) { Actions = [new CardAction("Skip", "x", ButtonRole.Skip), new CardAction("Bought", "y")] });

        Assert.Equal([("Done", true), ("Not now", false)], Buttons(fake.Requests[0].Body));
        Assert.Equal([("Start", true), ("Done", false), ("10 more minutes", false), ("Skip", false)], Buttons(fake.Requests[1].Body));
        Assert.Equal([("Did it", true), ("Make it smaller", false), ("Try 2 minutes", false), ("Pause", false)], Buttons(fake.Requests[2].Body));
        Assert.Equal([("Not now", false)], Buttons(fake.Requests[3].Body));
        Assert.Equal([("Bought", true), ("Skip", false)], Buttons(fake.Requests[4].Body));
    }

    [Fact]
    public async Task Session_cards_carry_their_buttons_and_only_offer_the_trial_once_begun()
    {
        var fake = new FakeDiscord("{}");

        await Bridge(fake).SendAlertAsync(new DiscordCard("Open the course site", null, [], CardTone.Reminder, HasSessionButtons: true));
        await Bridge(fake).SendAlertAsync(new DiscordCard("Next", null, [], CardTone.Reminder, HasStart: true, HasSessionButtons: true, CanTrial: true));

        Assert.Contains("\"waypoint:did-it\"", fake.Requests[0].Body);
        Assert.Contains("\"waypoint:smaller\"", fake.Requests[0].Body);
        Assert.Contains("\"waypoint:not-now\"", fake.Requests[0].Body);
        Assert.DoesNotContain("\"waypoint:trial\"", fake.Requests[0].Body);
        Assert.Contains("\"waypoint:trial\"", fake.Requests[1].Body);
        Assert.Contains("\"waypoint:start\"", fake.Requests[1].Body);
    }

    [Theory]
    [InlineData("waypoint:start", AlertActionKind.Start)]
    [InlineData("waypoint:did-it", AlertActionKind.DidIt)]
    [InlineData("waypoint:smaller", AlertActionKind.Smaller)]
    [InlineData("waypoint:not-now", AlertActionKind.NotNow)]
    [InlineData("waypoint:trial", AlertActionKind.Trial)]
    public async Task Session_buttons_reach_the_app(string button, AlertActionKind expected)
    {
        AlertActionKind? applied = null;

        await Bridge(new FakeDiscord("{}")).HandleInteractionAsync(Press(button, "owner"), (_, kind) => { applied = kind; return "Got it"; });

        Assert.Equal(expected, applied);
    }

    private static System.Text.Json.JsonElement Press(string button, string user) =>
        System.Text.Json.JsonDocument.Parse($$$"""
            {"id":"9","token":"tok","data":{"custom_id":"{{{button}}}"},"member":{"user":{"id":"{{{user}}}"}},
             "message":{"id":"700","embeds":[{"title":"Call mom"}]}}
            """).RootElement;

    [Fact]
    public async Task Pressing_done_applies_it_and_grays_the_card()
    {
        var fake = new FakeDiscord("{}");
        (string, AlertActionKind)? applied = null;

        await Bridge(fake).HandleInteractionAsync(Press("waypoint:done", "owner"), (id, kind) => { applied = (id, kind); return "Done: Call mom"; });

        Assert.Equal(("700", AlertActionKind.Done), applied);
        var call = fake.Requests.Single();
        Assert.Contains("/interactions/9/tok/callback", call.Url);
        Assert.Contains("\"components\":[]", call.Body);
        // The card keeps its title and says what was done under it, without saying the title twice.
        Assert.Contains("\"title\":\"Call mom\"", call.Body);
        Assert.Contains("\"footer\":{\"text\":\"Done\"}", call.Body);
        Assert.DoesNotContain("Done: Call mom", call.Body);
        Assert.Null(call.Auth);
    }

    [Fact]
    public async Task Only_the_owner_can_press_a_button()
    {
        var fake = new FakeDiscord("{}");
        var applied = false;

        await Bridge(fake).HandleInteractionAsync(Press("waypoint:done", "stranger"), (_, _) => { applied = true; return "x"; });

        Assert.False(applied);
        Assert.Contains("Only the owner", fake.Requests.Single().Body);
    }

    [Fact]
    public async Task Sending_returns_the_message_id()
    {
        Assert.Equal("44", await Bridge(new FakeDiscord("""{"id":"44"}""")).SendAlertAsync(DiscordText.Notice("hi", "")));
    }

    [Fact]
    public async Task Money_notes_only_go_with_a_what_to_do_question()
    {
        _database.SaveBudgetAccount(new BudgetAccount("Contoso Card", AccountKind.Card, 100000, Today, 250000, 9, 3));
        var seen = new List<AssistantContext>();
        var service = new CaptureService(_database, new RecordingSorter(seen));

        service.Capture("buy milk");
        service.Capture("what do I do now?");
        await service.ProcessPendingAsync();

        Assert.Null(seen[0].MoneyNotes);
        Assert.Contains(seen[1].MoneyNotes!, note => note.StartsWith("Pay $250.00 to get down to 30%"));
        Assert.Contains("Money:", ClaudeCliSorter.BuildPrompt("what do I do now?", DateTime.Now, seen[1]));
    }

    private sealed class RecordingSorter(List<AssistantContext> seen) : ICaptureSorter
    {
        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default)
        {
            seen.Add(context);
            return Task.FromResult<CaptureResult?>(new CaptureResult([], null, "ok"));
        }
    }

    [Fact]
    public async Task Discord_sends_to_the_right_channel_without_pings()
    {
        var fake = new FakeDiscord("{}");
        var bridge = Bridge(fake);

        await bridge.SendAlertAsync(DiscordText.Notice("Leave for class", ""));
        await bridge.SendBudgetAsync(DiscordText.Notice("$10", ""));

        Assert.Contains("/channels/200/messages", fake.Requests[0].Url);
        Assert.Contains("/channels/300/messages", fake.Requests[1].Url);
        Assert.Contains("\"parse\":[]", fake.Requests[0].Body);
    }

    [Fact]
    public async Task Discord_failure_is_reported_and_keeps_the_place()
    {
        _database.SetPreference("discord_last_message", "5");
        var bridge = Bridge(new FakeDiscord("{}", HttpStatusCode.Unauthorized));

        await bridge.PollAsync(_ => throw new InvalidOperationException("should not run"));

        Assert.NotNull(bridge.LastError);
        Assert.Equal("5", _database.GetPreference("discord_last_message"));
    }

    [Fact]
    public void Settings_read_discord_and_briefing_time()
    {
        var settings = WaypointSettings.Parse("""{ "briefingTime": "08:15", "discord": { "botToken": "t", "ownerUserId": "o", "alertsChannelId": "1" } }""");

        Assert.Equal(new TimeOnly(8, 15), settings.BriefingTime);
        Assert.Equal("1", settings.Discord!.AlertsChannelId);
        Assert.Null(settings.Discord.CaptureChannelId);
    }

    [Fact]
    public void Parser_reads_recurrence_and_budget()
    {
        var result = ClaudeCliSorter.ParseResult("""
            {"is_error": false, "structured_output": {"changes": [], "answer": null, "monthly_budget_cents": 40000, "items": [
                {"kind": "reminder", "title": "rent", "due_date": "2026-12-01", "amount_cents": null, "recurrence": {"unit": "month", "interval": 1}}
            ]}}
            """);

        Assert.Equal(40000, result!.MonthlyBudgetCents);
        Assert.Equal(new Recurrence(RecurrenceUnit.Month), result.Items.Single().Recurs);
    }
}
