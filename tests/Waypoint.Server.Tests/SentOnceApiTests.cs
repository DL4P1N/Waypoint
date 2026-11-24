using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Core.Background;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Email;
using Waypoint.Core.Remote;

namespace Waypoint.Server.Tests;

/// <summary>
/// Changes the PC sends with a key of its own: the same key sent again, as after a lost answer, is answered as
/// before and done once. Without a key, as from a PC from before, each is done as it always was.
/// </summary>
public sealed class SentOnceApiTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-once-{Guid.NewGuid()}");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _token;

    public SentOnceApiTests()
    {
        Directory.CreateDirectory(_folder);
        _token = ServerToken.Create(_folder);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Waypoint:DataDirectory", _folder);
            builder.UseSetting("Waypoint:RunBackground", "false");
            builder.ConfigureServices(services => services.AddSingleton(new WorkerParts(_ => new NoTokens(), CaptureSorter: new NeverSorts())));
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private sealed class NoTokens : ITokenStore
    {
        public string? Load() => null;

        public void Save(string token)
        {
        }
    }

    private sealed class NeverSorts : ICaptureSorter
    {
        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<CaptureResult?>(null);
    }

    private HttpClient Paired()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return client;
    }

    private WaypointDatabase Database => _factory.Services.GetRequiredService<WaypointServer>().Database;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private async Task<HttpResponseMessage> Post(HttpClient http, string path, object body, string? once)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, body.GetType(), options: RemoteJson.Options) };
        if (once is not null)
        {
            request.Headers.Add(ServerClient.OnceHeader, once);
        }

        var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private int Expenses(string title) => Database.SearchPurchases(title, 20).Count;

    [Fact]
    public async Task An_expense_sent_again_with_the_same_key_is_added_once_and_answered_as_before()
    {
        using var http = Paired();
        var expense = new ExpenseRequest("Made-up bakery", 450, null, Today);

        var first = await (await Post(http, "api/budget/expenses", expense, "pc-1:a")).Content.ReadFromJsonAsync<OpReply>(RemoteJson.Options);
        var again = await (await Post(http, "api/budget/expenses", expense, "pc-1:a")).Content.ReadFromJsonAsync<OpReply>(RemoteJson.Options);

        Assert.Equal(1, Expenses("bakery"));
        Assert.Equal(first, again);
        Assert.NotNull(first!.ItemId);
    }

    [Fact]
    public async Task The_answer_says_whether_a_change_was_done_now_or_only_answered_again()
    {
        using var http = Paired();
        var expense = new ExpenseRequest("Made-up bakery", 450, null, Today);

        using var first = await Post(http, "api/budget/expenses", expense, "pc-1:h");
        using var again = await Post(http, "api/budget/expenses", expense, "pc-1:h");
        using var none = await Post(http, "api/budget/expenses", expense, once: null);

        Assert.Equal(ServerClient.OnceDone, first.Headers.GetValues(ServerClient.OnceHeader).Single());
        Assert.Equal(ServerClient.OnceAgain, again.Headers.GetValues(ServerClient.OnceHeader).Single());
        Assert.False(none.Headers.Contains(ServerClient.OnceHeader));
    }

    [Fact]
    public async Task A_dose_sent_again_with_the_same_key_is_answered_as_before()
    {
        using var http = Paired();
        var item = Database.AddByHand(new HandAddedItem(ItemKind.Task, "Made-up vitamin", null), DateTime.Now).Item!;

        var took = await (await Post(http, "api/items/took", new ItemRequest(item), "pc-1:i")).Content.ReadAsStringAsync();
        using var again = await Post(http, "api/items/took", new ItemRequest(item), "pc-1:i");

        Assert.Equal(took, await again.Content.ReadAsStringAsync());
        Assert.Equal(ServerClient.OnceAgain, again.Headers.GetValues(ServerClient.OnceHeader).Single());
        Assert.NotNull(Database.SentOnceAnswer("pc-1:i"));
    }

    [Fact]
    public async Task The_same_expense_with_a_new_key_or_none_is_added_again()
    {
        using var http = Paired();
        var expense = new ExpenseRequest("Made-up bakery", 450, null, Today);

        await Post(http, "api/budget/expenses", expense, "pc-1:a");
        await Post(http, "api/budget/expenses", expense, "pc-1:b");
        await Post(http, "api/budget/expenses", expense, once: null);
        await Post(http, "api/budget/expenses", expense, once: null);

        Assert.Equal(4, Expenses("bakery"));
    }

    [Fact]
    public async Task A_list_entry_sent_again_with_the_same_key_is_added_once()
    {
        using var http = Paired();
        var entry = new ListEntryRequest(WaypointDatabase.ShoppingList, "oat milk");

        await Post(http, "api/lists/entries", entry, "pc-1:c");
        var added = Database.GetListEntries(WaypointDatabase.ShoppingList).Single(found => found.Text == "oat milk");

        // Checked off meanwhile, as from another PC: the same add sent again doesn't put it back on.
        Database.SetEntryDone(added, true, DateTime.Now);
        await Post(http, "api/lists/entries", entry, "pc-1:c");

        Assert.DoesNotContain(Database.GetListEntries(WaypointDatabase.ShoppingList), found => found.Text == "oat milk" && !found.Done);
        Assert.NotNull(Database.SentOnceAnswer("pc-1:c"));

        // Without a key, as from a PC from before, it goes back on as it always did.
        await Post(http, "api/lists/entries", entry, once: null);
        Assert.Contains(Database.GetListEntries(WaypointDatabase.ShoppingList), found => found.Text == "oat milk" && !found.Done);
    }

    [Fact]
    public async Task Pay_received_and_a_repayment_sent_again_with_the_same_key_give_the_same_answer()
    {
        using var http = Paired();
        var bought = (await (await Post(http, "api/budget/expenses", new ExpenseRequest("Made-up dinner for two", 4000, null, Today), "pc-1:d"))
            .Content.ReadFromJsonAsync<OpReply>(RemoteJson.Options))!.ItemId!.Value;

        var repaid = await (await Post(http, $"api/budget/purchases/{bought}/repayment", new AmountRequest(2000), "pc-1:e")).Content.ReadAsStringAsync();
        var repaidAgain = await (await Post(http, $"api/budget/purchases/{bought}/repayment", new AmountRequest(2000), "pc-1:e")).Content.ReadAsStringAsync();
        var paid = await (await Post(http, "api/budget/pay-received", new AmountRequest(150000), "pc-1:f")).Content.ReadAsStringAsync();
        var paidAgain = await (await Post(http, "api/budget/pay-received", new AmountRequest(150000), "pc-1:f")).Content.ReadAsStringAsync();

        Assert.Equal(repaid, repaidAgain);
        Assert.Equal(paid, paidAgain);
        Assert.Equal(2000, Database.SearchPurchases("dinner", 20).Single().ReimbursedCents);
    }

    [Fact]
    public void A_key_is_forgotten_after_a_day()
    {
        var now = new DateTime(2026, 5, 6, 9, 0, 0, DateTimeKind.Utc);
        Database.Once("pc-1:old", now, _ => 1);

        Database.Once("pc-1:new", now + WaypointDatabase.SentOnceKept + TimeSpan.FromMinutes(1), _ => 2);

        Assert.Null(Database.SentOnceAnswer("pc-1:old"));
        Assert.Equal("2", Database.SentOnceAnswer("pc-1:new"));
    }

    [Fact]
    public void A_change_that_fails_keeps_no_key_so_sending_it_again_tries_it_again()
    {
        Assert.Throws<InvalidOperationException>(() => Database.Once<int>("pc-1:g", DateTime.Now, _ => throw new InvalidOperationException("made up")));

        Assert.Null(Database.SentOnceAnswer("pc-1:g"));
        Assert.Equal(3, Database.Once("pc-1:g", DateTime.Now, _ => 3));
    }
}
