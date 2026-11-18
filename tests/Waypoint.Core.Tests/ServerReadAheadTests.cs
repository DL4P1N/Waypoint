using System.Net;
using System.Text.Json;
using Waypoint.Core.Capture;
using Waypoint.Core.Remote;

namespace Waypoint.Core.Tests;

/// <summary>Reading ahead off the window's thread, so a page refresh answers from reads made moments before.</summary>
public sealed class ServerReadAheadTests
{
    /// <summary>Answers with the open items, counting what it's asked, and can be held to act like a slow server.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        private readonly object _gate = new();

        public bool Up { get; set; } = true;

        public List<string> Items { get; } = ["Water the ferns"];

        public List<string> Asked { get; } = [];

        /// <summary>While set, every request waits for it, the way a slow server keeps the caller waiting.</summary>
        public ManualResetEventSlim? Hold { get; set; }

        public int Count(string path)
        {
            lock (_gate)
            {
                return Asked.Count(asked => asked == path);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Asked.Add(request.RequestUri!.AbsolutePath);
            }

            if (Hold is { } hold)
            {
                await Task.Run(() => hold.Wait(TimeSpan.FromSeconds(10)), cancellationToken);
            }

            if (!Up)
            {
                throw new HttpRequestException("No route to host");
            }

            if (request.Method == HttpMethod.Post && request.Content is not null)
            {
                Items.Add(JsonSerializer.Deserialize<string>(await request.Content.ReadAsStringAsync(cancellationToken))!);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }

            var body = request.RequestUri!.AbsolutePath == "/api/items/open"
                ? JsonSerializer.Serialize(Items.Select((title, index) => new StoredItem(index + 1, ItemKind.Task, title, null, null, false)), RemoteJson.Options)
                : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    private const string Open = "/api/items/open";

    private readonly FakeServer _fake = new();

    private ServerClient Client() => new(new HttpClient(_fake) { BaseAddress = new Uri("http://server/") }, "token");

    private static List<string> Titles(ServerClient client) => new ServerToDoData(client).GetOpenItems().Select(item => item.Title).ToList();

    [Fact]
    public async Task A_refresh_inside_recent_reads_answers_from_the_read_ahead_without_asking_again()
    {
        var client = Client();
        var before = Titles(client);

        await client.RefreshReadsAsync();
        Assert.Equal(2, _fake.Count(Open));

        using (client.UseRecentReads())
        {
            Assert.Equal(before, Titles(client));
            Assert.Equal(before, Titles(client));
        }

        Assert.Equal(2, _fake.Count(Open));
    }

    [Fact]
    public async Task Outside_recent_reads_every_read_still_asks_the_server()
    {
        var client = Client();
        Titles(client);
        await client.RefreshReadsAsync();

        _fake.Items.Add("Return the library book");
        Assert.Contains("Return the library book", Titles(client));
        Assert.Equal(3, _fake.Count(Open));
    }

    [Fact]
    public async Task A_change_sent_from_here_is_never_hidden_by_a_read_from_before_it()
    {
        var client = Client();
        Titles(client);
        await client.RefreshReadsAsync();

        client.Send(HttpMethod.Post, "api/items/add", "Buy stamps");

        using (client.UseRecentReads())
        {
            Assert.Contains("Buy stamps", Titles(client));
        }
    }

    [Fact]
    public async Task A_read_older_than_the_recent_age_is_asked_for_again()
    {
        var now = new DateTime(2030, 3, 4, 10, 0, 0, DateTimeKind.Utc);
        var client = Client();
        client.UtcNow = () => now;
        Titles(client);
        await client.RefreshReadsAsync();

        now += ServerClient.RecentReadAge + TimeSpan.FromSeconds(1);
        _fake.Items.Add("Call the plumber");
        using (client.UseRecentReads())
        {
            Assert.Contains("Call the plumber", Titles(client));
        }
    }

    [Fact]
    public async Task Reading_ahead_on_a_slow_server_never_holds_up_the_caller()
    {
        var client = Client();
        Titles(client);

        using var hold = new ManualResetEventSlim();
        _fake.Hold = hold;
        var started = DateTime.UtcNow;
        var reading = client.RefreshReadsAsync();
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
        Assert.False(reading.IsCompleted);

        // Asking again while it's on its way gives the same round instead of starting another.
        Assert.Same(reading, client.RefreshReadsAsync());

        hold.Set();
        await reading;
        Assert.Equal(2, _fake.Count(Open));
    }

    [Fact]
    public async Task With_the_server_away_reading_ahead_stops_and_the_page_shows_what_was_kept()
    {
        var client = Client();
        var before = Titles(client);

        _fake.Up = false;
        await client.RefreshReadsAsync();
        Assert.Equal(ServerState.Offline, client.State);

        using (client.UseRecentReads())
        {
            Assert.Equal(before, Titles(client));
        }

        Assert.Equal(2, _fake.Count(Open));
    }

    [Fact]
    public async Task Recent_reads_answer_only_on_the_thread_that_asked_for_them()
    {
        var client = Client();
        Titles(client);
        await client.RefreshReadsAsync();

        using (client.UseRecentReads())
        {
            var elsewhere = new Thread(() => Titles(client));
            elsewhere.Start();
            elsewhere.Join();
        }

        Assert.Equal(3, _fake.Count(Open));
    }
}
