using System.Text;
using Waypoint.Core.Remote;

namespace Waypoint.Core.Tests;

/// <summary>
/// The files server mode keeps on the PC while the server is away, found damaged when they're read: cut off partway,
/// as by a disk filling up or the power going, or overwritten with something that isn't JSON at all. The app has to
/// keep going, keep the damaged file aside byte for byte so whatever was typed can still be found, and carry on
/// with an empty one.
/// </summary>
public sealed class DamagedOfflineFilesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-damaged-{Guid.NewGuid():N}");

    public DamagedOfflineFilesTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>A server that never answers, so everything typed waits in the files.</summary>
    private sealed class NoServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
    }

    private string NotesFile => Path.Combine(_folder, "server-notes.json");

    private string QueueFile => Path.Combine(_folder, "server-queue.json");

    private ServerNotesData Notes() =>
        new(new ServerClient(new HttpClient(new NoServer()) { BaseAddress = new Uri("http://localhost/") }, "made-up-token"), new OfflineNotes(NotesFile));

    private string[] SetAside(string file) => Directory.GetFiles(_folder, Path.GetFileName(file) + ".unreadable-*");

    public static TheoryData<string, byte[]> Damage => new()
    {
        { "cut off partway", Encoding.UTF8.GetBytes("""[{"id":-638000000000000000,"title":"Made up packing","body":"socks and a lan""") },
        { "not JSON at all", [0x00, 0xFF, 0x13, 0x37, 0x80, 0x81, 0x7B, 0x5B, 0x00, 0xC3] },
        { "left empty", [] },
        { "the wrong shape", Encoding.UTF8.GetBytes("""{"title":"Made up packing"}""") },
    };

    [Theory]
    [MemberData(nameof(Damage))]
    public void Damaged_notes_typed_while_away_are_kept_aside_and_the_notes_page_keeps_working(string how, byte[] bytes)
    {
        File.WriteAllBytes(NotesFile, bytes);
        var notes = Notes();

        Assert.Equal(0, notes.WaitingToSave);
        Assert.Empty(notes.GetNotes());

        var aside = Assert.Single(SetAside(NotesFile));
        Assert.Equal(bytes, File.ReadAllBytes(aside));
        Assert.False(File.Exists(NotesFile), how);

        // Typing goes on as usual, into a new file.
        var id = notes.CreateNote("Made up errands", "post office", DateTime.Now);
        Assert.Equal(1, notes.WaitingToSave);
        Assert.Equal("post office", notes.GetNote(id)!.Body);
        Assert.Equal(1, Notes().WaitingToSave);
        Assert.Equal(bytes, File.ReadAllBytes(aside));
    }

    [Theory]
    [MemberData(nameof(Damage))]
    public void A_damaged_file_of_notes_the_server_turned_down_is_kept_aside(string how, byte[] bytes)
    {
        var unsaved = new OfflineNotes(NotesFile).UnsavedPath;
        File.WriteAllBytes(unsaved, bytes);
        var notes = Notes();

        Assert.Equal(0, notes.NotSaved);
        Assert.Null(notes.WriteUnsavedCopy());

        var aside = Assert.Single(SetAside(unsaved));
        Assert.Equal(bytes, File.ReadAllBytes(aside));
        Assert.False(File.Exists(unsaved), how);
    }

    [Theory]
    [MemberData(nameof(Damage))]
    public async Task Damaged_captures_waiting_for_the_server_are_kept_aside_and_new_ones_still_queue(string how, byte[] bytes)
    {
        File.WriteAllBytes(QueueFile, bytes);
        var queue = new CaptureQueue(QueueFile);

        Assert.Equal(0, queue.Count);
        var aside = Assert.Single(SetAside(QueueFile));
        Assert.Equal(bytes, File.ReadAllBytes(aside));

        queue.Add("buy a made up stamp", DateTimeOffset.Now);
        var sent = new List<string>();
        Assert.Equal(1, await queue.SendAsync(capture =>
        {
            sent.Add(capture.Text);
            return Task.FromResult(true);
        }));
        Assert.Equal(["buy a made up stamp"], sent);
        Assert.Equal(bytes, File.ReadAllBytes(aside));
        Assert.Single(SetAside(QueueFile));
        Assert.True(File.Exists(QueueFile), how);
    }

    [Fact]
    public void A_damaged_refused_captures_file_is_kept_aside()
    {
        var queue = new CaptureQueue(QueueFile);
        var bytes = Encoding.UTF8.GetBytes("""[{"id":"made-up","text":"call a made up plum""");
        File.WriteAllBytes(queue.RefusedPath, bytes);

        Assert.Equal(0, queue.RefusedCount);
        Assert.Null(queue.Refused);

        Assert.Equal(bytes, File.ReadAllBytes(Assert.Single(SetAside(queue.RefusedPath))));
    }

    [Fact(Skip = "bug: a notes file holding a null entry, such as [null] left by a damaged write, crashes the Notes page in server mode with a NullReferenceException instead of being kept aside. OfflineNotes.Read only sets a file aside on a JsonException, and null entries reach note.Id.")]
    public void A_notes_file_with_an_empty_entry_is_kept_aside_rather_than_crashing()
    {
        File.WriteAllText(NotesFile, "[null]");
        var notes = Notes();

        Assert.Empty(notes.GetNotes());
        notes.CreateNote("Made up errands", "post office", DateTime.Now);
        Assert.Equal(1, notes.WaitingToSave);
        Assert.Single(SetAside(NotesFile));
    }

    [Fact(Skip = "bug: notes typed while the server was away that are found damaged are set aside without a word, so the person never learns they weren't sent. Nothing such as NotSaved or the unsaved copy counts the set-aside file.")]
    public void Damaged_notes_typed_while_away_are_told_about()
    {
        File.WriteAllText(NotesFile, """[{"id":-638000000000000000,"title":"Made up packing","body":"socks and a lan""");
        var notes = Notes();
        notes.GetNotes();

        Assert.Equal(1, notes.NotSaved);
        Assert.Contains(Path.GetFileName(Assert.Single(SetAside(NotesFile))), File.ReadAllText(notes.WriteUnsavedCopy()!));
    }

    [Fact(Skip = "bug: captures waiting for the server that are found damaged are set aside without a word. The status line only shows Refused, which doesn't count the set-aside file, so the person never learns what they typed wasn't sent.")]
    public void Damaged_captures_waiting_for_the_server_are_told_about()
    {
        File.WriteAllText(QueueFile, """[{"id":"made-up","text":"call a made up plum""");
        var queue = new CaptureQueue(QueueFile);

        Assert.Equal(0, queue.Count);
        Assert.NotNull(queue.Refused);
    }
}
