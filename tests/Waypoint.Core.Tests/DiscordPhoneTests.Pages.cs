using Waypoint.Core.Background;
using System.Text.Json;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Tests;

/// <summary>Long views get Back and Next instead of being cut off, and pop-up forms can be opened by any command or button.</summary>
public sealed partial class DiscordPhoneTests
{
    private static string CommandJson(string name, params (string Name, string Value)[] options) => JsonSerializer.Serialize(new
    {
        id = "9",
        token = "tok",
        type = 2,
        application_id = "app",
        channel_id = "capture",
        data = new { name, options = options.Select(option => new { name = option.Name, value = option.Value }).ToArray() },
        member = new { user = new { id = "owner" } },
    });

    private static string FormJson(string id, params (string Id, string Value)[] boxes) => JsonSerializer.Serialize(new
    {
        id = "9",
        token = "tok",
        type = 5,
        application_id = "app",
        channel_id = "capture",
        data = new { custom_id = id, components = boxes.Select(box => new { type = 1, components = new[] { new { type = 4, custom_id = box.Id, value = box.Value } } }).ToArray() },
        member = new { user = new { id = "owner" } },
    });

    private static string PressJson(string customId, string user = "owner", string message = "m1") => JsonSerializer.Serialize(new
    {
        id = "9",
        token = "tok",
        type = 3,
        application_id = "app",
        channel_id = "capture",
        data = new { custom_id = customId, component_type = 2 },
        member = new { user = new { id = user } },
        message = new { id = message, embeds = new[] { new { title = "x" } } },
    });

    private static string PickJson(string customId, string value, string message = "m1") => JsonSerializer.Serialize(new
    {
        id = "9",
        token = "tok",
        type = 3,
        application_id = "app",
        channel_id = "capture",
        data = new { custom_id = customId, component_type = 3, values = new[] { value } },
        member = new { user = new { id = "owner" } },
        message = new { id = message, embeds = new[] { new { title = "x" } } },
    });

    private string LastCallback() => _discord.All.Last(request => request.Url.Contains("/callback")).Body;

    [Fact]
    public async Task A_long_week_has_next_and_back_and_each_page_is_a_press_away()
    {
        for (var number = 0; number < 60; number++)
        {
            AddTask($"Problem set question {number:00}", Today);
        }

        var worker = Worker(toDo: null, lists: null);
        var first = worker.OnDiscordCommand(new SlashCommand("todo", Options())).Card;
        Assert.Equal("To do, page 1 of 3", first.Title);
        var next = Assert.Single(first.MoreRows.Single());
        Assert.Equal("page:todo:1", next.Id);

        var second = await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + next.Id, null, "m1", "capture"));
        Assert.Equal("To do, page 2 of 3", second!.Card!.Title);
        Assert.Equal(["Back", "Next"], second.Card.MoreRows.Single().Select(button => button.Label));
        Assert.Equal(25, second.Card.Menu!.Choices.Count);
        Assert.Contains("Problem set question 25", second.Card.Fields[0].Value);

        // A page that's gone, such as after things were finished, shows the last one.
        var past = await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + "page:todo:9", null, "m1", "capture"));
        Assert.Equal("To do, page 3 of 3", past!.Card!.Title);
    }

    [Fact]
    public async Task Page_buttons_go_in_their_own_row_and_the_press_edits_the_message()
    {
        for (var number = 0; number < 30; number++)
        {
            _database.AddListEntry("Groceries", $"thing {number}", _now);
        }

        var worker = Worker(toDo: null, lists: null);
        var card = DiscordViews.ListCard(_database.GetListOverview().Single(list => list.Name == "Groceries"));
        await worker.Discord!.SendAlertAsync(card);
        var sent = _discord.Of(HttpMethod.Post).Last().Body;
        Assert.Contains("waypoint:act:page:list:Groceries:1", sent);
        Assert.Contains("waypoint:view-check", sent);

        await worker.Discord.HandleInteractionAsync(Json(PressJson("waypoint:act:page:list:Groceries:1")), (_, _) => null);
        var answer = LastCallback();
        Assert.Contains("\"type\":7", answer);
        Assert.Contains("thing 25", answer);
        Assert.Contains("waypoint:act:page:list:Groceries:0", answer);
    }

    [Fact]
    public async Task A_slow_press_on_a_pinned_view_that_opens_something_replies_privately_and_forms_open_at_once()
    {
        var worker = await Started(lists: null);
        var bridge = worker.Discord!;
        var pinned = "m1";
        Assert.Equal(DiscordViews.ToDo, worker.Views.ViewFor(pinned));

        // Opened from a pinned view, or Purchases from the budget view, are replies of their own.
        Assert.Equal(PressReply.Private, bridge.PressWillReply!(WaypointWorker.OpenMenu, pinned, ["note:1"]));
        Assert.Equal(PressReply.Private, bridge.PressWillReply!(DiscordBridge.ActionPrefix + WaypointWorker.PurchasesOpen, pinned, []));

        // From a private reply, such as a search's, the opened card replaces it.
        Assert.Equal(PressReply.Update, bridge.PressWillReply!(WaypointWorker.OpenMenu, "ephemeral", ["note:1"]));
        Assert.Equal(PressReply.Update, bridge.PressWillReply!(DiscordBridge.ActionPrefix + "page:todo:1", pinned, []));

        // A press that only opens a form is never promised, since a form can't come after that.
        Assert.Equal(PressReply.Form, bridge.PressWillReply!(DiscordBridge.ActionPrefix + WaypointWorker.NoteSearch, pinned, []));
        Assert.Equal(PressReply.Form, bridge.PressWillReply!(DiscordBridge.ActionPrefix + WaypointWorker.HistorySearch + "All", pinned, []));
        Assert.Equal(PressReply.Form, bridge.PressWillReply!(DiscordBridge.ActionPrefix + "settings-bedtime", pinned, []));
    }

    [Fact]
    public async Task A_form_can_open_from_a_command_and_its_answer_comes_back_privately()
    {
        var worker = Worker(toDo: null, lists: null);
        var bridge = worker.Discord!;
        bridge.Command = _ => new CommandReply(DiscordText.Notice("unused", "")) { Form = new CardForm("test:1", "A form with a title far longer than Discord allows", [new FormBox("what", "What it is", Required: true), new FormBox("more", "More", Long: true, Value: "filled")]) };
        string? seen = null;
        bridge.Submitted = (id, values) =>
        {
            seen = $"{id} {values["what"]}";
            return new CommandReply(DiscordText.Notice("Got it", ""), true);
        };

        await bridge.HandleInteractionAsync(Json(CommandJson("anything")), (_, _) => null, bridge.Command);
        var form = LastCallback();
        Assert.Contains("\"type\":9", form);
        Assert.Contains("waypoint:form:test:1", form);
        Assert.Contains("\"required\":true", form);
        Assert.Contains("\"value\":\"filled\"", form);
        using (var document = JsonDocument.Parse(form))
        {
            Assert.True(document.RootElement.GetProperty("data").GetProperty("title").GetString()!.Length <= 45);
        }

        await bridge.HandleInteractionAsync(Json(FormJson("waypoint:form:test:1", ("what", "a thing"))), (_, _) => null);
        Assert.Equal("test:1 a thing", seen);
        Assert.Contains("\"flags\":64", LastCallback());
        Assert.Contains("Got it", LastCallback());
    }

    [Fact]
    public async Task Only_the_owner_can_send_a_form_or_page_a_view()
    {
        var worker = Worker(toDo: null, lists: null);
        var bridge = worker.Discord!;
        var called = false;
        bridge.Submitted = (_, _) =>
        {
            called = true;
            return new CommandReply(DiscordText.Notice("Got it", ""));
        };

        await bridge.HandleInteractionAsync(Json(FormJson("waypoint:form:new:task", ("title", "x")).Replace("\"owner\"", "\"someone\"")), (_, _) => null);
        Assert.False(called);
        Assert.Contains("Only the owner", LastCallback());

        await bridge.HandleInteractionAsync(Json(PressJson("waypoint:act:page:todo:1", "someone")), (_, _) => null);
        Assert.Contains("Only the owner", LastCallback());
    }

    [Fact]
    public void Long_text_splits_at_line_breaks_into_pages_that_fit()
    {
        var text = string.Join("\n", Enumerable.Range(0, 400).Select(number => $"Line {number} of a long note with some words in it"));
        var pages = DiscordPages.SplitText(text, 3800);

        Assert.True(pages.Count > 1);
        Assert.All(pages, page => Assert.True(page.Length <= 3800));
        Assert.Equal(text, string.Join("\n", pages));
        Assert.All(DiscordPages.SplitText(new string('x', 9000), 3800), page => Assert.True(page.Length <= 3800));
    }
}
