using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

public sealed class CaptureTitleTests : IDisposable
{
    private const string Long =
        "Prof. Quill said the lab report needs a proper discussion section before Friday. Also the graphs should use the same axis scale, and I still need to ask Sam for the raw data from week three.";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public CaptureTitleTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void A_long_capture_is_titled_by_its_first_sentence()
    {
        var (title, full) = CaptureTitle.Shorten(Long);

        Assert.Equal("Prof. Quill said the lab report needs a proper discussion section before Friday.", title);
        Assert.Equal(Long, full);
    }

    [Fact]
    public void A_long_first_sentence_is_cut_between_words_at_about_120_characters()
    {
        var text = string.Join(' ', Enumerable.Repeat("remember", 40));

        var (title, full) = CaptureTitle.Shorten(text);

        Assert.True(title.Length <= CaptureTitle.MaxLength, title);
        Assert.EndsWith("remember…", title);
        Assert.Equal(text, full);
    }

    [Fact]
    public void A_short_capture_is_left_as_it_is()
    {
        Assert.Equal(("Email advisor about the extension. Then lunch.", null), CaptureTitle.Shorten("Email advisor about the extension. Then lunch."));
    }

    [Fact]
    public void A_long_capture_kept_as_a_note_has_a_short_title_and_the_whole_text()
    {
        _database.CompleteCapture(_database.AddCapture(Long), [new SortedItem(ItemKind.Note, Long, null, null)]);

        var note = _database.GetOpenItems().Single(item => item.Kind == ItemKind.Note);
        Assert.Equal("Prof. Quill said the lab report needs a proper discussion section before Friday.", note.Title);
        Assert.Equal(Long, _database.GetNote(note.Id)!.Body);
    }

    [Fact]
    public void A_long_title_from_the_sorter_is_shortened_and_kept_whole_on_the_item()
    {
        _database.CompleteCapture(_database.AddCapture($"todo: {Long}"), [new SortedItem(ItemKind.Task, Long, null, null)]);

        var task = _database.GetOpenItems().Single(item => item.Kind == ItemKind.Task);
        Assert.Equal("Prof. Quill said the lab report needs a proper discussion section before Friday.", task.Title);
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM items WHERE id = $id";
        command.Parameters.AddWithValue("$id", task.Id);
        Assert.Equal(Long, command.ExecuteScalar() as string);
    }

    [Fact]
    public void To_do_reads_the_whole_text_of_a_shortened_task_and_nothing_for_a_short_one()
    {
        _database.CompleteCapture(_database.AddCapture($"todo: {Long}"), [new SortedItem(ItemKind.Task, Long, DateOnly.FromDateTime(DateTime.Now).AddDays(1), null)]);
        _database.CompleteCapture(_database.AddCapture("buy stamps"), [new SortedItem(ItemKind.Task, "buy stamps", null, null)]);

        var open = _database.GetOpenItems();
        Assert.Equal(Long, open.Single(item => item.Title.StartsWith("Prof. Quill", StringComparison.Ordinal)).Body);
        Assert.Null(open.Single(item => item.Title == "buy stamps").Body);

        var task = open.Single(item => item.Body is not null);
        _database.SetDone(task, true);
        Assert.Equal(Long, _database.GetFinishedForList(DateTime.Now).Single(item => item.Id == task.Id).Body);
    }

    [Fact]
    public void A_note_brings_no_body_to_the_list()
    {
        _database.CompleteCapture(_database.AddCapture(Long), [new SortedItem(ItemKind.Note, Long, null, null)]);

        Assert.Null(_database.GetOpenItems().Single(item => item.Kind == ItemKind.Note).Body);
    }
}
