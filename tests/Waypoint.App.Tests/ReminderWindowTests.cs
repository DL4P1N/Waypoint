using System.Windows;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.App.Tests;

public sealed class ReminderWindowTests
{
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    [Fact]
    public void Start_clears_the_card_without_counting_as_not_now() => Ui.Run(ui =>
    {
        var item = ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, "Essay", DateOnly.FromDateTime(DateTime.Now)), DateTime.Now).Item!;
        var said = new List<string>();
        var transition = new ReminderTransition(true, () => said.Add("cleared"), () => said.Add("not now"), null, null);
        var card = new ReminderWindow(new DueReminder(1, item, null, null, IsFollowUp: true), "Done", 10, () => { }, () => said.Add("snoozed"), start: () => said.Add("started"), transition: transition);

        Click(UiTest.Named<Button>(card, "Start"));

        // "Not now" feeds the suggestion habits, so starting must not also be counted as declining.
        Assert.Equal(["cleared", "started"], said);
    });

    [Fact]
    public void Skip_is_recorded_as_not_now() => Ui.Run(ui =>
    {
        var item = ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, "Essay", DateOnly.FromDateTime(DateTime.Now)), DateTime.Now).Item!;
        var said = new List<string>();
        var transition = new ReminderTransition(true, () => said.Add("cleared"), () => said.Add("not now"), null, null);
        var card = new ReminderWindow(new DueReminder(1, item, null, null), "Done", 10, () => { }, () => { }, transition: transition);

        Click(UiTest.Named<Button>(card, "SkipOne"));

        Assert.Equal(["not now"], said);
    });

    [Fact]
    public void The_header_names_the_kind_in_sentence_case() => Ui.Run(ui =>
    {
        var item = ui.Database.AddByHand(new HandAddedItem(ItemKind.Reminder, "Water the plants", DateOnly.FromDateTime(DateTime.Now)), DateTime.Now).Item!;
        var card = new ReminderWindow(new DueReminder(1, item, null, null), "Done", 10, () => { }, () => { });

        Assert.Equal("Reminder", UiTest.Named<TextBlock>(card, "Kind").Text);
    });

    [Fact]
    public void The_detail_line_says_how_often_it_comes_back_and_the_class()
    {
        var today = new DateOnly(2026, 11, 10);
        var due = new DateTimeOffset(today.ToDateTime(new TimeOnly(8, 0)));
        var plants = new StoredItem(1, ItemKind.Reminder, "Water the plants", today, null, false, DueAt: due, Recurs: new Recurrence(RecurrenceUnit.Day));
        var reading = new StoredItem(2, ItemKind.Task, "Read chapter 4", today, null, false, Course: "CS 101", DueAt: due);

        var time = due.LocalDateTime.ToString("h:mm tt", System.Globalization.CultureInfo.CurrentCulture);
        Assert.Equal($"Today {time}  ·  Every day", ReminderWindow.DetailLine(new DueReminder(1, plants, null, 1440), today));
        Assert.Equal($"Read chapter 4  ·  Today {time}  ·  CS 101", ReminderWindow.DetailLine(new DueReminder(2, reading, "Time to read", null), today));
    }

    [Fact]
    public void Every_card_puts_the_main_action_first_then_not_now_then_skip_on_one_row() => Ui.Run(ui =>
    {
        var task = ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, "Essay", DateOnly.FromDateTime(DateTime.Now)), DateTime.Now).Item!;
        var transition = new ReminderTransition(true, () => { }, () => { }, () => { }, null);
        var card = new ReminderWindow(new DueReminder(1, task, null, null), "Done", 10, () => { }, () => { }, skip: () => { }, start: () => { }, transition: transition);

        // The widest card there is: a started-able item with Done, Not now, Skip, and Skip these.
        var visible = VisibleButtons(card);
        Assert.Equal(["Start", "Done", "10 more minutes", "Skip", "Skip these"], visible.Select(button => (string)button.Content));
        var row = UiTest.Named<WrapPanel>(card, "Buttons");
        Assert.Single(visible.Select(button => button.TranslatePoint(new Point(), row).Y).Distinct());
    });

    [Fact]
    public void A_plain_card_snoozes_with_not_now() => Ui.Run(ui =>
    {
        var item = ui.Database.AddByHand(new HandAddedItem(ItemKind.Reminder, "Water the plants", DateOnly.FromDateTime(DateTime.Now)), DateTime.Now).Item!;
        var said = new List<string>();
        var card = new ReminderWindow(new DueReminder(1, item, null, 1440), "Done", 1440, () => { }, () => said.Add("snoozed"));

        Assert.Equal(["Done", "Not now"], VisibleButtons(card).Select(button => (string)button.Content));
        Assert.Equal("Back in a day", UiTest.Named<Button>(card, "Snooze").ToolTip);
        Click(UiTest.Named<Button>(card, "Snooze"));
        Assert.Equal(["snoozed"], said);
    });

    [Fact]
    public void A_waiting_nudge_offers_follow_up_still_waiting_and_done() => Ui.Run(ui =>
    {
        var item = ui.Database.AddWaitingByHand("Prof. Quill", "the extension", null, null, DateTime.Now).Item!;
        var said = new List<string>();
        var waiting = new ReminderWaiting(() => said.Add("follow up"), () => said.Add("still waiting"));
        var card = new ReminderWindow(new DueReminder(1, item, "Follow up with Prof. Quill?", null), "Done", 10, () => said.Add("done"), () => said.Add("snoozed"), start: () => said.Add("started"), waiting: waiting);

        Assert.Equal([CardLabels.FollowUp, CardLabels.StillWaiting, "Done"], VisibleButtons(card).Select(button => (string)button.Content));
        Assert.Equal(Visibility.Collapsed, UiTest.Named<Button>(card, "SaveSpot").Visibility);

        Click(UiTest.Named<Button>(card, "FollowUp"));
        Click(UiTest.Named<Button>(card, "StillWaiting"));
        Click(UiTest.Named<Button>(card, "Primary"));
        Assert.Equal(["follow up", "still waiting", "done"], said);
    });

    [Fact]
    public void Esc_is_not_now_on_a_plain_card_and_more_time_on_one_that_can_be_started() => Ui.Run(ui =>
    {
        var plants = ui.Database.AddByHand(new HandAddedItem(ItemKind.Reminder, "Water the plants", DateOnly.FromDateTime(DateTime.Now)), DateTime.Now).Item!;
        var said = new List<string>();
        var plain = new ReminderWindow(new DueReminder(1, plants, null, 1440), "Done", 1440, () => said.Add("done"), () => said.Add("snoozed"));
        plain.Dismiss();
        Assert.True(plain.Answered);

        var essay = ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, "Essay", DateOnly.FromDateTime(DateTime.Now)), DateTime.Now).Item!;
        var transition = new ReminderTransition(true, () => said.Add("cleared"), () => said.Add("skipped"), () => said.Add("more time"), null);
        new ReminderWindow(new DueReminder(2, essay, null, null), "Done", 10, () => { }, () => said.Add("snoozed"), start: () => { }, transition: transition).Dismiss();

        Assert.Equal(["snoozed", "more time"], said);
    });

    [Fact]
    public void Esc_is_still_waiting_on_a_waiting_nudge_and_leaves_the_last_follow_up_unanswered() => Ui.Run(ui =>
    {
        var said = new List<string>();
        var wait = ui.Database.AddWaitingByHand("Prof. Quill", "the extension", null, null, DateTime.Now).Item!;
        new ReminderWindow(new DueReminder(1, wait, null, null), "Done", 10, () => said.Add("done"), () => { }, waiting: new ReminderWaiting(() => said.Add("follow up"), () => said.Add("still waiting"))).Dismiss();
        Assert.Equal(["still waiting"], said);

        // The follow-up has no more time to give, so Esc decides nothing: the card closes as if it was never answered.
        var essay = ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, "Essay", DateOnly.FromDateTime(DateTime.Now)), DateTime.Now).Item!;
        var transition = new ReminderTransition(true, () => said.Add("cleared"), () => said.Add("skipped"), null, null);
        var last = new ReminderWindow(new DueReminder(2, essay, null, null, IsFollowUp: true), "Done", 10, () => said.Add("done"), () => said.Add("snoozed"), start: () => { }, transition: transition);
        last.Dismiss();

        Assert.False(last.Answered);
        Assert.Equal(["still waiting"], said);
    });

    [Fact]
    public void The_main_button_is_the_first_on_the_row_and_the_header_names_the_shortcut() => Ui.Run(ui =>
    {
        var essay = ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, "Essay", DateOnly.FromDateTime(DateTime.Now)), DateTime.Now).Item!;
        var transition = new ReminderTransition(true, () => { }, () => { }, () => { }, null);
        var card = new ReminderWindow(new DueReminder(1, essay, null, null), "Done", 10, () => { }, () => { }, start: () => { }, transition: transition);

        Assert.Equal("Start", card.MainButton?.Content);
        Assert.Contains(CornerCards.KeyName, (string)UiTest.Named<TextBlock>(card, "Kind").ToolTip);
    });

    [Fact]
    public void A_deadline_card_offers_done_beside_start_and_an_exam_alert_does_not() => Ui.Run(ui =>
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var report = ui.Database.AddByHand(new HandAddedItem(ItemKind.Deadline, "Made up lab report", today), DateTime.Now).Item!;
        var due = new DueReminder(1, report, null, null);
        Assert.True(ReminderWindow.FinishesItem(due));
        Assert.True(ReminderWindow.FinishesItem(new DueReminder(2, new StoredItem(2, ItemKind.Deadline, "Made up essay", today, null, false, Source: ItemSource.Calendar), null, null)));
        Assert.False(ReminderWindow.FinishesItem(new DueReminder(3, new StoredItem(3, ItemKind.Exam, "Made up midterm", today, null, false), "Do a practice exam", null)));

        // Built the way the app builds it: Done finishes the deadline through the same call as every other card.
        var transition = new ReminderTransition(ReminderWindow.FinishesItem(due), () => { }, () => { }, () => { }, null);
        var card = new ReminderWindow(due, "Done", 10, () => ui.Database.SetDone(report, true), () => { }, start: () => { }, transition: transition);
        Assert.Equal(["Start", "Done", "10 more minutes", "Skip"], VisibleButtons(card).Select(button => (string)button.Content));

        Click(UiTest.Named<Button>(card, "Primary"));

        Assert.True(card.Answered);
        Assert.DoesNotContain(ui.Database.GetOpenItems(), item => item.Id == report.Id);
    });

    private static List<Button> VisibleButtons(ReminderWindow card)
    {
        var content = (FrameworkElement)card.Content;
        content.Measure(new Size(card.Width - 2, double.PositiveInfinity));
        content.Arrange(new Rect(0, 0, card.Width - 2, content.DesiredSize.Height));
        return UiTest.Named<WrapPanel>(card, "Buttons").Children.OfType<Button>().Where(button => button.Visibility == Visibility.Visible).ToList();
    }
}
