using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Remote;

namespace Waypoint.App.Tests;

/// <summary>The cards above Ask in server mode with a server slow to answer: their buttons never hold the window.</summary>
public sealed class ServerAskCardClickTests
{
    /// <summary>A session under way and an item held from earlier. Taking the held item, and ending the session, can be held.</summary>
    private sealed class CardServer(StoredItem held) : HttpMessageHandler
    {
        private readonly object _gate = new();
        private bool _active = true;
        private bool _taken;

        /// <summary>Each change, as it arrived.</summary>
        public List<string> Changes { get; } = [];

        public ManualResetEventSlim ReleaseChanges { get; } = new(initialState: true);

        public ManualResetEventSlim ChangeArrived { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path is "/api/session/cancel" or "/api/after-this/take")
            {
                ChangeArrived.Set();
                await Task.Run(() => ReleaseChanges.Wait(TimeSpan.FromSeconds(10)), cancellationToken);
                lock (_gate)
                {
                    if (path == "/api/session/cancel")
                    {
                        Changes.Add("cancel");
                        _active = false;
                        return Json(new OpReply("Ended Do the dishes."));
                    }

                    Changes.Add("take");
                    var item = _taken ? null : held;
                    _taken = true;
                    return Json(new HeldItemValue(item));
                }
            }

            lock (_gate)
            {
                return Json(path switch
                {
                    "/api/session" => new SessionValue(_active
                        ? new ActivationSession(1, "item:9", "Do the dishes", "Fill the sink", SessionStatus.Active, new DateTime(2030, 4, 1, 9, 0, 0), null, null, null, Activation.TypedMethod, null, [], null, 0)
                        : null),
                    _ => new { },
                });
            }
        }

        private static HttpResponseMessage Json(object body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), RemoteJson.Options), Encoding.UTF8, "application/json") };
    }

    /// <summary>Every read waits while it's held, and finds nothing.</summary>
    private sealed class HeldReads : HttpMessageHandler
    {
        public ManualResetEventSlim Release { get; } = new(initialState: true);

        public ManualResetEventSlim Arrived { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Arrived.Set();
            await Task.Run(() => Release.Wait(TimeSpan.FromSeconds(10)), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public void Opening_find_anywhere_searches_without_holding_the_window() => Ui.Run(ui =>
    {
        var server = new HeldReads();
        var client = new ServerClient(new HttpClient(server) { BaseAddress = new Uri("http://server/") }, "token");
        var view = new FindView();
        view.Connect(new FindSources(new ServerToDoData(client), new ServerListsData(client), null, null, null));
        ui.Host(view);
        var box = UiTest.Named<TextBox>(view, "SearchBox");
        var hint = UiTest.Named<TextBlock>(view, "Hint");
        server.Release.Reset();
        box.Text = "zebra";

        view.Search();

        // It came back while the server still held the search.
        Assert.True(server.Arrived.Wait(TimeSpan.FromSeconds(5)));
        ui.Pump();
        Assert.NotEqual("Nothing on any page matches \"zebra\".", hint.Text);

        server.Release.Set();
        ServerToDoClickTests.PumpUntil(ui, () => hint.Text == "Nothing on any page matches \"zebra\".", "the search");
    });

    private static Button ButtonIn(DependencyObject root, string label) =>
        UiTest.Descendants<Button>(root).First(button => button.Content as string == label && button.IsVisible);

    [Fact]
    public void Bringing_back_what_was_held_and_ending_a_session_return_at_once_while_the_server_holds_them() => Ui.Run(ui =>
    {
        var essay = ui.Database.AddByHand(new HandAddedItem(ItemKind.Task, "Write the essay", DateOnly.FromDateTime(DateTime.Now).AddDays(1)), DateTime.Now).Item!;
        var server = new CardServer(essay);
        var client = new ServerClient(new HttpClient(server) { BaseAddress = new Uri("http://server/") }, "token");
        server.ReleaseChanges.Reset();
        var window = new ItemsWindow(ui.Database, _ => { }, () => { }, _ => { }, sessions: new ServerSessionData(client), feedback: new ServerFeedbackData(client), server: client)
        {
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
        };
        try
        {
            // Opening takes the held item. It came back while the server still held that.
            window.ShowList();
            Assert.True(server.ChangeArrived.Wait(TimeSpan.FromSeconds(5)));
            ui.Pump();
            var card = UiTest.Named<Border>(window, "AfterThisCard");
            Assert.Equal(Visibility.Collapsed, card.Visibility);
            Assert.Empty(server.Changes);

            server.ReleaseChanges.Set();
            ServerToDoClickTests.PumpUntil(ui, () => card.Visibility == Visibility.Visible, "the held item");
            Assert.Equal("Earlier you meant to: Write the essay", UiTest.Named<TextBlock>(window, "AfterThisLine").Text);
            Assert.Equal(["take"], server.Changes);

            // End comes back while the server holds it, and can't be pressed again meanwhile.
            window.Refresh(0, null, null, null, null, null);
            var sessionCard = UiTest.Named<Border>(window, "SessionCard");
            ServerToDoClickTests.PumpUntil(ui, () => sessionCard.Visibility == Visibility.Visible, "the session");
            var end = ButtonIn(sessionCard, "End");
            server.ChangeArrived.Reset();
            server.ReleaseChanges.Reset();
            end.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, end));
            Assert.True(server.ChangeArrived.Wait(TimeSpan.FromSeconds(5)));
            ui.Pump();
            Assert.Equal(["take"], server.Changes);
            Assert.False(end.IsEnabled);

            server.ReleaseChanges.Set();
            ServerToDoClickTests.PumpUntil(ui, () => end.IsEnabled && server.Changes.Count == 2, "the end");
            Assert.Equal(["take", "cancel"], server.Changes);
        }
        finally
        {
            window.Hide();
        }
    });
}
