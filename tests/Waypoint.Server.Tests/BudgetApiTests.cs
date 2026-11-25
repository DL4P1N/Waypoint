using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Core.Background;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Email;
using Waypoint.Core.Remote;

namespace Waypoint.Server.Tests;

/// <summary>The Budget page, the cards above Ask, and CSV import, through the API the way the PC app uses them.</summary>
public sealed class BudgetApiTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-budget-{Guid.NewGuid()}");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _token;

    public BudgetApiTests()
    {
        Directory.CreateDirectory(_folder);
        _token = ServerToken.Create(_folder);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Waypoint:DataDirectory", _folder);
            builder.UseSetting("Waypoint:RunBackground", "false");
            builder.ConfigureServices(services => services.AddSingleton(new WorkerParts(_ => new NoTokens(), CaptureSorter: new NeverSorts())));
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    /// <summary>Taken once per test, so a test running past midnight, or into a new month, still asks about the same day.</summary>
    private DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.Now);

    private DateOnly ThisMonth => new(Today.Year, Today.Month, 1);

    private sealed class NoTokens : ITokenStore
    {
        public string? Load() => null;

        public void Save(string token)
        {
        }
    }

    private sealed class NeverSorts : ICaptureSorter
    {
        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<CaptureResult?>(null);
    }

    private HttpClient Paired()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return client;
    }

    private ServerClient Client() => InMemoryServer.Client(Paired(), _token);

    /// <summary>The server's own database file, opened the way the PC opens its own in local mode.</summary>
    private WaypointDatabase SameDatabase()
    {
        _factory.Services.GetRequiredService<WaypointServer>();
        return new WaypointDatabase(Path.Combine(_folder, "waypoint.db"));
    }

    [Theory]
    [InlineData("GET", "/api/budget/page/2026-11-01")]
    [InlineData("GET", "/api/session")]
    [InlineData("GET", "/api/pay-proposal")]
    [InlineData("GET", "/api/tonight")]
    [InlineData("POST", "/api/budget/expenses")]
    [InlineData("POST", "/api/budget/ops")]
    [InlineData("PUT", "/api/budget/bills")]
    [InlineData("DELETE", "/api/budget/goal")]
    [InlineData("POST", "/api/budget/purchases/1/category")]
    [InlineData("POST", "/api/budget/purchases/1/delete")]
    [InlineData("POST", "/api/budget/waits/1/resolve")]
    [InlineData("POST", "/api/budget/import")]
    [InlineData("POST", "/api/session/pause")]
    [InlineData("POST", "/api/after-this/take")]
    [InlineData("GET", "/api/feedback")]
    [InlineData("POST", "/api/feedback")]
    [InlineData("PUT", "/api/feedback/1/note")]
    [InlineData("DELETE", "/api/feedback")]
    [InlineData("POST", "/api/feedback/restore")]
    public async Task Every_budget_and_card_route_needs_the_token(string method, string path)
    {
        var none = await _factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) });

        var wrong = _factory.CreateClient();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token");
        var refused = await wrong.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) });

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task A_month_that_is_not_a_date_is_turned_down()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Paired().GetAsync("/api/budget/page/november")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Paired().GetAsync($"/api/budget/page/{BudgetPage.MonthKey(ThisMonth)}")).StatusCode);
    }

    [Fact]
    public void The_first_account_can_be_added_from_the_balance_form_through_the_server()
    {
        IBudgetData server = new ServerBudgetData(Client());

        var reply = server.ApplyBudgetOp(new BudgetOp(BudgetOpKind.AddAccount, "Credit union", 25000, nameof(AccountKind.Checking)), Today);

        Assert.Equal("Added Credit union at $250.00", reply);
        var account = Assert.Single(SameDatabase().GetBudgetAccounts(), found => found.Name == "Credit union");
        Assert.Equal((AccountKind.Checking, 25000L), (account.Kind, account.BalanceCents));
    }

    [Fact]
    public void The_budget_page_reads_the_same_through_the_server_as_on_this_pc()
    {
        IBudgetData server = new ServerBudgetData(Client());
        var database = SameDatabase();
        var local = new LocalBudgetData(database);
        database.SaveBudgetAccount(new BudgetAccount("Contoso Card", AccountKind.Card, 12000, Today, LimitCents: 100000));

        Assert.False(server.AddCategory("Groceries", 22500, BudgetPeriod.Month).IsAnswer);
        server.AddCategory("Fun", 5000, BudgetPeriod.Week);
        server.AddExpense("Trader Joes", 4200, "Groceries", Today, DateTime.Now);
        server.ApplyBudgetOp(new BudgetOp(BudgetOpKind.SetBalance, "checking", 120000, null), Today);
        server.ApplyBudgetOp(new BudgetOp(BudgetOpKind.AddWait, "headphones", 8000, null, 48, "old ones broke"), Today);
        server.SetBill("phone bill", 4500, WaypointDatabase.BillMonthly, ["12"], null, null, Today);
        server.SetSavingsGoal(50000, null, Today);
        server.AddCreditScore("720", "ScoreCheck", DateTime.Today, null, DateTime.Now);
        server.SetPaySchedule("biweekly", [], 85000, DateTime.Today.AddDays(3));

        var page = server.GetPage(ThisMonth, Today);
        Assert.Equal(["Fun", "Groceries"], page.Categories.Select(category => category.Name).Order());
        Assert.Equal("headphones", Assert.Single(page.WaitList).Description);
        Assert.Contains(page.Month.SelectMany(detail => detail.Rows), row => row.Title == "Trader Joes");
        Assert.Contains("phone bill", Assert.Single(page.Bills), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(50000, page.Goal!.Goal.TargetCents);
        Assert.NotEmpty(page.CreditScores.Rows);
        Assert.Equal("Contoso Card", Assert.Single(page.CardsAtMonthEnd).Label);

        // Everything the page shows, field for field, matches the PC reading the same database itself.
        Assert.Equal(
            JsonSerializer.Serialize(local.GetPage(ThisMonth, Today), RemoteJson.Options),
            JsonSerializer.Serialize(page, RemoteJson.Options));
        Assert.Equal(
            JsonSerializer.Serialize(local.GetPage(ThisMonth.AddMonths(-1), Today), RemoteJson.Options),
            JsonSerializer.Serialize(server.GetPage(ThisMonth.AddMonths(-1), Today), RemoteJson.Options));
    }

    [Fact]
    public void The_right_click_menus_and_forms_change_the_servers_budget()
    {
        IBudgetData server = new ServerBudgetData(Client());
        server.AddCategory("Groceries", 22500, BudgetPeriod.Month);
        server.AddCategory("Fun", 5000, BudgetPeriod.Month);
        server.AddExpense("Movie tickets", 1800, "Groceries", Today, DateTime.Now);
        var purchase = Assert.Single(server.GetPage(ThisMonth, Today).Month.SelectMany(detail => detail.Rows));

        server.CategorizePurchase(purchase.Id, "Fun");
        server.MarkPurchaseOneOff(purchase.Id, true);
        server.RecordRepayment(purchase.Id, 900);
        var changed = Assert.Single(server.GetPage(ThisMonth, Today).Month.SelectMany(detail => detail.Rows));
        Assert.Equal("Fun", changed.Category);
        Assert.True(changed.OneOff);
        Assert.Equal(900, changed.ReimbursedCents);

        server.ApplyBudgetOp(new BudgetOp(BudgetOpKind.RenameCategory, "Fun", null, "Going out"), Today);
        Assert.Contains(server.GetPage(ThisMonth, Today).Categories, category => category.Name == "Going out");

        server.ApplyBudgetOp(new BudgetOp(BudgetOpKind.AddWait, "lamp", 3000, null, 24), Today);
        var wait = Assert.Single(server.GetPage(ThisMonth, Today).WaitList);
        server.ResolveWaitItem(wait.Id, "skipped");
        Assert.Empty(server.GetPage(ThisMonth, Today).WaitList);

        server.SetBill("gym", 2000, WaypointDatabase.BillMonthly, ["3"], null, null, Today);
        server.RemoveBill(Assert.Single(server.GetPage(ThisMonth, Today).Bills));
        Assert.Empty(server.GetPage(ThisMonth, Today).Bills);

        server.SetSavingsGoal(10000, DateTime.Today.AddMonths(2), Today);
        server.ClearSavingsGoal();
        Assert.Null(server.GetPage(ThisMonth, Today).SavingsGoal);

        Assert.NotEmpty(server.AddExpectedDeposit("tax refund", 60000, DateTime.Today.AddDays(5)).Text);
        Assert.NotEmpty(server.CheckAffordable(4500, null, Today).Text);
        Assert.NotEmpty(server.ClearRepayment("no-such-message").Text);
    }

    [Fact]
    public void A_purchase_deleted_through_the_server_leaves_the_budget_and_undo_brings_it_back()
    {
        IBudgetData server = new ServerBudgetData(Client());
        server.AddExpense("Movie tickets", 1800, null, Today, DateTime.Now);
        var purchase = Assert.Single(server.GetPage(ThisMonth, Today).Month.SelectMany(detail => detail.Rows));

        var deleted = server.DeletePurchase(purchase.Id, DateTime.Now);
        Assert.NotNull(deleted);
        Assert.Empty(server.GetPage(ThisMonth, Today).Month.SelectMany(detail => detail.Rows));

        // Deleting it again finds nothing, and says so with null rather than an error.
        Assert.Null(server.DeletePurchase(purchase.Id, DateTime.Now));

        server.UndoDeletePurchase(deleted, DateTime.Now);
        Assert.Equal("Movie tickets", Assert.Single(server.GetPage(ThisMonth, Today).Month.SelectMany(detail => detail.Rows)).Title);
    }

    [Fact]
    public void A_csv_read_on_the_pc_is_imported_on_the_server_once()
    {
        const string csv = "Posted On,Purchase Date,Category,Description,Amount\n10/02/2026,10/01/2026,Restaurants,LUNCH SPOT,12.00\n";
        var server = new ServerBudgetData(Client());

        var first = server.ImportStatements([csv]);
        var again = server.ImportStatements([csv]);

        Assert.StartsWith("Added 1 purchase to Budget.", first);
        Assert.StartsWith("Nothing new. That one was already counted.", again);
        Assert.Contains(SameDatabase().GetCategoryDetails(new DateOnly(2026, 10, 1), Today).SelectMany(detail => detail.Rows), row => row.Title == "LUNCH SPOT");
    }

    [Fact]
    public async Task An_import_with_nothing_in_it_is_turned_down()
    {
        var response = await Paired().PostAsJsonAsync("/api/budget/import", new StatementsRequest([]), RemoteJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void The_session_card_reads_and_pauses_the_servers_session()
    {
        var client = Client();
        IToDoData toDo = new ServerToDoData(client);
        ISessionData sessions = new ServerSessionData(client);
        Assert.Null(sessions.GetActiveSession(DateTime.Now));

        var item = toDo.AddByHand(new HandAddedItem(ItemKind.Task, "Write the essay", Today), DateTime.Now).Item!;
        toDo.StartActivation(ItemRef.Format(item), "test", DateTime.Now, "Open the outline");

        var session = sessions.GetActiveSession(DateTime.Now);
        Assert.Equal("Write the essay", session!.TaskTitle);
        Assert.Equal("Open the outline", session.Action);

        Assert.NotEmpty(sessions.PauseSession(DateTime.Now.AddHours(2), DateTime.Now).Text);
        Assert.Null(sessions.GetActiveSession(DateTime.Now));
        Assert.Null(sessions.DescribePayProposal(DateTime.Now));

        // Picking it back up from To do, then End on the card.
        Assert.Equal("Write the essay", sessions.GetPausedSession()?.TaskTitle);
        Assert.NotEmpty(sessions.ResumeSession().Text);
        Assert.Equal("Write the essay", sessions.GetActiveSession(DateTime.Now)?.TaskTitle);
        Assert.NotEmpty(sessions.CancelSession(DateTime.Now).Text);
        Assert.Null(sessions.GetActiveSession(DateTime.Now));
        Assert.Null(sessions.GetPausedSession());

        // Tonight's card answers from the server's bedtime settings, the same as this PC would.
        var server = _factory.Services.GetRequiredService<WaypointServer>();
        var here = new LocalSessionData(server.Database, server.Worker.BedtimeSchedule);
        Assert.Equal(here.SnoozeBedtime(DateTime.Now).Text, sessions.SnoozeBedtime(DateTime.Now).Text);
        Assert.Equal(here.SkipBedtime(DateTime.Now).Text, sessions.SkipBedtime(DateTime.Now).Text);
        Assert.Equal(here.MarkRoutineStep("evening step", false, DateTime.Now).Text, sessions.MarkRoutineStep("evening step", false, DateTime.Now).Text);
    }

    [Fact]
    public void I_got_paid_wait_hours_and_patterns_go_through_the_server()
    {
        IBudgetData budget = new ServerBudgetData(Client());
        var server = _factory.Services.GetRequiredService<WaypointServer>();

        Assert.True(budget.ConfirmPayReceived(null, Today).IsAnswer);
        Assert.Equal("New wait list items will wait 24 hours", budget.SetDefaultWaitHours("24").Text);
        Assert.Equal(24, server.Database.GetDefaultWaitHours());
        Assert.Equal(server.Database.GetSpendingPatterns(Today), budget.GetSpendingPatterns(Today));
    }

    [Fact]
    public void Tonight_and_the_pay_card_match_what_this_pc_would_show()
    {
        var sessions = new ServerSessionData(Client());
        var server = _factory.Services.GetRequiredService<WaypointServer>();
        var expected = BedtimeRoutineStatus(server);

        Assert.Equal(JsonSerializer.Serialize(expected, RemoteJson.Options), JsonSerializer.Serialize(sessions.GetTonight(DateTime.Now), RemoteJson.Options));
        Assert.Equal(LocalSessionData.ReadPayProposal(server.Database, DateTime.Now), sessions.DescribePayProposal(DateTime.Now));
    }

    [Fact]
    public void What_was_held_from_earlier_is_taken_once_through_the_server()
    {
        var client = Client();
        IToDoData toDo = new ServerToDoData(client);
        ISessionData sessions = new ServerSessionData(client);
        var item = toDo.AddByHand(new HandAddedItem(ItemKind.Task, "Write the essay", Today), DateTime.Now).Item!;
        Assert.Null(sessions.TakeAfterThis(DateTime.Now));

        _factory.Services.GetRequiredService<WaypointServer>().Database.HoldAfterThis(item, DateTime.Now);

        Assert.Equal("Write the essay", sessions.TakeAfterThis(DateTime.Now)?.Title);
        Assert.Null(sessions.TakeAfterThis(DateTime.Now));
    }

    [Fact]
    public void This_didnt_help_is_kept_noted_listed_and_cleared_on_the_server()
    {
        IFeedbackData feedback = new ServerFeedbackData(Client());
        var server = _factory.Services.GetRequiredService<WaypointServer>();

        var id = feedback.AddFeedback(Feedback.Suggestion, null, DateTime.Now);
        feedback.NoteFeedback(id, "It picked an old one");

        var entry = Assert.Single(server.Database.GetFeedback());
        Assert.Equal((Feedback.Suggestion, "It picked an old one"), (entry.Feature, entry.Note));
        Assert.Equal(entry, Assert.Single(feedback.GetFeedback()));

        feedback.ClearFeedback();
        Assert.Empty(server.Database.GetFeedback());

        // Clear all's Undo puts them back as they were.
        feedback.RestoreFeedback([entry]);
        Assert.Equal(entry, Assert.Single(feedback.GetFeedback()));
    }

    private static Core.Schedule.BedtimeStatus? BedtimeRoutineStatus(WaypointServer server) =>
        new LocalSessionData(server.Database, server.Worker.BedtimeSchedule).GetTonight(DateTime.Now);
}
