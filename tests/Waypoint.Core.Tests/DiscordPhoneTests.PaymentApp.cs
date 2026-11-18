using System.Text.Json;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Discord;
using Waypoint.Core.Email;

namespace Waypoint.Core.Tests;

/// <summary>The Tailspin Pay card in Discord: a reply to it, and its buttons, settle the payment without sorting. No network.</summary>
public sealed partial class DiscordPhoneTests
{
    /// <summary>Makes a note of anything it's given, as sorting did with the reply that started this.</summary>
    private sealed class NoteSorter : ICaptureSorter
    {
        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<CaptureResult?>(new CaptureResult([new SortedItem(ItemKind.Note, text, null, null)]));
    }

    private void Spend(string title, long cents, int daysAgo = 1)
    {
        var capture = _database.AddCapture(title);
        _database.CompleteCapture(capture, [new SortedItem(ItemKind.Expense, title, Today.AddDays(-daysAgo), cents)]);
    }

    private long Repaid(string title) =>
        _database.GetSpending(Today.AddDays(-90), Today).Single(row => row.Title == title).ReimbursedCents;

    private PaymentAppOutcome Owed(long cents = 640, string payer = "Riley Park", string email = "email-1") =>
        _database.AddRepayment(email, new PaymentAppPayment(payer, cents, null), Today)!;

    /// <summary>Posts the card for a payment and hands back its Discord ID, which the fake numbers in order.</summary>
    private async Task<string> PostCard(WaypointWorker worker, PaymentAppOutcome outcome)
    {
        await worker.PostPaymentAppAsync(outcome);
        return $"m{_discord.Of(HttpMethod.Post).Count()}";
    }

    private List<(HttpMethod Method, string Url, string Body)> RepliesUnder(string messageId) =>
        _discord.Of(HttpMethod.Post).Where(request => request.Body.Contains($"\"message_id\":\"{messageId}\"")).ToList();

    /// <summary>What a sent card says, title, text and footer, with the JSON escapes undone.</summary>
    private static string Said(string body)
    {
        var embed = JsonDocument.Parse(body).RootElement.GetProperty("embeds")[0];
        return string.Join(" ", new[] { "title", "description" }.Select(name => embed.TryGetProperty(name, out var value) ? value.GetString() : null)
            .Append(embed.TryGetProperty("footer", out var footer) ? footer.GetProperty("text").GetString() : null)
            .Where(text => text is not null));
    }

    private List<(HttpMethod Method, string Url, string Body)> EditsOf(string messageId) =>
        _discord.Of(HttpMethod.Patch).Where(request => request.Url.EndsWith($"/channels/capture/messages/{messageId}")).ToList();

    [Fact]
    public async Task A_reply_to_the_tailspinPay_card_settles_that_payment_and_grays_the_card()
    {
        Spend("TRADER JOES #123", 2000);
        Spend("Chipotle", 3000);
        var worker = await Started();
        var card = await PostCard(worker, Owed());

        worker.OnDiscordMessage(new DiscordMessage("r1", "Trader Joes", _now, [], ReplyTo: card));
        await WaitFor(() => RepliesUnder("r1").Count > 0 && EditsOf(card).Count > 0);

        Assert.Equal(640, Repaid("TRADER JOES #123"));
        Assert.Equal(0, Repaid("Chipotle"));
        Assert.Empty(_database.GetWaitingRepayments());
        Assert.Contains("Marked $6.40 from Riley Park repaid on TRADER JOES #123", Said(Assert.Single(RepliesUnder("r1")).Body));
        Assert.Contains("\"components\":[]", Assert.Single(EditsOf(card)).Body);
    }

    [Fact]
    public async Task A_reply_naming_no_recent_purchase_says_so_lists_them_and_keeps_waiting()
    {
        Spend("TRADER JOES #123", 2000);
        var worker = await Started();
        var card = await PostCard(worker, Owed());

        worker.OnDiscordMessage(new DiscordMessage("r1", "Megamart", _now, [], ReplyTo: card));
        await WaitFor(() => RepliesUnder("r1").Count > 0);

        var reply = Said(Assert.Single(RepliesUnder("r1")).Body);
        Assert.Contains("Couldn't find a recent Megamart purchase", reply);
        Assert.Contains("Trader Joes, Nov 8, $20.00 left", reply);
        Assert.Single(_database.GetWaitingRepayments());
        Assert.Empty(EditsOf(card));
        Assert.Empty(_database.GetPendingCaptures());
    }

    [Theory]
    [InlineData("not a repayment")]
    [InlineData("Skip")]
    [InlineData("It wasn't a repayment.")]
    public async Task A_reply_saying_it_isnt_a_repayment_clears_it(string typed)
    {
        Spend("TRADER JOES #123", 2000);
        var worker = await Started();
        var card = await PostCard(worker, Owed());

        worker.OnDiscordMessage(new DiscordMessage("r1", typed, _now, [], ReplyTo: card));
        await WaitFor(() => RepliesUnder("r1").Count > 0 && EditsOf(card).Count > 0);

        Assert.Empty(_database.GetWaitingRepayments());
        Assert.Equal(0, Repaid("TRADER JOES #123"));
        Assert.Contains("won't count as a repayment", Said(Assert.Single(RepliesUnder("r1")).Body));
        Assert.Single(EditsOf(card));
    }

    [Fact]
    public async Task A_reply_to_the_card_never_becomes_a_note_or_task_even_when_sorting_would_make_one()
    {
        Spend("TRADER JOES #123", 2000);
        var worker = Worker(sorter: new NoteSorter());
        await worker.RefreshDiscordViewsAsync();
        await Task.Delay(50);
        var card = await PostCard(worker, Owed());
        var itemsBefore = _database.GetOpenItems().Count;

        worker.OnDiscordMessage(new DiscordMessage("r1", "Trader Joes", _now, [], ReplyTo: card));
        await WaitFor(() => RepliesUnder("r1").Count > 0);
        await Task.Delay(100);

        Assert.Empty(_database.GetPendingCaptures());
        Assert.Equal(itemsBefore, _database.GetOpenItems().Count);
        Assert.Empty(_database.GetNotes());
        Assert.DoesNotContain("Notes", Said(Assert.Single(RepliesUnder("r1")).Body));

        // Anything else typed there is still sorted as usual.
        worker.OnDiscordMessage(new DiscordMessage("r2", "Trader Joes", _now, []));
        await WaitFor(() => _database.GetNotes().Count > 0);
        Assert.Single(_database.GetNotes());
    }

    [Fact]
    public async Task A_reply_to_a_card_sent_before_ids_were_kept_is_known_by_what_it_says()
    {
        Spend("TRADER JOES #123", 2000);
        var worker = await Started();
        Owed();

        // As Discord sends it live: the reply, with the old card it answers.
        await worker.Discord!.HandleEventAsync("MESSAGE_CREATE", Json("""
            {"id":"r1","channel_id":"capture","content":"Trader Joes","author":{"id":"owner"},
             "message_reference":{"message_id":"old-card","channel_id":"capture"},
             "referenced_message":{"id":"old-card","author":{"id":"bot","bot":true},"content":"",
               "embeds":[{"title":"Tailspin Pay","description":"Riley Park paid you $6.40. Which purchase was it for?\nReply here with the purchase, and it's matched."}]}}
            """));
        await WaitFor(() => RepliesUnder("r1").Count > 0 && EditsOf("old-card").Count > 0);

        Assert.Equal(640, Repaid("TRADER JOES #123"));
        Assert.Empty(_database.GetWaitingRepayments());
        Assert.Empty(_database.GetPendingCaptures());
        Assert.Single(EditsOf("old-card"));
    }

    [Fact]
    public async Task A_reply_to_any_other_message_is_captured_as_usual()
    {
        var worker = await Started();
        Owed();

        worker.OnDiscordMessage(new DiscordMessage("r1", "buy stamps", _now, [], ReplyTo: "some-reminder", ReplyToText: "Call mom"));

        Assert.Equal("buy stamps", Assert.Single(_database.GetPendingCaptures()).Text);
        Assert.Single(_database.GetWaitingRepayments());
    }

    [Fact]
    public async Task A_second_reply_to_a_settled_card_changes_nothing()
    {
        Spend("TRADER JOES #123", 2000);
        var worker = await Started();
        var card = await PostCard(worker, Owed());
        worker.OnDiscordMessage(new DiscordMessage("r1", "Trader Joes", _now, [], ReplyTo: card));

        worker.OnDiscordMessage(new DiscordMessage("r2", "Trader Joes", _now, [], ReplyTo: card));
        await WaitFor(() => RepliesUnder("r2").Count > 0);

        Assert.Equal(640, Repaid("TRADER JOES #123"));
        Assert.Contains("already settled", Said(Assert.Single(RepliesUnder("r2")).Body));
        Assert.Empty(_database.GetPendingCaptures());
    }

    private static List<(string Label, string Id, int Style)> Buttons(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("components").EnumerateArray()
            .SelectMany(row => row.GetProperty("components").EnumerateArray())
            .Select(button => (button.GetProperty("label").GetString()!, button.GetProperty("custom_id").GetString()!, button.GetProperty("style").GetInt32()))
            .ToList();

    private static ButtonPress Press(string action, string card) => new(DiscordBridge.ActionPrefix + action, null, card, "capture");

    private long ItemId(string title) =>
        _database.GetRepaymentCandidates(_database.GetWaitingRepayments()[0], 10).Single(candidate => candidate.Title == title).ItemId;

    [Fact]
    public async Task The_tailspinPay_card_offers_up_to_four_purchases_then_not_a_repayment()
    {
        Spend("TRADER JOES #123", 2000, daysAgo: 1);
        Spend("Chipotle", 3000, daysAgo: 2);
        Spend("Thai Place", 4000, daysAgo: 3);
        Spend("Panera", 1500, daysAgo: 4);
        Spend("Megamart", 9000, daysAgo: 5);
        var worker = await Started();

        var card = await PostCard(worker, Owed());

        var sent = _discord.Of(HttpMethod.Post).Single(request => request.Url.EndsWith("/channels/capture/messages") && request.Body.Contains("Tailspin Pay"));
        Assert.Contains("Pick the purchase below, or reply with its name.", Said(sent.Body));
        var buttons = Buttons(sent.Body);
        Assert.Equal(5, buttons.Count);
        Assert.Equal(["Trader Joes, Nov 8, $20.00 left", "Chipotle, Nov 7, $30.00 left", "Thai Place, Nov 6, $40.00 left", "Panera, Nov 5, $15.00 left"], buttons.Take(4).Select(button => button.Label));
        Assert.All(buttons.Take(4), button => Assert.StartsWith(DiscordBridge.ActionPrefix + WaypointWorker.PaymentAppPick, button.Id));
        Assert.Equal((CardLabels.NotARepayment, DiscordBridge.ActionPrefix + WaypointWorker.PaymentAppNone + ":" + Waypoint.Core.Data.WaypointDatabase.RepaymentKey("email-1")), (buttons[4].Label, buttons[4].Id));
        Assert.NotNull(_database.FindRepaymentForCard(card));
    }

    [Fact]
    public async Task Pressing_a_purchase_on_the_card_settles_it_there()
    {
        Spend("TRADER JOES #123", 2000);
        Spend("Chipotle", 3000);
        var worker = await Started();
        var card = await PostCard(worker, Owed());

        var result = await worker.OnButtonActedAsync(Press(WaypointWorker.PaymentAppPick + ItemId("Chipotle"), card));

        Assert.Equal("Marked $6.40 from Riley Park repaid on Chipotle", result?.Outcome);
        Assert.Equal(640, Repaid("Chipotle"));
        Assert.Equal(0, Repaid("TRADER JOES #123"));
        Assert.Empty(_database.GetWaitingRepayments());

        // A second press, or one after it was settled on the Budget page, changes nothing.
        var again = await worker.OnButtonActedAsync(Press(WaypointWorker.PaymentAppNone, card));
        Assert.Equal("That repayment is already settled", again?.Outcome);
        Assert.Equal(640, Repaid("Chipotle"));
    }

    [Fact]
    public async Task Pressing_not_a_repayment_clears_it()
    {
        Spend("TRADER JOES #123", 2000);
        var worker = await Started();
        var card = await PostCard(worker, Owed());

        var result = await worker.OnButtonActedAsync(Press(WaypointWorker.PaymentAppNone, card));

        Assert.Equal("Riley Park's $6.40 won't count as a repayment", result?.Outcome);
        Assert.Empty(_database.GetWaitingRepayments());
        Assert.Equal(0, Repaid("TRADER JOES #123"));
    }

    [Fact]
    public async Task A_pressed_repayment_button_turns_the_card_gray_with_what_was_done()
    {
        Spend("TRADER JOES #123", 2000);
        var worker = await Started();
        var card = await PostCard(worker, Owed());
        var id = ItemId("Trader Joes");

        await worker.Discord!.HandleInteractionAsync(Json($$$"""
            {"id":"9","token":"tok","type":3,"channel_id":"capture","data":{"custom_id":"{{{DiscordBridge.ActionPrefix}}}{{{WaypointWorker.PaymentAppPick}}}{{{id}}}"},
             "member":{"user":{"id":"owner"}},
             "message":{"id":"{{{card}}}","embeds":[{"title":"Tailspin Pay","description":"Riley Park paid you $6.40. Which purchase was it for?"}]}}
            """), (_, _) => null);

        var answer = Assert.Single(_discord.All, request => request.Url.Contains("/callback"));
        Assert.Contains("\"type\":7", answer.Body);
        Assert.Contains("\"components\":[]", answer.Body);
        Assert.Contains("Marked $6.40 from Riley Park repaid on TRADER JOES #123", Said(JsonDocument.Parse(answer.Body).RootElement.GetProperty("data").GetRawText()));
        Assert.Equal(640, Repaid("TRADER JOES #123"));
    }

    [Fact]
    public async Task A_card_with_nothing_to_pick_still_offers_not_a_repayment()
    {
        var worker = await Started();

        await PostCard(worker, Owed());

        var sent = _discord.Of(HttpMethod.Post).Single(request => request.Url.EndsWith("/channels/capture/messages") && request.Body.Contains("Tailspin Pay"));
        Assert.Contains("Reply with the purchase's name.", Said(sent.Body));
        Assert.Equal(CardLabels.NotARepayment, Assert.Single(Buttons(sent.Body)).Label);
    }
}
