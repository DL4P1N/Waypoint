using System.Windows;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.App.Tests;

/// <summary>The Ask column in the main window: replies, their links, and the status line above the pages.</summary>
public sealed class AskColumnTests
{
    private static StoredItem Item(long id, ItemKind kind, string title, DateOnly? due = null) =>
        new(id, kind, title, due, null, false);

    [Fact]
    public void Every_reply_line_gets_a_link_with_one_each_for_shopping_and_budget()
    {
        var made = new[]
        {
            Item(1, ItemKind.Task, "Call the bank"),
            Item(2, ItemKind.Shopping, "milk"),
            Item(3, ItemKind.Shopping, "eggs"),
            Item(4, ItemKind.Expense, "Chipotle"),
            Item(5, ItemKind.Note, "Gift ideas"),
        };

        var labels = ItemsWindow.ReplyLinks(made).Select(ItemsWindow.LinkLabel).ToList();

        Assert.Equal(["Show Call the bank on To do", "Show the Shopping list", "Show Purchases in Budget", "Open Gift ideas in Notes"], labels);
    }

    [Fact]
    public void An_example_fills_the_box_and_comes_back_under_things_to_try() => Ui.Run(ui =>
    {
        var sent = new List<string>();
        var window = new ItemsWindow(ui.Database, sent.Add, () => { }, _ => { });
        var examples = UiTest.Named<ItemsControl>(window, "Examples");
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Show();
        ui.Pump();

        var first = UiTest.Descendants<Button>(examples).First();
        first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, first));

        var input = UiTest.Named<TextBox>(window, "Input");
        Assert.Equal("What do I do now?", input.Text);
        Assert.Empty(sent);

        input.Clear();
        window.ShowPrompt("hello");
        Assert.Equal(Visibility.Collapsed, UiTest.Named<StackPanel>(window, "ChatHint").Visibility);
        Assert.Equal(Visibility.Visible, UiTest.Named<Button>(window, "ThingsToTry").Visibility);
        input.Text = "typing";
        Assert.Equal(Visibility.Collapsed, UiTest.Named<Button>(window, "ThingsToTry").Visibility);
        window.Hide();
    });

    [Fact]
    public void A_reply_can_be_selected_and_copied_and_is_read_out_as_it_comes() => Ui.Run(ui =>
    {
        var window = new ItemsWindow(ui.Database, _ => { }, () => { }, _ => { })
        {
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
        };
        window.Show();
        ui.Pump();

        window.ShowReply("Added Call the bank for Friday.");
        ui.Pump();

        var words = UiTest.Descendants<TextBox>(UiTest.Named<ItemsControl>(window, "Chat")).Single(box => box.Name == "RowText");
        Assert.Equal("Added Call the bank for Friday.", words.Text);
        Assert.True(words.IsReadOnly);
        Assert.False(words.IsTabStop);
        words.SelectAll();
        Assert.Equal("Added Call the bank for Friday.", words.SelectedText);
        Assert.Equal(System.Windows.Automation.AutomationLiveSetting.Polite, System.Windows.Automation.AutomationProperties.GetLiveSetting(words));
        Assert.Equal("Added Call the bank for Friday.", System.Windows.Automation.AutomationProperties.GetName(words));

        // The wheel over a reply still scrolls the chat, since the reply has no scroller of its own.
        Assert.Empty(UiTest.Descendants<ScrollViewer>(words));
        window.Hide();
    });

    [Fact]
    public void A_notice_from_Waypoint_leaves_the_sorting_line_for_its_own_reply() => Ui.Run(ui =>
    {
        var window = new ItemsWindow(ui.Database, _ => { }, () => { }, _ => { })
        {
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
        };
        window.Show();
        ui.Pump();
        var chat = UiTest.Named<ItemsControl>(window, "Chat");

        window.ShowPrompt("add milk");
        window.ShowNotice("Something unexpected went wrong. Waypoint kept going, and the detail is in errors.log.");
        ui.Pump();
        Assert.Contains(ItemsWindow.WorkingText, chat.Items.Cast<object>().Select(row => row.ToString()));

        window.ShowReply("Added milk to Shopping.");
        ui.Pump();
        Assert.DoesNotContain(ItemsWindow.WorkingText, chat.Items.Cast<object>().Select(row => row.ToString()));
        window.Hide();
    });
}

public sealed class AskWorkingTests
{
    private static IReadOnlyList<string> Rows(ItemsWindow window) =>
        UiTest.Named<ItemsControl>(window, "Chat").Items.Cast<object>().Select(row => row.ToString()!).ToList();

    [Fact]
    public void Sorting_shows_under_a_message_until_its_reply_comes() => Ui.Run(ui =>
    {
        var window = new ItemsWindow(ui.Database, _ => { }, () => { }, _ => { });

        window.ShowPrompt("milk");
        window.Refresh(1, null, null, null, null, null);
        Assert.Equal(["milk", ItemsWindow.WorkingText], Rows(window));
        Assert.Equal(Visibility.Collapsed, UiTest.Named<Border>(window, "SortNotice").Visibility);

        window.ShowReply("Added milk to Shopping", []);
        Assert.Equal(["milk", "Added milk to Shopping"], Rows(window));
    });

    [Fact]
    public void A_card_button_shows_as_the_choice_made_not_as_typing() => Ui.Run(ui =>
    {
        ItemsWindow window = null!;
        var sent = new List<string>();
        window = new ItemsWindow(ui.Database, text =>
        {
            sent.Add(text);
            window.ShowPrompt(text);
        }, () => { }, _ => { });
        var didIt = UiTest.Descendants<Button>(UiTest.Named<Border>(window, "SessionCard")).First(button => (string)button.Content == "Did it");

        didIt.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, didIt));

        Assert.Equal(["did it"], sent);
        Assert.Equal(["You chose Did it", ItemsWindow.WorkingText], Rows(window));
    });

    [Fact]
    public void Make_it_smaller_says_it_is_working_on_the_card_and_in_Ask_while_it_waits() => Ui.Run(ui =>
    {
        var paper = ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, "Paper", DateOnly.FromDateTime(DateTime.Now)), DateTime.Now).Item!;
        ui.Database.StartActivation(ItemRef.Format(paper), "Open the doc", null, Activation.TypedMethod, DateTime.Now);
        ItemsWindow window = null!;
        window = new ItemsWindow(ui.Database, text => window.ShowPrompt(text), () => { }, _ => { });
        window.Refresh(0, null, null, null, null, null);
        var smaller = UiTest.Descendants<Button>(UiTest.Named<Border>(window, "SessionCard")).First(button => (string)button.Content == "Make it smaller");

        smaller.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, smaller));

        Assert.Equal(["You chose Make it smaller", Activation.SmallerWorking], Rows(window));
        Assert.Equal(Activation.SmallerWorking, UiTest.Named<TextBlock>(window, "SessionAction").Text);
        Assert.NotNull(smaller.ToolTip);
    });

    [Fact]
    public void Break_down_says_it_is_working_on_smaller_steps_while_it_waits() => Ui.Run(ui =>
    {
        var window = new ItemsWindow(ui.Database, _ => { }, () => { }, _ => { });

        window.ShowPrompt("break down Paper");

        Assert.Equal(["break down Paper", Activation.BreakDownWorking], Rows(window));
    });

    [Fact]
    public void The_session_card_has_one_way_to_put_it_off() => Ui.Run(ui =>
    {
        var window = new ItemsWindow(ui.Database, _ => { }, () => { }, _ => { });

        var labels = UiTest.Descendants<Button>(UiTest.Named<Border>(window, "SessionCard")).Select(button => button.Content as string).ToList();

        Assert.Contains("Pause", labels);
        Assert.DoesNotContain("Not now", labels);
        Assert.DoesNotContain("Later...", labels);
    });

    [Fact]
    public void A_failed_sort_takes_sorting_away() => Ui.Run(ui =>
    {
        var window = new ItemsWindow(ui.Database, _ => { }, () => { }, _ => { });

        window.ShowPrompt("milk");
        window.Refresh(1, "offline", null, null, null, null);

        Assert.Equal(["milk"], Rows(window));
        Assert.Equal(Visibility.Visible, UiTest.Named<Border>(window, "SortNotice").Visibility);
    });
}

public sealed class SortStatusTests
{
    [Fact]
    public void A_failure_is_said_plainly_without_the_error()
    {
        Assert.Equal("Sorting failed for now. Your message is saved and will be sorted once Claude can be reached.", SortStatus.Notice(1, failed: true));
        Assert.Equal("Sorting failed for now. Your 2 messages are saved and will be sorted once Claude can be reached.", SortStatus.Notice(2, failed: true));
        Assert.Equal("Waypoint: sorting failed, 2 messages saved", SortStatus.TrayTip(2, failed: true));
    }

    [Fact]
    public void Waiting_captures_are_messages_not_notes()
    {
        Assert.Equal("1 message waiting for a response", SortStatus.Notice(1, failed: false));
        Assert.Equal("Waypoint: 3 messages waiting for a response", SortStatus.TrayTip(3, failed: false));
        Assert.Null(SortStatus.Notice(0, failed: false));
        Assert.Null(SortStatus.TrayTip(0, failed: false));
    }

    [Fact]
    public void The_status_line_says_problems_plainly() => Ui.Run(ui =>
    {
        var window = new ItemsWindow(ui.Database, _ => { }, () => { }, _ => { });

        window.Refresh(0, null, "Response status code does not indicate success: 404 (Not Found).", "Claude Code exited with code 1.", "401 Unauthorized", null);

        var status = UiTest.Named<TextBlock>(window, "Status").Text;
        Assert.Contains("Couldn't update the calendar", status);
        Assert.DoesNotContain("404", status);
        Assert.DoesNotContain("Claude Code", status);
        Assert.DoesNotContain("401", status);
    });

    [Fact]
    public void The_sorting_notice_shows_once_in_ask_and_can_be_closed() => Ui.Run(ui =>
    {
        var window = new ItemsWindow(ui.Database, _ => { }, () => { }, _ => { });
        var retried = 0;
        window.RetrySorting = () => retried++;
        const string Error = @"An error occurred trying to start process 'C:\Users\me\claude.exe'";

        window.Refresh(1, Error, null, null, null, null);

        var notice = UiTest.Named<Border>(window, "SortNotice");
        var text = UiTest.Named<TextBlock>(window, "SortNoticeText").Text;
        Assert.Equal(Visibility.Visible, notice.Visibility);
        Assert.DoesNotContain("claude.exe", text);
        Assert.StartsWith("Sorting failed", text);
        Assert.Equal(Visibility.Collapsed, UiTest.Named<Border>(window, "StatusBar").Visibility);

        var close = UiTest.Named<Button>(window, "SortNoticeClose");
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, close));
        window.Refresh(1, Error, null, null, null, null);
        Assert.Equal(Visibility.Collapsed, notice.Visibility);

        // Another message waiting is news, so it shows again, with Try again.
        window.Refresh(2, Error, null, null, null, null);
        Assert.Equal(Visibility.Visible, notice.Visibility);
        var retry = UiTest.Named<Button>(window, "SortRetry");
        retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, retry));
        Assert.Equal(1, retried);
        window.Close();
    });
}
