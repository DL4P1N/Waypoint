using System.Collections;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Waypoint.App;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Remote;

namespace Waypoint.App.Tests;

/// <summary>The Budget page in server mode with a server slow to answer: clicks and forms never hold the window.</summary>
public sealed class ServerBudgetClickTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Serves the page from a database standing in for the server's. Changes can be held.</summary>
    private sealed class HeldServer(WaypointDatabase database) : HttpMessageHandler
    {
        public List<string> Changes { get; } = [];

        public ManualResetEventSlim Release { get; } = new(initialState: true);

        public ManualResetEventSlim ChangeArrived { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string body;
            if (path.StartsWith("/api/budget/page/", StringComparison.Ordinal))
            {
                var month = DateOnly.Parse(path["/api/budget/page/".Length..], System.Globalization.CultureInfo.InvariantCulture);
                lock (Changes)
                {
                    body = JsonSerializer.Serialize(BudgetPage.Read(database, month, Today), RemoteJson.Options);
                }
            }
            else if (path.StartsWith("/api/preferences/", StringComparison.Ordinal))
            {
                body = JsonSerializer.Serialize(new PreferenceValue(null), RemoteJson.Options);
            }
            else
            {
                var sent = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                ChangeArrived.Set();
                await Task.Run(() => Release.Wait(TimeSpan.FromMinutes(1)), cancellationToken);
                lock (Changes)
                {
                    Changes.Add($"{path} {sent}");
                }

                body = "{}";
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    private static List<string> Waiting(BudgetView view) =>
        ((IEnumerable)UiTest.Named<ItemsControl>(view, "WaitRows").ItemsSource).Cast<object>().Select(row => UiTest.Prop<string>(row, "Title")!).ToList();

    [Fact]
    public void Menu_changes_return_at_once_and_reach_the_server_in_order() => Ui.Run(ui =>
    {
        ui.Database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.AddWait, "headphones", 8000, null, 48), Today);
        ui.Database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.AddWait, "bike lights", 3000, null, 48), Today);
        var server = new HeldServer(ui.Database);
        var client = new ServerClient(new HttpClient(server) { BaseAddress = new Uri("http://server/") }, "token");
        var work = new PageWork(client);
        var view = new BudgetView { Work = work };
        ui.Host(view);
        view.Connect(new ServerBudgetData(client), () => { });
        var entries = UiTest.Named<ListBox>(UiTest.Named<NestedColumn>(view, "Column"), "Entries");
        ServerToDoClickTests.PumpUntil(ui, () => ServerToDoClickTests.Settled(work) && entries.Items.Count > 0, "the page");
        entries.SelectedItem = entries.Items.Cast<ColumnEntry>().Single(entry => entry.Key == "waiting");
        ServerToDoClickTests.PumpUntil(ui, () => ServerToDoClickTests.Settled(work) && Waiting(view).Count == 2, "the waits");

        server.Release.Reset();
        view.UpdateLayout();
        var rows = UiTest.Descendants<Grid>(view).Where(grid => grid.DataContext?.GetType().Name == "WaitRow").ToList();
        ui.Choose(ui.RightClick(rows.First(row => UiTest.Prop<string>(row.DataContext!, "Title") == "headphones"))!, "Let it go");
        ui.Choose(ui.RightClick(rows.First(row => UiTest.Prop<string>(row.DataContext!, "Title") == "bike lights"))!, "Let it go");

        // Both clicks came back while the server still held the first change.
        Assert.Empty(server.Changes);
        Assert.True(server.ChangeArrived.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(work.ChangesWaiting);

        server.Release.Set();
        ServerToDoClickTests.PumpUntil(ui, () => ServerToDoClickTests.Settled(work), "the changes");
        var waits = BudgetPage.Read(ui.Database, Today.AddDays(1 - Today.Day), Today).WaitList;
        string Resolve(string description) => $"/api/budget/waits/{waits.Single(item => item.Description == description).Id}/resolve";
        Assert.Equal(2, server.Changes.Count);
        Assert.StartsWith(Resolve("headphones"), server.Changes[0]);
        Assert.StartsWith(Resolve("bike lights"), server.Changes[1]);
    });

    [Fact]
    public void A_form_saves_once_without_holding_the_window_and_stays_open_until_the_answer_comes() => Ui.Run(ui =>
    {
        var server = new HeldServer(ui.Database);
        var client = new ServerClient(new HttpClient(server) { BaseAddress = new Uri("http://server/") }, "token");
        var work = new PageWork(client);
        var data = new ServerBudgetData(client);
        var owner = Window.GetWindow(ui.Host(new Grid()));
        var form = new BudgetFormWindow(owner, "New category", "Add", Today)
        {
            Work = work,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
        };
        form.AddText("name", "Name, such as Fun");
        form.Submit = values =>
        {
            data.AddCategory(values.Text("name"), null, BudgetPeriod.Month);
            return new OpReply("Made it", true);
        };
        form.Show();
        ui.Pump();
        try
        {
            UiTest.Descendants<TextBox>(form).First().Text = "Made up snacks";
            var done = UiTest.Named<Button>(form, "DoneButton");
            server.Release.Reset();
            UiTest.Call(form, "OnDoneClicked", form, new RoutedEventArgs());

            // It came back while the server still held the save.
            Assert.Empty(server.Changes);
            Assert.True(server.ChangeArrived.Wait(TimeSpan.FromSeconds(30)));

            // It can't be pressed again meanwhile, so it's saved once.
            Assert.False(done.IsEnabled);
            Assert.Equal("Saving...", done.Content);

            // Typing more meanwhile doesn't change what's on its way.
            UiTest.Descendants<TextBox>(form).First().Text = "Made up snacks and drinks";
            form.Close();
            Assert.True(form.IsVisible);

            server.Release.Set();
            ServerToDoClickTests.PumpUntil(ui, () => ServerToDoClickTests.Settled(work) && done.IsEnabled, "the save");
            var change = Assert.Single(server.Changes);
            Assert.Contains("Made up snacks\"", change);
            Assert.Equal("Add", done.Content);
            Assert.Contains("Made it", new TextRange(UiTest.Named<TextBlock>(form, "Answer").ContentStart, UiTest.Named<TextBlock>(form, "Answer").ContentEnd).Text);
        }
        finally
        {
            form.Close();
        }
    });
}
