using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Waypoint.Core;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Email;
using Waypoint.Core.Remote;
using Waypoint.Core.Theme;

namespace Waypoint.App;

/// <summary>
/// The budget, one page per column entry. Figures change by typing, email, a CSV import, or the forms and
/// right-click menus here, which call the same Core methods typing does and never the sorter.
/// </summary>
public partial class BudgetView : UserControl
{
    private const string PageKey = "budget_page";
    private const string OverviewKey = "overview";
    private const string BalancesKey = "balances";
    private const string PurchasesKey = "purchases";
    private const string PaydayKey = "payday";
    private const string ReviewKey = "review";
    private const string PaydayTitle = "Until payday";
    private const string ReviewTitle = "Spending review";
    private const string WaitingKey = "waiting";
    private const string SummaryKey = "summary";

    private const string NewExpense = "expense";
    private const string PickAutomatically = "Pick automatically";
    private const string NewIncome = "income";
    private const string NewWait = "wait";
    private const string NewCategory = "category";
    private const string NewPayDate = "pay";
    private const string NewBill = "bill";
    private const string NewGoal = "goal";
    private const string NewScore = "score";
    private const string NewBalance = "balance";
    private const string NewCap = "cap";

    private const int MaxAttention = 3;

    private static readonly ColumnAddChoice[] AddChoices =
    [
        new(NewExpense, "Expense"),
        new(NewIncome, "Income"),
        new(NewWait, "Waiting purchase"),
        new(NewBalance, "Set a balance"),
        new(NewCategory, "Category"),
        new(NewCap, "Set a budget"),
        new(NewPayDate, "Pay schedule"),
        new(NewBill, "Bill"),
        new(NewGoal, "Savings goal"),
        new(NewScore, "Credit score"),
    ];

    // Sections the report builds that belong on a page other than Balances.
    private static readonly string[] NotBalances = ["THIS WEEK", "THIS MONTH", "UNTIL PAYDAY", "WAIT LIST", "COMING UP", "OTHER INCOME", "TRANSFERS"];
    private static readonly string[] PaydaySectionTitles = ["UNTIL PAYDAY", "COMING UP", "TRANSFERS", "OTHER INCOME"];

    private readonly RefreshGate _gate = new();
    private IBudgetData _database = null!;
    private Action _import = null!;
    private DateOnly _month = FirstOfMonth(DateOnly.FromDateTime(DateTime.Now));
    private BudgetPage _shown = BudgetPage.Empty;
    private bool _menuOpen;

    public BudgetView() => InitializeComponent();

    private PageWork _work = PageWork.Local;

    /// <summary>How the page reads and changes its data: straight away on this PC, or in order without holding the window in server mode.</summary>
    internal PageWork Work
    {
        get => _work;
        set
        {
            _work = value;
            Column.Work = value;
        }
    }

    /// <summary>Draws the page again, at once from what was kept in server mode, then from what's read.</summary>
    private void ShowSoon() => Work.Show(() => Refresh(force: true), () => Refresh(force: false));

    /// <summary>
    /// <paramref name="database"/> is this PC's database or the server. Forms open with what the page last read, so
    /// while the server is away they still open, and saving says it has to wait.
    /// </summary>
    public void Connect(IBudgetData database, Action import)
    {
        _database = database;
        _import = import;

        Column.Connect(database, PageKey);
        Column.AddChoices = AddChoices;
        Column.QuickAdd = AddChoices[0];
        Column.SelectionChanged += _ => ShowPage();
        Column.SearchChanged += OnSearchChanged;
        Column.AddChosen += OnAddChosen;
        Scroll.LostKeyboardFocus += (_, _) => Work.Show(Refresh);
        Work.Show(Refresh);
    }

    public void Refresh() => Refresh(force: false);

    /// <summary>For Find anywhere: Purchases in the purchase's month, searched for its merchant so it's in view.</summary>
    public void RevealPurchase(SpendingRow row)
    {
        _month = FirstOfMonth(row.Date);
        Column.Select(PurchasesKey);
        Column.SetSearch(MerchantName.Clean(row.Title));
        ShowSoon();
    }

    /// <summary>
    /// The pay schedule form, the same one + then Pay schedule opens, for the setup card's pay line. It starts on
    /// the schedule already saved, so the page is read first if it hasn't been yet.
    /// </summary>
    public void OpenPaySchedule()
    {
        if (ReferenceEquals(_shown, BudgetPage.Empty))
        {
            Work.Peek(() => Refresh(force: true));
        }

        SetPaySchedule();
    }

    /// <summary>Opens Purchases on this month, for a reply's link to a purchase it added.</summary>
    public void ShowPurchases()
    {
        _month = FirstOfMonth(Today);
        Column.Select(PurchasesKey);
        ShowSoon();
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private static DateOnly FirstOfMonth(DateOnly day) => new(day.Year, day.Month, 1);

    private string Page => Column.SelectedKey ?? OverviewKey;

    private Window OwnerWindow => Window.GetWindow(this);

    /// <summary>
    /// Only redraws when a figure, the month, or the search changed, and not while a menu is open or
    /// someone is working in the page, so open sections stay open and focus stays put.
    /// </summary>
    private void Refresh(bool force)
    {
        if (_database is null)
        {
            return;
        }

        var today = Today;
        var page = _database.GetPage(_month, today);
        _shown = page;
        ShowEntries(page.WaitList.Count, force);

        var search = Column.SearchText.Trim();

        // Everything the page shows is in what was read, so it alone says whether anything changed.
        var signature = string.Join('|', JsonSerializer.Serialize(page, RemoteJson.Options), _month.ToString("O", CultureInfo.InvariantCulture), search, today.ToString("O", CultureInfo.InvariantCulture));
        var busy = !force && (_menuOpen || Scroll.IsKeyboardFocusWithin);
        if (!_gate.ShouldRebuild(signature, busy))
        {
            ShowPage();
            return;
        }

        var repayments = page.Repayments.Select(question => new RepaymentCard(question.Repayment, question.Candidates)).ToList();
        ShowOverview(today, page.ThisMonth, page.Report, page.Tip, page.SafeToSpend, repayments);
        ShowGoal(page.Goal, today);
        ShowBalances(today, page.Report, page.CardsAtMonthEnd);
        ShowTrend(page.Trend, page.TrendMonths, today);
        ShowScores(page.CreditScores, today);
        ShowSummary(page.Summary);
        ShowPurchases(page.Month, search);
        ShowPayday(page.Report, page.PayLine);
        ShowReview(page.Review, page.Month, page.Outcomes ?? new ReportSection("", []));
        ShowWaiting(page.WaitList);
        ShowPage();
    }

    private IReadOnlyList<BudgetCategory> BudgetCategories => _shown.Categories;

    private IReadOnlyList<BudgetAccount> Cards => _shown.Accounts.Where(account => account.Kind == AccountKind.Card).ToList();

    /// <summary>The column's entries are fixed. Waiting purchases carries a count so it's seen from any page.</summary>
    private void ShowEntries(int waiting, bool force) => Column.SetEntries(
        [
            new ColumnEntry(OverviewKey, "Overview"),
            new ColumnEntry(BalancesKey, "Balances"),
            new ColumnEntry(PurchasesKey, "Purchases"),
            new ColumnEntry(PaydayKey, "Until payday"),
            new ColumnEntry(ReviewKey, "Spending review"),
            new ColumnEntry(SummaryKey, "Monthly summary"),
            new ColumnEntry(WaitingKey, waiting > 0 ? $"Waiting purchases  ·  {waiting}" : "Waiting purchases", Name: "Waiting purchases"),
        ],
        force);

    private void ShowPage()
    {
        var page = Page;
        OverviewPage.Visibility = Shown(page == OverviewKey);
        BalancesPage.Visibility = Shown(page == BalancesKey);
        PurchasesPage.Visibility = Shown(page == PurchasesKey);
        PaydayPage.Visibility = Shown(page == PaydayKey);
        ReviewPage.Visibility = Shown(page == ReviewKey);
        WaitPage.Visibility = Shown(page == WaitingKey);
        SummaryPage.Visibility = Shown(page == SummaryKey);

        PageTitle.Text = page switch
        {
            BalancesKey => "Balances",
            PurchasesKey => "Purchases",
            PaydayKey => PaydayTitle,
            ReviewKey => ReviewTitle,
            WaitingKey => "Waiting purchases",
            SummaryKey => "Monthly summary",
            _ => "Budget",
        };

        // One switcher, shown only where the month changes what's on the page.
        MonthSwitcher.Visibility = Shown(page is BalancesKey or PurchasesKey or SummaryKey);
        MonthLabel.Text = _month.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        NextMonth.Visibility = _month < FirstOfMonth(Today) ? Visibility.Visible : Visibility.Hidden;
    }

    private static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    private void ShowOverview(DateOnly today, IReadOnlyList<CategoryDetail> thisMonth, IReadOnlyList<ReportSection> report, string? tip, long? safe, IReadOnlyList<RepaymentCard> repayments)
    {
        Hero.Visibility = Shown(safe is not null);
        SafeToSpend.Text = safe is { } cents ? Briefing.Money(cents) : "";

        // A bare $0.00 says nothing about why, so the reason goes right under it.
        SafeReason.Text = _shown.SafeToSpendReason ?? "";
        SafeReason.Visibility = Shown(_shown.SafeToSpendReason is not null);
        HeroNote.Text = $"{Briefing.Money(BudgetReport.LeftThisWeek(thisMonth))} left this week  ·  {Briefing.Money(BudgetReport.LeftThisMonth(thisMonth))} left this month";

        // A balance not set in over a week makes every figure from it a guess, so it's said once, plainly, and nothing more.
        StaleNote.Text = _shown.StaleBalances ?? "";
        StaleNote.Visibility = Shown(_shown.StaleBalances is not null);

        Repayments.ItemsSource = repayments;
        Repayments.Visibility = Shown(repayments.Count > 0);

        var attention = BudgetReport.Attention(report).Select(BudgetReport.ShortAttention).Take(MaxAttention).ToList();
        AttentionPanel.Visibility = Shown(attention.Count > 0);
        Attention.ItemsSource = attention;

        var weekRows = thisMonth
            .Where(detail => detail.WeekCapCents > 0 || detail.WeekCents > 0 && detail.WeekCapCents == 0)
            .Select(detail => new CapRow(detail.Name, detail.WeekCapCents ?? 0, detail.WeekCents ?? 0))
            .ToList();
        WeekRows.ItemsSource = weekRows;
        WeekPanel.Visibility = Shown(weekRows.Count > 0);

        var monthRows = thisMonth
            .Where(detail => detail.Period == BudgetPeriod.Month && (detail.CapCents > 0 || detail.MonthCents > 0 && detail.CapCents == 0))
            .Select(detail => new CapRow(detail.Name, detail.CapCents ?? 0, detail.MonthCents))
            .ToList();
        MonthRows.ItemsSource = monthRows;
        MonthPanel.Visibility = Shown(monthRows.Count > 0);

        TipPanel.Visibility = Shown(tip is not null);
        Tip.Text = tip;

        // A new budget would otherwise be a blank page, so it offers a few ways in, each one a click away.
        var hasCaps = _shown.Categories.Any(category => category.MonthlyCents > 0);
        var hasAccounts = _shown.Accounts.Count > 0;
        SetupPanel.Visibility = Shown(!hasCaps && !hasAccounts);
        SetupPay.Visibility = Shown(_shown.PayForm is null);
    }

    /// <summary>The savings goal in brief: how much is saved toward it, by when, and the pace that keeps it on track.</summary>
    private void ShowGoal(SavingsGoalStatus? goal, DateOnly today)
    {
        GoalPanel.Visibility = Shown(goal is not null);
        if (goal is null)
        {
            return;
        }

        GoalSaved.Text = $"{Briefing.Money(Math.Max(goal.SavedCents, 0))} of {Briefing.Money(goal.Goal.TargetCents)} saved";
        GoalBy.Text = goal.Goal.By is { } by ? $"by {by.ToString("MMM d", CultureInfo.InvariantCulture)}" : "";
        GoalBar.Value = goal.Fill;
        GoalBar.Foreground = ThemeBrushes.For(ItemMark.School);
        GoalNote.Text = SavingsPlan.Pace(goal, today);
    }

    /// <summary>Past months show card balances on the month's last day, since only those are kept by month.</summary>
    private void ShowBalances(DateOnly today, IReadOnlyList<ReportSection> report, IReadOnlyList<ReportRow> cardsAtMonthEnd)
    {
        var isCurrentMonth = _month >= FirstOfMonth(today);
        var monthEnd = isCurrentMonth ? today : _month.AddMonths(1).AddDays(-1);
        var cards = cardsAtMonthEnd.Select(card => new Row(card)).ToList();
        var title = monthEnd.ToString("MMM d", CultureInfo.InvariantCulture);
        MonthCards.ItemsSource = isCurrentMonth || cards.Count == 0 ? null : new[] { new SectionRow($"Cards on {title}", cards) };
        PastNote.Visibility = Shown(!isCurrentMonth);

        var sections = report
            .Where(section => !NotBalances.Contains(section.Title) && section.Rows.Count > 0)
            .Select(section => new SectionRow(section.Title, section.Rows.Select(row => new Row(row)).ToList()))
            .ToList();
        Sections.ItemsSource = sections;
        Sections.Visibility = Shown(isCurrentMonth);
        NoBalances.Visibility = Shown(isCurrentMonth && sections.Count == 0);
    }

    /// <summary>
    /// Net worth over time: a small line of the daily snapshots, and the month-end figures below it. Only the current
    /// month shows them, the same as the rest of Balances.
    /// </summary>
    private void ShowTrend(IReadOnlyList<NetWorthSnapshot> snapshots, ReportSection months, DateOnly today)
    {
        var isCurrentMonth = _month >= FirstOfMonth(today);
        var line = NetWorthTrend.Line(snapshots);
        var points = new PointCollection();
        var flat = line.Count > 0 && line.Min() == line.Max();
        if (line.Count > 0 && !flat)
        {
            var high = line.Max();
            var span = (double)(high - line.Min());
            for (var index = 0; index < line.Count; index++)
            {
                points.Add(new Point(index, (high - line[index]) / span));
            }
        }

        TrendLine.Points = points;
        TrendLine.Visibility = Shown(!flat);
        TrendFlat.Visibility = Shown(flat);
        TrendPanel.Visibility = Shown(isCurrentMonth && line.Count > 0 && _shown.Accounts.Count > 0);
        TrendMonths.ItemsSource = isCurrentMonth && months.Rows.Count > 0
            ? new[] { new SectionRow(months.Title, months.Rows.Select(row => new Row(row)).ToList()) }
            : null;
    }

    /// <summary>The credit score log, only as typed, under the current month's balances.</summary>
    private void ShowScores(ReportSection scores, DateOnly today) =>
        CreditScores.ItemsSource = _month >= FirstOfMonth(today) && scores.Rows.Count > 0
            ? new[] { new SectionRow(scores.Title, scores.Rows.Select(row => new Row(row)).ToList()) }
            : null;

    private void ShowSummary(IReadOnlyList<ReportSection> summary) =>
        SummarySections.ItemsSource = summary
            .Where(section => section.Rows.Count > 0)
            .Select(section => new SectionRow(section.Title, section.Rows.Select(row => new Row(row)).ToList()))
            .ToList();

    /// <summary>Search narrows purchases by merchant or category, and opens every category it leaves.</summary>
    private void ShowPurchases(IReadOnlyList<CategoryDetail> details, string search)
    {
        var spent = details.Sum(detail => detail.MonthCents);
        var planned = details.Sum(detail => detail.CapCents ?? 0);
        MonthTotal.Text = $"{Briefing.Money(spent)} spent of {Briefing.Money(planned)} budgeted";

        var rows = details
            .Select(detail => new CategoryRow(detail, detail.Rows.Where(row => Matches(row, search)).ToList(), search.Length > 0))
            .Where(row => row.Rows.Count > 0)
            .ToList();
        Categories.ItemsSource = rows;

        // Purchases with no category count against no cap, so the page says how many there are and how to file them.
        var unfiled = details.Where(detail => detail.Name == BudgetMath.UncategorizedLabel).Sum(detail => detail.Rows.Count);
        NeedCategory.Text = unfiled == 1
            ? "1 purchase needs a category. Right-click it under Uncategorized, or use its ⋯ button, and choose Change category."
            : $"{unfiled} purchases need a category. Right-click one under Uncategorized, or use its ⋯ button, and choose Change category.";
        NeedCategory.Visibility = Shown(unfiled > 0 && search.Length == 0);
        NoPurchases.Text = search.Length > 0 ? $"No purchases match \"{search}\" this month." : "No purchases this month.";
        NoPurchases.Visibility = Shown(rows.Count == 0);
    }

    private static bool Matches(SpendingRow row, string search) =>
        search.Length == 0
        || row.Category.Contains(search, StringComparison.OrdinalIgnoreCase)
        || row.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
        || MerchantName.Clean(row.Title).Contains(search, StringComparison.OrdinalIgnoreCase);

    /// <summary>A reply's first part, and what follows its first blank line, which says how it was worked out.</summary>
    private static (string Headline, string Details) Split(string text)
    {
        var split = text.IndexOf("\n\n", StringComparison.Ordinal);
        return split >= 0 ? (text[..split], text[(split + 2)..].Trim()) : (text, "");
    }

    private void ShowPayday(IReadOnlyList<ReportSection> report, string payLine)
    {
        // The next pay in one line, and how it's worked out folded away under it.
        var (headline, how) = Split(payLine);
        PayLine.Text = headline;
        PayHow.Text = how;
        PayHowPanel.Visibility = Shown(how.Length > 0);
        GotPaid.Visibility = Shown(_shown.PayForm is not null);
        PaydaySections.ItemsSource = report
            .Where(section => PaydaySectionTitles.Contains(section.Title) && section.Rows.Count > 0)
            .Select(section => new SectionRow(section.Title, section.Rows.Select(row => new Row(row)).ToList()) { UnderTitle = PaydayTitle })
            .ToList();
    }

    /// <summary>The review, top merchants, and last of all what's helped, which says plainly when it can't tell yet.</summary>
    private void ShowReview(ReportSection review, IReadOnlyList<CategoryDetail> details, ReportSection outcomes)
    {
        var sections = new[] { review, BudgetReport.TopMerchants(details), SpendingOutcomes.ForPage(outcomes) }
            .Where(section => section.Rows.Count > 0)
            .Select(section => new SectionRow(section.Title, section.Rows.Select(row => new Row(row)).ToList()) { UnderTitle = ReviewTitle })
            .ToList();
        Review.ItemsSource = sections;
        NoReview.Visibility = Shown(sections.Count == 0);
    }

    private void ShowWaiting(IReadOnlyList<WaitItem> waiting)
    {
        WaitRows.ItemsSource = waiting.Select(item => new WaitRow(item, DateTime.Now)).ToList();
        NoWaits.Visibility = Shown(waiting.Count == 0);
        WaitHours.Content = $"New waits last {_shown.DefaultWaitHours} hours. Change...";
    }

    /// <summary>Searching from another page means looking for a purchase, so it goes to Purchases.</summary>
    private void OnSearchChanged(string text)
    {
        if (text.Trim().Length > 0 && Page != PurchasesKey)
        {
            Column.Select(PurchasesKey);
        }

        ShowSoon();
    }

    private void OnPreviousMonth(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(-1);
        ShowSoon();
    }

    private void OnNextMonth(object sender, RoutedEventArgs e)
    {
        if (_month < FirstOfMonth(Today))
        {
            _month = _month.AddMonths(1);
            ShowSoon();
        }
    }

    /// <summary>Shows what a change said and redraws straight away, since it came from this page.</summary>
    private void Changed(string? reply)
    {
        // An earlier message's Undo may no longer hold once something else has changed.
        Bar.Changed();
        if (!string.IsNullOrWhiteSpace(reply))
        {
            Bar.Show(reply);
        }

        Work.ShowAfterChanges(nameof(Refresh), () => Refresh(force: true), () => Refresh(force: false));
    }

    /// <summary>
    /// A change from a button or menu. In server mode with the server away, or too slow to answer, it can't be made,
    /// so the page says so and keeps showing what it last had. False if it wasn't made. In server mode it's made off
    /// the window's thread, after the page's earlier changes.
    /// </summary>
    private async Task<bool> Change(Func<string?> change, [CallerMemberName] string what = "")
    {
        string? reply;
        try
        {
            reply = await Work.Run(change, what);
        }
        catch (ServerUnavailableException)
        {
            Bar.Show(BudgetFormWindow.ServerAway);
            return false;
        }
        catch (ServerTimedOutException slow)
        {
            Bar.Show(slow.Message);
            return false;
        }

        Changed(reply);
        return true;
    }

    /// <summary>A change nothing waits on, such as from a menu. Any other problem is said the way one from a click is.</summary>
    private async void Fire(Func<string?> change, [CallerMemberName] string what = "") => await Change(change, what);

    private BudgetFormWindow Form(string heading, string button) => new(OwnerWindow, heading, button, Today) { Work = Work };

    private void Open(BudgetFormWindow form)
    {
        if (form.ShowDialog() == true)
        {
            Changed(form.Reply);
        }
    }

    private IReadOnlyList<FormChoice> CategoryChoices() => BudgetCategories.Select(category => new FormChoice(category.Name, category.Name)).ToList();

    // + menu

    private void OnAddChosen(string? choice)
    {
        switch (choice)
        {
            case NewExpense:
                AddExpense();
                break;
            case NewIncome:
                AddIncome();
                break;
            case NewWait:
                AddWait();
                break;
            case NewCategory:
                AddCategory();
                break;
            case NewPayDate:
                SetPaySchedule();
                break;
            case NewBill:
                SetBill();
                break;
            case NewGoal:
                SetGoal();
                break;
            case NewScore:
                AddCreditScore();
                break;
            case NewBalance:
                OnSetBalanceClicked(this, new RoutedEventArgs());
                break;
            case NewCap:
                SetCap(null);
                break;
        }
    }

    private void AddExpense(string title = "", long? cents = null)
    {
        var form = Form("New expense", "Add");
        form.AddText("title", "What it was, such as Target", title);
        form.AddMoney("amount", "Amount, such as 12.50", cents);
        // Left alone, the category comes from the merchant's rule the way a typed purchase's does, so a store
        // already taught as Groceries counts against that budget.
        var choices = CategoryChoices();
        if (choices.Count > 0)
        {
            choices = [new FormChoice(PickAutomatically, ""), .. choices];
            form.AddChoice("category", "Category", choices, "");
        }

        form.AddDate("date", "Date", Today, quick: true);
        form.Submit = values => _database.AddExpense(values.Text("title"), values.Cents("amount"), choices.Count > 0 ? values.Choice("category") as string : null, values.Date("date"), DateTime.Now);
        if (form.ShowDialog() != true)
        {
            return;
        }

        if (form.AddedId is not { } id)
        {
            Changed(form.Reply);
            return;
        }

        // A slip of the amount or the date is easiest to fix straight away, so the message offers to take it back.
        Bar.Show(form.Reply ?? "Added it", () => Fire(() => _database.DeletePurchase(id, DateTime.Now) is null ? "That purchase is already gone" : "Took it back off the budget"));
        Work.ShowAfterChanges(nameof(Refresh), () => Refresh(force: true), () => Refresh(force: false));
    }

    private void AddIncome()
    {
        var form = Form("New income", "Add");
        form.AddNote("For money outside your paycheck. Your paycheck goes in Pay schedule.");
        form.AddText("source", "Where it came from, such as tutoring");
        form.AddMoney("amount", "Amount, such as 120");
        form.Submit = values =>
        {
            if (values.Text("source").Length == 0)
            {
                return new OpReply("Where did it come from?", true);
            }

            return values.Cents("amount") is > 0 and var cents
                ? new OpReply(_database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.AddIncome, values.Text("source"), cents, null), Today))
                : new OpReply("How much was it?", true);
        };
        Open(form);
    }

    private void AddWait()
    {
        var form = Form("New waiting purchase", "Add");
        form.AddText("name", "What you want, such as headphones");
        form.AddMoney("price", "Price, such as 80");
        form.AddText("hours", "Hours to wait", _shown.DefaultWaitHours.ToString(CultureInfo.InvariantCulture));
        form.AddText("reason", "Why you want it, optional");
        form.Submit = values =>
        {
            if (values.Text("name").Length == 0)
            {
                return new OpReply("What is it?", true);
            }

            if (values.Cents("price") is not > 0)
            {
                return new OpReply("About how much is it?", true);
            }

            if (!int.TryParse(values.Text("hours"), NumberStyles.None, CultureInfo.InvariantCulture, out var hours) || hours <= 0)
            {
                return new OpReply("How many hours should it wait?", true);
            }

            var reason = values.Text("reason");
            return new OpReply(_database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.AddWait, values.Text("name"), values.Cents("price"), null, hours, reason.Length > 0 ? reason : null), Today));
        };
        Open(form);
    }

    /// <summary>The same as typing "I got paid": it counts the paycheck expected around today until the export shows it.</summary>
    private void OnGotPaidClicked(object sender, RoutedEventArgs e)
    {
        var form = Form("I got paid", "Count it");
        form.AddMoney("amount", "Amount, if it wasn't the usual");
        form.AddNote("It counts until the bank export shows the real deposit, so the money is only counted once.");
        form.Submit = values => _database.ConfirmPayReceived(values.Cents("amount"), Today);
        Open(form);
    }

    private void OnWaitHoursClicked(object sender, RoutedEventArgs e)
    {
        var form = Form("How long new waits last", "Save");
        form.AddText("hours", "Hours to wait", _shown.DefaultWaitHours.ToString(CultureInfo.InvariantCulture));
        form.Submit = values => _database.SetDefaultWaitHours(values.Text("hours"));
        Open(form);
    }

    /// <summary>Only a question, so nothing changes. It's asked fresh, so in server mode it needs the server.</summary>
    private async void OnPatternsClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var lines = await Work.Read(() => _database.GetSpendingPatterns(Today));
            Patterns.Text = lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "Nothing stands out yet.";
            Patterns.Visibility = Visibility.Visible;
        }
        catch (ServerUnavailableException)
        {
            Bar.Show(BudgetFormWindow.ServerAway);
        }
        catch (ServerTimedOutException slow)
        {
            Bar.Show(slow.Message);
        }
    }

    private void AddCategory()
    {
        var form = Form("New category", "Add");
        form.AddText("name", "Name, such as Fun");
        form.AddMoney("cap", "Monthly budget, optional");
        form.AddChoice("period", "Watched", [new FormChoice("By the month", BudgetPeriod.Month), new FormChoice("By the week", BudgetPeriod.Week)]);
        form.Submit = values => _database.AddCategory(values.Text("name"), values.Cents("cap"), values.Choice("period") is BudgetPeriod period ? period : BudgetPeriod.Month);
        Open(form);
    }

    /// <summary>The main paycheck's schedule, or a one-off deposit. Pay calendar files still work by dropping them on Ask.</summary>
    private void SetPaySchedule()
    {
        const string Once = "once";
        var form = Form("Pay schedule", "Save");
        form.AddNote(Split(_shown.PayLine).Headline);

        // It starts on the schedule already saved, so changing one thing doesn't mean typing the rest again.
        var current = _shown.PayForm;
        if (current?.HourlyRate is { } rate)
        {
            form.AddNote($"Paychecks are estimated from your shifts at {Briefing.Money((long)(rate * 100))} an hour. The amount below is used for a pay period with no shifts.");
        }

        if (current?.CalendarThrough is { } through)
        {
            form.AddNote($"Saving here replaces your pay calendar, which runs through {through.ToString("MMM d", CultureInfo.CurrentCulture)}.");
        }

        form.AddChoice("pattern", "How often", [
            new FormChoice("Weekly", "weekly"),
            new FormChoice("Every other week", "biweekly"),
            new FormChoice("Twice a month", "twice_monthly"),
            new FormChoice("Monthly", "monthly"),
            new FormChoice("One deposit", Once),
        ], current?.Pattern);
        string Pattern() => form.Choice("pattern") as string ?? "";
        var monthDays = current is { Pattern: "twice_monthly" } ? string.Join(" and ", current.Days) : "";
        form.AddText("days", "Days of the month, such as 1 and 15", monthDays, () => Pattern() == "twice_monthly");
        form.AddDate("next", "Next payday", current?.Next ?? Today, () => Pattern() is not "twice_monthly");
        form.AddText("name", "Where it's from, such as tax refund", visibleWhen: () => Pattern() == Once);
        form.AddMoney("amount", "Amount, such as 600", current?.Cents);
        form.Submit = values =>
        {
            var next = values.Date("next").ToDateTime(TimeOnly.MinValue);
            if (Pattern() == Once)
            {
                var name = values.Text("name");
                return _database.AddExpectedDeposit(name.Length > 0 ? name : null, values.Cents("amount"), next);
            }

            var days = Pattern() == "twice_monthly"
                ? Regex.Matches(values.Text("days"), @"\d+").Select(match => match.Value).ToList()
                : [];
            return _database.SetPaySchedule(Pattern(), days, values.Cents("amount"), next);
        };
        Open(form);
    }

    /// <summary>A bill with an amount, so the forecast and "Can I afford this?" count it. The same name changes that bill.</summary>
    private void SetBill()
    {
        const string Change = "change";
        const string Remove = "remove";
        var bills = _shown.Bills;
        var form = Form("Bill", "Save");
        form.AddNote(_shown.BillsLine);
        if (bills.Count > 0)
        {
            form.AddChoice("action", "Do", [new FormChoice("Add or change a bill", Change), new FormChoice("Remove a bill", Remove)]);
        }

        bool Removing() => bills.Count > 0 && form.Choice("action") as string == Remove;
        if (bills.Count > 0)
        {
            form.AddChoice("bill", "Bill", bills.Select(bill => new FormChoice(bill, bill)).ToList(), visibleWhen: Removing);
        }

        form.AddText("name", "Name, such as phone bill", visibleWhen: () => !Removing());
        form.AddMoney("amount", "Amount, such as 45", visibleWhen: () => !Removing());
        form.AddChoice("often", "How often", [
            new FormChoice("Monthly", WaypointDatabase.BillMonthly),
            new FormChoice("Weekly", WaypointDatabase.BillWeekly),
            new FormChoice("Every other week", WaypointDatabase.BillBiweekly),
            new FormChoice("Once", WaypointDatabase.BillOnce),
        ], visibleWhen: () => !Removing());
        string Often() => form.Choice("often") as string ?? WaypointDatabase.BillMonthly;
        form.AddText("day", "Day of the month, such as 12", visibleWhen: () => !Removing() && Often() == WaypointDatabase.BillMonthly);
        form.AddDate("next", "Next due", Today, () => !Removing() && Often() != WaypointDatabase.BillMonthly);
        var cards = Cards;
        if (cards.Count > 0)
        {
            form.AddChoice("account", "Paid from", cards.Select(card => new FormChoice(card.Name, card.Name)).Prepend(new FormChoice("Checking", "")).ToList(), visibleWhen: () => !Removing());
        }

        form.Submit = values =>
        {
            if (Removing())
            {
                return _database.RemoveBill(values.Choice("bill") as string);
            }

            var monthly = Often() == WaypointDatabase.BillMonthly;
            var days = monthly ? Regex.Matches(values.Text("day"), @"\d+").Select(match => match.Value).ToList() : [];
            if (monthly && days.Count == 0)
            {
                return new OpReply("What day of the month is it due?", true);
            }

            var account = cards.Count > 0 ? values.Choice("account") as string : null;
            DateTime? next = monthly ? null : values.Date("next").ToDateTime(TimeOnly.MinValue);
            return _database.SetBill(values.Text("name"), values.Cents("amount"), Often(), days, next, string.IsNullOrEmpty(account) ? null : account, Today);
        };
        Open(form);
    }

    /// <summary>The savings goal "Can I afford this?" keeps on track. The date is optional.</summary>
    private void SetGoal()
    {
        const string Dated = "dated";
        const string Undated = "undated";
        const string Remove = "remove";
        var goal = _shown.SavingsGoal;
        var form = Form("Savings goal", "Save");
        form.AddNote(_shown.GoalLine);
        form.AddChoice("when", "Reach it", [
            new FormChoice("By a date", Dated),
            new FormChoice("Whenever", Undated),
            .. goal is null ? Array.Empty<FormChoice>() : [new FormChoice("Remove the goal", Remove)],
        ], goal is { By: null } ? Undated : Dated);
        string When() => form.Choice("when") as string ?? Dated;
        form.AddMoney("amount", "How much, such as 500", goal?.TargetCents, () => When() != Remove);
        form.AddDate("by", "By", goal?.By ?? Today.AddMonths(3), () => When() == Dated);
        form.Submit = values => When() switch
        {
            Remove => _database.ClearSavingsGoal(),
            Dated => _database.SetSavingsGoal(values.Cents("amount"), values.Date("by").ToDateTime(TimeOnly.MinValue), Today),
            _ => _database.SetSavingsGoal(values.Cents("amount"), null, Today),
        };
        Open(form);
    }

    /// <summary>A credit score as the user saw it. Nothing is looked up.</summary>
    private void AddCreditScore()
    {
        var form = Form("New credit score", "Add");
        form.AddText("score", "Score, such as 720");
        form.AddText("source", "Where you saw it, such as a credit app");
        form.AddDate("date", "Date", Today);
        form.AddText("note", "Note, optional");
        form.Submit = values =>
        {
            var note = values.Text("note");
            return _database.AddCreditScore(values.Text("score"), values.Text("source"), values.Date("date").ToDateTime(TimeOnly.MinValue), note.Length > 0 ? note : null, DateTime.Now);
        };
        Open(form);
    }

    // Buttons above the page

    private void OnAffordClicked(object sender, RoutedEventArgs e)
    {
        var form = Form("Can I afford this?", "Check");
        form.SetCancelText("Close");
        form.AddMoney("amount", "Price, such as 45");
        var cards = Cards;
        if (cards.Count > 0)
        {
            form.AddChoice("card", "Paying with", cards.Select(card => new FormChoice(card.Name, card.Name)).Prepend(new FormChoice("Checking or debit", "")).ToList());
        }

        // Always an answer, so the form stays open for another amount.
        form.Submit = values =>
        {
            var card = cards.Count > 0 ? values.Choice("card") as string : null;
            var reply = _database.CheckAffordable(values.Cents("amount"), string.IsNullOrEmpty(card) ? null : card, Today);
            return reply with { IsAnswer = true };
        };
        form.ShowDialog();
    }

    private void OnSetBalanceClicked(object sender, RoutedEventArgs e)
    {
        // Brought up to date, so a card shows the same balance here as on the page.
        // A new account can be added here too, so the first balance doesn't need a CSV or typing.
        const string NewAccount = "+new account";
        var accounts = _shown.CurrentAccounts;
        var form = Form(accounts.Count == 0 ? "Add a balance" : "Set a balance", "Save");
        form.AddChoice(
            "account",
            "Account",
            accounts.Select(account => new FormChoice($"{account.Name}  ·  {Briefing.Money(account.BalanceCents)}", account.Name))
                .Append(new FormChoice("A new account", NewAccount))
                .ToList());
        bool IsNew() => form.Choice("account") as string == NewAccount;
        form.AddText("name", $"Name, such as {BankProfiles.Current.Checking.Name}", visibleWhen: IsNew);
        form.AddChoice("kind", "Kind", [
            new FormChoice("Checking", AccountKind.Checking),
            new FormChoice("Credit card", AccountKind.Card),
            new FormChoice("Cushion", AccountKind.Cushion),
            new FormChoice("Savings", AccountKind.Savings),
        ], AccountKind.Checking, IsNew);
        form.AddMoney("amount", "Balance now, such as 400");
        form.Submit = values =>
        {
            if (values.Cents("amount") is not { } cents)
            {
                return new OpReply("What's the balance now?", true);
            }

            if (values.Choice("account") is not string name)
            {
                return new OpReply("Which account?", true);
            }

            if (name != NewAccount)
            {
                return new OpReply(_database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.SetBalance, name, cents, null), Today));
            }

            return values.Text("name").Length == 0
                ? new OpReply("What's the account called?", true)
                : new OpReply(_database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.AddAccount, values.Text("name"), cents, (values.Choice("kind") as AccountKind? ?? AccountKind.Checking).ToString()), Today));
        };
        Open(form);
    }

    private void OnSetCapClicked(object sender, RoutedEventArgs e) => SetCap(null);

    private void SetCap(string? category)
    {
        if (BudgetCategories.Count == 0)
        {
            AddCategory();
            return;
        }

        var form = Form("Set a budget", "Save");
        form.AddChoice("category", "Category", BudgetCategories.Select(found => new FormChoice(CapLabel(found), found.Name)).ToList(), category);
        form.AddMoney("amount", "Monthly amount, such as 320");
        form.AddNote("A weekly category's week budget comes from its monthly amount.");
        form.Submit = values => values.Cents("amount") is { } cents && values.Choice("category") is string name
            ? new OpReply(_database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.SetCap, name, cents, null), Today))
            : new OpReply("How much a month?", true);
        Open(form);
    }

    private static string CapLabel(BudgetCategory category) =>
        category.MonthlyCents is { } cents ? $"{category.Name}  ·  {Briefing.Money(cents)}" : category.Name;

    private void OnPayScheduleClicked(object sender, RoutedEventArgs e) => SetPaySchedule();

    private void OnImportClicked(object sender, RoutedEventArgs e)
    {
        _import();
        ShowSoon();
    }

    // Tailspin Pay

    private void OnRepaymentPicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CandidateRow candidate })
        {
            Fire(() => _database.MatchRepaymentTo(candidate.MessageId, candidate.ItemId).Text);
        }
    }

    private void OnRepaymentCleared(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RepaymentCard card })
        {
            Fire(() => _database.ClearRepayment(card.MessageId).Text);
        }
    }

    // Right-click menus

    private void OpenMenu(FrameworkElement row, ContextMenuEventArgs e, IEnumerable<RowMenuEntry> entries)
    {
        e.Handled = true;
        _menuOpen = true;
        RowMenu.Open(row, e, entries, () =>
        {
            _menuOpen = false;
            Work.Show(Refresh);
        });
    }

    private void OnCategoryMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = (FrameworkElement)sender;
        var name = row.DataContext switch
        {
            CapRow cap => cap.Name,
            CategoryRow category => category.Name,
            _ => null,
        };
        if (name is null)
        {
            e.Handled = true;
            return;
        }

        OpenMenu(row, e, CategoryMenu(name));
    }

    private IEnumerable<RowMenuEntry> CategoryMenu(string name)
    {
        if (BudgetCategories.FirstOrDefault(category => category.Name == name) is { } category)
        {
            yield return category.Period == BudgetPeriod.Week
                ? new RowMenuEntry("Watch by the month", () => SetPeriod(category, "month"))
                : new RowMenuEntry("Watch by the week", () => SetPeriod(category, "week"));
            yield return new RowMenuEntry("Rename", () => RenameCategory(category));
            yield return new RowMenuEntry("Set budget", () => SetCap(category.Name));
        }

        yield return new RowMenuEntry("Copy name", () => RowMenu.Copy(name));
    }

    private void SetPeriod(BudgetCategory category, string period) =>
        Fire(() => _database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.SetPeriod, category.Name, null, period), Today));

    private void RenameCategory(BudgetCategory category)
    {
        var rename = new RenameWindow(OwnerWindow, category.Name, heading: "Rename category");
        if (rename.ShowDialog() == true && rename.NewName != category.Name)
        {
            var newName = rename.NewName;
            Fire(() => _database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.RenameCategory, category.Name, null, newName), Today));
        }
    }

    private void OnPurchaseMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = (FrameworkElement)sender;
        if (row.DataContext is not PurchaseRow { Row: var purchase })
        {
            e.Handled = true;
            return;
        }

        OpenMenu(row, e, PurchaseMenu(purchase));
    }

    /// <summary>The row's ⋯ button opens the same menu as right-click, dropping down from the button.</summary>
    private void OnPurchaseMoreClicked(object sender, RoutedEventArgs e)
    {
        var button = (FrameworkElement)sender;
        if (button.DataContext is not PurchaseRow { Row: var purchase })
        {
            return;
        }

        e.Handled = true;
        _menuOpen = true;
        RowMenu.Open(button, PlacementMode.Bottom, PurchaseMenu(purchase), () =>
        {
            _menuOpen = false;
            Work.Show(Refresh);
        });
    }

    private List<RowMenuEntry> PurchaseMenu(SpendingRow purchase)
    {
        var categories = BudgetCategories
            .Where(category => category.Name != purchase.Category)
            .Select(category => new RowMenuEntry(category.Name, () => Fire(() => _database.CategorizePurchase(purchase.Id, category.Name).Text)))
            .ToList();
        var entries = new List<RowMenuEntry>();
        if (categories.Count > 0)
        {
            entries.Add(new RowMenuEntry("Change category", Children: categories));
        }

        entries.Add(purchase.OneOff
            ? new RowMenuEntry("Count against budgets again", () => Fire(() => _database.MarkPurchaseOneOff(purchase.Id, false).Text))
            : new RowMenuEntry("Mark one-off", () => Fire(() => _database.MarkPurchaseOneOff(purchase.Id, true).Text)));
        if (purchase.NetCents > 0)
        {
            entries.Add(new RowMenuEntry("Record repayment", () => RecordRepayment(purchase)));
        }

        entries.Add(new RowMenuEntry("Copy merchant", () => RowMenu.Copy(MerchantName.Clean(purchase.Title))));
        entries.Add(new RowMenuEntry("Delete", () => DeletePurchase(purchase)));
        return entries;
    }

    /// <summary>
    /// Takes the purchase off the budget, and undo puts it back. An imported one stays known to the import, so the
    /// next CSV import or email poll doesn't add it again.
    /// </summary>
    private async void DeletePurchase(SpendingRow purchase)
    {
        DeletedItem? deleted = null;
        if (!await Change(() =>
        {
            deleted = _database.DeletePurchase(purchase.Id, DateTime.Now);
            return deleted is null ? "That purchase is already gone" : null;
        }) || deleted is null)
        {
            return;
        }

        var name = MerchantName.Clean(purchase.Title);
        Bar.Show($"Deleted {Briefing.Money(purchase.AmountCents)} for {name}", () => Fire(() =>
        {
            _database.UndoDeletePurchase(deleted, DateTime.Now);
            return null;
        }));
    }

    private void RecordRepayment(SpendingRow purchase)
    {
        var form = Form($"Repaid on {MerchantName.Clean(purchase.Title)}", "Save");
        form.AddNote($"{Briefing.Money(purchase.NetCents)} of {Briefing.Money(purchase.AmountCents)} is still unrepaid.");
        form.AddMoney("amount", "Amount paid back", purchase.NetCents);
        form.Submit = values => _database.RecordRepayment(purchase.Id, values.Cents("amount"));
        Open(form);
    }

    private void OnWaitMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = (FrameworkElement)sender;
        if (row.DataContext is not WaitRow { Item: var item })
        {
            e.Handled = true;
            return;
        }

        OpenMenu(row, e,
        [
            new RowMenuEntry("Bought it", () => Bought(item)),
            new RowMenuEntry("Let it go", () => Fire(() => _database.ResolveWaitItem(item.Id, "skipped").Text)),
            new RowMenuEntry("Copy", () => RowMenu.Copy(item.Description)),
        ]);
    }

    /// <summary>Settles the wait, then offers the expense, the same as typing "I bought it" does. A later import won't count it twice.</summary>
    private async void Bought(WaitItem item)
    {
        if (await Change(() => _database.ResolveWaitItem(item.Id, "bought").Text))
        {
            AddExpense(item.Description, item.PriceCents);
        }
    }

    private sealed record SectionRow(string Title, IReadOnlyList<Row> Rows)
    {
        public string Heading => Headings.Sentence(Title);

        /// <summary>The page's own title. A section with the same name, such as Until payday, doesn't say it again.</summary>
        public string? UnderTitle { get; init; }

        public Visibility HeadingVisibility =>
            Heading.Length == 0 || string.Equals(Heading, UnderTitle, StringComparison.OrdinalIgnoreCase) ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>What a screen reader says for the section, rather than the record's fields.</summary>
        public override string ToString() => Title;
    }

    private sealed class Row(ReportRow row)
    {
        public string Label => row.Label;

        public string Value => row.Value;

        public FontWeight Weight => row.Flagged ? FontWeights.Bold : FontWeights.Normal;

        /// <summary>What a screen reader says for the row, rather than the class name.</summary>
        public override string ToString() => $"{Label} {Value}";
    }

    /// <summary>A category at a glance: how much is left of its week or month cap.</summary>
    private sealed class CapRow(string name, long cap, long spent)
    {
        public string Name => name;

        public string Left => spent > cap ? $"{Briefing.Money(spent - cap)} over" : $"{Briefing.Money(cap - spent)} left";

        public double Fill => cap > 0 ? Math.Clamp((double)spent / cap, 0, 1) : 1;

        public Brush Bar => ThemeBrushes.For(spent > cap ? ItemMark.Urgent : ItemMark.School);

        public FontWeight Weight => spent > cap ? FontWeights.Bold : FontWeights.Normal;

        /// <summary>What a screen reader says for the row, rather than the class name.</summary>
        public override string ToString() => $"{Name}, {Left}";
    }

    private sealed class CategoryRow(CategoryDetail detail, IReadOnlyList<SpendingRow> rows, bool expanded)
    {
        public string Name => detail.Name;

        public bool Expanded => expanded;

        public string Summary => detail.CapCents is { } cap
            ? $"{Briefing.Money(detail.MonthCents)} of {Briefing.Money(cap)}"
            : Briefing.Money(detail.MonthCents);

        public FontWeight Weight => detail.IsOver ? FontWeights.Bold : FontWeights.Normal;

        public IReadOnlyList<PurchaseRow> Rows { get; } = rows.OrderByDescending(row => row.Date).Select(row => new PurchaseRow(row)).ToList();

        /// <summary>What a screen reader says for the row, rather than the class name.</summary>
        public override string ToString() => $"{Name}, {Summary}";
    }

    private sealed class PurchaseRow(SpendingRow row)
    {
        public SpendingRow Row => row;

        public string Date => row.Date.ToString("MMM d", CultureInfo.InvariantCulture);

        public string Title => row.OneOff ? $"{MerchantName.Clean(row.Title)} (one-off)" : MerchantName.Clean(row.Title);

        public string Source => row.Source;

        public string Amount => row.ReimbursedCents > 0
            ? $"{Briefing.Money(row.NetCents)} after repaid"
            : Briefing.Money(row.AmountCents);

        /// <summary>What a screen reader says for the row, rather than the class name.</summary>
        public override string ToString() => $"{Date}, {Title}, {Amount}";
    }

    private sealed class WaitRow(WaitItem item, DateTime now)
    {
        public WaitItem Item => item;

        public string Title => item.Description;

        public string Price => Briefing.Money(item.PriceCents);

        public FontWeight Weight => item.ReadyAt <= now ? FontWeights.SemiBold : FontWeights.Normal;

        public string Details
        {
            get
            {
                var when = item.ReadyAt <= now
                    ? "Ready to decide. Right-click to choose Bought it or Let it go."
                    : $"Ready {item.ReadyAt.ToString("ddd, MMM d, h:mm tt", CultureInfo.InvariantCulture)}";
                return item.Reason is { Length: > 0 } reason ? $"{when}  ·  {reason}" : when;
            }
        }

        /// <summary>What a screen reader says for the row, rather than the class name.</summary>
        public override string ToString() => $"{Title}, {Price}, {Details}";
    }

    /// <summary>A Tailspin Pay payment no purchase clearly fit, with the purchases it could be for.</summary>
    private sealed class RepaymentCard(PaymentAppRepayment repayment, IReadOnlyList<RepaymentCandidate> candidates)
    {
        public string MessageId => repayment.MessageId;

        public string Question
        {
            get
            {
                var reason = repayment.Note is { Length: > 0 } note ? $" for {note}" : "";
                return $"{repayment.Payer} paid you {Briefing.Money(repayment.Cents)}{reason}. Which purchase was it for?";
            }
        }

        public string PickLabel => candidates.Count > 0
            ? "Pick the purchase"
            : "No recent purchase fits. Type which one it was, or choose Not a repayment.";

        public IReadOnlyList<CandidateRow> Candidates { get; } = candidates.Select(candidate => new CandidateRow(repayment.MessageId, candidate)).ToList();

        /// <summary>What a screen reader says for the row, rather than the class name.</summary>
        public override string ToString() => Question;
    }

    private sealed class CandidateRow(string messageId, RepaymentCandidate candidate)
    {
        public string MessageId => messageId;

        public long ItemId => candidate.ItemId;

        public string Label => $"{candidate.Date.ToString("MMM d", CultureInfo.InvariantCulture)}  ·  {candidate.Title}  ·  {Briefing.Money(candidate.LeftCents)} unrepaid";

        /// <summary>What a screen reader says for the row, rather than the class name.</summary>
        public override string ToString() => Label;
    }
}
