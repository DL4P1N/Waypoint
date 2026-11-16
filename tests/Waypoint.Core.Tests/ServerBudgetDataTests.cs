using System.Text.Json;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Remote;

namespace Waypoint.Core.Tests;

/// <summary>The Budget page and the cards above Ask in server mode, while the server can't be reached.</summary>
public sealed class ServerBudgetDataTests
{
    private readonly FakeServer _fake = new();

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private static DateOnly ThisMonth => new(Today.Year, Today.Month, 1);

    /// <summary>Answers with a page that has one waiting purchase, or acts like a server that's switched off.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public bool Up { get; set; } = true;

        public string Waiting { get; set; } = "headphones";

        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!Up)
            {
                throw new HttpRequestException("No route to host");
            }

            var path = request.RequestUri!.AbsolutePath;
            Paths.Add($"{request.Method} {path}");
            var body = path switch
            {
                _ when path.StartsWith("/api/budget/page/", StringComparison.Ordinal) =>
                    JsonSerializer.Serialize(BudgetPage.Empty with { WaitList = [new WaitItem(Waiting, 8000, DateTime.Now, Id: 7)] }, RemoteJson.Options),
                "/api/session" => JsonSerializer.Serialize(new SessionValue(null), RemoteJson.Options),
                _ => JsonSerializer.Serialize(new OpReply("Done."), RemoteJson.Options),
            };
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private ServerClient Client() => new(new HttpClient(_fake) { BaseAddress = new Uri("http://server/") }, "token");

    [Fact]
    public void While_the_server_is_away_the_page_shows_its_last_read()
    {
        var data = new ServerBudgetData(Client());
        Assert.Equal("headphones", Assert.Single(data.GetPage(ThisMonth, Today).WaitList).Description);

        _fake.Up = false;
        _fake.Waiting = "something new";

        Assert.Equal("headphones", Assert.Single(data.GetPage(ThisMonth, Today).WaitList).Description);
    }

    [Fact]
    public void A_month_never_read_before_the_server_went_away_shows_empty()
    {
        var client = Client();
        _fake.Up = false;
        client.CheckAsync().GetAwaiter().GetResult();

        var page = new ServerBudgetData(client).GetPage(ThisMonth.AddMonths(-3), Today);

        Assert.Empty(page.WaitList);
        Assert.Empty(page.Categories);
        Assert.Null(page.SafeToSpend);
    }

    [Fact]
    public void While_the_server_is_away_budget_changes_and_imports_say_so_instead_of_pretending()
    {
        var client = Client();
        var data = new ServerBudgetData(client);
        data.GetPage(ThisMonth, Today);
        _fake.Up = false;

        Assert.Throws<ServerUnavailableException>(() => data.AddExpense("Target", 1200, null, Today, DateTime.Now));
        Assert.Throws<ServerUnavailableException>(() => data.ResolveWaitItem(7, "bought"));
        Assert.Throws<ServerUnavailableException>(() => data.ApplyBudgetOp(new BudgetOp(BudgetOpKind.SetCap, "Fun", 5000, null), Today));
        Assert.Throws<ServerUnavailableException>(() => data.CheckAffordable(4500, null, Today));
        Assert.Throws<ServerUnavailableException>(() => data.ImportStatements(["Posted On,Purchase Date,Category,Description,Amount\n"]));
        Assert.Throws<ServerUnavailableException>(() => new ServerSessionData(client).PauseSession(DateTime.Now.AddHours(1), DateTime.Now));
        Assert.Equal(ServerState.Offline, client.State);
    }

    [Fact]
    public void Changes_go_to_their_own_routes()
    {
        var data = new ServerBudgetData(Client());

        data.MarkPurchaseOneOff(12, true);
        data.CategorizePurchase(12, "Fun");
        data.ResolveWaitItem(7, "skipped");
        data.ClearSavingsGoal();

        Assert.Equal(
            ["POST /api/budget/purchases/12/one-off", "POST /api/budget/purchases/12/category", "POST /api/budget/waits/7/resolve", "DELETE /api/budget/goal"],
            _fake.Paths);
    }

    [Fact]
    public void With_the_server_away_and_nothing_read_the_cards_stay_hidden()
    {
        var client = Client();
        _fake.Up = false;
        var sessions = new ServerSessionData(client);

        Assert.Null(sessions.GetActiveSession(DateTime.Now));
        Assert.Null(sessions.DescribePayProposal(DateTime.Now));
        Assert.Null(sessions.GetTonight(DateTime.Now));
    }
}
