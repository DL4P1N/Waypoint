using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Remote;

namespace Waypoint.App.Tests;

/// <summary>Find anywhere in server mode, where Enter and Down wait for the search: leaving Find meanwhile goes nowhere.</summary>
public sealed class ServerFindClickTests
{
    /// <summary>Every read waits while it's held. One list has an entry to find.</summary>
    private sealed class HeldLists : HttpMessageHandler
    {
        public ManualResetEventSlim Release { get; } = new(initialState: true);

        public ManualResetEventSlim Arrived { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Arrived.Set();
            await Task.Run(() => Release.Wait(TimeSpan.FromSeconds(10)), cancellationToken);
            object body = request.RequestUri!.AbsolutePath == "/api/lists"
                ? new List<ListOverview> { new("Shopping", [new ListEntry(5, "Shopping", "zebra cakes", false, new DateTime(2030, 1, 1))]) }
                : new List<object>();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), RemoteJson.Options), Encoding.UTF8, "application/json") };
        }
    }

    private static (FindView View, TextBox Box, HeldLists Server) Open(UiTest ui)
    {
        var server = new HeldLists();
        var client = new ServerClient(new HttpClient(server) { BaseAddress = new Uri("http://server/") }, "token");
        var view = new FindView();
        view.Connect(new FindSources(new ServerToDoData(client), new ServerListsData(client), null, null, null));
        ui.Host(view);
        var box = UiTest.Named<TextBox>(view, "SearchBox");
        server.Release.Reset();
        box.Text = "zebra";
        ui.Focus(box);
        return (view, box, server);
    }

    /// <summary>Lets the held search answer, and waits until Enter or Down has done whatever it was going to.</summary>
    private static void Answer(UiTest ui, FindView view, HeldLists server)
    {
        server.Release.Set();
        ServerToDoClickTests.PumpUntil(ui, () => view.Waiting.IsCompleted, "the search");
        ui.Pump();
    }

    [Fact]
    public void Enter_picks_the_first_result_once_the_server_answers() => Ui.Run(ui =>
    {
        var (view, box, server) = Open(ui);
        FoundThing? chosen = null;
        view.Chosen += thing => chosen = thing;

        ui.Press(box, Key.Enter);
        Assert.True(server.Arrived.Wait(TimeSpan.FromSeconds(5)));
        Assert.Null(chosen);
        Answer(ui, view, server);

        Assert.Equal("zebra cakes", chosen?.Title);
    });

    [Fact]
    public void Escape_while_enter_waits_on_the_server_goes_nowhere() => Ui.Run(ui =>
    {
        var (view, box, server) = Open(ui);
        FoundThing? chosen = null;
        var closed = false;
        view.Chosen += thing => chosen = thing;
        view.Closed += () => closed = true;

        ui.Press(box, Key.Enter);
        Assert.True(server.Arrived.Wait(TimeSpan.FromSeconds(5)));
        ui.Press(box, Key.Escape);
        Assert.True(closed);
        Answer(ui, view, server);

        Assert.Null(chosen);
    });

    [Fact]
    public void Close_while_enter_waits_on_the_server_goes_nowhere() => Ui.Run(ui =>
    {
        var (view, box, server) = Open(ui);
        FoundThing? chosen = null;
        view.Chosen += thing => chosen = thing;

        ui.Press(box, Key.Enter);
        Assert.True(server.Arrived.Wait(TimeSpan.FromSeconds(5)));
        UiTest.Descendants<Button>(view).First(button => button.Content as string == "Close")
            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Answer(ui, view, server);

        Assert.Null(chosen);
    });

    [Fact]
    public void Switching_page_while_enter_waits_on_the_server_goes_nowhere() => Ui.Run(ui =>
    {
        var (view, box, server) = Open(ui);
        FoundThing? chosen = null;
        view.Chosen += thing => chosen = thing;

        ui.Press(box, Key.Enter);
        Assert.True(server.Arrived.Wait(TimeSpan.FromSeconds(5)));
        view.Visibility = Visibility.Collapsed;
        Answer(ui, view, server);

        Assert.Null(chosen);
    });

    [Fact]
    public void Leaving_find_while_enter_waits_on_the_server_goes_nowhere() => Ui.Run(ui =>
    {
        var (view, box, server) = Open(ui);
        FoundThing? chosen = null;
        view.Chosen += thing => chosen = thing;

        ui.Press(box, Key.Enter);
        Assert.True(server.Arrived.Wait(TimeSpan.FromSeconds(5)));
        view.Leave();
        Answer(ui, view, server);

        Assert.Null(chosen);
    });
}
