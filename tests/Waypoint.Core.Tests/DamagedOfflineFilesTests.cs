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

        var aside = Assert.Single(SetAside(unsaved));
        Assert.Equal(bytes, File.ReadAllBytes(aside));
        Assert.False(File.Exists(unsaved), how);

        // Told about, and Open names where the file is kept.
        Assert.Contains(Path.GetFileName(aside), notes.Unreadable);
        Assert.Contains(aside, File.ReadAllText(notes.WriteUnsavedCopy()!));
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

        var aside = Assert.Single(SetAside(queue.RefusedPath));
        Assert.Equal(bytes, File.ReadAllBytes(aside));
        Assert.Contains(Path.GetFileName(aside), queue.Refused);
        Assert.Contains(aside, File.ReadAllText(queue.WriteRefusedCopy()!));
    }

    [Fact]
    public void A_notes_file_with_an_empty_entry_is_kept_aside_rather_than_crashing()
    {
        File.WriteAllText(NotesFile, "[null]");
        var notes = Notes();

        Assert.Empty(notes.GetNotes());
        notes.CreateNote("Made up errands", "post office", DateTime.Now);
        Assert.Equal(1, notes.WaitingToSave);
        Assert.Single(SetAside(NotesFile));
    }

    [Fact]
    public void A_notes_file_with_entries_of_the_wrong_shape_is_kept_aside_and_the_whole_ones_still_wait()
    {
        var bytes = Encoding.UTF8.GetBytes("""
            [null, {"id":-638000000000000001,"title":"Made up packing","body":"socks","at":"2026-03-04T10:00:00"}, {"id":7}, {"title":"Made up","body":"no id"}]
            """);
        File.WriteAllBytes(NotesFile, bytes);
        var notes = Notes();

        Assert.Equal(1, notes.WaitingToSave);
        Assert.Equal("socks", notes.GetNote(-638000000000000001)!.Body);
        Assert.Equal(bytes, File.ReadAllBytes(Assert.Single(SetAside(NotesFile))));
        Assert.NotNull(notes.Unreadable);

        // Read again, the file of whole ones is fine, so nothing more is set aside.
        Assert.Equal(1, Notes().WaitingToSave);
        Assert.Single(SetAside(NotesFile));
    }

    [Fact]
    public async Task A_captures_file_with_entries_of_the_wrong_shape_is_kept_aside_and_the_whole_ones_still_go()
    {
        var bytes = Encoding.UTF8.GetBytes("""
            [null, {"id":"made-up-1","text":"buy a made up stamp","writtenAt":"2026-03-04T10:00:00+00:00"}, {"id":"made-up-2"}]
            """);
        File.WriteAllBytes(QueueFile, bytes);
        var queue = new CaptureQueue(QueueFile);

        Assert.Equal(1, queue.Count);
        Assert.Equal(bytes, File.ReadAllBytes(Assert.Single(SetAside(QueueFile))));
        var sent = new List<string>();
        Assert.Equal(1, await queue.SendAsync(capture =>
        {
            sent.Add(capture.Text);
            return Task.FromResult(true);
        }));
        Assert.Equal(["buy a made up stamp"], sent);
        Assert.NotNull(queue.Refused);
    }

    [Fact]
    public void Damaged_notes_typed_while_away_are_told_about()
    {
        File.WriteAllText(NotesFile, """[{"id":-638000000000000000,"title":"Made up packing","body":"socks and a lan""");
        var notes = Notes();

        // Told on the status line even before the Notes page is opened, with the file they're kept in.
        var told = notes.Unreadable;
        var aside = Assert.Single(SetAside(NotesFile));
        Assert.Equal(
            $"Some notes typed while the server was away couldn't be read, so they weren't saved on the server. They're kept on this PC in {Path.GetFileName(aside)}.",
            told);

        // Open shows where the file is kept and what it held, so the text can be copied back in.
        var copy = File.ReadAllText(notes.WriteUnsavedCopy()!);
        Assert.Contains(aside, copy);
        Assert.Contains("socks and a lan", copy);
        Assert.Empty(notes.GetNotes());
    }

    [Fact]
    public void Damaged_captures_waiting_for_the_server_are_told_about()
    {
        File.WriteAllText(QueueFile, """[{"id":"made-up","text":"call a made up plum""");
        var queue = new CaptureQueue(QueueFile);

        var told = queue.Refused;
        Assert.Equal(0, queue.Count);
        var aside = Assert.Single(SetAside(QueueFile));
        Assert.Equal(
            $"Some things you typed couldn't be read, so they weren't saved on the server. They're kept on this PC in {Path.GetFileName(aside)}.",
            told);
        var copy = File.ReadAllText(queue.WriteRefusedCopy()!);
        Assert.Contains(aside, copy);
        Assert.Contains("call a made up plum", copy);

        // A queue whose files are fine says nothing.
        Assert.Null(new CaptureQueue(Path.Combine(_folder, "other-queue.json")).Refused);
    }

    [Fact]
    public void Damaged_notes_are_told_about_again_after_a_restart_until_the_notice_is_dismissed()
    {
        File.WriteAllText(NotesFile, """[{"id":-638000000000000000,"title":"Made up packing","body":"socks and a lan""");
        var told = Notes().Unreadable;
        Assert.NotNull(told);

        // Started again: the file read now is fine, but the one set aside is still told about.
        Assert.Equal(told, Notes().Unreadable);

        var notes = Notes();
        notes.MarkSetAsideSeen();
        Assert.Null(notes.Unreadable);
        Assert.Null(Notes().Unreadable);

        // The file itself stays, and the copy that Open shows still has it.
        var aside = Assert.Single(SetAside(NotesFile));
        Assert.Contains(aside, File.ReadAllText(Notes().WriteUnsavedCopy()!));

        // Another one found damaged later is told about, by itself.
        File.WriteAllText(NotesFile, "not notes at all");
        var later = Notes().Unreadable;
        var newer = Assert.Single(SetAside(NotesFile), file => file != aside);
        Assert.Equal(
            $"Some notes typed while the server was away couldn't be read, so they weren't saved on the server. They're kept on this PC in {Path.GetFileName(newer)}.",
            later);
    }

    [Fact]
    public void Damaged_captures_are_told_about_again_after_a_restart_until_the_notice_is_dismissed()
    {
        File.WriteAllText(QueueFile, """[{"id":"made-up","text":"call a made up plum""");
        var told = new CaptureQueue(QueueFile).Refused;
        Assert.NotNull(told);
        Assert.Equal(told, new CaptureQueue(QueueFile).Refused);

        new CaptureQueue(QueueFile).MarkSetAsideSeen();
        var restarted = new CaptureQueue(QueueFile);
        Assert.Null(restarted.Refused);
        Assert.Empty(restarted.SetAsideFiles);
        Assert.Contains("call a made up plum", File.ReadAllText(restarted.WriteRefusedCopy()!));
    }

    [Fact]
    public void A_dismissal_is_kept_per_file_and_a_damaged_list_of_them_only_tells_again()
    {
        File.WriteAllText(QueueFile, "[{");
        var queue = new CaptureQueue(QueueFile);
        var first = Assert.Single(queue.SetAsideFiles);
        queue.MarkSetAsideSeen();

        // A file set aside from the notes beside it is its own, and still told about.
        File.WriteAllText(NotesFile, "[{");
        Assert.NotNull(Notes().Unreadable);
        Assert.Null(new CaptureQueue(QueueFile).Refused);

        File.WriteAllText(Path.Combine(_folder, KeptCopy.SeenName), "{ not a list");
        Assert.Equal([first], new CaptureQueue(QueueFile).SetAsideFiles);
    }
}
