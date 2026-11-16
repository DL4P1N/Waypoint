using System.Net;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Tests;

public sealed class TransitionAssistTests : IDisposable
{
    // Tomorrow afternoon by the real clock, since reminders already past when an item is saved are dropped then.
    private static readonly DateTime Now = DateTime.Today.AddDays(1).AddHours(15);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public TransitionAssistTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private sealed class FakeDiscord : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private StoredItem AddWithReminder(string title, ItemKind kind = ItemKind.Task, int? repeat = null)
    {
        _database.CompleteCapture(_database.AddCapture(title), [new SortedItem(kind, title, Today.AddDays(1), null, Reminders: [new PlannedReminder(Now.AddMinutes(-1), null)], RepeatMinutes: repeat)]);
        return _database.GetOpenItems().First(item => item.Title == title);
    }

    private DueReminder Due(string title, DateTime at) => _database.GetDueReminders(at).Single(due => due.Item.Title == title);

    [Fact]
    public void Startable_reminders_get_transition_buttons_and_repeating_ones_keep_their_own()
    {
        AddWithReminder("Write the essay");
        AddWithReminder("Drink water", ItemKind.Reminder, repeat: 30);
        AddWithReminder("Milk", ItemKind.Shopping);

        Assert.True(TransitionAssist.Applies(Due("Write the essay", Now)));
        Assert.True(TransitionAssist.OffersMoreTime(Due("Write the essay", Now)));
        Assert.False(TransitionAssist.Applies(Due("Drink water", Now)));
        Assert.False(TransitionAssist.Applies(Due("Milk", Now)));
    }

    [Fact]
    public void More_time_brings_it_back_once_after_ten_minutes_and_the_follow_up_offers_no_more()
    {
        var item = AddWithReminder("Write the essay");

        _database.FollowUpReminder(Due("Write the essay", Now), Now);

        Assert.DoesNotContain(_database.GetDueReminders(Now.AddMinutes(9)), due => due.Item.Id == item.Id);
        var followUp = Due("Write the essay", Now.AddMinutes(10));
        Assert.True(followUp.IsFollowUp);
        Assert.True(TransitionAssist.Applies(followUp));
        Assert.False(TransitionAssist.OffersMoreTime(followUp));
    }

    [Fact]
    public void Not_now_clears_the_reminder_and_leaves_the_item_open()
    {
        var item = AddWithReminder("Write the essay");

        _database.DismissReminder(Due("Write the essay", Now));

        Assert.Empty(_database.GetDueReminders(Now.AddDays(1)));
        Assert.Contains(_database.GetOpenItems(), open => open.Id == item.Id && !open.Done);
    }

    [Fact]
    public void Discord_more_time_adds_one_follow_up_and_not_now_adds_nothing()
    {
        var essay = AddWithReminder("Write the essay");
        var exam = AddWithReminder("Calc exam", ItemKind.Exam);
        foreach (var sent in _database.GetDueReminders(Now))
        {
            _database.SettleSentReminder(sent, Now);
        }

        _database.TrackSentReminder("500", essay.Id);
        _database.TrackSentReminder("501", exam.Id, startOnly: true);

        Assert.Equal("Back once in 10 minutes: Write the essay", _database.ApplyTransitionAction("500", AlertActionKind.MoreTime, Now));
        Assert.Equal("Okay. Calc exam is still on your list.", _database.ApplyTransitionAction("501", AlertActionKind.NotNowReminder, Now));
        Assert.Null(_database.ApplyTransitionAction("500", AlertActionKind.MoreTime, Now));

        var due = _database.GetDueReminders(Now.AddMinutes(10)).Where(reminder => reminder.IsFollowUp).ToList();
        Assert.Equal(essay.Id, Assert.Single(due).Item.Id);
        Assert.DoesNotContain(_database.GetDueReminders(Now.AddMinutes(10)), reminder => reminder.Item.Id == exam.Id);
        Assert.Contains(_database.GetOpenItems(), open => open.Id == exam.Id);
    }

    [Fact]
    public void A_snooze_reaction_or_command_comes_back_once_as_the_follow_up()
    {
        var essay = AddWithReminder("Write the essay");
        foreach (var sent in _database.GetDueReminders(Now))
        {
            _database.SettleSentReminder(sent, Now);
        }

        _database.TrackSentReminder("600", essay.Id);

        Assert.NotNull(_database.ApplyAlertAction(new AlertAction("600", AlertActionKind.Snooze), Now, 10));

        var followUp = Due("Write the essay", Now.AddMinutes(10));
        Assert.True(followUp.IsFollowUp);
        Assert.False(TransitionAssist.OffersMoreTime(followUp));
    }

    [Fact]
    public void Each_button_word_means_one_thing_on_every_card()
    {
        // The ten minutes say what they do, and Pause is the session card's stop that brings nothing back.
        Assert.Equal($"{TransitionAssist.FollowUpMinutes} more minutes", CardLabels.MoreTime);
        Assert.Equal("Pause", CardLabels.Pause);
        Assert.Equal("Not now", CardLabels.NotNow);
        Assert.Equal("Skip", CardLabels.Skip);
    }

    [Fact]
    public void Discord_cards_trade_snooze_for_more_time_and_not_now()
    {
        var item = new StoredItem(1, ItemKind.Task, "Write the essay", Today, null, false);
        var first = DiscordText.Reminder(new DueReminder(1, item, null, null), Today, true, canStart: true);
        var followUp = DiscordText.Reminder(new DueReminder(1, item, null, null, IsFollowUp: true), Today, true, canStart: true);
        var repeating = DiscordText.Reminder(new DueReminder(1, item, null, 30), Today, true, canStart: true);

        Assert.True(first.Transition && first.MoreTime);
        Assert.True(followUp.Transition);
        Assert.False(followUp.MoreTime);
        Assert.False(repeating.Transition);
    }

    [Fact]
    public async Task Transition_cards_send_start_more_time_and_not_now_but_no_snooze()
    {
        var fake = new FakeDiscord();
        var bridge = new DiscordBridge(new HttpClient(fake), _database, new DiscordSettings("token", "owner", "100", "200", "300"));

        await bridge.SendAlertAsync(new DiscordCard("Write the essay", null, [], CardTone.Reminder, HasButtons: true, HasStart: true, Transition: true, MoreTime: true));
        await bridge.SendAlertAsync(new DiscordCard("Write the essay", null, [], CardTone.Reminder, HasButtons: true, HasStart: true, Transition: true));

        Assert.Contains("\"waypoint:start\"", fake.Bodies[0]);
        Assert.Contains("\"waypoint:more-time\"", fake.Bodies[0]);
        Assert.Contains("\"waypoint:not-now-reminder\"", fake.Bodies[0]);
        Assert.DoesNotContain("\"waypoint:snooze\"", fake.Bodies[0]);
        Assert.DoesNotContain("\"waypoint:more-time\"", fake.Bodies[1]);
        Assert.Contains("\"waypoint:not-now-reminder\"", fake.Bodies[1]);
    }

    [Fact]
    public void Save_my_spot_is_offered_only_when_none_was_saved_in_the_last_hour()
    {
        Assert.True(TransitionAssist.OffersSaveSpot(null, Now));
        Assert.False(TransitionAssist.OffersSaveSpot(Now.AddMinutes(-20), Now));
        Assert.True(TransitionAssist.OffersSaveSpot(Now.AddHours(-2), Now));
    }

    [Fact]
    public void Starting_a_task_with_a_saved_spot_opens_on_its_next_action_and_link()
    {
        var item = AddWithReminder("Essay draft");
        _database.SaveHandoff("essay draft", "second paragraph", "Write the topic sentence", ["https://docs.example.com/essay"], _database.AddCapture("x"), Now.AddHours(-3));

        var reply = _database.StartActivation(ItemRef.Format(item), null, null, Activation.ReminderMethod, Now);

        Assert.Equal("Last time you stopped at: second paragraph. Your only job right now: Write the topic sentence. You don't have to do the rest yet.", reply.Text);
        var session = _database.GetActiveSession(Now)!;
        Assert.Equal("Write the topic sentence", session.Action);
        Assert.Equal("https://docs.example.com/essay", session.Resource);
    }

    [Fact]
    public void A_spot_matches_by_whole_words_and_not_by_part_of_a_word()
    {
        var task = new StoredItem(1, ItemKind.Task, "Start the art history essay", Today, null, false);
        Handoff Spot(string project, int daysAgo = 0) => new(1, project, null, "Next", [], [], Now.AddDays(-daysAgo));

        Assert.NotNull(TransitionAssist.SpotFor(task, [Spot("Art history essay")], Now));
        Assert.NotNull(TransitionAssist.SpotFor(task, [Spot("start the ART history essay!")], Now));
        Assert.NotNull(TransitionAssist.SpotFor(task, [Spot("art")], Now));
        Assert.Null(TransitionAssist.SpotFor(task, [Spot("tart")], Now));
        Assert.Null(TransitionAssist.SpotFor(new StoredItem(2, ItemKind.Task, "Calc", Today, null, false), [Spot("Calculus homework")], Now));
    }

    [Fact]
    public void A_spot_saved_more_than_a_month_ago_is_left_alone()
    {
        var task = new StoredItem(1, ItemKind.Task, "Essay draft", Today, null, false);
        Handoff Spot(int daysAgo) => new(1, "Essay draft", null, "Next", [], [], Now.AddDays(-daysAgo));

        Assert.NotNull(TransitionAssist.SpotFor(task, [Spot(29)], Now));
        Assert.Null(TransitionAssist.SpotFor(task, [Spot(31)], Now));
    }

    [Fact]
    public void A_spot_for_something_else_is_left_alone()
    {
        var item = AddWithReminder("Essay draft");
        _database.SaveHandoff("Waypoint", "UI", "Fix the card", null, _database.AddCapture("x"), Now.AddHours(-3));

        var reply = _database.StartActivation(ItemRef.Format(item), null, null, Activation.ReminderMethod, Now);

        Assert.Equal(Activation.JobReply(Activation.Fallback(0)), reply.Text);
    }
}
