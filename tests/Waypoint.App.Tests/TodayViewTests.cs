using System.Windows;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.App.Tests;

public sealed class TodayViewTests
{
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    // Nothing in a new database limits the day, no classes, shifts, or bedtime, so these don't depend on the real clock.
    private static DateOnly InDays(int days) => DateOnly.FromDateTime(DateTime.Now).AddDays(days);

    private static StoredItem Add(UiTest ui, string title, int inDays) =>
        ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, title, InDays(inDays)), DateTime.Now).Item!;

    private static TodayView Connected(UiTest ui, Action<string> reply)
    {
        var view = new TodayView();
        view.Connect(ui.Database, () => { }, _ => { }, reply, () => { });
        return view;
    }

    private static (TodayView View, List<string> Replies) Make(UiTest ui)
    {
        var replies = new List<string>();
        var view = Connected(ui, replies.Add);
        ui.Host(view);
        view.Refresh(null);
        ui.Pump();
        return (view, replies);
    }

    private static Border Card(TodayView view) => UiTest.Named<Border>(view, "SuggestionCard");

    private static string Title(TodayView view) => UiTest.Named<TextBlock>(view, "SuggestionTitle").Text;

    [Fact]
    public void A_study_window_for_an_exam_has_a_practice_questions_button_that_opens_studying() => Ui.Run(ui =>
    {
        var (view, _) = Make(ui);
        var asked = new List<StoredItem>();
        view.StudyRequested += asked.Add;
        var exam = new StoredItem(9, ItemKind.Exam, "Calc midterm", InDays(2), null, false);

        // The window itself depends on the clock, so the opportunity is set directly.
        typeof(TodayView).GetField("_study", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(view, new Waypoint.Core.Schedule.StudyOpportunity(exam, DateTime.Now, DateTime.Now.AddHours(1), "Open your notes"));
        UiTest.Call(view, "OnPracticeClicked", view, new RoutedEventArgs());

        Assert.Equal(exam, Assert.Single(asked));
        Assert.Equal("Practice questions", UiTest.Named<Button>(view, "PracticeButton").Content);
    });

    [Fact]
    public void In_a_wide_window_the_rows_keep_a_readable_width() => Ui.Run(ui =>
    {
        Add(ui, "Email advisor", 1);
        var (view, _) = Make(ui);
        view.Width = 1300;
        ui.Pump();

        var rows = UiTest.Named<StackPanel>(view, "RowsPanel");
        Assert.True(rows.ActualWidth + rows.Margin.Left + rows.Margin.Right <= TodayView.RowsMaxWidth + 0.5);
        Assert.Equal(0, rows.TranslatePoint(new Point(0, 0), view).X - UiTest.Named<NestedColumn>(view, "Column").ActualWidth - rows.Margin.Left, 0.5);
    });

    [Fact]
    public void Shows_one_suggestion_with_a_reason() => Ui.Run(ui =>
    {
        Add(ui, "Read chapter 4", 3);
        Add(ui, "Email advisor", 1);
        var (view, _) = Make(ui);

        Assert.Equal(Visibility.Visible, Card(view).Visibility);
        Assert.Equal("Email advisor", Title(view));
        Assert.Equal("Due tomorrow", UiTest.Named<TextBlock>(view, "SuggestionReason").Text);
    });

    [Fact]
    public void The_row_start_steps_aside_while_the_suggestion_has_its_own() => Ui.Run(ui =>
    {
        var (empty, _) = Make(ui);
        Assert.Equal(Visibility.Visible, UiTest.Named<Button>(empty, "RowStart").Visibility);

        Add(ui, "Email advisor", 1);
        var (view, _) = Make(ui);

        Assert.Equal(Visibility.Visible, Card(view).Visibility);
        Assert.Equal(Visibility.Collapsed, UiTest.Named<Button>(view, "RowStart").Visibility);

        // Without a wind-down there's no Tonight card, so the row keeps Save my spot.
        Assert.Equal(Visibility.Visible, UiTest.Named<Button>(view, "RowSaveSpot").Visibility);
    });

    [Fact]
    public void Hides_when_nothing_is_open() => Ui.Run(ui =>
    {
        var (view, _) = Make(ui);

        Assert.Equal(Visibility.Collapsed, Card(view).Visibility);
    });

    [Fact]
    public void Not_this_one_moves_on_and_stays_skipped_for_the_day() => Ui.Run(ui =>
    {
        Add(ui, "Read chapter 4", 3);
        Add(ui, "Email advisor", 1);
        var (view, _) = Make(ui);
        var skip = UiTest.Named<Button>(view, "SuggestionSkip");

        Click(skip);
        Assert.Equal("Read chapter 4", Title(view));

        // A refresh, or the page made again after a restart, still remembers what was passed on.
        view.Refresh(null);
        Assert.Equal("Read chapter 4", Title(view));
        var again = Connected(ui, _ => { });
        again.Refresh(null);
        Assert.Equal("Read chapter 4", Title(again));

        Click(skip);
        Assert.Equal(Visibility.Collapsed, Card(view).Visibility);
    });

    [Fact]
    public void Start_begins_a_session_on_the_suggestion_and_the_card_steps_aside() => Ui.Run(ui =>
    {
        Add(ui, "Read chapter 4", 3);
        var email = Add(ui, "Email advisor", 1);
        var (view, replies) = Make(ui);

        Click(UiTest.Named<Button>(view, "SuggestionStart"));

        var session = ui.Database.GetActiveSession(DateTime.Now);
        Assert.NotNull(session);
        Assert.Equal(ItemRef.Format(email), session.TaskRef);
        Assert.StartsWith("Your only job right now", Assert.Single(replies));
        Assert.Equal(Visibility.Collapsed, Card(view).Visibility);
    });

    [Fact]
    public void The_row_start_hides_while_a_session_is_going_so_it_cant_replace_it() => Ui.Run(ui =>
    {
        Add(ui, "Read chapter 4", 3);
        Add(ui, "Email advisor", 1);
        var (view, _) = Make(ui);

        Click(UiTest.Named<Button>(view, "SuggestionStart"));
        view.Refresh(null);
        ui.Pump();

        Assert.Equal(Visibility.Collapsed, Card(view).Visibility);
        Assert.Equal(Visibility.Collapsed, UiTest.Named<Button>(view, "RowStart").Visibility);
    });

    [Fact]
    public void The_suggestion_ignores_the_selected_filter() => Ui.Run(ui =>
    {
        Add(ui, "Email advisor", 1);
        var work = ui.Database.CreateToDoFilter("Work").Filter!;
        var (view, _) = Make(ui);

        UiTest.Named<NestedColumn>(view, "Column").Select($"f{work.Id}");
        view.Refresh(null);
        ui.Pump();

        Assert.Equal("Email advisor", Title(view));
    });

    [Fact]
    public void Purchases_and_shopping_stay_off_the_rows_the_counts_and_the_week() => Ui.Run(ui =>
    {
        // A purchase typed on Budget or sorted from Ask is stored open, so only the kind keeps it off To do.
        ui.Database.AddExpense("Coffee beans", 1450, null, InDays(-2), DateTime.Now);
        ui.Database.CompleteCapture(ui.Database.AddCapture("lunch 12"), [new SortedItem(ItemKind.Expense, "Lunch out", InDays(0), 1200)]);
        ui.Database.CompleteCapture(ui.Database.AddCapture("buy milk"), [new SortedItem(ItemKind.Shopping, "Milk", null, null)]);
        Add(ui, "Email advisor", 0);
        var (view, _) = Make(ui);

        var shown = UiTest.Descendants<TextBlock>(view).Where(text => text.IsVisible).Select(text => text.Text).ToList();
        Assert.Contains("Email advisor", shown);
        Assert.DoesNotContain("Coffee beans", shown);
        Assert.DoesNotContain("Lunch out", shown);
        Assert.DoesNotContain("Milk", shown);
        Assert.DoesNotContain("Overdue", shown);

        Click(UiTest.Named<Button>(view, "ViewToggle"));
        ui.Pump();
        var week = UiTest.Named<WeekView>(view, "WeekPage");
        var inWeek = UiTest.Descendants<TextBlock>(week).Select(text => text.Text).ToList();
        Assert.Contains("Email advisor", inWeek);
        Assert.DoesNotContain("Coffee beans", inWeek);
        Assert.DoesNotContain("Lunch out", inWeek);
        Assert.Equal(Visibility.Collapsed, UiTest.Named<Button>(week, "UndatedLine").Visibility);
    });

    [Fact]
    public void A_screen_reader_hears_a_rows_time_and_that_it_is_overdue() => Ui.Run(ui =>
    {
        ui.Database.CompleteCapture(ui.Database.AddCapture("paper"), [new SortedItem(ItemKind.Deadline, "Paper two", InDays(-1), null, new TimeOnly(15, 0))]);
        ui.Database.CompleteCapture(ui.Database.AddCapture("quiz"), [new SortedItem(ItemKind.Deadline, "Quiz", InDays(1), null, new TimeOnly(9, 30))]);
        var (view, _) = Make(ui);

        var names = UiTest.Descendants<CheckBox>(view).Select(box => System.Windows.Automation.AutomationProperties.GetName(box)).ToList();
        Assert.Contains("Paper two, 3:00 PM, Overdue", names);
        Assert.Contains("Quiz, 9:30 AM", names);
    });

    [Fact]
    public void Rows_and_their_boxes_are_named_for_screen_readers() => Ui.Run(ui =>
    {
        Add(ui, "Email advisor", 0);
        var (view, _) = Make(ui);

        Assert.Contains(UiTest.Descendants<CheckBox>(view), box => System.Windows.Automation.AutomationProperties.GetName(box) == "Email advisor");

        var groups = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(UiTest.Named<ItemsControl>(view, "Groups"));
        var names = groups.GetChildren()?.Select(child => child.GetName()).ToList() ?? [];
        Assert.NotEmpty(names);
        Assert.DoesNotContain(names, name => name.Contains("Waypoint.App", StringComparison.Ordinal));
    });
    private static readonly Lazy<bool> SaveSpotFormsAnswered = new(() =>
    {
        // A form a test opens is filled in and saved as soon as it shows, the way RenameWindow is answered.
        EventManager.RegisterClassHandler(typeof(SaveSpotWindow), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            var form = (SaveSpotWindow)sender;
            ((TextBox)form.FindName("StoppedBox")).Text = "Stopped on the intro";
            var save = UiTest.Named<Button>(form, "Save");
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, save));
        }));
        return true;
    });

    [Fact]
    public void Save_my_spot_from_the_tray_opens_the_same_form_and_saves_without_sorting() => Ui.Run(ui =>
    {
        _ = SaveSpotFormsAnswered.Value;
        var (view, replies) = Make(ui);

        view.OpenSaveSpot();
        ui.Pump();

        Assert.NotNull(ui.Database.GetLastSpotSaved());
        Assert.Single(replies);
        Assert.Empty(ui.Database.GetPendingCaptures());
    });

    [Fact]
    public void An_empty_page_says_where_to_start_and_a_filter_names_itself() => Ui.Run(ui =>
    {
        var work = ui.Database.CreateToDoFilter("Work").Filter!;
        var (view, _) = Make(ui);
        var empty = UiTest.Named<TextBlock>(view, "Empty");
        UiTest.Call(view, "OnSetupHideClicked", view, new RoutedEventArgs());
        view.Refresh(null);
        ui.Pump();

        Assert.Equal(Visibility.Visible, empty.Visibility);
        Assert.Equal("Nothing on To do yet. Type anything in Ask on the right, or press +.", empty.Text);

        UiTest.Named<NestedColumn>(view, "Column").Select($"f{work.Id}");
        view.Refresh(null);
        ui.Pump();
        Assert.Equal("Nothing for Work.", empty.Text);
    });

    [Fact]
    public void A_fresh_page_says_where_to_start_once_with_the_setup_card_showing() => Ui.Run(ui =>
    {
        var (view, _) = Make(ui);
        var empty = UiTest.Named<TextBlock>(view, "Empty");
        var card = UiTest.Named<Border>(view, "SetupCard");

        // The setup card's first line already says to add a task with + or Ask, so the empty line doesn't repeat it.
        Assert.Equal(Visibility.Visible, card.Visibility);
        Assert.Equal("Nothing on To do yet.", empty.Text);

        UiTest.Call(view, "OnSetupHideClicked", view, new RoutedEventArgs());
        view.Refresh(null);
        ui.Pump();

        Assert.Equal(Visibility.Collapsed, card.Visibility);
        Assert.Equal("Nothing on To do yet. Type anything in Ask on the right, or press +.", empty.Text);
    });

    [Fact]
    public void The_setup_card_keeps_its_lines_when_the_page_refreshes_so_focus_on_one_stays() => Ui.Run(ui =>
    {
        var (view, _) = Make(ui);
        var steps = UiTest.Named<ItemsControl>(view, "SetupSteps");
        var before = UiTest.Descendants<Button>(steps).ToList();
        Assert.NotEmpty(before);

        // The window refreshes To do every minute. Nothing on the card changed, so its buttons are the same ones.
        view.Refresh(null);
        ui.Pump();

        Assert.Equal(before, UiTest.Descendants<Button>(steps).ToList());
    });

    [Fact]
    public void Nothing_to_pick_points_at_ask_and_isnt_said_twice_in_a_row() => Ui.Run(ui =>
    {
        var (view, replies) = Make(ui);

        Click(UiTest.Named<Button>(view, "RowStart"));
        Click(UiTest.Descendants<Button>(view).First(button => button.Content as string == "I have 15 minutes"));

        Assert.Equal(["Nothing to pick yet. Type anything in Ask, or use + to add a task."], replies);
    });

    [Fact]
    public void Hide_finished_counts_what_it_hides_and_day_headings_are_sentence_case() => Ui.Run(ui =>
    {
        var done = Add(ui, "Return books", 0);
        Add(ui, "Email advisor", 0);
        ui.Database.SetDone(done, true);
        var (view, _) = Make(ui);
        view.UpdateLayout();

        Assert.Equal("Hide 1 finished", UiTest.Named<TextBlock>(view, "HideFinishedText").Text);
        Assert.Contains(UiTest.Descendants<TextBlock>(view), block => block.Text == "Today");
        Assert.DoesNotContain(UiTest.Descendants<TextBlock>(view), block => block.Text == "TODAY");
    });

    [Fact]
    public void Fifteen_minutes_starts_a_session_on_what_it_picked() => Ui.Run(ui =>
    {
        var email = Add(ui, "Email advisor", 1);
        var (view, replies) = Make(ui);

        Click(UiTest.Descendants<Button>(view).First(button => button.Content as string == "I have 15 minutes"));

        Assert.Equal(ItemRef.Format(email), ui.Database.GetActiveSession(DateTime.Now)?.TaskRef);
        var reply = Assert.Single(replies);
        Assert.StartsWith("For 15 minutes: Email advisor.", reply);
        Assert.DoesNotContain("Your only job", reply);
    });

    [Fact]
    public void The_money_line_shows_only_when_there_is_something_safe_to_spend()
    {
        Assert.Null(TodayView.SafeToSpendLine(new Waypoint.Core.Remote.SpendSummary(null, null)));
        Assert.Null(TodayView.SafeToSpendLine(new Waypoint.Core.Remote.SpendSummary(0, null, "Spent more than today's share")));
        Assert.StartsWith("Safe to spend today: $12.50", TodayView.SafeToSpendLine(new Waypoint.Core.Remote.SpendSummary(1250, null)));
    }

    [Fact]
    public void With_no_budget_there_is_no_money_line() => Ui.Run(ui =>
    {
        var (view, _) = Make(ui);

        Assert.Equal(Visibility.Collapsed, UiTest.Named<Button>(view, "SafeLine").Visibility);
    });

    [Fact]
    public void During_a_shift_the_card_says_until_when_and_the_row_start_stays() => Ui.Run(ui =>
    {
        Add(ui, "Email advisor", 1);
        var now = DateTime.Now;
        // Kept inside today, so the shift doesn't start yesterday when the test runs just after midnight.
        var start = now.AddHours(-1) < now.Date ? now.Date : now.AddHours(-1);
        var end = now.AddHours(1) >= now.Date.AddDays(1) ? now.Date.AddDays(1).AddMinutes(-1) : now.AddHours(1);
        ui.Database.AddWorkShift(start, end);
        var (view, _) = Make(ui);

        Assert.Equal(Visibility.Visible, Card(view).Visibility);
        Assert.StartsWith("Work until ", Title(view));
        Assert.EndsWith("A suggestion will be here after.", Title(view));
        Assert.Equal(Visibility.Collapsed, UiTest.Named<StackPanel>(view, "SuggestionButtons").Visibility);
        Assert.Equal(Visibility.Visible, UiTest.Named<Button>(view, "RowStart").Visibility);
    });

    [Fact]
    public void A_line_sets_the_classes_and_filters_apart_from_all_and_waiting_on() => Ui.Run(ui =>
    {
        var (view, _) = Make(ui);
        var entries = UiTest.Named<ListBox>(UiTest.Named<NestedColumn>(view, "Column"), "Entries").Items.Cast<ColumnEntry>().ToList();

        Assert.Equal(["all", "waiting"], entries.Take(2).Select(entry => entry.Key));
        Assert.False(entries[0].RuleAbove || entries[1].RuleAbove);
        Assert.True(entries[2].RuleAbove);
        Assert.Single(entries, entry => entry.RuleAbove);
    });

    private static Action<Window>? _addFormShown;

    private static readonly Lazy<bool> AddFormsWatched = new(() =>
    {
        // Only while a test watches, so other tests' forms are left alone. The watcher answers every form it sees,
        // so a wrong one fails the test instead of waiting forever.
        void Shown(object sender, RoutedEventArgs _) => _addFormShown?.Invoke((Window)sender);

        EventManager.RegisterClassHandler(typeof(WaitingWindow), FrameworkElement.LoadedEvent, new RoutedEventHandler(Shown));
        EventManager.RegisterClassHandler(typeof(AddItemWindow), FrameworkElement.LoadedEvent, new RoutedEventHandler(Shown));
        return true;
    });

    [Fact]
    public void Ctrl_N_on_waiting_on_adds_a_wait_and_elsewhere_a_task() => Ui.Run(ui =>
    {
        _ = AddFormsWatched.Value;
        var shown = new List<Window>();
        _addFormShown = form =>
        {
            shown.Add(form);
            if (form is WaitingWindow wait)
            {
                ((TextBox)wait.FindName("WhoBox")).Text = "Prof. Quill";
                UiTest.Call(wait, "OnSaveClicked", wait, new RoutedEventArgs());
            }
            else
            {
                form.DialogResult = false;
            }
        };

        try
        {
            var (view, _) = Make(ui);
            var column = UiTest.Named<NestedColumn>(view, "Column");
            var add = UiTest.Named<Button>(column, "AddButton");
            Assert.Equal("Add to To do (Ctrl+N for a new task)", add.ToolTip);

            column.Select("waiting");
            ui.Pump();
            Assert.Equal("Add to To do (Ctrl+N for a new waiting-on item)", add.ToolTip);

            view.AddByShortcut();
            ui.Pump();
            Assert.IsType<WaitingWindow>(Assert.Single(shown));
            Assert.Equal("Prof. Quill", Assert.Single(ui.Database.GetWaiting()).Who);
            Assert.Equal("waiting", column.SelectedKey);

            column.Select("all");
            ui.Pump();
            Assert.Equal("Add to To do (Ctrl+N for a new task)", add.ToolTip);
            view.AddByShortcut();
            ui.Pump();
            Assert.IsType<AddItemWindow>(shown[1]);
        }
        finally
        {
            _addFormShown = null;
        }
    });

    private static List<string> SetupLines(TodayView view) =>
        UiTest.Named<ItemsControl>(view, "SetupSteps").Items.Cast<object>().Select(step => step.ToString()!).ToList();

    [Fact]
    public void The_setup_cards_pay_line_asks_for_the_pay_schedule_form_rather_than_just_Budget() => Ui.Run(ui =>
    {
        var budgetOpened = 0;
        var formAsked = 0;
        var view = new TodayView();
        view.Connect(ui.Database, () => budgetOpened++, _ => { }, _ => { }, () => { });
        view.PayScheduleRequested += () => formAsked++;
        ui.Host(view);
        view.Refresh(null);
        ui.Pump();

        var pay = UiTest.Named<ItemsControl>(view, "SetupSteps").Items.Cast<object>().Single(step => step.ToString()!.Contains("how you're paid"));
        UiTest.Call(view, "OnSetupStepClicked", new Button { DataContext = pay }, new RoutedEventArgs());

        Assert.Equal(1, formAsked);
        Assert.Equal(0, budgetOpened);
        Assert.DoesNotContain(SetupLines(view), line => line.Contains("how you're paid"));
    });

    [Fact]
    public void A_fresh_copy_shows_the_setup_card_and_its_lines_go_as_they_are_done() => Ui.Run(ui =>
    {
        var (view, _) = Make(ui);
        var card = UiTest.Named<Border>(view, "SetupCard");
        Assert.Equal(Visibility.Visible, card.Visibility);
        Assert.Equal(4, SetupLines(view).Count);

        Add(ui, "Email advisor", 1);
        view.Refresh(null);
        Assert.DoesNotContain(SetupLines(view), line => line.Contains("first task"));

        ui.Database.AddWorkShift(DateTime.Today.AddDays(1).AddHours(9), DateTime.Today.AddDays(1).AddHours(17));
        view.Refresh(null);
        Assert.DoesNotContain(SetupLines(view), line => line.Contains("work hours"));

        Click(UiTest.Named<Button>(view, "SetupHide"));
        Assert.Equal(Visibility.Collapsed, card.Visibility);
        var again = Connected(ui, _ => { });
        again.Refresh(null);
        Assert.Equal(Visibility.Collapsed, UiTest.Named<Border>(again, "SetupCard").Visibility);
    });

    [Fact]
    public void A_copy_that_already_has_things_in_it_never_shows_the_setup_card() => Ui.Run(ui =>
    {
        Add(ui, "Email advisor", 1);
        var (view, _) = Make(ui);

        Assert.Equal(Visibility.Collapsed, UiTest.Named<Border>(view, "SetupCard").Visibility);
    });
}
