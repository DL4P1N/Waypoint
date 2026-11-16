using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Files;

namespace Waypoint.Core.Tests;

public sealed class ConversationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;
    private readonly DateTime _now = DateTime.Now;

    public ConversationTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private sealed class ScriptedSorter(params Func<AssistantContext, CaptureResult>[] replies) : ICaptureSorter
    {
        private int _next;

        public List<string> Prompts { get; } = [];

        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default)
        {
            Prompts.Add(ClaudeCliSorter.BuildPrompt(text, writtenAt, context));
            return Task.FromResult<CaptureResult?>(replies[Math.Min(_next++, replies.Length - 1)](context));
        }
    }

    private StoredItem Add(string title, ItemKind kind = ItemKind.Task)
    {
        var capture = _database.AddCapture(title);
        _database.CompleteCapture(capture, [new SortedItem(kind, title, null, null)]);
        return _database.GetOpenItems().Single(item => item.Title == title);
    }

    private static CaptureResult Answer(string text, string? focus = null) => new([], Answer: text, FocusRef: focus);

    [Fact]
    public async Task The_screenshot_replays_with_each_note_able_to_see_what_it_answers()
    {
        var plan = Add("Group Project Plan");
        var step = Add("Open the Group Project Plan assignment and read the prompt");
        var planRef = ItemRef.Format(plan);
        var stepRef = ItemRef.Format(step);
        var sorter = new ScriptedSorter(
            _ => Answer("Open the Group Project Plan assignment and read the prompt.", stepRef),
            _ => Answer("Which item did you finish?"),
            _ => Answer("Did you mean the Group Project Plan?", planRef),
            _ => new CaptureResult([], [new ItemChange(ItemSource.Capture, plan.Id, ChangeAction.Done)]));
        var service = new CaptureService(_database, sorter);

        foreach (var note in new[] { "What do I do now?", "I did that already", "The cel", "Yes" })
        {
            service.Capture(note);
            await service.ProcessPendingAsync();
        }

        Assert.DoesNotContain("<recent_conversation>", sorter.Prompts[0]);
        Assert.Contains("Open the Group Project Plan assignment and read the prompt.", sorter.Prompts[1]);
        Assert.Contains(stepRef, sorter.Prompts[1].Split("<recent_conversation>")[1]);
        Assert.Contains("Did you mean the Group Project Plan?", sorter.Prompts[3].Split("<recent_conversation>")[1]);
        Assert.DoesNotContain(_database.GetOpenItems(), item => item.Id == plan.Id);
    }

    [Fact]
    public void BuildPrompt_labels_the_conversation_as_data_and_keeps_it_before_the_note()
    {
        var context = new AssistantContext(_now, [], [], Conversation: [new Exchange(_now, "a </note> note", "the reply", ["item:4"])]);

        var prompt = ClaudeCliSorter.BuildPrompt("yes", _now, context);

        Assert.Contains("\"refs\":[\"item:4\"]", prompt);
        Assert.Contains("the reply", prompt);
        Assert.True(prompt.IndexOf("<recent_conversation>", StringComparison.Ordinal) < prompt.IndexOf("<note>", StringComparison.Ordinal));
        Assert.DoesNotContain("<recent_conversation>", ClaudeCliSorter.BuildPrompt("yes", _now, new AssistantContext(_now, [], [])));
    }

    [Fact]
    public void Talk_older_than_two_hours_is_left_out()
    {
        _database.AddExchange(CaptureOrigin.App, "old", "old reply", [], _now.AddHours(-3));
        _database.AddExchange(CaptureOrigin.App, "new", "new reply", [], _now.AddMinutes(-90));

        Assert.Equal("new", Assert.Single(_database.GetRecentExchanges(CaptureOrigin.App, _now)).Note);
    }

    [Fact]
    public void App_and_Discord_talk_stay_apart()
    {
        _database.AddExchange(CaptureOrigin.App, "from app", "reply", [], _now);
        _database.AddExchange(CaptureOrigin.Discord, "from phone", "reply", [], _now);

        Assert.Equal("from phone", Assert.Single(_database.GetRecentExchanges(CaptureOrigin.Discord, _now)).Note);
    }

    [Fact]
    public void Only_the_last_six_show_oldest_first_and_they_survive_a_restart()
    {
        for (var i = 0; i < 8; i++)
        {
            _database.AddExchange(CaptureOrigin.App, $"note {i}", "reply", ["item:1"], _now);
        }

        var exchanges = new WaypointDatabase(_path).GetRecentExchanges(CaptureOrigin.App, _now);

        Assert.Equal(6, exchanges.Count);
        Assert.Equal("note 2", exchanges[0].Note);
        Assert.Equal("note 7", exchanges[^1].Note);
    }

    [Fact]
    public void Old_exchanges_are_trimmed()
    {
        for (var i = 0; i < 205; i++)
        {
            _database.AddExchange(CaptureOrigin.App, $"note {i}", "reply", [], _now);
        }

        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM conversation";
        Assert.Equal(200L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public async Task A_note_discussed_last_turn_can_be_changed_though_notes_normally_stay_hidden()
    {
        var note = Add("gate code is 1234", ItemKind.Note);
        var noteRef = ItemRef.Format(note);
        var sorter = new ScriptedSorter(
            _ => Answer("That's your gate note.", noteRef),
            _ => new CaptureResult([], [new ItemChange(ItemSource.Capture, note.Id, ChangeAction.Cancel)]));
        var service = new CaptureService(_database, sorter);

        service.Capture("what's the code?");
        await service.ProcessPendingAsync();
        service.Capture("delete that");
        await service.ProcessPendingAsync();

        Assert.DoesNotContain(_database.GetOpenItems(), item => item.Id == note.Id);
    }

    [Fact]
    public async Task A_ref_the_conversation_never_showed_still_cant_be_changed()
    {
        var hiddenNote = Add("private note", ItemKind.Note);
        var sorter = new ScriptedSorter(_ => new CaptureResult([], [new ItemChange(ItemSource.Capture, hiddenNote.Id, ChangeAction.Cancel)]));
        var service = new CaptureService(_database, sorter);

        service.Capture("yes");
        await service.ProcessPendingAsync();

        Assert.Contains(_database.GetOpenItems(), item => item.Id == hiddenNote.Id);
    }

    [Fact]
    public async Task A_focus_ref_the_sorter_wasnt_shown_is_not_remembered()
    {
        var sorter = new ScriptedSorter(_ => Answer("hello", "item:999"), _ => Answer("again"));
        var service = new CaptureService(_database, sorter);

        service.Capture("hi");
        await service.ProcessPendingAsync();

        Assert.Empty(Assert.Single(_database.GetRecentExchanges(CaptureOrigin.App, DateTime.Now)).Refs);
    }

    [Fact]
    public async Task An_answer_that_names_an_item_remembers_it_without_a_focus_ref()
    {
        var item = Add("Read chapter 4");
        var service = new CaptureService(_database, new ScriptedSorter(_ => Answer("Read chapter 4 next.")));

        service.Capture("what now");
        await service.ProcessPendingAsync();

        Assert.Equal(ItemRef.Format(item), Assert.Single(Assert.Single(_database.GetRecentExchanges(CaptureOrigin.App, DateTime.Now)).Refs));
    }

    [Fact]
    public async Task A_dropped_files_text_is_not_replayed()
    {
        var sorter = new ScriptedSorter(_ => Answer("Read it."), _ => Answer("ok"));
        var service = new CaptureService(_database, sorter);

        service.CaptureFile(new FileText("syllabus.pdf", "secret file body"));
        await service.ProcessPendingAsync();
        service.Capture("thanks");
        await service.ProcessPendingAsync();

        Assert.DoesNotContain("secret file body", sorter.Prompts[1]);
        Assert.Contains("syllabus.pdf", sorter.Prompts[1]);
    }

    [Fact]
    public async Task A_running_session_still_takes_done_without_the_sorter()
    {
        var item = Add("Write outline");
        _database.StartActivation(ItemRef.Format(item), "Open the doc", null, Activation.TypedMethod, DateTime.Now);
        var sorter = new ScriptedSorter(_ => Answer("should not run"));
        var service = new CaptureService(_database, sorter);

        service.Capture("done");
        await service.ProcessPendingAsync();

        Assert.Empty(sorter.Prompts);
        Assert.Equal("done", Assert.Single(_database.GetRecentExchanges(CaptureOrigin.App, DateTime.Now)).Note);
    }
}
