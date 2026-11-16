using System.Windows;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.App.Tests;

/// <summary>"Earlier you meant to" above Ask, and This didn't help on the PC's cards and row menu.</summary>
public sealed class AfterThisAndFeedbackViewTests
{
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    private static Button ButtonIn(DependencyObject root, string label) =>
        UiTest.Descendants<Button>(root).First(button => button.Content as string == label && button.IsVisible);

    private static StoredItem Add(UiTest ui, string title, int inDays = 1) =>
        ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, title, DateOnly.FromDateTime(DateTime.Now).AddDays(inDays)), DateTime.Now).Item!;

    private static ItemsWindow Shown(UiTest ui)
    {
        var window = new ItemsWindow(ui.Database, _ => { }, () => { }, _ => { })
        {
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
        };
        window.ShowList();
        ui.Pump();
        return window;
    }

    [Fact]
    public void Opening_the_window_brings_back_what_was_held_once_with_start() => Ui.Run(ui =>
    {
        var essay = Add(ui, "Write the essay");
        ui.Database.HoldAfterThis(essay, DateTime.Now);

        var window = Shown(ui);
        var card = UiTest.Named<Border>(window, "AfterThisCard");
        Assert.Equal(Visibility.Visible, card.Visibility);
        Assert.Equal("Earlier you meant to: Write the essay", UiTest.Named<TextBlock>(window, "AfterThisLine").Text);

        Click(ButtonIn(card, "Start"));
        Assert.Equal(Visibility.Collapsed, card.Visibility);
        Assert.Equal("Write the essay", ui.Database.GetActiveSession(DateTime.Now)?.TaskTitle);

        // It was shown once, so opening again doesn't bring it back.
        window.Hide();
        window.ShowList();
        ui.Pump();
        Assert.Equal(Visibility.Collapsed, card.Visibility);
        window.Hide();
    });

    [Fact]
    public void Closing_the_window_puts_it_away_even_unanswered() => Ui.Run(ui =>
    {
        ui.Database.HoldAfterThis(Add(ui, "Write the essay"), DateTime.Now);
        var window = Shown(ui);
        var card = UiTest.Named<Border>(window, "AfterThisCard");
        Assert.Equal(Visibility.Visible, card.Visibility);

        window.Hide();
        window.ShowList();
        ui.Pump();

        Assert.Equal(Visibility.Collapsed, card.Visibility);
        window.Hide();
    });

    [Fact]
    public void Start_does_nothing_once_the_item_was_finished_meanwhile() => Ui.Run(ui =>
    {
        var essay = Add(ui, "Write the essay");
        ui.Database.HoldAfterThis(essay, DateTime.Now);
        var window = Shown(ui);
        var card = UiTest.Named<Border>(window, "AfterThisCard");
        var start = ButtonIn(card, "Start");

        ui.Database.SetDone(essay, true);
        Click(start);

        Assert.Equal(Visibility.Collapsed, card.Visibility);
        Assert.Null(ui.Database.GetActiveSession(DateTime.Now));
        window.Hide();
    });

    [Fact]
    public void It_isnt_shown_above_Ask_while_its_own_reminder_card_is_up() => Ui.Run(ui =>
    {
        var essay = Add(ui, "Write the essay");
        ui.Database.HoldAfterThis(essay, DateTime.Now);
        var window = new ItemsWindow(ui.Database, _ => { }, () => { }, _ => { })
        {
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            CardUpFor = item => item.Id == essay.Id,
        };
        window.ShowList();
        ui.Pump();

        Assert.Equal(Visibility.Collapsed, UiTest.Named<Border>(window, "AfterThisCard").Visibility);
        window.Hide();
    });

    [Fact]
    public void Did_it_that_ends_a_session_brings_it_back_above_Ask() => Ui.Run(ui =>
    {
        var essay = Add(ui, "Write the essay");
        var dishes = Add(ui, "Do the dishes");
        ui.Database.StartActivation(ItemRef.Format(dishes), Activation.TrialOffer, null, Activation.TypedMethod, DateTime.Now);
        ItemsWindow? shown = null;
        var window = new ItemsWindow(ui.Database, text =>
        {
            // Did it goes through typing in the app. Here it's applied directly, then the window refreshes as it would.
            Assert.Equal("did it", text);
            ui.Database.ApplySessionWord(SessionWord.Did, DateTime.Now);
            shown!.Refresh(0, null, null, null, null, null);
        }, () => { }, _ => { })
        {
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
        };
        shown = window;
        window.ShowList();
        window.Refresh(0, null, null, null, null, null);
        ui.Pump();
        var card = UiTest.Named<Border>(window, "AfterThisCard");
        Assert.Equal(Visibility.Collapsed, card.Visibility);
        ui.Database.HoldAfterThis(essay, DateTime.Now);

        Click(ButtonIn(UiTest.Named<Border>(window, "SessionCard"), "Did it"));

        Assert.Null(ui.Database.GetActiveSession(DateTime.Now));
        Assert.Equal(Visibility.Visible, card.Visibility);
        Assert.Equal("Earlier you meant to: Write the essay", UiTest.Named<TextBlock>(window, "AfterThisLine").Text);
        window.Hide();
    });

    [Fact]
    public void A_pc_card_left_for_half_an_hour_holds_its_item_dated_from_when_it_showed() => Ui.Run(ui =>
    {
        var essay = Add(ui, "Write the essay");
        var shownAt = DateTime.Now.AddMinutes(-30);
        var hold = new ReminderCardHold(ui.Database, essay, shownAt);

        hold.Elapsed();

        Assert.Equal("Write the essay", ui.Database.TakeAfterThis(DateTime.Now)?.Title);
    });

    [Fact]
    public void Answering_or_closing_the_pc_card_drops_its_hold() => Ui.Run(ui =>
    {
        var essay = Add(ui, "Write the essay");
        var late = new ReminderCardHold(ui.Database, essay, DateTime.Now);
        late.Elapsed();
        late.Answered();
        Assert.Null(ui.Database.TakeAfterThis(DateTime.Now));

        // Answered before the half hour, so nothing is held when it would have run out.
        var early = new ReminderCardHold(ui.Database, essay, DateTime.Now);
        early.Answered();
        early.Elapsed();
        Assert.Null(ui.Database.TakeAfterThis(DateTime.Now));
        Assert.False(ReminderCardHold.Applies(essay, waiting: true));
    });

    [Fact]
    public void Not_now_puts_it_away_and_it_stays_on_the_list() => Ui.Run(ui =>
    {
        var essay = Add(ui, "Write the essay");
        ui.Database.HoldAfterThis(essay, DateTime.Now);
        var window = Shown(ui);
        var card = UiTest.Named<Border>(window, "AfterThisCard");

        Click(ButtonIn(card, CardLabels.NotNow));

        Assert.Equal(Visibility.Collapsed, card.Visibility);
        Assert.Contains(ui.Database.GetOpenItems(), item => item.Title == "Write the essay");
        Assert.Null(ui.Database.GetActiveSession(DateTime.Now));
        Assert.Null(ui.Database.TakeAfterThis(DateTime.Now));
        window.Hide();
    });

    [Fact]
    public void With_nothing_held_there_is_no_card() => Ui.Run(ui =>
    {
        var window = Shown(ui);

        Assert.Equal(Visibility.Collapsed, UiTest.Named<Border>(window, "AfterThisCard").Visibility);
        window.Hide();
    });

    [Fact]
    public void The_suggestion_card_keeps_this_didnt_help_with_an_optional_line() => Ui.Run(ui =>
    {
        var email = Add(ui, "Email advisor");
        var view = new TodayView();
        view.Connect(ui.Database, () => { }, _ => { }, _ => { }, () => { });
        ui.Host(view);
        view.Refresh(null);
        ui.Pump();

        var prompt = UiTest.Named<FeedbackPrompt>(view, "SuggestionFeedback");
        Click(ButtonIn(prompt, CardLabels.DidntHelp));

        var kept = Assert.Single(ui.Database.GetFeedback());
        Assert.Equal((Feedback.Suggestion, ItemRef.Format(email)), (kept.Feature, kept.ItemRef));

        UiTest.Descendants<TextBox>(prompt).Single().Text = "I'd already emailed";
        Click(ButtonIn(prompt, "Add"));
        Assert.Equal("I'd already emailed", Assert.Single(ui.Database.GetFeedback()).Note);
        Assert.Contains(UiTest.Descendants<TextBlock>(prompt), block => block.IsVisible && block.Text.StartsWith("Thanks."));
    });

    [Fact]
    public void Skipping_the_line_still_keeps_the_click() => Ui.Run(ui =>
    {
        Add(ui, "Email advisor");
        var view = new TodayView();
        view.Connect(ui.Database, () => { }, _ => { }, _ => { }, () => { });
        ui.Host(view);
        view.Refresh(null);
        ui.Pump();
        var prompt = UiTest.Named<FeedbackPrompt>(view, "SuggestionFeedback");

        Click(ButtonIn(prompt, CardLabels.DidntHelp));
        Click(ButtonIn(prompt, FeedbackPrompt.NoNote));

        Assert.Null(Assert.Single(ui.Database.GetFeedback()).Note);
    });

    [Fact]
    public void A_row_menu_keeps_this_didnt_help_for_its_item_and_opens_the_line() => Ui.Run(ui =>
    {
        var form = Add(ui, "Fill in the form");
        var view = new TodayView();
        view.Connect(ui.Database, () => { }, _ => { }, _ => { }, () => { });
        ui.Host(view);
        view.Refresh(null);
        ui.Pump();
        view.UpdateLayout();
        var row = UiTest.Descendants<CheckBox>(view).First(box => box.DataContext is { } data && data.GetType().Name == "ItemRow" && UiTest.Prop<string>(data, "Title") == "Fill in the form");

        ui.Choose(ui.RightClick(row)!, TodayView.FeedbackMenuLabel);

        var kept = Assert.Single(ui.Database.GetFeedback());
        Assert.Equal((Feedback.Row, ItemRef.Format(form), "Fill in the form"), (kept.Feature, kept.ItemRef, kept.ItemTitle));
        var prompt = UiTest.Named<FeedbackPrompt>(view, "RowFeedback");
        Assert.Contains(UiTest.Descendants<TextBlock>(prompt), block => block.Text.Contains("Fill in the form"));
    });

    [Fact]
    public void A_reminder_card_has_this_didnt_help_only_when_it_can_keep_it() => Ui.Run(ui =>
    {
        var item = new StoredItem(1, ItemKind.Task, "Write the essay", null, null, false);
        var kept = new List<string>();
        var plain = new ReminderWindow(new DueReminder(1, item, null, null), "Done", 10, () => { }, () => { });
        Assert.Equal(Visibility.Collapsed, UiTest.Named<FeedbackPrompt>(plain, "FeedbackLine").Visibility);

        var card = new ReminderWindow(new DueReminder(1, item, null, null), "Done", 10, () => { }, () => { })
        {
            FeedbackKept = () =>
            {
                kept.Add("kept");
                return 7;
            },
        };
        var prompt = UiTest.Named<FeedbackPrompt>(card, "FeedbackLine");
        Assert.Equal(Visibility.Visible, prompt.Visibility);
        var link = UiTest.Descendants<Button>(prompt).First(button => button.Content as string == CardLabels.DidntHelp);
        Click(link);
        Assert.Equal(["kept"], kept);
    });
}
