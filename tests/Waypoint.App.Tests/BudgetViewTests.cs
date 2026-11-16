using System.Collections;
using System.Windows;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Budget;
using Waypoint.Core.Remote;

namespace Waypoint.App.Tests;

public sealed class BudgetViewTests
{
    private static readonly string[] Pages = ["OverviewPage", "BalancesPage", "PurchasesPage", "PaydayPage", "ReviewPage", "WaitPage", "SummaryPage"];

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private static (BudgetView View, NestedColumn Column, ListBox Entries, TextBox Search) Make(UiTest ui)
    {
        var view = new BudgetView();
        ui.Host(view);
        view.Connect(new LocalBudgetData(ui.Database), () => { });
        ui.Pump();
        var column = UiTest.Named<NestedColumn>(view, "Column");
        return (view, column, UiTest.Named<ListBox>(column, "Entries"), UiTest.Named<TextBox>(column, "SearchBox"));
    }

    private static string ShownPage(BudgetView view) =>
        Assert.Single(Pages, name => UiTest.Named<FrameworkElement>(view, name).Visibility == Visibility.Visible);

    private static List<string> Purchases(BudgetView view) =>
        ((IEnumerable)UiTest.Named<ItemsControl>(view, "Categories").ItemsSource)
            .Cast<object>()
            .SelectMany(category => UiTest.Prop<IEnumerable>(category, "Rows")!.Cast<object>())
            .Select(row => UiTest.Prop<string>(row, "Title")!)
            .ToList();

    [Theory]
    [InlineData("12.345", 1235L)]
    [InlineData("0.125", 13L)]
    [InlineData("$1,200.50", 120050L)]
    [InlineData("12", 1200L)]
    public void Typed_amounts_round_half_cents_up_like_money_does(string typed, long cents) =>
        Assert.Equal(cents, BudgetFormWindow.ParseCents(typed));

    [Fact]
    public void The_expense_date_has_today_and_yesterday_one_click_away() => Ui.Run(ui =>
    {
        var form = new BudgetFormWindow(null!, "New expense", "Add", Today);
        form.AddDate("date", "Date", Today, quick: true);
        var yesterday = UiTest.Descendants<Button>((DependencyObject)form.Content).Single(button => Equals(button.Content, "Yesterday"));

        yesterday.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

        Assert.Equal(Today.AddDays(-1), form.Date("date"));
        form.Close();
    });

    // Set only while a test wants the forms it opens answered, so other tests' forms are left alone.
    private static Action<BudgetFormWindow>? _formShown;

    private static readonly Lazy<bool> FormsWatched = new(() =>
    {
        EventManager.RegisterClassHandler(typeof(BudgetFormWindow), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => _formShown?.Invoke((BudgetFormWindow)sender)));
        return true;
    });

    [Fact]
    public void Opening_the_pay_schedule_from_setup_shows_the_same_form_as_plus_with_the_saved_schedule() => Ui.Run(ui =>
    {
        _ = FormsWatched.Value;
        ui.Database.SetPaySchedule("biweekly", [], 85000, DateTime.Now.AddDays(10));
        var shown = new List<(string Title, string Amount)>();
        _formShown = form =>
        {
            var amount = UiTest.Descendants<TextBox>(form).Select(box => box.Text).FirstOrDefault(text => text == "850.00") ?? "";
            shown.Add((form.Title, amount));
            form.Close();
        };

        try
        {
            var (view, column, _, _) = Make(ui);
            view.OpenPaySchedule();
            UiTest.Call(view, "OnAddChosen", "pay");
        }
        finally
        {
            _formShown = null;
        }

        Assert.Equal([("Pay schedule", "850.00"), ("Pay schedule", "850.00")], shown);
    });

    [Fact]
    public void Overview_says_plainly_when_a_balance_was_last_set_over_a_week_ago() => Ui.Run(ui =>
    {
        ui.Database.SaveBudgetAccount(new BudgetAccount("Northwind Bank checking", AccountKind.Checking, 50000, Today.AddDays(-12)));

        var (view, _, _, _) = Make(ui);

        var stale = UiTest.Named<TextBlock>(view, "StaleNote");
        Assert.Equal("Checking last set 12 days ago", stale.Text);
        Assert.Equal(Visibility.Visible, stale.Visibility);

        ui.Database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.SetBalance, "checking", 40000, null), Today);
        view.Refresh();
        ui.Pump();
        Assert.Equal(Visibility.Collapsed, stale.Visibility);
    });

    [Fact]
    public void A_new_expense_picks_its_category_automatically_unless_one_is_chosen() => Ui.Run(ui =>
    {
        _ = FormsWatched.Value;
        ui.Database.SaveCategory(new BudgetCategory("Buffer", 12500));
        ui.Database.SaveCategory(new BudgetCategory("Groceries", 22500));
        ui.Database.SaveMerchantRule("trader joe", "Groceries");
        string? picked = null;
        _formShown = form =>
        {
            picked = (UiTest.Descendants<ListBox>(form).Single().SelectedItem as FormChoice)?.Label;
            var boxes = UiTest.Descendants<TextBox>(form).ToList();
            boxes[0].Text = "Trader Joes";
            boxes[1].Text = "42";
            UiTest.Call(form, "OnDoneClicked", form, new RoutedEventArgs());
        };

        try
        {
            var (view, _, _, _) = Make(ui);
            UiTest.Call(view, "OnAddChosen", "expense");
        }
        finally
        {
            _formShown = null;
        }

        Assert.Equal("Pick automatically", picked);
        Assert.Equal("Groceries", ui.Database.GetSpending(Today, Today).Single().Category);
    });

    [Fact]
    public void The_toolbar_keeps_two_buttons_and_the_rest_are_under_plus() => Ui.Run(ui =>
    {
        var (view, column, _, _) = Make(ui);

        var toolbar = UiTest.Descendants<Button>(view).Where(button => button.Style == view.TryFindResource("PageAction")).Select(button => button.Content as string).ToList();
        Assert.Equal(["Can I afford this?", "Import card or bank CSV..."], toolbar);

        var labels = column.AddChoices.Select(choice => choice.Label).ToList();
        Assert.Contains("Set a balance", labels);
        Assert.Single(labels, label => label == "Pay schedule");
    });

    [Fact]
    public void Picking_each_column_entry_shows_its_page() => Ui.Run(ui =>
    {
        var (view, column, entries, _) = Make(ui);
        var title = UiTest.Named<TextBlock>(view, "PageTitle");
        var switcher = UiTest.Named<FrameworkElement>(view, "MonthSwitcher");

        Assert.Equal("OverviewPage", ShownPage(view));
        Assert.Equal("Budget", title.Text);

        var expected = new (string Key, string Page, string Title, bool Month)[]
        {
            ("balances", "BalancesPage", "Balances", true),
            ("purchases", "PurchasesPage", "Purchases", true),
            ("payday", "PaydayPage", "Until payday", false),
            ("review", "ReviewPage", "Spending review", false),
            ("waiting", "WaitPage", "Waiting purchases", false),
            ("summary", "SummaryPage", "Monthly summary", true),
            ("overview", "OverviewPage", "Budget", false),
        };
        foreach (var (key, page, text, month) in expected)
        {
            entries.SelectedItem = entries.Items.Cast<ColumnEntry>().Single(entry => entry.Key == key);
            ui.Pump();

            Assert.Equal(page, ShownPage(view));
            Assert.Equal(text, title.Text);
            Assert.Equal(month ? Visibility.Visible : Visibility.Collapsed, switcher.Visibility);
            Assert.Equal(key, column.SelectedKey);
            Assert.Equal(key, ui.Database.GetPreference("budget_page"));
        }
    });

    [Theory]
    [InlineData("payday", "Until payday")]
    [InlineData("review", "Spending review")]
    public void A_page_doesnt_repeat_its_title_as_its_first_heading(string key, string title) => Ui.Run(ui =>
    {
        var lastMonth = Today.AddDays(-Today.Day);
        ui.Database.SaveBudgetAccount(new BudgetAccount("Checking", AccountKind.Checking, 80000, Today));
        ui.Database.SetPaySchedule("biweekly", ["Thursday"], 85000, DateTime.Now.AddDays(10));
        ui.Database.SetBill("Internet", 5500, "monthly", ["28"], null, null, Today);
        ui.Database.SaveCategory(new BudgetCategory("Groceries", 10000));
        ui.Database.SaveBudgetAccount(new BudgetAccount("Contoso Card", AccountKind.Card, 30000, Today, 250000, 22, 17));
        // A month needs ten purchases before the review compares it.
        ui.Database.ImportTransactions("Contoso Card", Enumerable.Range(0, 10).Select(day => new ImportedTransaction(lastMonth.AddDays(-day), "MEGAMART", 3000, $"c{day}")).ToList());
        var (view, _, entries, _) = Make(ui);

        entries.SelectedItem = entries.Items.Cast<ColumnEntry>().Single(entry => entry.Key == key);
        ui.Pump();

        var page = UiTest.Named<FrameworkElement>(view, key == "payday" ? "PaydayPage" : "ReviewPage");
        var headings = UiTest.Descendants<TextBlock>(page).Where(text => text.IsVisible && text.Style == (Style)view.FindResource("Heading")).Select(text => text.Text).ToList();
        Assert.NotEmpty(headings);
        Assert.DoesNotContain(title, headings);
    });

    [Fact]
    public void Searching_filters_purchases_and_opens_the_purchases_page() => Ui.Run(ui =>
    {
        ui.Database.SaveCategory(new BudgetCategory("Groceries", 22500));
        ui.Database.SaveCategory(new BudgetCategory("Fun", 5000));
        ui.Database.AddExpense("Trader Joes", 4200, "Groceries", Today, DateTime.Now);
        ui.Database.AddExpense("Movie tickets", 1800, "Fun", Today, DateTime.Now);
        ui.Database.AddExpense("Bowling", 2500, "Fun", Today, DateTime.Now);
        var (view, column, _, search) = Make(ui);
        Assert.Equal("OverviewPage", ShownPage(view));

        ui.Focus(search);
        search.Text = "movie";
        ui.Pump();

        Assert.Equal("PurchasesPage", ShownPage(view));
        Assert.Equal("purchases", column.SelectedKey);
        Assert.Equal(["Movie tickets"], Purchases(view));
        Assert.True(search.IsKeyboardFocused);

        // A category name matches all of its purchases.
        search.Text = "fun";
        ui.Pump();
        Assert.Equal(["Bowling", "Movie tickets"], Purchases(view).Order());

        search.Text = "nothing like this";
        ui.Pump();
        Assert.Empty(Purchases(view));
        var none = UiTest.Named<TextBlock>(view, "NoPurchases");
        Assert.Equal(Visibility.Visible, none.Visibility);
        Assert.Contains("nothing like this", none.Text);

        // A refresh keeps the search and its results while typing.
        search.Text = "trader";
        ui.Pump();
        view.Refresh();
        ui.Pump();
        Assert.Equal("trader", search.Text);
        Assert.Equal(["Trader Joes"], Purchases(view));

        search.Text = "";
        ui.Pump();
        Assert.Equal(3, Purchases(view).Count);
    });

    [Fact]
    public void A_savings_goal_shows_on_the_overview() => Ui.Run(ui =>
    {
        ui.Database.SaveBudgetAccount(new BudgetAccount("Cushion", AccountKind.Cushion, 10000, Today));
        ui.Database.SetSavingsGoal(50000, null, Today);
        ui.Database.SaveBudgetAccount(new BudgetAccount("Cushion", AccountKind.Cushion, 22500, Today));
        var (view, _, _, _) = Make(ui);

        Assert.Equal(Visibility.Visible, UiTest.Named<FrameworkElement>(view, "GoalPanel").Visibility);
        Assert.Equal("$125.00 of $500.00 saved", UiTest.Named<TextBlock>(view, "GoalSaved").Text);
        Assert.Equal(0.25, UiTest.Named<ProgressBar>(view, "GoalBar").Value, 3);
        Assert.Contains("$375.00 to go", UiTest.Named<TextBlock>(view, "GoalNote").Text);

        ui.Database.ClearSavingsGoal();
        view.Refresh();
        ui.Pump();
        Assert.Equal(Visibility.Collapsed, UiTest.Named<FrameworkElement>(view, "GoalPanel").Visibility);
    });

    [Fact]
    public void Balances_show_the_net_worth_trend_and_the_summary_page_shows_the_month() => Ui.Run(ui =>
    {
        ui.Database.SaveBudgetAccount(new BudgetAccount("Checking", AccountKind.Checking, 40000, Today.AddDays(-3)));
        ui.Database.RecordNetWorthSnapshot(Today.AddDays(-3));
        ui.Database.SaveBudgetAccount(new BudgetAccount("Checking", AccountKind.Checking, 45000, Today));
        ui.Database.SaveCategory(new BudgetCategory("Groceries", 22500));
        ui.Database.AddExpense("Kroger", 4200, "Groceries", Today, DateTime.Now);
        var (view, _, entries, _) = Make(ui);

        // Opening the page worked out today's budget, which kept today's snapshot.
        Assert.Equal(2, ui.Database.GetNetWorthSnapshots(Today.AddDays(-30), Today).Count);
        Assert.Equal(Visibility.Visible, UiTest.Named<FrameworkElement>(view, "TrendPanel").Visibility);
        Assert.Equal(2, UiTest.Named<System.Windows.Shapes.Polyline>(view, "TrendLine").Points.Count);
        var months = ((IEnumerable)UiTest.Named<ItemsControl>(view, "TrendMonths").ItemsSource).Cast<object>().Single();
        Assert.Equal("NET WORTH BY MONTH", UiTest.Prop<string>(months, "Title"));
        Assert.Equal("Net worth by month", UiTest.Prop<string>(months, "Heading"));

        entries.SelectedItem = entries.Items.Cast<ColumnEntry>().Single(entry => entry.Key == "summary");
        ui.Pump();
        var titles = ((IEnumerable)UiTest.Named<ItemsControl>(view, "SummarySections").ItemsSource).Cast<object>().Select(section => UiTest.Prop<string>(section, "Title")).ToList();
        Assert.Contains("INCOME", titles);
        Assert.Contains("NET WORTH", titles);
        Assert.StartsWith("SPENT IN", titles[0]);
    });

    [Fact]
    public void A_new_budget_offers_ways_to_start_and_hides_net_worth_until_there_is_an_account() => Ui.Run(ui =>
    {
        var (view, _, entries, _) = Make(ui);
        Assert.Equal(Visibility.Visible, UiTest.Named<FrameworkElement>(view, "SetupPanel").Visibility);
        Assert.Equal(Visibility.Visible, UiTest.Named<FrameworkElement>(view, "SetupPay").Visibility);

        entries.SelectedItem = entries.Items.Cast<ColumnEntry>().Single(entry => entry.Key == "balances");
        ui.Pump();
        var sections = ((IEnumerable)UiTest.Named<ItemsControl>(view, "Sections").ItemsSource).Cast<object>().ToList();
        Assert.Empty(sections);
        Assert.Equal(Visibility.Visible, UiTest.Named<FrameworkElement>(view, "NoBalances").Visibility);

        // The balance form can add the first account itself.
        Assert.Equal("Added Checking at $400.00", ui.Database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.AddAccount, "Checking", 40000, "checking"), Today));
        view.Refresh();
        ui.Pump();
        var titles = ((IEnumerable)UiTest.Named<ItemsControl>(view, "Sections").ItemsSource).Cast<object>().Select(section => UiTest.Prop<string>(section, "Title")).ToList();
        Assert.Contains("NET WORTH", titles);
        Assert.Equal(Visibility.Collapsed, UiTest.Named<FrameworkElement>(view, "SetupPanel").Visibility);
    });

    [Fact]
    public void A_ready_wait_says_how_to_decide_and_can_be_reached_from_the_keyboard() => Ui.Run(ui =>
    {
        ui.Database.AddWaitItem("Headphones", 8000, DateTime.Now.AddHours(-3), 1);
        var (view, _, entries, _) = Make(ui);
        entries.SelectedItem = entries.Items.Cast<ColumnEntry>().Single(entry => entry.Key == "waiting");
        ui.Pump();

        var row = Assert.Single(((IEnumerable)UiTest.Named<ItemsControl>(view, "WaitRows").ItemsSource).Cast<object>());
        Assert.StartsWith("Ready to decide. Right-click to choose Bought it or Let it go.", UiTest.Prop<string>(row, "Details"));
        Assert.Contains(UiTest.Descendants<Grid>(UiTest.Named<ItemsControl>(view, "WaitRows")), grid => grid.Focusable && grid.DataContext == row);
    });

    [Fact]
    public void A_purchase_can_be_deleted_from_its_menu_and_undo_puts_it_back() => Ui.Run(ui =>
    {
        ui.Database.SaveCategory(new BudgetCategory("Fun", 5000));
        ui.Database.AddExpense("Movie tickets", 1800, "Fun", Today, DateTime.Now);
        var (view, _, entries, _) = Make(ui);
        entries.SelectedItem = entries.Items.Cast<ColumnEntry>().Single(entry => entry.Key == "purchases");
        ui.Pump();
        foreach (var expander in UiTest.Descendants<Expander>(view))
        {
            expander.IsExpanded = true;
        }

        view.UpdateLayout();
        var row = UiTest.Descendants<Grid>(view).First(grid => grid.DataContext?.GetType().Name == "PurchaseRow");
        var menu = ui.RightClick(row)!;
        Assert.Contains("Delete", UiTest.Labels(menu));
        ui.Choose(menu, "Delete");

        Assert.Empty(Purchases(view));
        var bar = UiTest.Named<UndoBar>(view, "Bar");
        Assert.Equal("Deleted $18.00 for Movie tickets", UiTest.Named<TextBlock>(bar, "Message").Text);
        var undo = UiTest.Named<Button>(bar, "UndoButton");
        Assert.Equal(Visibility.Visible, undo.Visibility);

        undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, undo));
        ui.Pump();
        Assert.Equal(["Movie tickets"], Purchases(view));
    });

    [Fact]
    public void Purchase_and_category_rows_open_their_menus_from_the_keyboard() => Ui.Run(ui =>
    {
        ui.Database.SaveCategory(new BudgetCategory("Fun", 5000));
        ui.Database.AddExpense("Movie tickets", 1800, "Fun", Today, DateTime.Now);
        var (view, _, entries, _) = Make(ui);
        entries.SelectedItem = entries.Items.Cast<ColumnEntry>().Single(entry => entry.Key == "purchases");
        ui.Pump();
        var expander = UiTest.Descendants<Expander>(view).First();
        expander.IsExpanded = true;
        view.UpdateLayout();

        var row = UiTest.Descendants<Grid>(view).First(grid => grid.DataContext?.GetType().Name == "PurchaseRow");
        Assert.True(row.Focusable);
        Assert.Contains("Movie tickets", System.Windows.Automation.AutomationProperties.GetName(row));
        ui.Focus(row);
        ui.Close(ui.RightClick(row)!);

        // The menu key on the category's header, which is what takes focus there.
        var header = UiTest.Descendants<System.Windows.Controls.Primitives.ToggleButton>(expander).First();
        var menu = ui.RightClick(header)!;
        Assert.Contains("Rename", UiTest.Labels(menu));
        ui.Close(menu);
    });

    [Fact]
    public void Credit_scores_show_on_balances() => Ui.Run(ui =>
    {
        ui.Database.AddCreditScore("720", "ScoreCheck", null, null, DateTime.Now);
        var (view, _, _, _) = Make(ui);

        var section = ((IEnumerable)UiTest.Named<ItemsControl>(view, "CreditScores").ItemsSource).Cast<object>().Single();
        Assert.Equal("CREDIT SCORE NOTES", UiTest.Prop<string>(section, "Title"));
        var row = UiTest.Prop<IEnumerable>(section, "Rows")!.Cast<object>().Single();
        Assert.Equal("720", UiTest.Prop<string>(row, "Value"));
    });
}
