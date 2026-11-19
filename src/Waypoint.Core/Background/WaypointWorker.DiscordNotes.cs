using System.Globalization;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Files;
using Waypoint.Core.Notes;

namespace Waypoint.Core.Background;

/// <summary>
/// Notes on the phone: the folder index pinned in the notes channel, a folder's notes, a note a page at a time, a
/// search, and a new note. A file sent in the notes channel makes notes the same way dropping it on the Notes page
/// does. Everything reads and changes notes through the same calls the Notes page uses.
/// </summary>
public sealed partial class WaypointWorker
{
    /// <summary>Opens what's picked: a folder, a note, or anything search found. Its choices say which, such as "note:12".</summary>
    public const string OpenMenu = DiscordBridge.ViewPrefix + "open";

    /// <summary>Search notes and New note on the notes views open a form. New note's folder follows, or "u" for Unfiled.</summary>
    public const string NoteSearch = "note-search";
    public const string NewNote = "note-new:";

    private const string FolderTarget = "folder:";
    private const string NoteTarget = "note:";
    private const string NoteSearchTarget = "notesearch:";
    private const string Unfiled = "u";
    private const int NoteText = 3800;

    /// <summary>
    /// Makes notes from files sent in the notes channel, the same steps as the Notes page. The PC app and the server
    /// set it to the one their Notes page uses. Without it, a file there says notes from files aren't available.
    /// </summary>
    public INotesData? NoteFiles { get; set; }

    private static string FolderKey(long? id) => id?.ToString(CultureInfo.InvariantCulture) ?? Unfiled;

    private static string Day(DateOnly date) => date.ToString("MMM d", CultureInfo.InvariantCulture);

    /// <summary>Every folder in order, inside ones under their parent, with how many notes each holds. Unfiled comes first.</summary>
    private DiscordCard NotesIndexCard(int page)
    {
        var rows = new List<(string Line, MenuChoice Choice)>
        {
            ($"• Unfiled ({_database.CountNotesInFolder(null).ToString(CultureInfo.InvariantCulture)})", new MenuChoice("Unfiled", FolderTarget + Unfiled)),
        };

        void Add(IEnumerable<NoteFolder> folders, int depth)
        {
            foreach (var folder in folders)
            {
                var indent = string.Concat(Enumerable.Repeat("› ", depth));
                rows.Add((Shorten($"{indent}• {DiscordText.Escape(folder.Name)} ({folder.NoteCount.ToString(CultureInfo.InvariantCulture)})", 300), new MenuChoice(folder.Name, FolderTarget + FolderKey(folder.Id), depth > 0 ? "Inside another folder" : null)));
                Add(folder.Children, depth + 1);
            }
        }

        Add(_database.GetNoteFolders(), 0);
        var pages = DiscordPages.Split(rows, row => row.Line.Length + 1, NoteText);
        page = DiscordPages.Clamp(page, pages.Count);
        var shown = pages[page];
        return new DiscordCard(DiscordPages.Titled("Notes", page, pages.Count), string.Join("\n", shown.Select(row => row.Line)), [], CardTone.Reminder)
        {
            Menu = new DiscordMenu(OpenMenu, "Open a folder", shown.Select(row => row.Choice).ToList()),
            MoreRows = DiscordViews.Rows(
                [new CardAction("Search notes", NoteSearch, ButtonRole.Other), new CardAction("New note", NewNote + Unfiled, ButtonRole.Other)],
                DiscordPages.Pager(DiscordViews.Notes, page, pages.Count)),
        };
    }

    /// <summary>A folder's own folders, then its notes, newest first. Null when the folder is gone.</summary>
    private DiscordCard? FolderCard(long? folderId, int page)
    {
        NoteFolder? folder = null;
        if (folderId is { } id && (folder = _database.GetNoteFolder(id)) is null)
        {
            return null;
        }

        var rows = (folder?.Children ?? [])
            .Select(child => ($"› {DiscordText.Escape(child.Name)} ({child.NoteCount.ToString(CultureInfo.InvariantCulture)})", new MenuChoice(child.Name, FolderTarget + FolderKey(child.Id), "Folder")))
            .Concat(_database.GetNotesInFolder(folderId).Select(note =>
                ($"• {DiscordText.Escape(NoteTitle(note))} · {Day(DateOnly.FromDateTime(note.At))}", new MenuChoice(NoteTitle(note), NoteTarget + note.Id.ToString(CultureInfo.InvariantCulture), Day(DateOnly.FromDateTime(note.At))))))
            .Select(row => (Line: Shorten(row.Item1, 300), Choice: row.Item2))
            .ToList();
        var pages = DiscordPages.Split(rows, row => row.Line.Length + 1, NoteText);
        page = DiscordPages.Clamp(page, pages.Count);
        var shown = pages[page];
        var name = folder?.Name ?? "Unfiled";
        return new DiscordCard(Shorten(DiscordPages.Titled(DiscordText.Escape(name), page, pages.Count), 256), rows.Count == 0 ? "No notes here yet." : string.Join("\n", shown.Select(row => row.Line)), [], CardTone.Reminder)
        {
            Menu = new DiscordMenu(OpenMenu, "Open a note", shown.Select(row => row.Choice).ToList()),
            MoreRows = DiscordViews.Rows(
                [new CardAction("All folders", DiscordPages.Id(DiscordViews.Notes, 0), ButtonRole.Other), new CardAction("New note here", NewNote + FolderKey(folderId), ButtonRole.Other)],
                DiscordPages.Pager(FolderTarget + FolderKey(folderId), page, pages.Count)),
        };
    }

    private static string NoteTitle(NoteDetail note) => string.IsNullOrWhiteSpace(note.Title) ? "Untitled" : note.Title;

    /// <summary>A note a page at a time, laid out for Discord. Null when the note is gone.</summary>
    private DiscordCard? NoteCard(long noteId, int page)
    {
        if (_database.GetNote(noteId) is not { } note)
        {
            return null;
        }

        var text = DiscordNoteText.Format(note.Body);
        var pages = text.Length == 0 ? [note.About is { Length: > 0 } about ? DiscordText.Escape(about) : "This note is empty."] : DiscordPages.SplitText(text, NoteText);
        page = DiscordPages.Clamp(page, pages.Count);
        return new DiscordCard(Shorten(DiscordPages.Titled(DiscordText.Escape(NoteTitle(note)), page, pages.Count), 256), pages[page], [], CardTone.Reminder)
        {
            MoreRows = DiscordViews.Rows(
                [new CardAction("Back to folder", DiscordPages.Id(FolderTarget + FolderKey(note.FolderId), 0), ButtonRole.Other)],
                DiscordPages.Pager(NoteTarget + noteId.ToString(CultureInfo.InvariantCulture), page, pages.Count)),
        };
    }

    /// <summary>Notes matching the words, searched the way the Notes page's box does it.</summary>
    private DiscordCard NoteSearchCard(string words, int page)
    {
        var found = _database.SearchNotes(words)
            .Select(note => (Line: Shorten($"• {DiscordText.Escape(NoteTitle(note))}{(note.About is { Length: > 0 } about ? " · " + DiscordText.Escape(about) : "")}", 300), Choice: new MenuChoice(NoteTitle(note), NoteTarget + note.Id.ToString(CultureInfo.InvariantCulture), note.About)))
            .ToList();
        var pages = DiscordPages.Split(found, row => row.Line.Length + 1, NoteText);
        page = DiscordPages.Clamp(page, pages.Count);
        var shown = pages[page];
        return new DiscordCard(Shorten(DiscordPages.Titled($"Notes matching {DiscordText.Escape(words)}", page, pages.Count), 256), found.Count == 0 ? $"No notes match {DiscordText.Escape(words)}." : string.Join("\n", shown.Select(row => row.Line)), [], CardTone.Reminder)
        {
            Menu = new DiscordMenu(OpenMenu, "Open a note", shown.Select(row => row.Choice).ToList()),
            MoreRows = DiscordViews.Rows(
                [new CardAction("All folders", DiscordPages.Id(DiscordViews.Notes, 0), ButtonRole.Other), new CardAction("Search again", NoteSearch, ButtonRole.Other)],
                DiscordPages.Pager(NoteSearchTarget + Carry(words), page, pages.Count)),
        };
    }

    /// <summary>/notes: the folder index, a folder, or a search.</summary>
    private CommandReply NotesCommand(SlashCommand command)
    {
        if (command.Options.GetValueOrDefault("search")?.Trim() is { Length: > 0 } words)
        {
            return new CommandReply(NoteSearchCard(words, 0), true);
        }

        if (command.Options.GetValueOrDefault("folder")?.Trim() is { Length: > 0 } folder)
        {
            var id = FindFolder(folder);
            return id is { } found
                ? new CommandReply(FolderCard(found == 0 ? null : found, 0)!, true)
                : new CommandReply(DiscordText.Notice("That didn't work", "There's no folder by that name. Start typing and pick it from the suggestions.", CardTone.Attention), true);
        }

        return new CommandReply(NotesIndexCard(0), true);
    }

    /// <summary>A folder by ID or name, with 0 for Unfiled. Null when nothing matches.</summary>
    private long? FindFolder(string text)
    {
        if (text == Unfiled || string.Equals(text, "Unfiled", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var all = FlatFolders(_database.GetNoteFolders()).ToList();
        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && all.Any(folder => folder.Id == id))
        {
            return id;
        }

        return all.FirstOrDefault(folder => string.Equals(folder.Name, text, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private static IEnumerable<NoteFolder> FlatFolders(IEnumerable<NoteFolder> folders) =>
        folders.SelectMany(folder => FlatFolders(folder.Children).Prepend(folder));

    /// <summary>Folder names as /notes is typed.</summary>
    private IEnumerable<MenuChoice> FolderSuggestions(string typed) =>
        FlatFolders(_database.GetNoteFolders())
            .Where(folder => typed.Length == 0 || folder.Name.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .Select(folder => new MenuChoice(folder.Name, folder.Id.ToString(CultureInfo.InvariantCulture)))
            .Prepend(new MenuChoice("Unfiled", Unfiled))
            .Where(choice => typed.Length == 0 || choice.Label.Contains(typed, StringComparison.OrdinalIgnoreCase));

    private static CardForm NoteSearchForm() =>
        new(NoteSearch, "Search notes", [new FormBox("words", "Words to look for", Required: true, MaxLength: 100)]);

    private static CardForm NewNoteForm(string folder) =>
        new(NewNote + folder, "New note", [new FormBox("title", "Title", MaxLength: 200), new FormBox("body", "Note", Long: true, Required: true, MaxLength: 4000)]);

    /// <summary>A new note from the phone, saved the same way as one typed on the Notes page, then opened.</summary>
    private CommandReply SaveNewNote(string folder, IReadOnlyDictionary<string, string> values)
    {
        var body = values.GetValueOrDefault("body")?.Trim() ?? "";
        var title = values.GetValueOrDefault("title")?.Trim() ?? "";
        if (body.Length == 0 && title.Length == 0)
        {
            return new CommandReply(DiscordText.Notice("Nothing was saved", "The note was empty.", CardTone.Attention), true);
        }

        long? folderId = folder != Unfiled && long.TryParse(folder, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && _database.GetNoteFolder(id) is not null ? id : null;
        var noteId = _database.CreateNote(title, body, _clock(), folderId);
        _host.Changed();
        ViewsChanged();
        return new CommandReply(NoteCard(noteId, 0)!, true);
    }

    /// <summary>
    /// Something the owner sent in the notes channel. Each file makes a note the same way dropping it on the Notes
    /// page does. Recordings still can't be sent from Discord. The message is marked finished once each file has
    /// its note started or was answered, so one cut off by a crash or a close is handed over again for the rest,
    /// and a file whose note was already started isn't made into a second one.
    /// </summary>
    public void OnNotesMessage(DiscordMessage message)
    {
        if (Discord is not { } discord)
        {
            return;
        }

        if (message.Attachments.Count == 0)
        {
            _ = discord.ReplyInNotesAsync(message.Id, DiscordText.Notice("Send files here", "A PDF, slides, a document, or a picture sent here becomes a note. To find a note, use the menu on the pinned view or /notes. To add a note by typing, use New note."));
            _database.FinishDiscordMessage(message.Id, _clock());
            return;
        }

        foreach (var attachment in message.Attachments)
        {
            if (!_database.IsDiscordPartDone(message.Id, attachment.Id))
            {
                _ = MakeNotesFromDiscordAsync(discord, message, attachment);
            }
        }
    }

    private async Task MakeNotesFromDiscordAsync(DiscordBridge discord, DiscordMessage message, DiscordAttachment attachment)
    {
        // A file already being made into a note here isn't made into a second one.
        if (!StartDiscordFile(message, attachment))
        {
            return;
        }

        // Run aside from the message handler, so anything that goes wrong is answered here or nobody ever hears.
        try
        {
            await MakeNotesFromFileAsync(discord, message, attachment, () => FinishDiscordFile(message, attachment));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logError(exception);
            try
            {
                // Told to send it again, so it isn't tried again on its own. Until that gets through it's tried again.
                await TellThenDealtWith(discord.ReplyInNotesAsync(message.Id, DiscordText.Notice("File not read", $"Couldn't make notes from {DiscordText.Escape(attachment.FileName)}. Try sending it again.", CardTone.Attention), nonce: FileNonce(attachment)), () => FinishDiscordFile(message, attachment));
            }
            catch (Exception replyFailure) when (replyFailure is not OutOfMemoryException)
            {
                _logError(replyFailure);
            }
        }
        finally
        {
            _discordFilesWorking.TryRemove((message.Id, attachment.Id), out _);
        }
    }

    /// <summary><paramref name="dealtWith"/> is called once the note is started, or the owner has been told why it wasn't.</summary>
    private async Task MakeNotesFromFileAsync(DiscordBridge discord, DiscordMessage message, DiscordAttachment attachment, Action dealtWith)
    {
        if (Transcription.TranscriptionQueue.IsAudioFile(attachment.FileName))
        {
            await TellThenDealtWith(discord.ReplyInNotesAsync(message.Id, DiscordText.Notice("Recording not read", $"Recordings can't be sent from Discord yet, so {attachment.FileName} wasn't read. Add it on the Notes page on the PC."), nonce: FileNonce(attachment)), dealtWith);
            return;
        }

        if (NoteFiles is not { CanMakeNotes: true } notes)
        {
            await TellThenDealtWith(discord.ReplyInNotesAsync(message.Id, DiscordText.Notice("File not read", "Notes from files aren't available right now.", CardTone.Attention), nonce: FileNonce(attachment)), dealtWith);
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), "Waypoint", "discord");
        var name = string.Concat(Path.GetFileName(attachment.FileName).Select(letter => Path.GetInvalidFileNameChars().Contains(letter) ? '_' : letter));
        var path = Path.Combine(folder, $"{attachment.Id}-{name}");
        StartedNote started;
        try
        {
            Directory.CreateDirectory(folder);
            if (await discord.DownloadAsync(attachment, path, FileReader.MaxBytes) is { } problem)
            {
                await TellThenDealtWith(discord.ReplyInNotesAsync(message.Id, DiscordText.Notice("File not read", problem, CardTone.Attention), nonce: FileNonce(attachment)), dealtWith);
                return;
            }

            var file = await Task.Run(() => _readFile(path)) with { Name = Redaction.ApplyToName(attachment.FileName) };
            // Kept under the file's own ID, so a try whose answer was lost, such as from the server, isn't a second note.
            started = notes.StartFromFile(file, null, false, _clock(), DiscordClientId(message.Id, attachment.Id));

            // The note is saved and writes on from here, so the file isn't made into a note again.
            dealtWith();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await TellThenDealtWith(discord.ReplyInNotesAsync(message.Id, DiscordText.Notice("File not read", $"Couldn't read {attachment.FileName}. Try sending it again.", CardTone.Attention), nonce: FileNonce(attachment)), dealtWith);
            return;
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The system clears its temp folder in the end.
            }
        }

        if (started.Problem is { } unreadable)
        {
            await discord.ReplyInNotesAsync(message.Id, DiscordText.Notice("File not read", unreadable, CardTone.Attention));
            return;
        }

        _host.Changed();
        ViewsChanged();
        var place = started.FolderId is { } placed ? _database.GetNoteFolder(placed)?.Name ?? "Unfiled" : "Unfiled";
        await discord.ReplyInNotesAsync(message.Id, DiscordText.Notice($"Made {started.Title} in {place}", "The notes are being written now.", CardTone.Finished));

        var written = await NoteJobText.GuardNotes(() => notes.WriteNotesAsync(started.NoteId, null, false, _clock()));
        _host.Changed();
        ViewsChanged();
        var card = written.Status == NoteWriteStatus.Written
            ? NoteCard(started.NoteId, 0) ?? DiscordText.Notice(written.Message, "", CardTone.Finished)
            : DiscordText.Notice(started.Title, written.Message, CardTone.Attention);
        await discord.ReplyInNotesAsync(message.Id, card);
    }
}
