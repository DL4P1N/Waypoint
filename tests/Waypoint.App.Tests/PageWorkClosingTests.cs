using System.Net;
using System.Net.Http;
using System.Text;
using Waypoint.App;
using Waypoint.Core.Remote;

namespace Waypoint.App.Tests;

/// <summary>Closing with several pages' changes still on their way to the server.</summary>
public sealed class PageWorkClosingTests
{
    /// <summary>A server where the first page's change is only answered once the second page's change has arrived.</summary>
    private sealed class TwoPagesServer : HttpMessageHandler
    {
        public ManualResetEventSlim SecondArrived { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/second-change")
            {
                SecondArrived.Set();
            }
            else if (path == "/api/first-change")
            {
                await Task.Run(() => SecondArrived.Wait(TimeSpan.FromSeconds(10)), cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("\"pea shoots\"", Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public void Closing_waits_for_every_pages_changes_together_while_a_later_page_has_a_drawing_queued() => Ui.Run(ui =>
    {
        var server = new TwoPagesServer();
        var client = new ServerClient(new HttpClient(server) { BaseAddress = new Uri("http://server/") }, "token");
        var first = new PageWork(client);
        var second = new PageWork(client);

        // The second page is drawn from nothing kept, so it's read and then drawn again on this thread, and its change
        // waits behind that drawing. The first page's change is only answered once the second one's has arrived.
        second.Show(() => client.GetOr("api/second-page", ""));
        var secondChange = second.Run(() => client.Send(HttpMethod.Post, "api/second-change"), "Second change");
        var firstChange = first.Run(() => client.Send(HttpMethod.Post, "api/first-change"), "First change");

        // This thread is held while closing waits, so the second page's drawing can never happen. It still carries on.
        Assert.Empty(PageWork.FinishAll([first, second], TimeSpan.FromSeconds(3)));
        Assert.True(server.SecondArrived.IsSet);
        Assert.True(firstChange.IsCompletedSuccessfully);
        Assert.True(secondChange.IsCompletedSuccessfully);
        ui.Pump();
    });
}
