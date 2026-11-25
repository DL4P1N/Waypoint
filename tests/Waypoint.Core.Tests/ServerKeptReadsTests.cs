using System.Net;
using System.Text.Json;
using Waypoint.Core.Capture;
using Waypoint.Core.Remote;

namespace Waypoint.Core.Tests;

/// <summary>Showing a page from kept answers without asking the server, then reading what it asked for again.</summary>
public sealed class ServerKeptReadsTests
{
    /// <summary>Answers with the open items as they are when the request arrives, and can hold one request.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        /// <summary>
        /// Only so a hung test fails rather than hangs. Each wait here is for a signal, so a slow machine only makes
        /// it later, never wrong.
        /// </summary>
        public static readonly TimeSpan Hung = TimeSpan.FromSeconds(60);

        private readonly object _gate = new();
        private int _requests;

        public List<string> Items { get; } = ["Water the ferns"];

        public int Requests => Volatile.Read(ref _requests);

        /// <summary>The next request waits on this once it has read the items, the way a slow answer arrives late.</summary>
        public ManualResetEventSlim? HoldNext { get; set; }

        public ManualResetEventSlim Held { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            string body;
            lock (_gate)
            {
                body = JsonSerializer.Serialize(Items.Select((title, index) => new StoredItem(index + 1, ItemKind.Task, title, null, null, false)), RemoteJson.Options);
            }

            if (Interlocked.Exchange(ref _holdNow, null) is { } hold)
            {
                Held.Set();
                await Task.Run(() => Assert.True(hold.Wait(Hung), "The held answer was never let go"), cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }

        private ManualResetEventSlim? _holdNow;

        public void HoldOne(ManualResetEventSlim release) => _holdNow = release;

        public void Set(params string[] items)
        {
            lock (_gate)
            {
                Items.Clear();
                Items.AddRange(items);
            }
        }
    }

    private const string Open = "api/items/open";

    private readonly FakeServer _fake = new();

    private ServerClient Client() => new(new HttpClient(_fake) { BaseAddress = new Uri("http://server/") }, "token");

    private static List<string> Titles(ServerClient client) => new ServerToDoData(client).GetOpenItems().Select(item => item.Title).ToList();

    [Fact]
    public void Inside_kept_reads_nothing_is_asked_and_an_old_answer_is_noted_to_read_again()
    {
        var now = new DateTime(2030, 3, 4, 10, 0, 0, DateTimeKind.Utc);
        var client = Client();
        client.UtcNow = () => now;
        Assert.Equal(["Water the ferns"], Titles(client));
        _fake.Set("Water the ferns", "Mail the form");
        now += ServerClient.RecentReadAge + TimeSpan.FromSeconds(1);

        var reads = new KeptReads();
        using (client.UseKeptReads(reads))
        {
            Assert.Equal(["Water the ferns"], Titles(client));
        }

        Assert.Equal(1, _fake.Requests);
        Assert.Equal([Open], reads.Missed);

        client.ReadAgain(reads.Missed);
        var again = new KeptReads();
        using (client.UseKeptReads(again))
        {
            Assert.Equal(["Water the ferns", "Mail the form"], Titles(client));
        }

        Assert.Empty(again.Missed);
        Assert.Equal([Open], again.Asked);
    }

    [Fact]
    public void With_nothing_kept_a_page_read_gives_its_empty_answer_without_waiting()
    {
        var client = Client();
        var reads = new KeptReads();
        using (client.UseKeptReads(reads))
        {
            Assert.Empty(Titles(client));
        }

        Assert.Equal(0, _fake.Requests);
        Assert.Equal([Open], reads.Missed);
    }

    [Fact]
    public void A_change_made_since_a_read_makes_it_one_to_read_again()
    {
        var client = Client();
        Titles(client);
        client.Send(HttpMethod.Post, "api/items/done", new { });

        var reads = new KeptReads();
        using (client.UseKeptReads(reads))
        {
            Titles(client);
        }

        Assert.Equal([Open], reads.Missed);
    }

    [Fact]
    public async Task A_slow_answer_to_an_earlier_read_never_replaces_a_later_one()
    {
        var client = Client();
        using var release = new ManualResetEventSlim();
        _fake.HoldOne(release);
        var slow = Task.Run(() => Titles(client));
        Assert.True(_fake.Held.Wait(FakeServer.Hung));

        _fake.Set("Water the ferns", "Mail the form");
        client.ReadAgain([Open]);
        release.Set();
        Assert.Equal(["Water the ferns"], await slow);

        var reads = new KeptReads();
        using (client.UseKeptReads(reads))
        {
            Assert.Equal(["Water the ferns", "Mail the form"], Titles(client));
        }
    }

    [Fact]
    public async Task A_kept_answer_changed_here_stays_over_a_read_that_was_already_on_its_way()
    {
        var client = Client();
        Titles(client);
        using var release = new ManualResetEventSlim();
        _fake.HoldOne(release);
        var slow = Task.Run(() => client.ReadAgain([Open]));
        Assert.True(_fake.Held.Wait(FakeServer.Hung));

        client.Amend<List<StoredItem>>(path => path == Open, items => [.. items, new StoredItem(9, ItemKind.Task, "Mail the form", null, null, false)]);
        release.Set();
        await slow;

        using (client.UseKeptReads(new KeptReads()))
        {
            Assert.Equal(["Water the ferns", "Mail the form"], Titles(client));
        }
    }
}
