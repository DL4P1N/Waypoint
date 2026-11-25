using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;
using Waypoint.Core.Remote;

namespace Waypoint.Server.Tests;

/// <summary>
/// The server's notes change stamp, which lets a PC's notes folder sync skip a pass when nothing changed there.
/// </summary>
public sealed partial class NotesApiTests
{
    private const string StampPath = "/api/notes/stamp";
    private const string FileStatesPath = "/api/notes/file-states";

    /// <summary>
    /// Notes every path asked for, and can answer the stamp as a server from before would, with a 404. What it
    /// passes requests on to can be swapped, as for a server started again at the same address.
    /// </summary>
    private sealed class Counted(HttpMessageHandler inner) : HttpMessageHandler
    {
        public HttpMessageInvoker Target { get; set; } = new(inner);

        public List<string> Used { get; } = [];

        public bool NoStamp { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Used)
            {
                Used.Add(path);
            }

            return NoStamp && path == StampPath
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
                : Target.SendAsync(request, cancellationToken);
        }

        public string[] TakeUsed()
        {
            lock (Used)
            {
                var used = Used.ToArray();
                Used.Clear();
                return used;
            }
        }
    }

    private (ServerNotesData Notes, Counted Counted) CountedPc(bool noStamp = false)
    {
        var counted = new Counted(_factory.Server.CreateHandler()) { NoStamp = noStamp };
        var client = InMemoryServer.Client(new HttpClient(counted) { BaseAddress = _factory.Server.BaseAddress }, _token, "counted-pc");
        return (new ServerNotesData(client, new OfflineNotes(Path.Combine(_pc, "counted-notes.json"))), counted);
    }

    [Fact]
    public async Task The_notes_stamp_needs_the_token_and_stays_the_same_until_a_note_changes()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(StampPath)).StatusCode);

        var first = await Paired().GetFromJsonAsync<TextValue>(StampPath, RemoteJson.Options);
        var second = await Paired().GetFromJsonAsync<TextValue>(StampPath, RemoteJson.Options);
        Assert.False(string.IsNullOrEmpty(first!.Value));
        Assert.Equal(first, second);
    }

    [Fact]
    public void The_notes_stamp_moves_when_a_note_is_added_edited_moved_or_deleted()
    {
        var (notes, _, _) = Pc();
        var folder = notes.CreateNoteFolder("Biology", null, DateTime.Now).Id;

        var before = notes.NotesChangeStamp();
        Assert.NotNull(before);
        var id = notes.CreateNote("Cells", "The cell is the unit of life.", DateTime.Now);
        var added = notes.NotesChangeStamp();
        Assert.NotEqual(before, added);
        Assert.Equal(added, notes.NotesChangeStamp());

        Assert.True(notes.SaveNote(id, "Cells", "Cells have a membrane.", DateTime.Now));
        var edited = notes.NotesChangeStamp();
        Assert.NotEqual(added, edited);

        Assert.True(notes.MoveNoteToFolder(id, folder));
        var moved = notes.NotesChangeStamp();
        Assert.NotEqual(edited, moved);

        notes.DeleteNote(id, DateTime.Now);
        var deleted = notes.NotesChangeStamp();
        Assert.NotEqual(moved, deleted);
        Assert.NotNull(deleted);
    }

    [Fact]
    public void The_notes_stamp_moves_when_a_folder_is_renamed_or_deleted()
    {
        var (notes, _, _) = Pc();
        var folder = notes.CreateNoteFolder("Chemistry", null, DateTime.Now).Id;

        var before = notes.NotesChangeStamp();
        Assert.Null(notes.RenameNoteFolder(folder, "Organic chemistry").Problem);
        var renamed = notes.NotesChangeStamp();
        Assert.NotEqual(before, renamed);

        notes.DeleteNoteFolder(folder, NotesInFolder.Refuse);
        Assert.Null(notes.GetNoteFolder(folder));
        Assert.NotEqual(renamed, notes.NotesChangeStamp());
    }

    [Fact]
    public void After_the_server_restarts_a_settled_notes_folder_sync_makes_a_full_pass()
    {
        var root = Path.Combine(_pc, "Waypoint Notes");
        var (notes, counted) = CountedPc();
        var files = new NoteFiles(notes.ForSync(), root);
        notes.CreateNote("Groceries", "milk", DateTime.Now);
        files.Sync(DateTime.Now);
        files.Sync(DateTime.Now);
        var before = notes.NotesChangeStamp();
        counted.TakeUsed();
        Assert.Equal(new NoteSyncResult(0, 0, 0, false), files.Sync(DateTime.Now));
        Assert.Equal([StampPath], counted.TakeUsed());

        // Started again over the same data, with nothing changed: the count is the same, but it's another run's.
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        using var restarted = StartServer();
        counted.Target = new HttpMessageInvoker(restarted.Server.CreateHandler());

        Assert.NotEqual(before, notes.NotesChangeStamp());
        counted.TakeUsed();
        files.Sync(DateTime.Now);
        Assert.Contains(FileStatesPath, counted.TakeUsed());
        Assert.Equal("milk", File.ReadAllText(Path.Combine(root, "Groceries.md")));
    }

    [Fact]
    public void The_notes_folder_sync_skips_its_pass_while_the_servers_notes_are_unchanged()
    {
        var root = Path.Combine(_pc, "Waypoint Notes");
        var (notes, counted) = CountedPc();
        var files = new NoteFiles(notes.ForSync(), root);
        var id = notes.CreateNote("Groceries", "milk", DateTime.Now);

        // The first pass writes the file and its record, which moves the stamp, so the one after it reads again.
        files.Sync(DateTime.Now);
        files.Sync(DateTime.Now);
        counted.TakeUsed();

        Assert.Equal(new NoteSyncResult(0, 0, 0, false), files.Sync(DateTime.Now));
        Assert.Equal([StampPath], counted.TakeUsed());

        // Changed on the server from elsewhere: the next sync makes a full pass and writes it to the file.
        Elsewhere().SaveNote(id, "Groceries", "milk and eggs", DateTime.Now);
        Assert.Equal(1, files.Sync(DateTime.Now).Written);
        Assert.Contains(FileStatesPath, counted.TakeUsed());
        Assert.Equal("milk and eggs", File.ReadAllText(Path.Combine(root, "Groceries.md")));
    }

    [Fact]
    public void Against_a_server_without_the_stamp_every_notes_folder_sync_makes_a_full_pass()
    {
        var root = Path.Combine(_pc, "Waypoint Notes");
        var (notes, counted) = CountedPc(noStamp: true);
        var files = new NoteFiles(notes.ForSync(), root);
        var id = notes.CreateNote("Groceries", "milk", DateTime.Now);

        Assert.Null(notes.NotesChangeStamp());
        for (var round = 0; round < 3; round++)
        {
            counted.TakeUsed();
            files.Sync(DateTime.Now);
            Assert.Contains(FileStatesPath, counted.TakeUsed());
        }

        Elsewhere().SaveNote(id, "Groceries", "milk and eggs", DateTime.Now);
        Assert.Equal(1, files.Sync(DateTime.Now).Written);
        Assert.Equal("milk and eggs", File.ReadAllText(Path.Combine(root, "Groceries.md")));
    }

    [Fact]
    public async Task While_the_server_is_away_the_stamp_is_not_known_and_the_sync_waits_as_before()
    {
        var root = Path.Combine(_pc, "Waypoint Notes");
        var (notes, client, line) = Pc();
        var files = new NoteFiles(notes.ForSync(), root);
        var id = notes.CreateNote("Groceries", "milk", DateTime.Now);
        files.Sync(DateTime.Now);
        files.Sync(DateTime.Now);

        line.Down = true;
        Assert.Null(notes.ForSync().NotesChangeStamp());
        WriteOutside(Path.Combine(root, "Groceries.md"), "milk and bread");
        Assert.Throws<ServerUnavailableException>(() => files.Sync(DateTime.Now));

        line.Down = false;
        await client.CheckAsync();
        files.Sync(DateTime.Now);
        Assert.Equal("milk and bread", Elsewhere().GetNote(id)!.Body);
    }

    [Fact]
    public void After_a_restore_on_the_server_a_settled_notes_folder_sync_makes_a_full_pass()
    {
        var root = Path.Combine(_pc, "Waypoint Notes");
        var (notes, counted) = CountedPc();
        var files = new NoteFiles(notes.ForSync(), root);
        var id = notes.CreateNote("Groceries", "milk", DateTime.Now);
        var database = _factory.Services.GetRequiredService<WaypointServer>().Database;
        var backup = database.BackUpNow(DateTime.Now);

        Elsewhere().SaveNote(id, "Groceries", "milk and bread", DateTime.Now);
        files.Sync(DateTime.Now);
        files.Sync(DateTime.Now);
        counted.TakeUsed();
        Assert.Equal(new NoteSyncResult(0, 0, 0, false), files.Sync(DateTime.Now));

        Assert.True(database.RestoreBackup(backup, DateTime.Now).Restored);
        files.Sync(DateTime.Now);

        Assert.Contains(FileStatesPath, counted.TakeUsed());
        Assert.Equal("milk", File.ReadAllText(Path.Combine(root, "Groceries.md")));
    }
}
