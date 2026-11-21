using System.Text.Json;

namespace Waypoint.Core.Remote;

/// <summary>
/// A note typed while the server was away. Id is the note's own id for an edit, and below zero for a note made
/// while away. BasedOn is the server's text the edit started from, so a change made elsewhere meanwhile is noticed.
/// For a new note it's the text first sent, in case the server made it then without the PC hearing. ClientId goes
/// with what's made from it, so sending it again after a lost answer doesn't make a second note. BasedOnTitle is
/// the title the edit started from, when known, so a rename made elsewhere meanwhile is kept, and is missing from
/// files written before it was kept.
/// </summary>
public sealed record OfflineNote(long Id, string Title, string Body, string? BasedOn, long? FolderId, DateTime At, string? ClientId = null, string? BasedOnTitle = null)
{
    public bool IsNew => Id < 0;
}

/// <summary>
/// What happened to one note typed while away, once the server was back. Problem says why the server turned it
/// down for good, in which case what was typed is kept in the unsaved file instead.
/// </summary>
public sealed record OfflineNoteSent(long Id, long NoteId, bool KeptBoth, string? Problem = null);

/// <summary>A note the server turned down, kept with why, so the text can be found by hand.</summary>
public sealed record UnsavedNote(OfflineNote Note, string Problem, DateTime At);

/// <summary>
/// Notes typed in server mode while the server is away wait here, in a file, so they survive the app closing.
/// Typing in a note is never lost: when the server is back each one is saved, and if the note changed there in the
/// meantime, or is gone, what was typed here is kept as a note of its own beside it.
/// </summary>
public sealed class OfflineNotes(string path)
{
    public const string KeptBothSuffix = " (edited on this PC)";

    private readonly object _gate = new();
    private readonly SemaphoreSlim _sending = new(1, 1);

    /// <summary>Next to the waiting file: notes the server turned down for good, so what was typed is never thrown away.</summary>
    public string UnsavedPath { get; } = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "", Path.GetFileNameWithoutExtension(path) + "-unsaved.json");

    /// <summary>How many notes are kept in the unsaved file.</summary>
    public int Unsaved
    {
        get
        {
            lock (_gate)
            {
                return ReadUnsaved().Count;
            }
        }
    }

    public const string UnsavedCopyName = "Notes not saved on the server.txt";

    /// <summary>
    /// The files of notes found damaged and set aside, then or before a restart, that haven't been opened or had their
    /// notice closed, so the person can be told. What was in them was never sent, so they're named in the unsaved copy
    /// too.
    /// </summary>
    public IReadOnlyList<string> SetAsideFiles
    {
        get
        {
            lock (_gate)
            {
                // Read first, so a damaged file is found even before the Notes page is opened.
                Read();
                ReadUnsaved();
            }

            return KeptCopy.SetAsideOf([path, UnsavedPath], seen: false);
        }
    }

    /// <summary>Opened or closed: the files set aside so far aren't told about again. One set aside later is.</summary>
    public void MarkSetAsideSeen() => KeptCopy.MarkSeen(SetAsideFiles);

    /// <summary>What to show while any file of notes was found damaged and set aside. Null when none was.</summary>
    public string? Unreadable => SetAsideFiles switch
    {
        [] => null,
        [var one] => $"Some notes typed while the server was away couldn't be read, so they weren't saved on the server. They're kept on this PC in {Path.GetFileName(one)}.",
        var files => $"Some notes typed while the server was away couldn't be read, so they weren't saved on the server. They're kept on this PC in {files.Count} files, starting with {Path.GetFileName(files[0])}.",
    };

    /// <summary>
    /// Writes the notes the server turned down as plain text beside the unsaved file and returns where, so they can
    /// be opened and copied back in. Null when there are none. The unsaved file itself stays as it is.
    /// </summary>
    public string? WriteUnsavedCopy()
    {
        List<UnsavedNote> unsaved;
        lock (_gate)
        {
            unsaved = ReadUnsaved();
        }

        // Every one still on this PC, opened before or not, so the copy shows all that's kept.
        var aside = KeptCopy.SetAsideOf([path, UnsavedPath]);

        return unsaved.Count == 0 && aside.Count == 0
            ? null
            : KeptCopy.Write(
                Path.Combine(Path.GetDirectoryName(UnsavedPath) ?? "", UnsavedCopyName),
                unsaved.Select(kept => (
                    $"{(string.IsNullOrWhiteSpace(kept.Note.Title) ? "A note" : kept.Note.Title)}, typed {KeptCopy.When(kept.Note.At)}. {kept.Problem.TrimEnd('.')}",
                    kept.Note.Body))
                .Concat(aside.Select(file => KeptCopy.SetAsideEntry("Notes typed while the server was away", file))));
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return Read().Count;
            }
        }
    }

    public IReadOnlyList<OfflineNote> All
    {
        get
        {
            lock (_gate)
            {
                return Read();
            }
        }
    }

    public OfflineNote? Find(long id)
    {
        lock (_gate)
        {
            return Read().FirstOrDefault(note => note.Id == id);
        }
    }

    /// <summary>Keeps a new note, with an id below zero that stands for it until the server gives it a real one.</summary>
    /// <param name="clientId">The id it was already sent with, when the server may have made it without the PC hearing.</param>
    public OfflineNote AddNew(string title, string body, long? folderId, DateTime now, string? clientId = null)
    {
        lock (_gate)
        {
            var waiting = Read();
            // From the clock, so an id is never handed out twice, even after an earlier one was sent and forgotten.
            var id = -Math.Max(DateTime.UtcNow.Ticks, 1 - waiting.Select(note => note.Id).DefaultIfEmpty(0).Min());
            var note = new OfflineNote(id, title, body, clientId is null ? null : body, folderId, now, clientId ?? Guid.NewGuid().ToString("N"));
            waiting.Add(note);
            Write(waiting);
            return note;
        }
    }

    /// <summary>Keeps the latest text for a note. The first edit's starting point is what counts.</summary>
    public void Keep(long id, string title, string body, string? basedOn, long? folderId, DateTime now, string? basedOnTitle = null)
    {
        lock (_gate)
        {
            var waiting = Read();
            var index = waiting.FindIndex(note => note.Id == id);
            if (index >= 0)
            {
                waiting[index] = waiting[index] with { Title = title, Body = body, At = now };
            }
            else
            {
                waiting.Add(new OfflineNote(id, title, body, basedOn, folderId, now, Guid.NewGuid().ToString("N"), basedOn is null ? null : basedOnTitle));
            }

            Write(waiting);
        }
    }

    /// <summary>
    /// Remembers the text a new note is first sent with, before it goes. If the server makes it but the answer is
    /// lost, the next send can tell that note was made from this and not changed elsewhere since.
    /// </summary>
    public void Sending(OfflineNote note)
    {
        lock (_gate)
        {
            var waiting = Read();
            var index = waiting.FindIndex(entry => entry.Id == note.Id);
            if (index >= 0 && waiting[index].BasedOn is null)
            {
                waiting[index] = waiting[index] with { BasedOn = note.Body };
                Write(waiting);
            }
        }
    }

    /// <summary>Takes a note out of what's waiting, such as a note made while away and then deleted. Null if it isn't here.</summary>
    public OfflineNote? Remove(long id)
    {
        lock (_gate)
        {
            var waiting = Read();
            var note = waiting.FirstOrDefault(entry => entry.Id == id);
            if (note is not null)
            {
                waiting.Remove(note);
                Write(waiting);
            }

            return note;
        }
    }

    /// <summary>Puts back a note taken out with <see cref="Remove"/>, for undo.</summary>
    public void PutBack(OfflineNote note)
    {
        lock (_gate)
        {
            var waiting = Read();
            if (waiting.All(entry => entry.Id != note.Id))
            {
                waiting.Add(note);
                Write(waiting);
            }
        }
    }

    /// <summary>
    /// Sends what's waiting, oldest first. <paramref name="send"/> returns the note it was saved as, or null if it
    /// should stay and be tried again. One that can't go now doesn't hold up the rest. One the server turned down
    /// for good moves to the unsaved file, so it stops waiting without anything typed being lost.
    /// </summary>
    public async Task<IReadOnlyList<OfflineNoteSent>> SendAsync(Func<OfflineNote, Task<OfflineNoteSent?>> send, CancellationToken cancellationToken = default)
    {
        await _sending.WaitAsync(cancellationToken);
        try
        {
            List<long> ids;
            lock (_gate)
            {
                ids = Read().Select(note => note.Id).ToList();
            }

            var sent = new List<OfflineNoteSent>();
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Find(id) is not { } next || await send(next) is not { } done)
                {
                    continue;
                }

                lock (_gate)
                {
                    // Typing that came in while it was being sent stays for the next round.
                    var waiting = Read();
                    var now = waiting.FirstOrDefault(note => note.Id == next.Id);
                    if (done.Problem is not null)
                    {
                        // Kept before it leaves the waiting file, so it's always in one or the other.
                        KeepUnsaved(new UnsavedNote(now ?? next, done.Problem, DateTime.Now));
                    }

                    waiting.RemoveAll(note => note.Id == next.Id);
                    if (done.Problem is null && now is not null && (now.Title != next.Title || now.Body != next.Body))
                    {
                        waiting.Insert(0, now with { Id = done.NoteId, BasedOn = next.Body, ClientId = Guid.NewGuid().ToString("N") });
                    }

                    Write(waiting);
                }

                sent.Add(done);
            }

            return sent;
        }
        finally
        {
            _sending.Release();
        }
    }

    private void KeepUnsaved(UnsavedNote note)
    {
        var unsaved = ReadUnsaved();
        unsaved.Add(note);
        var temporary = $"{UnsavedPath}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(unsaved, RemoteJson.Options));
        File.Move(temporary, UnsavedPath, overwrite: true);
    }

    private List<UnsavedNote> ReadUnsaved() =>
        ReadKept<UnsavedNote>(UnsavedPath, kept => kept?.Note is { Title: not null, Body: not null } && kept.Problem is not null, unsaved =>
        {
            var temporary = $"{UnsavedPath}.tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(unsaved, RemoteJson.Options));
            File.Move(temporary, UnsavedPath, overwrite: true);
        });

    private List<OfflineNote> Read() =>
        ReadKept<OfflineNote>(path, note => note is { Title: not null, Body: not null } && note.Id != 0, Write);

    /// <summary>
    /// Reads a kept file. One that can't be read, or that holds an entry that's empty or the wrong shape, is set
    /// aside byte for byte, so whatever was typed can still be found by hand, and is told about. The whole entries
    /// are kept in a new file, so they still go.
    /// </summary>
    private List<T> ReadKept<T>(string file, Func<T?, bool> whole, Action<List<T>> keep)
    {
        if (!File.Exists(file))
        {
            return [];
        }

        List<T?> entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<T?>>(File.ReadAllText(file), RemoteJson.Options) ?? [];
        }
        catch (JsonException)
        {
            KeptCopy.SetAside(file);
            return [];
        }

        var good = entries.Where(whole).Select(entry => entry!).ToList();
        if (good.Count != entries.Count)
        {
            KeptCopy.SetAside(file);
            if (good.Count > 0)
            {
                keep(good);
            }
        }

        return good;
    }

    private void Write(List<OfflineNote> waiting)
    {
        if (waiting.Count == 0)
        {
            File.Delete(path);
            return;
        }

        var temporary = $"{path}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(waiting, RemoteJson.Options));
        File.Move(temporary, path, overwrite: true);
    }
}
