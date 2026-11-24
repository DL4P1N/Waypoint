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

    /// <summary>Answers each read, or throws as if the server can't be reached. Open to-dos include one to find.</summary>
    private sealed class Reachable(bool up) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!up)
            {
                throw new HttpRequestException("No route to host");
            }

            object body = request.RequestUri!.AbsolutePath == "/api/items/open"
                ? new List<StoredItem> { new(9, ItemKind.Task, "Feed the zebra", null, null, false) }
                : new List<object>();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), RemoteJson.Options), Encoding.UTF8, "application/json") });
        }
    }

    private static ServerClient Client(HttpMessageHandler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("http://server/") }, "token");

    private static (FindView View, ListBox Results, TextBlock Hint) Searched(UiTest ui, FindSources sources, string text = "zebra")
    {
        var view = new FindView();
        view.Connect(sources);
        ui.Host(view);
        var results = UiTest.Named<ListBox>(view, "Results");
        var hint = UiTest.Named<TextBlock>(view, "Hint");
        UiTest.Named<TextBox>(view, "SearchBox").Text = text;
        view.Search();
        ServerToDoClickTests.PumpUntil(ui, () => hint.Text != FindView.StartHint && hint.Text != FindView.SearchingHint, "the search");
        ui.Pump();
        return (view, results, hint);
    }

    [Fact]
    public void Searching_shows_only_once_a_search_takes_a_moment_and_goes_once_it_answers() => Ui.Run(ui =>
    {
        var (view, box, server) = Open(ui);
        var hint = UiTest.Named<TextBlock>(view, "Hint");
        var results = UiTest.Named<ListBox>(view, "Results");

        view.Search();
        Assert.True(server.Arrived.Wait(TimeSpan.FromSeconds(5)));
        Assert.NotEqual(FindView.SearchingHint, hint.Text);
        Assert.True(UiTest.Field<System.Windows.Threading.DispatcherTimer>(view, "_searching")!.IsEnabled);

        // The moment passes with the server still held.
        UiTest.Call(view, "OnSearchingTick");
        Assert.Equal(FindView.SearchingHint, hint.Text);
        Assert.Equal(Visibility.Visible, hint.Visibility);

        server.Release.Set();
        ServerToDoClickTests.PumpUntil(ui, () => results.Items.Count == 1, "the results");
        Assert.Equal(FindView.FoundCount(1), hint.Text);
        Assert.False(UiTest.Field<System.Windows.Threading.DispatcherTimer>(view, "_searching")!.IsEnabled);
    });

    [Fact]
    public void Leaving_find_while_searching_shows_takes_the_searching_note_away() => Ui.Run(ui =>
    {
        var (view, box, server) = Open(ui);
        var hint = UiTest.Named<TextBlock>(view, "Hint");

        ui.Press(box, Key.Enter);
        Assert.True(server.Arrived.Wait(TimeSpan.FromSeconds(5)));
        UiTest.Call(view, "OnSearchingTick");
        Assert.Equal(FindView.SearchingHint, hint.Text);
        view.Leave();
        Assert.Equal(FindView.StartHint, hint.Text);

        Answer(ui, view, server);
        Assert.Equal(FindView.StartHint, hint.Text);
        Assert.Empty(UiTest.Named<ListBox>(view, "Results").Items);
    });

    [Fact]
    public void When_some_places_cant_be_reached_the_results_that_came_show_with_a_quiet_note() => Ui.Run(ui =>
    {
        var up = Client(new Reachable(up: true));
        var away = Client(new Reachable(up: false));
        var (_, results, hint) = Searched(ui, new FindSources(new ServerToDoData(up), new ServerListsData(away), null, null, null));

        Assert.Equal(["Feed the zebra"], results.Items.Cast<FoundThing>().Select(thing => thing.Title));
        Assert.Equal(FindView.PartialHint, hint.Text);
        Assert.Equal("Some places couldn't be searched just now.", hint.Text);
    });

    [Fact]
    public void When_no_place_can_be_reached_find_says_so_rather_than_nothing_matches() => Ui.Run(ui =>
    {
        var away = Client(new Reachable(up: false));
        var (_, results, hint) = Searched(ui, new FindSources(new ServerToDoData(away), new ServerListsData(away), new ServerNotesData(away), new ServerHistoryData(away), new ServerBudgetData(away)));

        Assert.Empty(results.Items);
        Assert.Equal(FindView.NoneReachedHint, hint.Text);
        Assert.DoesNotContain("matches", hint.Text, StringComparison.Ordinal);
    });

    [Fact]
    public void When_every_place_answers_and_nothing_matches_find_says_nothing_matches() => Ui.Run(ui =>
    {
        var up = Client(new Reachable(up: true));
        var (_, results, hint) = Searched(ui, new FindSources(new ServerToDoData(up), new ServerListsData(up), null, null, null), "giraffe");

        Assert.Empty(results.Items);
        Assert.Equal("Nothing on any page matches \"giraffe\".", hint.Text);
    });
}
