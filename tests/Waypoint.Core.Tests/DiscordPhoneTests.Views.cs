using System.Text.Json;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Files;
using Waypoint.Core.Notes;

namespace Waypoint.Core.Tests;

/// <summary>Notes, History, search, the add form, settings, order, and purchases on the phone, against a fake Discord.</summary>
public sealed partial class DiscordPhoneTests
{
    private sealed class FakeWriter : INoteWriter
    {
        public int Calls { get; private set; }

        public Task<WrittenNotes> WriteAsync(IReadOnlyList<NoteSource> sources, NoteWriteContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new WrittenNotes("## Cell parts\nThe nucleus holds the DNA.", []));
        }
    }

    private WaypointWorker NotesWorker(FakeWriter writer)
    {
        var settings = new WaypointSettings(Discord: new DiscordSettings("token", "owner", "capture", "alerts", "budget", null, null, "notes-ch"));
        var worker = new WaypointWorker(_database, settings, _folder, new Host(), exception => throw exception, new WorkerParts(
            _ => new NoTokens(),
            Clock: () => _now,
            Http: () => new HttpClient(_discord),
            CaptureSorter: new NullSorter(),
            ReadFile: path => new FileText(Path.GetFileName(path), "Cells have a nucleus and a membrane."),
            ConnectGateway: false))
        {
            RetryWait = _ => Task.CompletedTask,
        };
        worker.NoteFiles = new LocalNotesData(_database, new NoteMaker(_database, writer, null));
        worker.StartDiscord();
        return worker;
    }

    private static JsonElement NotesMessage(string id, string attachment) => Json($$"""
        {"id":"{{id}}","channel_id":"notes-ch","content":"","author":{"id":"owner"},"attachments":[{"id":"a{{id}}","filename":"{{attachment}}","size":2048,"url":"https://cdn.discordapp.com/attachments/1/2/{{attachment}}","content_type":"application/pdf"}]}
        """);

    [Fact]
    public async Task The_notes_view_lists_folders_and_what_it_opens_comes_privately()
    {
        var biology = _database.CreateNoteFolder("Biology", null, _now).Id;
        _database.CreateNoteFolder("Labs", biology, _now);
        var note = _database.CreateNote("Cells", "The nucleus holds the DNA.", _now, biology);
        _database.CreateNote("Loose thought", "Something", _now);
        var worker = NotesWorker(new FakeWriter());
        await worker.RefreshDiscordViewsAsync();

        var pinned = Assert.Single(_discord.Of(HttpMethod.Post), post => post.Url.EndsWith("/channels/notes-ch/messages"));
        Assert.Contains("Unfiled (1)", pinned.Body);
        Assert.Contains("Biology (1)", pinned.Body);
        Assert.Contains("Labs (0)", pinned.Body);
        Assert.Contains("waypoint:view-open", pinned.Body);
        Assert.Contains("Search notes", pinned.Body);

        // Picked on the pinned view, the folder comes as a private reply, so the index stays where it is.
        var folder = worker.OnViewPicked(WaypointWorker.OpenMenu, "m1", [$"folder:{biology}"])!;
        Assert.True(folder.Private);
        Assert.Equal("Biology", folder.Title);
        Assert.Contains("Labs", folder.Description);
        Assert.Contains("Cells", folder.Description);

        // Opened from a private reply, it changes in place.
        var opened = worker.OnViewPicked(WaypointWorker.OpenMenu, "private", [$"note:{note}"])!;
        Assert.False(opened.Private);
        Assert.Equal("Cells", opened.Title);
        Assert.Contains("nucleus", opened.Description);
        Assert.Contains(opened.MoreRows.Single(), button => button.Label == "Back to folder" && button.Id == $"page:folder:{biology}:0");
    }

    [Fact]
    public async Task A_long_note_opens_in_pages_and_its_tables_read_as_lines()
    {
        var body = "| Part | Job |\n|---|---|\n| Nucleus | Holds DNA |\n#### Small heading\n" + string.Join("\n", Enumerable.Range(0, 300).Select(number => $"Line {number} of a long note with words"));
        var id = _database.CreateNote("Long", body, _now);
        var worker = Worker(toDo: null, lists: null);

        var first = (await worker.OnButtonActedAsync(new ButtonPress($"{DiscordBridge.ActionPrefix}page:note:{id}:0", null, "m1", "c")))!.Card!;
        Assert.StartsWith("Long, page 1 of", first.Title);
        Assert.Contains("Nucleus · Holds DNA", first.Description);
        Assert.DoesNotContain("|---", first.Description);
        Assert.Contains("**Small heading**", first.Description);
        Assert.True(first.Description!.Length <= 4096);

        var second = (await worker.OnButtonActedAsync(new ButtonPress($"{DiscordBridge.ActionPrefix}page:note:{id}:1", null, "m1", "c")))!.Card!;
        Assert.StartsWith("Long, page 2 of", second.Title);
        Assert.Contains(second.MoreRows[1], button => button.Label == "Back");
    }

    [Fact]
    public async Task Notes_can_be_searched_and_a_new_note_typed_from_the_phone()
    {
        _database.CreateNote("Cells", "The nucleus holds the DNA.", _now);
        var worker = Worker(toDo: null, lists: null);

        var found = worker.OnDiscordCommand(new SlashCommand("notes", Options(("search", "nucleus"))));
        Assert.True(found.Private);
        Assert.Contains("Cells", found.Card.Description);
        Assert.Single(found.Card.Menu!.Choices);

        var form = (await worker.OnButtonActedAsync(new ButtonPress($"{DiscordBridge.ActionPrefix}{WaypointWorker.NewNote}u", null, "m1", "c")))!.Form!;
        Assert.Contains(form.Boxes, box => box.Id == "body" && box.Long);

        var saved = worker.OnFormSent(form.Id, new Dictionary<string, string> { ["title"] = "Shopping ideas", ["body"] = "A new lamp" });
        Assert.Equal("Shopping ideas", saved.Card.Title);
        Assert.Contains(_database.GetNotes(), note => note.Title == "Shopping ideas" && note.Body == "A new lamp");
    }

    [Fact]
    public async Task A_file_sent_in_the_notes_channel_becomes_a_note_but_a_recording_still_does_not()
    {
        var writer = new FakeWriter();
        var worker = NotesWorker(writer);

        await worker.Discord!.HandleEventAsync("MESSAGE_CREATE", NotesMessage("501", "biology.pdf"));
        await WaitFor(() => _discord.Of(HttpMethod.Post).Count(post => post.Url.EndsWith("/channels/notes-ch/messages") && post.Body.Contains("\"message_id\":\"501\"")) >= 2);

        var note = Assert.Single(_database.GetNotes());
        Assert.Equal(1, writer.Calls);
        Assert.Contains("nucleus", note.Body);
        var replies = _discord.Of(HttpMethod.Post).Where(post => post.Body.Contains("\"message_id\":\"501\"")).Select(post => post.Body).ToList();
        Assert.Contains(replies, reply => reply.Contains("The notes are being written now"));
        Assert.Contains(replies, reply => reply.Contains("Cell parts"));

        await worker.Discord.HandleEventAsync("MESSAGE_CREATE", NotesMessage("502", "lecture.m4a"));
        await WaitFor(() => _discord.Of(HttpMethod.Post).Any(post => post.Body.Contains("\"message_id\":\"502\"")));
        Assert.Contains("be sent from Discord yet", _discord.Of(HttpMethod.Post).Single(post => post.Body.Contains("\"message_id\":\"502\"")).Body);
        Assert.Single(_database.GetNotes());
    }

    [Fact]
    public void History_has_the_pages_filters_and_reopen_puts_it_back_the_same_way()
    {
        var laundry = AddTask("Laundry", Today);
        _database.SetDone(laundry, true);
        var worker = Worker(toDo: null, lists: null);

        var history = worker.OnDiscordCommand(new SlashCommand("history", Options()));
        Assert.True(history.Private);
        Assert.Contains("Laundry", history.Card.Description);
        Assert.Equal(["All", "Completed", "Canceled", "Reopened", "Past"], history.Card.MoreRows[0].Select(button => button.Label));
        Assert.Equal(ButtonRole.Main, history.Card.MoreRows[0][0].Role);
        var choice = Assert.Single(history.Card.Menu!.Choices);

        var reopened = worker.OnViewPicked(history.Card.Menu.CustomId, "m1", [choice.Value])!;
        Assert.Contains("Put Laundry back on the list", reopened.Description);
        Assert.Contains(_database.GetOpenItems(), item => item.Title == "Laundry");

        var reopenedOnly = worker.OnDiscordCommand(new SlashCommand("history", Options(("filter", "reopened")))).Card;
        Assert.Contains("Laundry", reopenedOnly.Description);
        Assert.Empty(reopenedOnly.Menu!.Choices);
        Assert.Contains("Nothing here yet", worker.OnDiscordCommand(new SlashCommand("history", Options(("filter", "canceled")))).Card.Description);
    }

    [Fact]
    public async Task History_can_be_pinned_in_its_own_channel()
    {
        var worker = await Started(toDo: null, lists: null);

        worker.OnDiscordCommand(new SlashCommand("pinhere", Options(("view", "history")), "history-ch"));
        _now = _now.AddMinutes(2);
        await worker.RefreshDiscordViewsAsync();

        Assert.Contains(_discord.Of(HttpMethod.Post), post => post.Url.Contains("/channels/history-ch/") && post.Body.Contains("History"));
    }

    [Fact]
    public void Search_looks_everywhere_find_anywhere_does_and_opens_what_is_picked()
    {
        var essay = AddTask("Biology essay", Today);
        _database.CreateNote("Biology notes", "Cells", _now);
        _database.AddListEntry("Errands", "biology book", _now);
        var worker = Worker(toDo: null, lists: null);

        var found = worker.OnDiscordCommand(new SlashCommand("search", Options(("words", "biology")))).Card;
        Assert.Contains("Biology essay", found.Description);
        Assert.Contains("Biology notes", found.Description);
        Assert.Contains("biology book", found.Description);
        Assert.Equal(3, found.Menu!.Choices.Count);

        var item = worker.OnViewPicked(WaypointWorker.OpenMenu, "private", [found.Menu.Choices.Single(choice => choice.Label == "Biology essay").Value])!;
        Assert.Equal("Biology essay", item.Title);
        Assert.Contains(item.Actions, action => action.Id == WaypointWorker.FinishPick + ItemRef.Format(essay));

        var list = worker.OnViewPicked(WaypointWorker.OpenMenu, "private", [found.Menu.Choices.Single(choice => choice.Label == "biology book").Value])!;
        Assert.Equal("Errands", list.Title);

        Assert.Contains("Nothing on any page matches", worker.OnDiscordCommand(new SlashCommand("search", Options(("words", "zebra")))).Card.Description);
    }

    [Fact]
    public void New_opens_the_add_form_and_sending_it_adds_the_same_way_the_to_do_form_does()
    {
        var worker = Worker(toDo: null, lists: null);

        var opened = worker.OnDiscordCommand(new SlashCommand("new", Options(("kind", "event"))));
        var form = opened.Form!;
        Assert.True(form.Boxes.Count <= 5);
        Assert.Contains(form.Boxes, box => box.Id == "end");

        var added = worker.OnFormSent(form.Id, new Dictionary<string, string> { ["title"] = "Study group", ["date"] = "tomorrow", ["time"] = "3pm", ["end"] = "5pm", ["class"] = "" });
        Assert.Equal("Added Study group to To do, for tomorrow at 3:00 PM", added.Card.Title);
        var item = _database.GetOpenItems().Single(open => open.Title == "Study group");
        Assert.Equal(ItemKind.Event, item.Kind);
        Assert.Equal(new TimeOnly(17, 0), _database.GetItemPlacements().Values.Single(place => place.EndTime is not null).EndTime);

        var task = worker.OnDiscordCommand(new SlashCommand("new", Options())).Form!;
        Assert.DoesNotContain(task.Boxes, box => box.Id == "end");
        var missing = worker.OnFormSent(task.Id, new Dictionary<string, string> { ["title"] = "Call", ["class"] = "Underwater basket weaving" });
        Assert.Contains("no class or filter called", missing.Card.Description);
    }

    [Fact]
    public async Task Settings_show_no_secrets_and_change_bedtime_and_work_hours_the_same_way()
    {
        var worker = Worker(toDo: null, lists: null);

        // A failed send leaves its own text in LastError, which can hold addresses or tokens.
        _discord.FailWith = "connect to 10.0.0.7 failed, token ABC-secret-123";
        await worker.Discord!.EditAsync("c", "m", DiscordText.Notice("x", ""));
        Assert.Contains("ABC-secret-123", worker.Discord.LastError);
        Assert.Contains("ABC-secret-123", worker.DescribeConnections().Single(connection => connection.Name == "Discord").State);

        var card = worker.OnDiscordCommand(new SlashCommand("settings", Options())).Card;
        var shown = JsonSerializer.Serialize(card);
        Assert.DoesNotContain("ABC-secret-123", shown);
        Assert.DoesNotContain("10.0.0.7", shown);
        Assert.Contains("last try", shown);
        Assert.Contains("Work hours", shown);
        Assert.Contains("Bedtime", shown);
        Assert.Contains("Theme", shown);
        Assert.Contains("Discord", shown);
        Assert.DoesNotContain("token", shown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("below", shown);

        var bedtime = (await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + card.Actions[0].Id, null, "m1", "c")))!.Form!;
        Assert.Equal("8", bedtime.Boxes.Single(box => box.Id == "sleep").Value);
        var saved = worker.OnFormSent(bedtime.Id, new Dictionary<string, string> { ["sleep"] = "7.5", ["ready"] = "30", ["latest"] = "1am" });
        Assert.Contains("Bedtime saved", saved.Card.Description);
        Assert.Equal(7.5, worker.BedtimeSchedule.Settings.SleepHours);
        Assert.Equal(new TimeOnly(1, 0), worker.BedtimeSchedule.Settings.LatestBedtime);

        var hours = (await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + card.Actions[1].Id, null, "m1", "c")))!.Form!;
        var work = worker.OnFormSent(hours.Id, new Dictionary<string, string> { ["days"] = "Monday, Wednesday", ["hours"] = "9am-5pm" });
        Assert.Equal(2, _database.GetWeeklyHours().Count);
        Assert.Contains("Mon 9:00 AM to 5:00 PM", work.Card.Fields.Single(field => field.Name == "Work hours").Value);
        worker.OnFormSent(hours.Id, new Dictionary<string, string> { ["days"] = "Monday", ["hours"] = "off" });
        Assert.Single(_database.GetWeeklyHours());
    }

    [Fact]
    public async Task Order_moves_lists_and_folders_up_and_down_the_way_dragging_does()
    {
        _database.AddListEntry("Errands", "stamps", _now);
        _database.AddListEntry("Packing", "charger", _now);
        var first = _database.CreateNoteFolder("Biology", null, _now).Id;
        var second = _database.CreateNoteFolder("Chemistry", null, _now).Id;
        var worker = Worker(toDo: null, lists: null);

        var lists = worker.OnDiscordCommand(new SlashCommand("order", Options(("what", "lists")))).Card;
        Assert.DoesNotContain(lists.Menu!.Choices, choice => choice.Label == WaypointDatabase.ShoppingList);
        var picked = worker.OnViewPicked(lists.Menu.CustomId, "m1", [lists.Menu.Choices.Single(choice => choice.Label == "Packing").Value])!;
        var up = picked.MoreRows.Single().Single(button => button.Label == "Move up");
        var moved = (await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + up.Id, null, "m1", "c")))!.Card!;
        Assert.Contains("Moved Packing up", moved.Description);
        var names = _database.GetListNames().Where(name => name != WaypointDatabase.ShoppingList).ToList();
        Assert.True(names.IndexOf("Packing") < names.IndexOf("Errands"));
        var again = (await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + up.Id, null, "m1", "c")))!.Card!;
        Assert.Contains("can't go any further up", again.Description);

        var folders = worker.OnDiscordCommand(new SlashCommand("order", Options(("what", "folders")))).Card;
        var chemistry = worker.OnViewPicked(folders.Menu!.CustomId, "m1", [second.ToString()])!;
        await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + chemistry.MoreRows.Single()[0].Id, null, "m1", "c"));
        Assert.Equal(second, _database.GetNoteFolders()[0].Id);
        Assert.Equal(first, _database.GetNoteFolders()[1].Id);
    }

    [Fact]
    public void Purchases_come_a_page_at_a_time_from_the_budget_card()
    {
        for (var number = 0; number < 30; number++)
        {
            _database.AddExpense($"Coffee {number}", 450, null, Today, _now);
        }

        var worker = Worker(toDo: null, lists: null);
        var budget = worker.OnDiscordCommand(new SlashCommand("budget", Options())).Card;
        Assert.Contains(budget.Actions, action => action.Label == "Purchases");

        var purchases = worker.OnDiscordCommand(new SlashCommand("purchases", Options())).Card;
        Assert.Equal("Purchases in November 2026, page 1 of 2", purchases.Title);
        Assert.Contains("$4.50", purchases.Description);
        Assert.Contains(purchases.MoreRows.Single(), button => button.Label == "Month before");
        Assert.DoesNotContain(purchases.MoreRows.Single(), button => button.Label == "Month after");
    }

    [Fact]
    public async Task Save_spot_with_nothing_typed_opens_the_form_and_the_old_name_still_works()
    {
        var worker = Worker(toDo: null, lists: null);
        var bridge = worker.Discord!;

        await bridge.HandleInteractionAsync(Json(CommandJson("save-spot")), (_, _) => null, worker.OnDiscordCommand);
        Assert.Contains("waypoint:spot-form", LastCallback());

        var typed = worker.OnDiscordCommand(new SlashCommand("savespot", Options(("stopped", "page 4"))));
        Assert.Contains("Saved", typed.Card.Title);
        Assert.Contains("\"name\":\"save-spot\"", JsonSerializer.Serialize(DiscordBridge.CommandList));
    }
}
