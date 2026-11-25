using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Tests;

/// <summary>A slow press doesn't keep the next one from being answered in time, and presses still take effect in order.</summary>
public sealed class DiscordSlowPressTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;
    private readonly List<(string Url, string Body)> _requests = [];

    public DiscordSlowPressTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    /// <summary>Keeps every request, safely, since presses are answered from more than one thread.</summary>
    private sealed class FakeDiscord(List<(string Url, string Body)> requests) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (requests)
            {
                requests.Add((request.RequestUri!.ToString(), body));
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private List<(string Url, string Body)> Requests()
    {
        lock (_requests)
        {
            return [.. _requests];
        }
    }

    private DiscordBridge Bridge(TimeSpan deferAfter) =>
        new(new HttpClient(new FakeDiscord(_requests)), _database, new DiscordSettings("token", "owner", "100", "200", "300"))
        {
            DeferAfter = deferAfter,
            Delay = (_, _) => Task.CompletedTask,
        };

    private static JsonElement Press(string id, string message) => JsonDocument.Parse($$$"""
        {"id":"{{{id}}}","token":"tok{{{id}}}","application_id":"app1","type":3,"data":{"custom_id":"{{{DiscordBridge.ActionPrefix}}}made-up","component_type":2},
         "member":{"user":{"id":"owner"}},"message":{"id":"{{{message}}}","embeds":[{"title":"Made-up card"}]}}
        """).RootElement.Clone();

    private int Callbacks(string id) => Requests().Count(request => request.Url.Contains($"/interactions/{id}/tok{id}/callback"));

    [Fact]
    public async Task A_slow_press_does_not_stop_the_next_one_being_promised_at_once_and_they_still_take_effect_in_order()
    {
        var bridge = Bridge(TimeSpan.FromMilliseconds(100));
        var gateway = new DiscordGateway("token", bridge.HandleEventAsync, arrived: bridge.EventArrived);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        bridge.Acted = async press =>
        {
            lock (order)
            {
                order.Add($"{press.MessageId} begins");
            }

            if (press.MessageId == "m1")
            {
                await release.Task;
            }

            lock (order)
            {
                order.Add($"{press.MessageId} ends");
            }

            return new ButtonResult(Outcome: $"Done with {press.MessageId}");
        };

        try
        {
            gateway.Deliver("INTERACTION_CREATE", Press("1", "m1"));
            gateway.Deliver("INTERACTION_CREATE", Press("2", "m2"));

            // The second is promised while the first is still going, since the first is held until the end. How long
            // that takes isn't checked against the clock, which a busy machine can't keep to, only that it doesn't
            // wait for the first. The limit is only so a hung test fails rather than hangs.
            Assert.True(SpinWait.SpinUntil(() => Callbacks("2") == 1, Waits.Hung), "The second press was never promised");
            Assert.Contains("\"type\":6", Requests().Single(request => request.Url.Contains("/interactions/2/")).Body);

            // The first has its own promise, which a busy machine can send after the second's. Letting it go before
            // then would have it answered directly, with nothing left to fill in.
            Assert.True(SpinWait.SpinUntil(() => Callbacks("1") == 1, Waits.Hung), "The first press was never promised");
            Assert.True(SpinWait.SpinUntil(() => { lock (order) { return order.Count > 0; } }, Waits.Hung));
            lock (order)
            {
                Assert.Equal(["m1 begins"], order);
            }
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.True(SpinWait.SpinUntil(() => Requests().Count(request => request.Url.Contains("/messages/@original")) == 2, Waits.Hung), "The answers were never filled in");
        Assert.Equal(["m1 begins", "m1 ends", "m2 begins", "m2 ends"], order);

        // Each is answered once, then filled in through its own promise.
        Assert.Equal(1, Callbacks("1"));
        Assert.Equal(1, Callbacks("2"));
        Assert.Contains("Done with m1", Requests().Single(request => request.Url.EndsWith("/webhooks/app1/tok1/messages/@original")).Body);
        Assert.Contains("Done with m2", Requests().Single(request => request.Url.EndsWith("/webhooks/app1/tok2/messages/@original")).Body);
    }

    [Fact]
    public async Task Taps_left_undone_when_Waypoint_closes_say_plainly_they_didnt_go_through()
    {
        var bridge = Bridge(TimeSpan.FromMilliseconds(50));

        // One is read and promised, but never dealt with before closing.
        bridge.EventArrived("INTERACTION_CREATE", Press("1", "m1"));
        Assert.True(SpinWait.SpinUntil(() => Callbacks("1") == 1, TimeSpan.FromSeconds(30)));

        // Another is read just as it closes, before its promise.
        bridge.DeferAfter = TimeSpan.FromMinutes(1);
        bridge.EventArrived("INTERACTION_CREATE", Press("2", "m2"));

        await bridge.AnswerDroppedAsync();
        await bridge.AnswerDroppedAsync();

        // The promised one gets a note under the card, which stays as it was. The other is answered with the note.
        var note = Requests().Single(request => request.Url.EndsWith("/webhooks/app1/tok1"));
        Assert.Contains("go through and nothing changed", note.Body);
        Assert.Contains("\"flags\":64", note.Body);
        Assert.Equal(1, Callbacks("1"));
        var answered = Requests().Single(request => request.Url.Contains("/interactions/2/tok2/callback"));
        Assert.Contains("\"type\":4", answered.Body);
        Assert.Contains("go through and nothing changed", answered.Body);
        Assert.DoesNotContain(Requests(), request => request.Url.Contains("/messages/@original"));
    }
}
