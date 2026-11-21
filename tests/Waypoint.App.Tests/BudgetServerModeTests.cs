using System.Collections;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Budget;
using Waypoint.Core.Data;
using Waypoint.Core.Remote;

namespace Waypoint.App.Tests;

/// <summary>The Budget page in server mode: it reads through the server, and keeps what it had while the server is away.</summary>
public sealed class BudgetServerModeTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Serves the page from a database standing in for the server's, or acts like a server that's switched off.</summary>
    private sealed class FakeServer(WaypointDatabase database) : HttpMessageHandler
    {
        public bool Up { get; set; } = true;

        public List<string> Changes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!Up)
            {
                throw new HttpRequestException("No route to host");
            }

            var path = request.RequestUri!.AbsolutePath;
            string body;
            if (path.StartsWith("/api/budget/page/", StringComparison.Ordinal))
            {
                var month = DateOnly.Parse(path["/api/budget/page/".Length..], System.Globalization.CultureInfo.InvariantCulture);
                body = JsonSerializer.Serialize(BudgetPage.Read(database, month, Today), RemoteJson.Options);
            }
            else if (path.StartsWith("/api/preferences/", StringComparison.Ordinal))
            {
                body = JsonSerializer.Serialize(new PreferenceValue(null), RemoteJson.Options);
            }
            else
            {
                Changes.Add(path);
                body = "{}";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static List<string> Waiting(BudgetView view) =>
        ((IEnumerable)UiTest.Named<ItemsControl>(view, "WaitRows").ItemsSource).Cast<object>().Select(row => UiTest.Prop<string>(row, "Title")!).ToList();

    [Fact]
    public void While_the_server_is_away_the_page_keeps_its_figures_and_a_change_says_it_has_to_wait() => Ui.Run(ui =>
    {
        ui.Database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.AddWait, "headphones", 8000, null, 48), Today);
        ui.Database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.SetBalance, "checking", 50000, null), Today);
        var fake = new FakeServer(ui.Database);
        var view = new BudgetView();
        ui.Host(view);
        view.Connect(new ServerBudgetData(new ServerClient(new HttpClient(fake) { BaseAddress = new Uri("http://server/") }, "token")), () => { });
        ui.Pump();
        var entries = UiTest.Named<ListBox>(UiTest.Named<NestedColumn>(view, "Column"), "Entries");
        entries.SelectedItem = entries.Items.Cast<ColumnEntry>().Single(entry => entry.Key == "waiting");
        ui.Pump();
        Assert.Equal(["headphones"], Waiting(view));

        fake.Up = false;
        view.Refresh();
        ui.Pump();
        Assert.Equal(["headphones"], Waiting(view));

        view.UpdateLayout();
        var row = UiTest.Descendants<Grid>(view).First(grid => grid.DataContext?.GetType().Name == "WaitRow");
        ui.Choose(ui.RightClick(row)!, "Let it go");

        var bar = UiTest.Named<UndoBar>(view, "Bar");
        Assert.Equal(Visibility.Visible, bar.Visibility);
        Assert.Equal(BudgetFormWindow.ServerAway, UiTest.Named<TextBlock>(bar, "Message").Text);
        Assert.Equal(["headphones"], Waiting(view));
        Assert.Single(ui.Database.GetWaitList());
        Assert.Empty(fake.Changes);
    });

    /// <summary>
    /// Opened again with the server away, as after a restart, Budget shows the last saved page, the way To do shows
    /// its saved items, and the status line says it's what was there before.
    /// </summary>
    [Fact]
    public void After_a_restart_with_the_server_away_Budget_shows_its_saved_page_like_To_do() => Ui.Run(ui =>
    {
        ui.Database.SaveCategory(new BudgetCategory("Groceries", 22500));
        ui.Database.AddExpense("Trader Joes", 4200, "Groceries", Today, DateTime.Now);
        var saved = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"waypoint-reads-{Guid.NewGuid()}.json");
        try
        {
            var fake = new FakeServer(ui.Database);
            var before = new ServerClient(new HttpClient(fake) { BaseAddress = new Uri("http://server/") }, "token");
            before.SaveReadsIn(saved);
            var first = new BudgetView();
            ui.Host(first);
            first.Connect(new ServerBudgetData(before), () => { });
            ui.Pump();
            var total = UiTest.Named<TextBlock>(first, "MonthTotal").Text;
            Assert.Contains("$42.00", total);

            before.FlushSavedReads();
            fake.Up = false;
            var after = new ServerClient(new HttpClient(fake) { BaseAddress = new Uri("http://server/") }, "token");
            after.SaveReadsIn(saved);
            var again = new BudgetView();
            ui.Host(again);
            again.Connect(new ServerBudgetData(after), () => { });
            ui.Pump();

            Assert.Equal(total, UiTest.Named<TextBlock>(again, "MonthTotal").Text);
            Assert.Equal(UiTest.Named<TextBlock>(first, "HeroNote").Text, UiTest.Named<TextBlock>(again, "HeroNote").Text);
            Assert.Equal(ServerState.Offline, after.State);
            Assert.StartsWith("Can't reach the server. Showing what was there at", after.Problem);
        }
        finally
        {
            System.IO.File.Delete(saved);
        }
    });

    [Fact]
    public void Server_mode_shows_the_same_page_as_local_mode() => Ui.Run(ui =>
    {
        ui.Database.SaveCategory(new BudgetCategory("Groceries", 22500));
        ui.Database.AddExpense("Trader Joes", 4200, "Groceries", Today, DateTime.Now);
        var local = new BudgetView();
        ui.Host(local);
        local.Connect(new LocalBudgetData(ui.Database), () => { });
        var remote = new BudgetView();
        ui.Host(remote);
        remote.Connect(new ServerBudgetData(new ServerClient(new HttpClient(new FakeServer(ui.Database)) { BaseAddress = new Uri("http://server/") }, "token")), () => { });
        ui.Pump();

        foreach (var name in new[] { "SafeToSpend", "HeroNote", "MonthTotal", "PayLine" })
        {
            Assert.Equal(UiTest.Named<TextBlock>(local, name).Text, UiTest.Named<TextBlock>(remote, name).Text);
        }

        Assert.Contains("$42.00", UiTest.Named<TextBlock>(remote, "MonthTotal").Text);
    });
}
