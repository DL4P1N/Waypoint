using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Files;
using Waypoint.Core.Notes;

namespace Waypoint.Core.Tests;

/// <summary>Fixes found reviewing the Discord views: lists opened anywhere, lists kept by name, and pages kept after a change.</summary>
public sealed partial class DiscordPhoneTests
{
    [Fact]
    public void Checking_off_from_a_list_opened_by_search_works()
    {
        _database.AddListEntry("Groceries", "eggs", _now);
        var worker = Worker(toDo: null, lists: null);
        var eggs = _database.GetListEntries("Groceries").Single();

        // A search's reply isn't a pinned view, so nothing but the menu says which list this is.
        var opened = worker.OnViewPicked(WaypointWorker.OpenMenu, "search-reply", ["entry:" + eggs.Id]);
        var card = worker.OnViewPicked(opened!.Menu!.CustomId, "search-reply", [eggs.Id.ToString()]);

        Assert.True(_database.GetListEntries("Groceries").Single().Done);
        Assert.Contains("~~eggs~~", card!.Description);
        Assert.StartsWith(DiscordViews.CheckMenu, card.Menu!.CustomId);
    }

    [Fact]
    public async Task A_list_with_a_long_name_is_found_by_its_name_after_lists_are_added_or_reordered()
    {
        var name = "Things to pack for the long trip at the end of the summer break";
        _database.AddListEntry(name, "lamp", _now);
        for (var number = 0; number < 28; number++)
        {
            _database.AddListEntry(name, $"filler {number:00}", _now);
        }

        var worker = await Started(toDo: null);
        var before = DiscordViews.ListCard(_database.GetListOverview().Single(list => list.Name == name));
        var menu = before.Menu!.CustomId;
        var next = Assert.Single(Assert.Single(before.MoreRows)).Id;
        Assert.True(menu.Length <= 100);
        Assert.True(DiscordBridge.ActionPrefix.Length + next.Length <= 100);

        // Another list takes its place in the order before the button is pressed.
        _database.AddListEntry("Aardvark food", "carrots", _now);
        var lamp = _database.GetListEntries(name).Single(entry => entry.Text == "lamp");
        var card = worker.OnViewPicked(menu, "m2", [lamp.Id.ToString()]);

        Assert.True(_database.GetListEntries(name).Single(entry => entry.Text == "lamp").Done);
        Assert.False(_database.GetListEntries("Aardvark food").Single().Done);
        Assert.DoesNotContain(card!.Menu!.Choices, choice => choice.Label == "lamp");

        // Its Next button still finds the same list, not whatever is now where it was.
        var second = await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + next, null, "m2", "lists-ch"));
        Assert.StartsWith(name, second!.Card!.Title);
    }

    [Fact]
    public async Task Checking_an_item_on_the_second_page_of_a_list_keeps_that_page()
    {
        for (var number = 0; number < 30; number++)
        {
            _database.AddListEntry("Groceries", $"thing {number:00}", _now);
        }

        var worker = await Started(toDo: null);
        var late = _database.GetListEntries("Groceries").Single(entry => entry.Text == "thing 27");
        var patches = _discord.Of(HttpMethod.Patch).Count();

        var card = worker.OnViewPicked(DiscordViews.CheckMenu, "m2", [late.Id.ToString()]);
        await Task.Delay(200);
        await worker.RefreshDiscordViewsAsync();

        Assert.Equal("Groceries, page 2 of 2", card!.Title);
        Assert.Equal(patches, _discord.Of(HttpMethod.Patch).Count());
    }

    [Fact]
    public async Task Finishing_an_item_on_the_second_page_of_the_week_keeps_that_page()
    {
        var items = Enumerable.Range(0, 60).Select(number => AddTask($"Problem set question {number:00}", Today)).ToList();
        var worker = await Started(lists: null);
        var patches = _discord.Of(HttpMethod.Patch).Count();

        var card = worker.OnViewPicked(DiscordViews.DoneMenu, "m1", [ItemRef.Format(items[30])]);
        await Task.Delay(200);
        await worker.RefreshDiscordViewsAsync();

        Assert.Equal("To do, page 2 of 3", card!.Title);
        Assert.Equal(patches, _discord.Of(HttpMethod.Patch).Count());
    }

    [Fact]
    public async Task A_file_sent_in_Discord_keeps_the_redacted_name_not_the_one_it_came_with()
    {
        var worker = NotesWorker(new FakeWriter());

        await worker.Discord!.HandleEventAsync("MESSAGE_CREATE", NotesMessage("601", "stmt_4111111111111111.pdf"));
        // The note is made first and its file added just after, so this waits for both.
        await WaitFor(() => _database.GetNotes() is [var made] && _database.GetNoteSources(made.Id).Count > 0);

        var note = Assert.Single(_database.GetNotes());
        var source = Assert.Single(_database.GetNoteSources(note.Id));
        Assert.DoesNotContain("4111111111111111", note.Title);
        Assert.DoesNotContain("4111111111111111", source.Name);
        Assert.Contains("[removed]", source.Name);
    }

    private sealed class BrokenWriter : INoteWriter
    {
        public Task<WrittenNotes> WriteAsync(IReadOnlyList<NoteSource> sources, NoteWriteContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the writer broke");
    }

    [Fact]
    public async Task A_file_that_breaks_something_unexpected_still_gets_a_plain_answer_and_is_logged()
    {
        var logged = new List<Exception>();
        var settings = new WaypointSettings(Discord: new DiscordSettings("token", "owner", "capture", "alerts", "budget", null, null, "notes-ch"));
        var worker = new WaypointWorker(_database, settings, _folder, new Host(), logged.Add, new WorkerParts(
            _ => new NoTokens(),
            Clock: () => _now,
            Http: () => new HttpClient(_discord),
            CaptureSorter: new NullSorter(),
            ReadFile: path => throw new InvalidOperationException("the reader broke"),
            ConnectGateway: false))
        {
            RetryWait = _ => Task.CompletedTask,
        };
        worker.NoteFiles = new LocalNotesData(_database, new NoteMaker(_database, new BrokenWriter(), null));
        worker.StartDiscord();

        await worker.Discord!.HandleEventAsync("MESSAGE_CREATE", NotesMessage("701", "biology.pdf"));
        await WaitFor(() => _discord.Of(HttpMethod.Post).Any(post => post.Body.Contains("\"message_id\":\"701\"")));

        var reply = _discord.Of(HttpMethod.Post).Single(post => post.Body.Contains("\"message_id\":\"701\"")).Body;
        Assert.Contains("Try sending it again", reply);
        Assert.DoesNotContain("the reader broke", reply);
        Assert.Contains(logged, exception => exception.Message == "the reader broke");
    }

    [Fact]
    public async Task Order_moves_a_list_whose_name_has_a_colon_in_it()
    {
        _database.AddListEntry("Errands", "stamps", _now);
        _database.AddListEntry("Work: errands", "badge", _now);
        var worker = Worker(toDo: null, lists: null);

        var lists = worker.OnDiscordCommand(new SlashCommand("order", Options(("what", "lists")))).Card;
        var picked = worker.OnViewPicked(lists.Menu!.CustomId, "m1", [lists.Menu.Choices.Single(choice => choice.Label == "Work: errands").Value])!;
        var up = picked.MoreRows.Single().Single(button => button.Label == "Move up");
        var moved = (await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + up.Id, null, "m1", "c")))!.Card!;

        Assert.Contains("Moved Work: errands up", moved.Description);
        var names = _database.GetListNames().Where(name => name != WaypointDatabase.ShoppingList).ToList();
        Assert.True(names.IndexOf("Work: errands") < names.IndexOf("Errands"));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void Hours_of_sleep_that_are_not_a_real_number_are_turned_down(string typed)
    {
        var worker = Worker(toDo: null, lists: null);
        var before = worker.BedtimeSchedule.Settings.SleepHours;

        var reply = worker.OnFormSent("settings-bedtime", new Dictionary<string, string> { ["sleep"] = typed, ["ready"] = "30" });

        Assert.Contains("number like 8", reply.Card.Description);
        Assert.Equal(before, worker.BedtimeSchedule.Settings.SleepHours);
    }

    [Fact]
    public void A_page_of_History_with_long_titles_still_fits_in_a_card()
    {
        for (var number = 0; number < 30; number++)
        {
            _database.SetDone(AddTask($"Task {number:00} " + new string('x', 290), Today), true);
        }

        var worker = Worker(toDo: null, lists: null);
        var card = worker.OnDiscordCommand(new SlashCommand("history", Options())).Card;

        Assert.True(card.Description!.Length <= 4096, $"The description is {card.Description.Length} long.");
        Assert.Contains(card.MoreRows.SelectMany(row => row), button => button.Label == "Next");
    }

    [Fact]
    public void Words_searched_for_in_History_are_shown_as_typed_not_as_formatting()
    {
        var worker = Worker(toDo: null, lists: null);

        var none = worker.OnDiscordCommand(new SlashCommand("history", Options(("search", "**bold** _x_")))).Card;

        Assert.Contains(@"\*\*bold\*\*", none.Title);
        Assert.Contains(@"\*\*bold\*\*", none.Description);
    }

    [Fact]
    public void The_search_boxes_on_commands_hold_at_most_a_hundred_characters()
    {
        using var commands = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(DiscordBridge.CommandList));
        foreach (var (command, option) in new[] { ("search", "words"), ("notes", "search"), ("history", "search") })
        {
            var found = commands.RootElement.EnumerateArray().Single(each => each.GetProperty("name").GetString() == command)
                .GetProperty("options").EnumerateArray().Single(each => each.GetProperty("name").GetString() == option);
            Assert.Equal(100, found.GetProperty("max_length").GetInt32());
        }
    }

    [Fact]
    public void Cutting_text_short_never_splits_a_pair_of_characters()
    {
        var cut = DiscordText.Shorten("ab" + "😀" + "cd", 4);

        Assert.Equal("ab…", cut);
        Assert.Equal("abcd", DiscordText.Shorten("abcd", 4));
    }
}
