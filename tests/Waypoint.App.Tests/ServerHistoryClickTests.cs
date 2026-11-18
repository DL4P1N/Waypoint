using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Remote;

namespace Waypoint.App.Tests;

/// <summary>History in server mode with a server slow to answer: putting rows back never holds the window, and goes in order.</summary>
public sealed class ServerHistoryClickTests
{
    private sealed class HistoryServer : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<HistoryEntry> _entries =
        [
            Entry(1, "Email advisor"),
            Entry(2, "Repot the basil"),
        ];

        public List<string> Reopened { get; } = [];

        public ManualResetEventSlim Release { get; } = new(initialState: true);

        public ManualResetEventSlim ChangeArrived { get; } = new();

        private static HistoryEntry Entry(long id, string title) =>
            new(id, new DateTime(2030, 4, 1, 9, 0, 0), HistoryAction.Done, ItemSource.Capture, id, ItemKind.Task, title, null, null, null, []);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            object body;
            if (path == "/api/history/reopen")
            {
                var sent = JsonSerializer.Deserialize<HistoryEntryRequest>(await request.Content!.ReadAsStringAsync(cancellationToken), RemoteJson.Options)!;
                ChangeArrived.Set();
                await Task.Run(() => Release.Wait(TimeSpan.FromSeconds(10)), cancellationToken);
                lock (_gate)
                {
                    Reopened.Add(sent.Entry.Title);
                    _entries.RemoveAll(entry => entry.Id == sent.Entry.Id);
                }

                body = new FlagValue(true);
            }
            else if (path.StartsWith("/api/preferences/", StringComparison.Ordinal))
            {
                body = new PreferenceValue(null);
            }
            else
            {
                lock (_gate)
                {
                    body = _entries.Select(entry => new HistoryRow(entry, true)).ToList();
                }
            }

            var json = JsonSerializer.Serialize(body, body.GetType(), RemoteJson.Options);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public void Putting_rows_back_returns_at_once_and_reaches_the_server_in_order() => Ui.Run(ui =>
    {
        var server = new HistoryServer();
        var client = new ServerClient(new HttpClient(server) { BaseAddress = new Uri("http://server/") }, "token");
        var work = new PageWork(client);
        var view = new HistoryView { Work = work };
        var changed = 0;
        view.Connect(new ServerHistoryData(client), () => changed++);
        ui.Host(view);
        work.Show(view.Refresh);
        var rows = UiTest.Named<ItemsControl>(view, "Entries");
        List<CheckBox> Boxes() => UiTest.Descendants<CheckBox>(rows).ToList();
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && Boxes().Count == 2, "the rows");

        server.Release.Reset();
        Boxes()[0].IsChecked = false;
        Boxes()[1].IsChecked = false;

        // Both clicks came back while the server still held the first change.
        Assert.Empty(server.Reopened);
        Assert.True(server.ChangeArrived.Wait(TimeSpan.FromSeconds(5)));

        server.Release.Set();
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && Boxes().Count == 0, "the changes");
        Assert.Equal(["Email advisor", "Repot the basil"], server.Reopened);
        Assert.Equal(2, changed);
    });
}
