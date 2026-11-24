using System.Net;
using Waypoint.Core.Remote;

namespace Waypoint.Core.Tests;

/// <summary>
/// Changes that mustn't happen twice, sent from the PC in server mode: each goes with a key of its own, sent again
/// with the same key when no answer came back, and a server that's only slow isn't taken to be away.
/// </summary>
public sealed class ServerOnceTests
{
    /// <summary>
    /// Answers each change, or holds it past the wait, as a server that did it but answered too late. Unreached, it
    /// holds a change before it's sent, as a server that can't be connected to. Says, as a server that keeps changes
    /// once does, whether it did it now or only answered again, unless it's one from before.
    /// </summary>
    private sealed class SlowServer : HttpMessageHandler
    {
        public bool Slow { get; set; }

        public bool Unreached { get; set; }

        public bool FromBefore { get; set; }

        public string Said { get; set; } = ServerClient.OnceDone;

        public List<(string Path, string? Once)> Received { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Unreached)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (request.Content is not null)
            {
                await request.Content.ReadAsStringAsync(cancellationToken);
            }

            var once = request.Headers.TryGetValues(ServerClient.OnceHeader, out var values) ? values.Single() : null;
            lock (Received)
            {
                Received.Add((request.RequestUri!.AbsolutePath, once));
            }

            if (Slow)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"text":"Added $4.50 for coffee","recorded":true}""") };
            if (once is not null && !FromBefore)
            {
                response.Headers.Add(ServerClient.OnceHeader, Said);
            }

            return response;
        }
    }

    private readonly SlowServer _server = new();

    private DateTime _now = new(2026, 5, 6, 9, 0, 0, DateTimeKind.Utc);

    private ServerClient Client() => new(new HttpClient(_server) { BaseAddress = new Uri("http://server/"), Timeout = TimeSpan.FromMilliseconds(200) }, "token", "pc-1")
    {
        UtcNow = () => _now,
    };

    private string?[] Keys() => _server.Received.Select(sent => sent.Once).ToArray();

    [Fact]
    public void An_expense_the_server_was_slow_to_answer_says_it_may_have_gone_through_and_the_server_isnt_taken_to_be_away()
    {
        var client = Client();
        var budget = new ServerBudgetData(client);
        _server.Slow = true;

        var slow = Assert.Throws<ServerUnavailableException>(() => budget.AddExpense("coffee", 450, null, new DateOnly(2026, 5, 6), _now.ToLocalTime()));

        Assert.True(slow.TimedOut);
        Assert.Contains("may have gone through", slow.Message);
        Assert.Equal(slow.Message, PageMessages.CantChange(slow));
        Assert.NotEqual(ServerState.Offline, client.State);
        Assert.True(client.IsOnline);
    }

    [Fact]
    public void Saving_the_same_expense_again_after_no_answer_goes_with_the_same_key_and_a_new_one_after_an_answer()
    {
        var client = Client();
        var budget = new ServerBudgetData(client);
        var day = new DateOnly(2026, 5, 6);
        _server.Slow = true;
        Assert.Throws<ServerUnavailableException>(() => budget.AddExpense("coffee", 450, null, day, _now));

        _server.Slow = false;
        budget.AddExpense("coffee", 450, null, day, _now);
        budget.AddExpense("coffee", 450, null, day, _now);

        var keys = Keys();
        Assert.Equal(3, keys.Length);
        Assert.All(keys, key => Assert.StartsWith("pc-1:", key));
        Assert.Equal(keys[0], keys[1]);
        Assert.NotEqual(keys[1], keys[2]);
    }

    [Fact]
    public void Something_else_sent_after_no_answer_gets_a_key_of_its_own()
    {
        var client = Client();
        var budget = new ServerBudgetData(client);
        var day = new DateOnly(2026, 5, 6);
        _server.Slow = true;
        Assert.Throws<ServerUnavailableException>(() => budget.AddExpense("coffee", 450, null, day, _now));

        _server.Slow = false;
        budget.AddExpense("lunch", 1200, null, day, _now);

        var keys = Keys();
        Assert.NotEqual(keys[0], keys[1]);
    }

    [Fact]
    public void The_same_sent_again_long_after_no_answer_is_taken_to_be_a_new_one()
    {
        var client = Client();
        var budget = new ServerBudgetData(client);
        var day = new DateOnly(2026, 5, 6);
        _server.Slow = true;
        Assert.Throws<ServerUnavailableException>(() => budget.AddExpense("coffee", 450, null, day, _now));

        _server.Slow = false;
        _now += ServerClient.OnceReuse + TimeSpan.FromMinutes(1);
        budget.AddExpense("coffee", 450, null, day, _now);

        var keys = Keys();
        Assert.NotEqual(keys[0], keys[1]);
    }

    [Fact]
    public void A_dose_taken_again_after_no_answer_is_the_same_dose_even_if_the_item_was_read_again_since()
    {
        var client = Client();
        var toDo = new ServerToDoData(client);
        var pill = new Capture.StoredItem(12, Capture.ItemKind.Task, "Vitamin D", null, null, false);
        _server.Slow = true;
        Assert.Throws<ServerUnavailableException>(() => toDo.TakeDose(pill, _now));

        _server.Slow = false;
        try
        {
            toDo.TakeDose(pill with { Title = "Vitamin D 1000" }, _now);
        }
        catch (System.Text.Json.JsonException)
        {
            // The made-up answer isn't a dose reply. Only the key it was sent with matters here.
        }

        var keys = Keys();
        Assert.Equal(2, keys.Length);
        Assert.Equal(keys[0], keys[1]);
    }

    [Fact]
    public void A_list_entry_and_pay_received_and_a_repayment_each_go_with_a_key()
    {
        var client = Client();
        new ServerListsData(client).AddListEntry("Shopping", "milk", _now);
        var budget = new ServerBudgetData(client);
        budget.ConfirmPayReceived(150000, new DateOnly(2026, 5, 6));
        budget.RecordRepayment(31, 1500);

        Assert.All(Keys(), key => Assert.NotNull(key));
        Assert.Equal(3, Keys().Distinct().Count());
    }

    [Fact]
    public void A_plain_change_the_server_was_slow_to_answer_says_to_check_before_trying_again_and_keeps_the_server_online()
    {
        var client = Client();
        _server.Slow = true;

        var slow = Assert.Throws<ServerUnavailableException>(() => client.Send(HttpMethod.Post, "api/budget/categories", new { name = "Food" }));

        Assert.True(slow.TimedOut);
        Assert.Equal(ServerClient.NotAnsweredInTime, slow.Message);
        Assert.Null(Assert.Single(_server.Received).Once);
        Assert.True(client.IsOnline);
    }

    [Fact]
    public void An_unrelated_change_in_between_doesnt_take_the_key_of_one_still_unanswered()
    {
        var client = Client();
        var budget = new ServerBudgetData(client);
        var day = new DateOnly(2026, 5, 6);
        _server.Slow = true;
        Assert.Throws<ServerUnavailableException>(() => budget.AddExpense("coffee", 450, null, day, _now));

        _server.Slow = false;
        budget.AddExpense("lunch", 1200, null, day, _now);
        budget.AddExpense("coffee", 450, null, day, _now);

        var keys = Keys();
        Assert.Equal(keys[0], keys[2]);
        Assert.NotEqual(keys[0], keys[1]);
    }

    [Fact]
    public void The_same_is_taken_as_the_same_for_an_hour_from_when_it_was_first_sent_not_from_each_try()
    {
        var client = Client();
        var budget = new ServerBudgetData(client);
        var day = new DateOnly(2026, 5, 6);
        _server.Slow = true;
        Assert.Throws<ServerUnavailableException>(() => budget.AddExpense("coffee", 450, null, day, _now));
        _now += TimeSpan.FromMinutes(50);
        Assert.Throws<ServerUnavailableException>(() => budget.AddExpense("coffee", 450, null, day, _now));

        _server.Slow = false;
        _now += TimeSpan.FromMinutes(20);
        budget.AddExpense("coffee", 450, null, day, _now);

        var keys = Keys();
        Assert.Equal(keys[0], keys[1]);
        Assert.NotEqual(keys[1], keys[2]);
    }

    [Fact]
    public void A_change_whose_wait_ran_out_before_it_was_sent_means_the_server_is_away()
    {
        var client = Client();
        _server.Unreached = true;

        var away = Assert.Throws<ServerUnavailableException>(() => new ServerBudgetData(client).AddExpense("coffee", 450, null, new DateOnly(2026, 5, 6), _now));

        Assert.False(away.TimedOut);
        Assert.DoesNotContain("may have gone through", away.Message);
        Assert.Equal(ServerState.Offline, client.State);
    }

    [Fact]
    public void The_same_change_answered_again_says_it_was_kept_once_so_a_second_one_can_be_added()
    {
        var client = Client();
        _server.Said = ServerClient.OnceAgain;

        var reply = new ServerBudgetData(client).AddExpense("coffee", 450, null, new DateOnly(2026, 5, 6), _now);
        var dose = new ServerToDoData(client).TakeDose(new Capture.StoredItem(12, Capture.ItemKind.Task, "Vitamin D", null, null, false), _now);

        Assert.Contains("kept once", reply.Text);
        Assert.StartsWith("Added $4.50 for coffee.", reply.Text);
        Assert.Contains("kept once", dose.Text);
    }

    [Fact]
    public void A_change_done_now_says_nothing_more()
    {
        var reply = new ServerBudgetData(Client()).AddExpense("coffee", 450, null, new DateOnly(2026, 5, 6), _now);

        Assert.Equal("Added $4.50 for coffee", reply.Text);
    }

    [Fact]
    public void Only_a_server_that_said_it_keeps_changes_once_is_said_to_keep_a_slow_one_once()
    {
        var client = Client();
        var budget = new ServerBudgetData(client);
        var day = new DateOnly(2026, 5, 6);
        _server.FromBefore = true;
        budget.AddExpense("lunch", 1200, null, day, _now);
        _server.Slow = true;
        var fromBefore = Assert.Throws<ServerUnavailableException>(() => budget.AddExpense("coffee", 450, null, day, _now));
        Assert.Equal(ServerClient.NotAnsweredInTime, fromBefore.Message);

        _server.Slow = false;
        _server.FromBefore = false;
        budget.AddExpense("tea", 300, null, day, _now);
        Assert.True(client.KeepsChangesOnce);
        _server.Slow = true;
        var keeps = Assert.Throws<ServerUnavailableException>(() => budget.AddExpense("cake", 500, null, day, _now));
        Assert.Equal(ServerClient.NotAnsweredInTimeOnce, keeps.Message);
    }

    [Fact]
    public void A_read_the_server_was_slow_to_answer_still_counts_as_away()
    {
        var client = Client();
        _server.Slow = true;

        client.GetOr<List<string>>("api/items/open", []);

        Assert.Equal(ServerState.Offline, client.State);
    }
}
