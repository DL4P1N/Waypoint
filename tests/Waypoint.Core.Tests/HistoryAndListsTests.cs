using System.Text.Json;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

public sealed class HistoryAndListsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public HistoryAndListsTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private sealed class FakeSorter(Func<AssistantContext, CaptureResult?> sort) : ICaptureSorter
    {
        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(sort(context));
    }

    private static DateOnly InDays(int days) => DateOnly.FromDateTime(DateTime.Now).AddDays(days);

    private StoredItem Store(SortedItem item)
    {
        _database.CompleteCapture(_database.AddCapture(item.Title), [item]);
        return _database.GetOpenItems().Single(stored => stored.Title == item.Title);
    }

    private async Task<CaptureOutcome> Run(string text, CaptureResult result)
    {
        CaptureOutcome? outcome = null;
        var service = new CaptureService(_database, new FakeSorter(_ => result));
        service.Sorted += sorted => outcome = sorted;
        service.Capture(text);
        await service.ProcessPendingAsync();
        return outcome!;
    }

    private Task<CaptureOutcome> Run(ItemChange change) => Run("change it", new CaptureResult([], [change]));

    private Task<CaptureOutcome> Run(params AssistantOp[] ops) => Run("do it", new CaptureResult([], Ops: ops));

    private async Task<AssistantContext> ContextFor(string text)
    {
        AssistantContext? seen = null;
        var service = new CaptureService(_database, new FakeSorter(context =>
        {
            seen = context;
            return new CaptureResult([], Answer: "ok");
        }));
        service.Capture(text);
        await service.ProcessPendingAsync();
        return seen!;
    }

    [Fact]
    public async Task Finishing_and_canceling_are_kept_apart_in_the_history()
    {
        var paper = Store(new SortedItem(ItemKind.Task, "paper", null, null));
        var laundry = Store(new SortedItem(ItemKind.Task, "laundry", null, null));

        _database.SetDone(paper, true);
        await Run(new ItemChange(ItemSource.Capture, laundry.Id, ChangeAction.Cancel));

        var history = _database.GetHistory();
        Assert.Equal(HistoryAction.Done, history.Single(entry => entry.Title == "paper").Action);
        Assert.Equal(HistoryAction.Canceled, history.Single(entry => entry.Title == "laundry").Action);
    }

    [Fact]
    public void Expenses_stay_out_of_the_history()
    {
        var lunch = Store(new SortedItem(ItemKind.Expense, "lunch", InDays(0), 1200));

        _database.SetDone(lunch, true);

        Assert.Empty(_database.GetHistory());
    }

    [Fact]
    public async Task Undoing_takes_an_entry_back_out_of_the_history()
    {
        var paper = Store(new SortedItem(ItemKind.Task, "paper", null, null));
        _database.SetDone(paper, true);

        await Run(new ItemChange(ItemSource.Capture, paper.Id, ChangeAction.Reopen));

        Assert.Empty(_database.GetHistory());
        Assert.Contains(_database.GetOpenItems(), item => item.Id == paper.Id);
    }

    [Fact]
    public async Task Each_recurring_occurrence_is_logged_and_undo_restores_its_date_and_reminders()
    {
        var due = InDays(1);
        var plants = Store(new SortedItem(ItemKind.Reminder, "water the plants", due, null, new TimeOnly(9, 0), Recurs: new Recurrence(RecurrenceUnit.Day)));

        _database.SetDone(plants, true);
        var moved = _database.GetOpenItems().Single();
        Assert.NotEqual(due, moved.DueDate);

        _database.SetDone(moved, true);
        Assert.Equal(2, _database.GetHistory().Count);

        var outcome = await Run(new ItemChange(ItemSource.Capture, plants.Id, ChangeAction.Reopen));
        Assert.True(outcome.Changes.Single().Applied);
        Assert.Equal(moved.DueDate, _database.GetOpenItems().Single().DueDate);

        await Run(new ItemChange(ItemSource.Capture, plants.Id, ChangeAction.Reopen));
        Assert.Equal(due, _database.GetOpenItems().Single().DueDate);
        Assert.Empty(_database.GetHistory());
        Assert.Contains(_database.GetDueReminders(due.ToDateTime(new TimeOnly(9, 1))), reminder => reminder.Item.Id == plants.Id);
    }

    [Fact]
    public async Task A_repeat_that_just_moved_on_can_be_seen_for_undo()
    {
        var plants = Store(new SortedItem(ItemKind.Reminder, "water the plants", InDays(0), null, Recurs: new Recurrence(RecurrenceUnit.Day)));
        _database.SetDone(plants, true);

        var context = await ContextFor("undo that");

        Assert.Contains(context.RecentlyFinished, item => item.Id == plants.Id);
    }

    [Fact]
    public async Task A_reopen_of_an_open_item_with_nothing_to_undo_does_nothing()
    {
        var paper = Store(new SortedItem(ItemKind.Reminder, "water the plants", InDays(0), null, Recurs: new Recurrence(RecurrenceUnit.Day)));
        _database.SetDone(paper, true);
        await Run(new ItemChange(ItemSource.Capture, paper.Id, ChangeAction.Reopen));

        var outcome = await Run(new ItemChange(ItemSource.Capture, paper.Id, ChangeAction.Reopen));

        Assert.Empty(outcome.Changes);
    }

    [Fact]
    public void A_sent_reminder_moving_on_is_not_logged_as_finished()
    {
        var daily = Store(new SortedItem(ItemKind.Reminder, "water the plants", InDays(-1), null, Recurs: new Recurrence(RecurrenceUnit.Day)));
        var due = _database.GetDueReminders(DateTime.Now).Single();

        _database.SettleSentReminder(due, DateTime.Now);

        var entry = _database.GetHistory().Single();
        Assert.Equal(HistoryAction.Past, entry.Action);
        Assert.Equal(daily.Id, entry.ItemId);
    }

    [Fact]
    public async Task Older_finished_items_show_to_the_sorter_only_when_the_note_names_them()
    {
        var paper = Store(new SortedItem(ItemKind.Task, "history paper", null, null));
        _database.SetDone(paper, true);
        using (var connection = new SqliteConnection($"Data Source={_path}"))
        {
            connection.Open();
            using var age = connection.CreateCommand();
            age.CommandText = "UPDATE items SET done_at = '2020-01-01T00:00:00'; UPDATE item_history SET happened_at = '2020-01-01T00:00:00'";
            age.ExecuteNonQuery();
        }

        Assert.DoesNotContain((await ContextFor("buy socks")).RecentlyFinished, item => item.Id == paper.Id);
        Assert.Contains((await ContextFor("bring back the paper")).RecentlyFinished, item => item.Id == paper.Id);

        var outcome = await Run("bring back the paper", new CaptureResult([], [new ItemChange(ItemSource.Capture, paper.Id, ChangeAction.Reopen)]));
        Assert.True(outcome.Changes.Single().Applied);
    }

    [Fact]
    public async Task A_canceled_feed_event_is_canceled_and_keeps_its_history_after_leaving_the_feed()
    {
        _database.ReplaceCalendarEvents([new CalendarEvent("quiz", "Quiz 4", "CS 101", InDays(2), null)]);
        var quiz = _database.GetOpenItems().Single();

        await Run(new ItemChange(ItemSource.Calendar, quiz.Id, ChangeAction.Cancel));
        _database.ReplaceCalendarEvents([]);

        var entry = _database.GetHistory().Single();
        Assert.Equal(HistoryAction.Canceled, entry.Action);
        Assert.Equal("Quiz 4", entry.Title);
        Assert.Equal("CS 101", entry.Course);
    }

    [Fact]
    public void A_past_event_that_leaves_the_feed_is_remembered_as_past()
    {
        _database.ReplaceCalendarEvents([new CalendarEvent("old", "Old lab", null, InDays(-3), null), new CalendarEvent("new", "New lab", null, InDays(3), null)]);

        _database.ReplaceCalendarEvents([]);

        Assert.Equal(HistoryAction.Past, _database.GetHistory().Single().Action);
    }

    [Fact]
    public void Things_finished_before_the_log_existed_are_brought_in_once()
    {
        var paper = Store(new SortedItem(ItemKind.Task, "paper", null, null));
        _database.SetDone(paper, true);
        using (var connection = new SqliteConnection($"Data Source={_path}"))
        {
            connection.Open();
            using var clear = connection.CreateCommand();
            clear.CommandText = "DELETE FROM item_history; DELETE FROM preferences WHERE key = 'history_backfilled'";
            clear.ExecuteNonQuery();
        }

        new WaypointDatabase(_path);
        new WaypointDatabase(_path);

        Assert.Equal("paper", _database.GetHistory().Single().Title);
    }

    [Fact]
    public async Task Notes_stay_off_the_main_view_and_out_of_the_sorter_unless_named()
    {
        var note = Store(new SortedItem(ItemKind.Note, "the wifi password is on the fridge", null, null));

        Assert.Empty(ItemGroups.ByDay(_database.GetOpenItems(), InDays(0)));
        Assert.Empty((await ContextFor("buy socks")).OpenItems);
        Assert.Contains((await ContextFor("what's the wifi?")).OpenItems, item => item.Id == note.Id);
    }

    [Fact]
    public void A_note_keeps_its_original_text_and_what_it_is_about()
    {
        var capture = _database.AddCapture("raw words about redis caching, plus more");
        _database.CompleteCapture(capture, [new SortedItem(ItemKind.Note, "redis caching", null, null, About: "Waypoint")]);

        var hit = _database.SearchEverything(new SearchQuery("plus", SearchIndex.Note), DateTime.Now).Single();

        Assert.Equal("raw words about redis caching, plus more", hit.Raw);
        Assert.Equal("Waypoint", hit.Detail);
    }

    [Fact]
    public async Task Search_answers_from_open_and_finished_things()
    {
        Store(new SortedItem(ItemKind.Shopping, "shampoo", null, null));
        _database.SetDone(Store(new SortedItem(ItemKind.Task, "history paper", null, null)), true);
        Store(new SortedItem(ItemKind.Task, "math paper", null, null));

        var outcome = await Run(new AssistantOp(AssistantOpKind.Search, "paper"));
        Assert.Contains("history paper", outcome.Answer);
        Assert.Contains("math paper", outcome.Answer);
        Assert.DoesNotContain("shampoo", outcome.Answer);

        var shopping = await Run(new AssistantOp(AssistantOpKind.Search, null, SearchIndex.Shopping));
        Assert.Contains("shampoo", shopping.Answer);
        Assert.DoesNotContain("paper", shopping.Answer);
    }

    [Fact]
    public void Search_can_be_held_to_a_time_range()
    {
        _database.SetDone(Store(new SortedItem(ItemKind.Task, "paper", null, null)), true);
        var now = DateTime.Now;

        Assert.Single(_database.SearchEverything(new SearchQuery(null, SearchIndex.Finished, now.AddDays(-1), now.AddDays(1)), now));
        Assert.Empty(_database.SearchEverything(new SearchQuery(null, SearchIndex.Finished, now.AddDays(-9), now.AddDays(-2)), now));
    }

    [Fact]
    public void Search_finds_past_feed_events_and_says_when_nothing_matches()
    {
        _database.ReplaceCalendarEvents([new CalendarEvent("old", "Old lab", "CS 101", InDays(-3), null)]);

        Assert.Single(_database.SearchEverything(new SearchQuery("lab", SearchIndex.Past), DateTime.Now));
        Assert.Equal("Nothing found for zebra.", SearchIndex.Reply(_database.SearchEverything(new SearchQuery("zebra"), DateTime.Now), "zebra"));
    }

    [Fact]
    public async Task A_list_is_made_shown_checked_and_run_again()
    {
        var made = await Run(new AssistantOp(AssistantOpKind.ListCreate, "my packing list", Entries: ["socks", "charger"]));
        Assert.Contains("Added socks, charger to packing", made.OpReplies!.Single());
        Assert.Null(made.Answer);

        await Run(new AssistantOp(AssistantOpKind.ListCheck, "packing", Entries: ["socks"]));
        var shown = await Run(new AssistantOp(AssistantOpKind.ListShow, "Packing"));
        Assert.Contains("- charger", shown.Answer);
        Assert.Contains("Checked off: socks", shown.Answer);

        var started = await Run(new AssistantOp(AssistantOpKind.ListStart, "packing"));
        Assert.Equal("packing is ready. Start with: socks", started.Answer);
        Assert.All(_database.GetListEntries("packing"), entry => Assert.False(entry.Done));
    }

    [Fact]
    public async Task The_shopping_list_is_the_shopping_items()
    {
        Store(new SortedItem(ItemKind.Shopping, "shampoo", null, null));

        await Run(new AssistantOp(AssistantOpKind.ListAdd, "shopping list", Entries: ["milk", "SHAMPOO"]));

        Assert.Equal(["shampoo", "milk"], _database.GetListEntries("Shopping").Select(entry => entry.Text));
        Assert.Equal(2, _database.GetOpenItems().Count(item => item.Kind == ItemKind.Shopping));

        await Run(new AssistantOp(AssistantOpKind.ListCheck, null, Entries: ["milk"]));

        Assert.Equal("shampoo", _database.GetListEntries("Shopping").Single().Text);
        Assert.Equal("milk", _database.GetHistory().Single().Title);
    }

    [Fact]
    public async Task A_vague_check_off_asks_instead_of_guessing()
    {
        await Run(new AssistantOp(AssistantOpKind.ListCreate, "packing", Entries: ["blue shirt", "red shirt"]));

        var outcome = await Run(new AssistantOp(AssistantOpKind.ListCheck, null, Entries: ["shirt"]));

        Assert.StartsWith("Which one:", outcome.OpReplies!.Single());
        Assert.All(_database.GetListEntries("packing"), entry => Assert.False(entry.Done));
    }

    [Fact]
    public async Task A_deleted_list_is_hidden_and_comes_back_if_named_again()
    {
        await Run(new AssistantOp(AssistantOpKind.ListCreate, "packing", Entries: ["socks"]));

        await Run(new AssistantOp(AssistantOpKind.ListDelete, "packing"));
        Assert.Empty(_database.GetListNames());

        await Run(new AssistantOp(AssistantOpKind.ListAdd, "packing", Entries: ["hat"]));
        Assert.Equal(["socks", "hat"], _database.GetListEntries("packing").Select(entry => entry.Text));
    }

    [Fact]
    public async Task A_brain_dump_keeps_the_raw_text_and_nothing_else_happens_without_a_change()
    {
        const string dump = "ugh ok so need to email prof, maybe $20 on lunch?? also that idea about flashcards";
        await Run(dump, new CaptureResult(
        [
            new SortedItem(ItemKind.Task, "email prof", null, null),
            new SortedItem(ItemKind.Note, "that idea about flashcards", null, null),
        ],
        Answer: "Was the $20 lunch a real purchase?"));

        var note = _database.SearchEverything(new SearchQuery("flashcards", SearchIndex.Note), DateTime.Now).Single();
        Assert.Equal(dump, note.Raw);
        Assert.DoesNotContain(_database.GetOpenItems(), item => item.Kind == ItemKind.Expense);
    }

    [Fact]
    public void Ops_are_read_from_the_sorter_reply_and_unknown_ones_are_skipped()
    {
        var result = ClaudeCliSorter.ParseResult("""
            {"is_error": false, "structured_output": {"items": [{"kind": "note", "title": "idea", "due_date": null, "amount_cents": null, "about": "Waypoint"}],
             "changes": [], "answer": null, "monthly_budget_cents": null, "budget_ops": [], "ops": [
               {"op": "list_add", "name": "packing", "text": null, "entries": ["socks"], "amount_cents": null, "at": null, "until": null},
               {"op": "search", "name": null, "text": "finished", "entries": null, "amount_cents": null, "at": "2026-10-25T00:00", "until": "2026-11-01T00:00"},
               {"op": "launch_rocket", "name": null, "text": null, "entries": null, "amount_cents": null, "at": null, "until": null}]}}
            """);

        Assert.Equal("Waypoint", result!.Items.Single().About);
        Assert.Equal(2, result.Ops!.Count);
        Assert.Equal(AssistantOpKind.ListAdd, result.Ops[0].Kind);
        Assert.Equal(["socks"], result.Ops[0].Entries);
        Assert.Equal(new DateTime(2026, 10, 25), result.Ops[1].At);
    }

    [Fact]
    public void Every_op_kind_is_in_the_sorter_schema_and_can_be_read_back()
    {
        using var schema = JsonDocument.Parse(ClaudeCliSorter.Schema);
        var names = schema.RootElement.GetProperty("properties").GetProperty("ops").GetProperty("items")
            .GetProperty("properties").GetProperty("op").GetProperty("enum").EnumerateArray().Select(name => name.GetString()).ToList();

        Assert.Contains("list_uncheck", names);
        Assert.Equal(Enum.GetValues<AssistantOpKind>().Length, names.Count);
        Assert.All(names, name => Assert.True(AssistantOps.TryParseKind(name, out _)));
    }

    [Fact]
    public void The_prompt_names_the_lists()
    {
        var context = new AssistantContext(DateTime.Now, [], [], Lists: ["packing"]);

        Assert.Contains("Lists: packing", ClaudeCliSorter.BuildPrompt("x", DateTime.Now, context));
    }
}
