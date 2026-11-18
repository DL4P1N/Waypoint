using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Remote;
using Waypoint.Core.Schedule;

namespace Waypoint.App.Tests;

/// <summary>Settings in server mode with a server slow to answer: saving a field never holds the window, and saves go in order.</summary>
public sealed class ServerSettingsClickTests
{
    private sealed class SettingsServer : HttpMessageHandler
    {
        private readonly object _gate = new();
        private decimal _rate = 20.00m;
        private int _commute = 15;

        public List<string> Saves { get; } = [];

        public ManualResetEventSlim Release { get; } = new(initialState: true);

        public ManualResetEventSlim SaveArrived { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put && path is "/api/settings/rate" or "/api/settings/commute")
            {
                var sent = await request.Content!.ReadAsStringAsync(cancellationToken);
                SaveArrived.Set();
                await Task.Run(() => Release.Wait(TimeSpan.FromSeconds(10)), cancellationToken);
                lock (_gate)
                {
                    if (path == "/api/settings/rate")
                    {
                        _rate = JsonSerializer.Deserialize<RateRequest>(sent, RemoteJson.Options)!.Dollars ?? 0;
                        Saves.Add($"rate {_rate:0.00}");
                    }
                    else
                    {
                        _commute = int.Parse(JsonSerializer.Deserialize<TextValue>(sent, RemoteJson.Options)!.Value!, System.Globalization.CultureInfo.InvariantCulture);
                        Saves.Add($"commute {_commute}");
                    }
                }

                return Json(new OpReply("Saved."));
            }

            lock (_gate)
            {
                return Json(path switch
                {
                    "/api/settings/rate" => new NumberValue(_rate),
                    "/api/settings/commute" => new NumberValue(_commute),
                    "/api/settings/tonight" => new TextValue("Bed by 11:30 PM"),
                    "/api/settings/weekly" or "/api/settings/dates" => Array.Empty<object>(),
                    _ => new { },
                });
            }
        }

        private static HttpResponseMessage Json(object body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), RemoteJson.Options), Encoding.UTF8, "application/json") };
    }

    [Fact]
    public void Saving_fields_returns_at_once_and_the_saves_reach_the_server_in_order() => Ui.Run(ui =>
    {
        var server = new SettingsServer();
        var client = new ServerClient(new HttpClient(server) { BaseAddress = new Uri("http://server/") }, "token");
        var work = new PageWork(client);
        var view = new SettingsView { Work = work };
        view.Connect(new ServerSettingsData(client), ui.Database, null, new BedtimeSettings(), () => [], () => { });
        ui.Host(view);
        view.LoadSoon();
        var rate = UiTest.Named<TextBox>(view, "HourlyRate");
        var commute = UiTest.Named<TextBox>(view, "Commute");
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && rate.Text == "20.00" && commute.Text == "15", "the settings");

        server.Release.Reset();
        rate.Text = "21.50";
        commute.Text = "25";
        var watch = Stopwatch.StartNew();
        UiTest.Call(view, "OnHourlyRateSaved", view, new RoutedEventArgs());
        UiTest.Call(view, "OnCommuteSaved", view, new RoutedEventArgs());
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500), $"The window waited {watch.Elapsed.TotalMilliseconds:0} ms for the server");
        Assert.True(server.SaveArrived.Wait(TimeSpan.FromSeconds(5)));

        server.Release.Set();
        ServerToDoClickTests.PumpUntil(ui, () => !work.IsBusy && server.Saves.Count == 2, "the saves");
        Assert.Equal(["rate 21.50", "commute 25"], server.Saves);
        Assert.Equal("21.50", rate.Text);
        Assert.Equal("25", commute.Text);
    });

    [Fact]
    public void Without_the_page_queue_as_on_this_pcs_database_a_field_is_saved_before_the_click_returns() => Ui.Run(ui =>
    {
        var server = new SettingsServer();
        var view = new SettingsView();
        Assert.True(view.Work.RunsNow);
        view.Connect(new ServerSettingsData(new ServerClient(new HttpClient(server) { BaseAddress = new Uri("http://server/") }, "token")), ui.Database, null, new BedtimeSettings(), () => [], () => { });
        ui.Host(view);
        view.Load();
        ui.Pump();

        UiTest.Named<TextBox>(view, "Commute").Text = "25";
        UiTest.Call(view, "OnCommuteSaved", view, new RoutedEventArgs());

        Assert.Equal(["commute 25"], server.Saves);
        ui.Pump();
    });
}
