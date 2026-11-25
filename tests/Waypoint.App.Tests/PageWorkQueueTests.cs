using System.Net;
using System.Net.Http;
using System.Text;
using Waypoint.App;
using Waypoint.Core.Remote;

namespace Waypoint.App.Tests;

/// <summary>A page's changes in server mode when reading the page again after one goes wrong.</summary>
public sealed class PageWorkQueueTests
{
    /// <summary>A server whose page read can be made to fail in a way nobody expected.</summary>
    private sealed class BreakingServer : HttpMessageHandler
    {
        public volatile bool BreakReads;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && BreakReads)
            {
                throw new NotSupportedException("The seed catalogue page can't be read.");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("\"pea shoots\"", Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public void A_change_still_finishes_when_reading_the_page_again_after_it_fails() => Ui.Run(ui =>
    {
        var server = new BreakingServer();
        var client = new ServerClient(new HttpClient(server) { BaseAddress = new Uri("http://server/") }, "token");
        var work = new PageWork(client);
        work.Show(() => client.GetOr("api/seed-page", ""));
        ServerToDoClickTests.PumpUntil(ui, () => ServerToDoClickTests.Settled(work), "the page");

        server.BreakReads = true;
        var change = work.Run(() => client.Send(HttpMethod.Post, "api/seed-change"), "Seed change");
        ServerToDoClickTests.PumpUntil(ui, () => change.IsCompleted && ServerToDoClickTests.Settled(work), "the change");

        Assert.True(change.IsCompletedSuccessfully);
        Assert.False(work.ChangesWaiting);
        Assert.Empty(work.Waiting);
    });
}
