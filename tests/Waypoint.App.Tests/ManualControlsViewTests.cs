using System.Windows;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Remote;
using Waypoint.Core.Schedule;

namespace Waypoint.App.Tests;

/// <summary>
/// Buttons and menu lines for assistant ops that had none. Each calls the page's data, which uses the same Core
/// method as the op (checked in Waypoint.Core.Tests), and none of them sends anything to be sorted.
/// </summary>
public sealed class ManualControlsViewTests
{
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    private static Button ButtonIn(DependencyObject root, string label) =>
        UiTest.Descendants<Button>(root).Single(button => button.Content as string == label);

    /// <summary>Records what tonight's card asked for, since whether the wind-down is open depends on the real clock.</summary>
    private sealed class RecordingSessions : ISessionData
    {
        public List<string> Calls { get; } = [];

        public ActivationSession? GetActiveSession(DateTime now) => null;

        public OpReply PauseSession(DateTime until, DateTime now) => Said("pause");

        public ActivationSession? GetPausedSession() => null;

        public OpReply ResumeSession() => Said("resume");

        public OpReply CancelSession(DateTime now) => Said("cancel");

        public string? DescribePayProposal(DateTime now) => null;

        public BedtimeStatus? GetTonight(DateTime now) => null;

        public OpReply MarkRoutineStep(string step, bool done, DateTime now) => Said($"mark {step} {done}");

        public OpReply SnoozeBedtime(DateTime now) => Said("snooze");

        public OpReply SkipBedtime(DateTime now) => Said("skip");

        public StoredItem? TakeAfterThis(DateTime now) => null;

        private OpReply Said(string call)
        {
            Calls.Add(call);
            return new OpReply($"reply to {call}");
        }
    }

    private static (TodayView View, List<string> Replies, List<string> Sent) MakeToday(UiTest ui)
    {
        var replies = new List<string>();
        var sent = new List<string>();
        var view = new TodayView();
        view.Connect(ui.Database, () => { }, sent.Add, replies.Add, () => { });
        ui.Host(view);
        view.Refresh(null);
        ui.Pump();
        return (view, replies, sent);
    }

    [Fact]
    public void Tonight_cards_checkboxes_and_buttons_go_through_the_bedtime_calls_and_say_their_reply() => Ui.Run(ui =>
    {
        var (view, replies, sent) = MakeToday(ui);
        var sessions = new RecordingSessions();
        view.Sessions = sessions;
        var now = DateTime.Now;
        var plan = new BedtimePlan(now.AddHours(1), now.AddHours(8), null, Morning: DateOnly.FromDateTime(now.AddDays(1)));
        view.Refresh(new BedtimeStatus(plan, [new RoutineStep(BedtimeSettings.DefaultEveningStepName, false), new RoutineStep(BedtimeRoutine.Teeth, false)]));
        ui.Pump();
        var card = UiTest.Named<Border>(view, "TonightCard");

        var step = UiTest.Descendants<CheckBox>(card).Single(box => box.DataContext is RoutineStep { Label: BedtimeSettings.DefaultEveningStepName });
        step.IsChecked = true;
        Click(ButtonIn(card, CardLabels.NotNow));
        Click(ButtonIn(card, "Skip tonight"));

        Assert.Equal([$"mark {BedtimeSettings.DefaultEveningStepName} True", "snooze", "skip"], sessions.Calls);
        Assert.Equal([$"reply to mark {BedtimeSettings.DefaultEveningStepName} True", "reply to snooze", "reply to skip"], replies);
        Assert.Empty(sent);
    });

    [Fact]
    public void A_paused_sessions_row_offers_to_pick_it_back_up() => Ui.Run(ui =>
    {
        var essay = ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, "Write the essay", DateOnly.FromDateTime(DateTime.Now)), DateTime.Now).Item!;
        ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, "Laundry", DateOnly.FromDateTime(DateTime.Now)), DateTime.Now);
        ui.Database.StartActivation(ItemRef.Format(essay), "Open the outline", null, "test", DateTime.Now);
        ui.Database.PauseSession(null, null, DateTime.Now);
        var (view, replies, sent) = MakeToday(ui);

        CheckBox Row(string title)
        {
            view.UpdateLayout();
            return UiTest.Descendants<CheckBox>(view).First(box => box.DataContext is { } row && row.GetType().Name == "ItemRow" && UiTest.Prop<string>(row, "Title") == title);
        }

        var other = ui.RightClick(Row("Laundry"))!;
        Assert.DoesNotContain("Pick up where I left off", UiTest.Labels(other));
        ui.Close(other);

        var menu = ui.RightClick(Row("Write the essay"))!;
        Assert.Equal("Pick up where I left off", UiTest.Labels(menu)[0]);
        ui.Choose(menu, "Pick up where I left off");

        Assert.Equal("Write the essay", ui.Database.GetActiveSession(DateTime.Now)?.TaskTitle);
        Assert.Null(ui.Database.GetPausedSession());
        Assert.Single(replies);
        Assert.Empty(sent);
    });

    [Fact]
    public void End_on_the_session_card_ends_it_without_sending_anything_to_be_sorted() => Ui.Run(ui =>
    {
        var essay = ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, "Write the essay", DateOnly.FromDateTime(DateTime.Now)), DateTime.Now).Item!;
        ui.Database.StartActivation(ItemRef.Format(essay), "Open the outline", null, "test", DateTime.Now);
        var sent = new List<string>();
        var window = new ItemsWindow(ui.Database, sent.Add, () => { }, _ => { });
        UiTest.Call(window, "ShowSession");
        var card = UiTest.Named<Border>(window, "SessionCard");
        Assert.Equal(Visibility.Visible, card.Visibility);

        Click(ButtonIn(card, "End"));

        Assert.Empty(sent);
        Assert.Null(ui.Database.GetActiveSession(DateTime.Now));
        Assert.Contains(ui.Database.GetOpenItems(), item => item.Title == "Write the essay");
        Assert.Equal(Visibility.Collapsed, card.Visibility);
    });

    // Set only while a test wants the forms it opens answered, so other tests' forms are left alone.
    private static Action<BudgetFormWindow>? _formShown;

    private static readonly Lazy<bool> FormsWatched = new(() =>
    {
        EventManager.RegisterClassHandler(typeof(BudgetFormWindow), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => _formShown?.Invoke((BudgetFormWindow)sender)));
        return true;
    });

    /// <summary>Clicks the button, types into the form it opens as asked, and presses its main button.</summary>
    private static List<string> Answer(Button button, params string[] typed)
    {
        _ = FormsWatched.Value;
        var titles = new List<string>();
        _formShown = form =>
        {
            titles.Add(form.Title);
            var boxes = UiTest.Descendants<TextBox>(form).ToList();
            for (var i = 0; i < typed.Length; i++)
            {
                boxes[i].Text = typed[i];
            }

            Click(UiTest.Named<Button>(form, "DoneButton"));
            if (form.IsVisible)
            {
                form.Close();
            }
        };

        try
        {
            Click(button);
        }
        finally
        {
            _formShown = null;
        }

        return titles;
    }

    private static BudgetView MakeBudget(UiTest ui)
    {
        var view = new BudgetView();
        ui.Host(view);
        view.Connect(new LocalBudgetData(ui.Database), () => { });
        ui.Pump();
        return view;
    }

    [Fact]
    public void I_got_paid_on_until_payday_counts_the_expected_paycheck() => Ui.Run(ui =>
    {
        ui.Database.SetPaySchedule("biweekly", [], 85000, DateTime.Now);
        var view = MakeBudget(ui);
        var button = UiTest.Named<Button>(view, "GotPaid");
        Assert.Equal(Visibility.Visible, button.Visibility);

        Assert.Equal(["I got paid"], Answer(button, "512.34"));
        Assert.Equal(51234, Assert.Single(ui.Database.GetPayDeposits()).Cents);
    });

    [Fact]
    public void How_long_new_waits_last_can_be_changed_on_waiting_purchases() => Ui.Run(ui =>
    {
        var view = MakeBudget(ui);
        var button = UiTest.Named<Button>(view, "WaitHours");
        Assert.Equal("New waits last 48 hours. Change...", button.Content);

        Answer(button, "24");
        ui.Pump();

        Assert.Equal(24, ui.Database.GetDefaultWaitHours());
        Assert.Equal("New waits last 24 hours. Change...", button.Content);
    });

    [Fact]
    public void Look_for_patterns_on_spending_review_shows_what_typing_it_would() => Ui.Run(ui =>
    {
        var view = MakeBudget(ui);
        var patterns = UiTest.Named<TextBlock>(view, "Patterns");
        Assert.Equal(Visibility.Collapsed, patterns.Visibility);

        Click(ButtonIn(UiTest.Named<StackPanel>(view, "ReviewPage"), "Look for patterns"));

        Assert.Equal(Visibility.Visible, patterns.Visibility);
        var lines = ui.Database.GetSpendingPatterns(DateOnly.FromDateTime(DateTime.Now));
        Assert.Equal(lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "Nothing stands out yet.", patterns.Text);
    });

    [Fact]
    public void A_saved_spot_waiting_for_its_next_step_can_be_answered_in_saved_spots() => Ui.Run(ui =>
    {
        ui.Database.SaveHandoff("essay", "the intro", null, [], ui.Database.AddCapture("save my spot"), DateTime.Now);
        var view = new NotesView();
        view.Connect(ui.Database);
        ui.Host(view);
        view.Refresh(force: true);
        ui.Pump();
        var places = UiTest.Named<ListBox>(UiTest.Named<NestedColumn>(view, "Column"), "Entries");
        places.SelectedItem = places.Items.Cast<ColumnEntry>().Single(entry => entry.Key == "spots");
        ui.Pump();
        var spots = UiTest.Named<ListBox>(view, "SpotsList");
        spots.SelectedIndex = 0;
        ui.Pump();
        var button = UiTest.Named<Button>(view, "SpotNextButton");
        Assert.Equal(Visibility.Visible, button.Visibility);

        ui.RenameAnswer = "outline part two";
        Click(button);
        ui.Pump();

        Assert.Equal("outline part two", ui.Database.GetSavedSpots().Single().Next);
        Assert.Equal(Visibility.Collapsed, button.Visibility);
    });
}
