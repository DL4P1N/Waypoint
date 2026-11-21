using System.Globalization;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Files;
using Waypoint.Core.Notes;

namespace Waypoint.Core.Background;

/// <summary>
/// What the owner types in a post: read in order from where the post was last dealt with, added to its note or list
/// with any files, then marked as saved.
/// </summary>
public sealed partial class WaypointWorker
{
    /// <summary>
    /// The owner wrote in a post Waypoint knows. The post is read from where it was last dealt with, in order, so
    /// this message and any sent while Waypoint wasn't listening are each dealt with once.
    /// </summary>
    public Task OnThreadMessageAsync(string threadId, DiscordMessage message)
    {
        Track(threadId, async () =>
        {
            if (Discord is { } discord && _database.FindDiscordThread(threadId) is { } row)
            {
                await ReadPostAsync(discord, row);
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads a post from where it was last dealt with and deals with each of the owner's messages in order. How far it
    /// was read moves past each one only once it's saved, and past anyone else's messages, Waypoint's own included.
    /// It stops at the first one that can't be dealt with yet, so nothing after it is skipped.
    /// </summary>
    private async Task ReadPostAsync(DiscordBridge discord, DiscordThreadRow row)
    {
        var cursor = row.Cursor;
        var told = false;
        for (var page = 0; page < MaxPagesPerRead; page++)
        {
            if (await discord.ReadThreadPageAsync(row.Thread, cursor) is not { } read)
            {
                return;
            }

            foreach (var entry in read)
            {
                if (entry.FromOwner && entry.Message is { } message)
                {
                    // Only tries while its note or list is still there count, since one deleted on the PC keeps it unread on purpose.
                    var tryKey = $"{row.Thread}:{message.Id}";
                    if (row.State != WaypointDatabase.PostPending && StillThere(row)
                        && _threadMessageTries.AddOrUpdate(tryKey, 1, (_, tries) => tries + 1) > MaxThreadMessageTries)
                    {
                        // Tried enough, so it's left in the post with a word why, and the messages after it are read.
                        await NoticeAsync(discord, row.Thread, DiscordText.Notice("Not added", "A message here couldn't be added after several tries, so it was left as it is. Any files in it may already be on the note, so send the text again.", CardTone.Attention));
                    }
                    else
                    {
                        var (dealt, said) = await DealWithAsync(discord, row, message, told);
                        told |= said;
                        if (!dealt)
                        {
                            return;
                        }
                    }

                    _threadMessageTries.TryRemove(tryKey, out _);
                }

                _database.AdvanceDiscordThread(row.Thread, entry.Id);
                cursor = entry.Id;
            }

            if (read.Count < DiscordBridge.ThreadPage)
            {
                return;
            }
        }
    }

    /// <summary>How many times one message in a post is tried, while the app runs, before it's left as it is.</summary>
    private const int MaxThreadMessageTries = 5;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _threadMessageTries = new();

    /// <summary>
    /// One of the owner's messages in a post. Dealt is true once it's saved, or once there's nothing more to do with
    /// it, and Said is true when a reply was sent that shouldn't be sent again in the same read.
    /// </summary>
    private async Task<(bool Dealt, bool Said)> DealWithAsync(DiscordBridge discord, DiscordThreadRow row, DiscordMessage message, bool told)
    {
        if (row.State == WaypointDatabase.PostPending)
        {
            return (false, false);
        }

        if (row.State == WaypointDatabase.PostGone)
        {
            return (true, false);
        }

        var what = row.Kind == WaypointDatabase.ThreadNote ? "This note" : "This list";
        if (row.State == WaypointDatabase.PostLive)
        {
            var added = row.Kind == WaypointDatabase.ThreadNote
                ? await AddToNoteFromThreadAsync(discord, row, message)
                : await AddToListFromThreadAsync(discord, row, message);
            return (added, false);
        }

        // A retired post. What's typed there still goes to its note or list while that's still on the PC, such as
        // after the post moved, or after the note came back from History, and the reply says where its post is now.
        if (!StillThere(row))
        {
            if (!told)
            {
                var why = row.Key.StartsWith(PendingKey, StringComparison.Ordinal)
                    ? "No list was made from this post, so this wasn't added. Start a new post with a list name."
                    : $"{what} was deleted on the PC, so this wasn't added. It stays here. Bring it back from History on the PC, or start a new post.";
                await discord.SendInThreadAsync(row.Thread, DiscordText.Notice("Not added", why, CardTone.Attention));
            }

            return (true, true);
        }

        var dealt = row.Kind == WaypointDatabase.ThreadNote
            ? await AddToNoteFromThreadAsync(discord, row, message)
            : await AddToListFromThreadAsync(discord, row, message);
        if (!dealt || told)
        {
            return (dealt, false);
        }

        var now = _database.GetDiscordThread(row.Kind, row.Key)?.Thread ?? row.MovedTo;
        var where = now is null || now == row.Thread
            ? $"{what} gets a new post soon. Anything typed here still goes to it."
            : $"Its new post is <#{now}>. Anything typed here still goes to it.";
        await discord.SendInThreadAsync(row.Thread, DiscordText.Notice("Saved, and this post moved", where, CardTone.Reminder));
        return (true, true);
    }

    /// <summary>The key a post the owner started has until it's made into a note or list.</summary>
    private const string PendingKey = "pending:";

    /// <summary>Whether a post's note or list is still on the PC.</summary>
    private bool StillThere(DiscordThreadRow row) =>
        row.Kind == WaypointDatabase.ThreadNote
            ? long.TryParse(row.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var noteId) && _database.GetNote(noteId) is not null
            : ListNamed(row.Key) is not null;

    /// <summary>
    /// A message in a note's post. Its files are downloaded and read first, with nothing changed yet, then join the
    /// note, and then its text goes at the end of the note, saved along with how far the post was read, so the text
    /// is never added twice. False when the note is gone or kept changing, so it's tried again later.
    /// </summary>
    private async Task<bool> AddToNoteFromThreadAsync(DiscordBridge discord, DiscordThreadRow row, DiscordMessage message)
    {
        // A note deleted on the PC leaves the message unread, so its post is archived with it rather than deleted.
        if (!long.TryParse(row.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var noteId) || _database.GetNote(noteId) is null)
        {
            return false;
        }

        var allFiles = true;
        if (message.Attachments.Count > 0)
        {
            var files = await ReadThreadFilesAsync(discord, message);
            allFiles = await AttachThreadFilesAsync(discord, row.Thread, noteId, files);
        }

        if (message.Text.Trim().Length > 0)
        {
            if (!AppendToNote(noteId, message.Text, (row.Thread, message.Id)))
            {
                return false;
            }

            _host.Changed();
            ViewsChanged();
        }

        if (allFiles)
        {
            await FoldedInAsync(discord, row.Thread, message.Id);
        }

        // Otherwise what it said is saved, and the files that couldn't be kept were answered, so the message stays for sending again.
        return true;
    }

    /// <summary>A message's files, downloaded and read, and what went wrong with any that couldn't be.</summary>
    private sealed record ThreadFiles(IReadOnlyList<(string AttachmentId, FileText File)> Files, IReadOnlyList<string> Problems);

    /// <summary>A file already added from an earlier read of the same message is skipped, so it's never added twice.</summary>
    private async Task<ThreadFiles> ReadThreadFilesAsync(DiscordBridge discord, DiscordMessage message)
    {
        var files = new List<(string, FileText)>();
        var problems = new List<string>();
        foreach (var attachment in message.Attachments.Where(attachment => !_database.WasDiscordFileAttached(attachment.Id)))
        {
            if (await ReadThreadFileAsync(discord, attachment) is not { } file)
            {
                problems.Add($"Couldn't read {attachment.FileName}. Try sending it again.");
            }
            else if (file.Problem is { } unreadable)
            {
                problems.Add(unreadable);
            }
            else
            {
                files.Add((attachment.Id, file.File!));
            }
        }

        return new ThreadFiles(files, problems);
    }

    /// <summary>Each file joins the note's Sources, the same as dropping it on that note on the PC. False, after saying why, when any couldn't.</summary>
    private async Task<bool> AttachThreadFilesAsync(DiscordBridge discord, string thread, long noteId, ThreadFiles read)
    {
        var problems = read.Problems.ToList();
        foreach (var (attachmentId, file) in read.Files)
        {
            var attached = NoteFiles is { CanMakeNotes: true } notes
                ? notes.AttachFile(noteId, file, _clock())
                : new StartedNote(noteId, "", null, null, "Files can't be added to notes right now.");
            if (attached.Problem is { } problem)
            {
                problems.Add(problem);
            }
            else
            {
                _database.MarkDiscordFileAttached(attachmentId, noteId, _clock());
            }
        }

        _host.Changed();
        ViewsChanged();
        if (problems.Count == 0)
        {
            return true;
        }

        await NoticeAsync(discord, thread, DiscordText.Notice("Not every file was added", string.Join("\n", problems), CardTone.Attention));
        return false;
    }

    private sealed record ThreadFile(FileText? File, string? Problem);

    /// <summary>Downloads and reads a file the way files sent in the notes channel are. Null when it couldn't be downloaded.</summary>
    private async Task<ThreadFile?> ReadThreadFileAsync(DiscordBridge discord, DiscordAttachment attachment)
    {
        if (Transcription.TranscriptionQueue.IsAudioFile(attachment.FileName))
        {
            return new ThreadFile(null, $"Recordings can't be sent from Discord yet, so {attachment.FileName} wasn't read. Add it on the Notes page on the PC.");
        }

        var folder = Path.Combine(Path.GetTempPath(), "Waypoint", "discord");
        var name = string.Concat(Path.GetFileName(attachment.FileName).Select(letter => Path.GetInvalidFileNameChars().Contains(letter) ? '_' : letter));
        var path = Path.Combine(folder, $"{attachment.Id}-{name}");
        try
        {
            Directory.CreateDirectory(folder);
            if (await discord.DownloadAsync(attachment, path, FileReader.MaxBytes) is { } problem)
            {
                return new ThreadFile(null, problem);
            }

            var file = await Task.Run(() => _readFile(path)) with { Name = Redaction.ApplyToName(attachment.FileName) };
            return new ThreadFile(file, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
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
    }

    private async Task<bool> AddToListFromThreadAsync(DiscordBridge discord, DiscordThreadRow row, DiscordMessage message)
    {
        // A list deleted on the PC leaves the message unread, so its post is archived with it rather than deleted.
        if (ListNamed(row.Key) is not { } name)
        {
            return false;
        }

        var entries = ListLines(message.Text);
        if (entries.Count > 0)
        {
            _database.AddListEntries(name, entries, _clock());
            _host.Changed();
            ViewsChanged();
        }

        if (message.Attachments.Count > 0)
        {
            await NoticeAsync(discord, row.Thread, DiscordText.Notice("Files stay out of lists", "A file can't go on a list, so it was left here. Send it in a note's post to add it to that note.", CardTone.Attention));
            return true;
        }

        if (entries.Count > 0)
        {
            await FoldedInAsync(discord, row.Thread, message.Id);
        }

        return true;
    }

    /// <summary>A list's name as Waypoint keeps it, found without regard to case, or null when there's no such list.</summary>
    private string? ListNamed(string name) =>
        _database.GetListOverview(includeEmptyShopping: true).FirstOrDefault(list => string.Equals(list.Name, name, StringComparison.OrdinalIgnoreCase))?.Name;

    /// <summary>
    /// Each line, or each part between commas, is one thing to add. A comma inside a number, such as in "1,000 piece"
    /// or "$1,000", isn't a break.
    /// </summary>
    public static IReadOnlyList<string> ListLines(string text) =>
        System.Text.RegularExpressions.Regex.Split(text, @"[\r\n]+|(?<!\d),|,(?!\d)")
            .Select(line => line.Trim().TrimStart('-', '*', '•').Trim())
            .Where(line => line.Length > 0)
            .ToList();

    /// <summary>
    /// What a message said is saved, so it's taken away to keep the post tidy. Without Manage Messages it gets a ✅
    /// instead, and the post says once what permission would clear them.
    /// </summary>
    private async Task FoldedInAsync(DiscordBridge discord, string thread, string messageId)
    {
        if (await discord.RemoveMessageAsync(thread, messageId) != ThreadChange.NotAllowed)
        {
            return;
        }

        await discord.ReactAsync(thread, messageId, "✅");
        if (_database.GetPreference(ManageMessagesSaidKey) != "1")
        {
            _database.SetPreference(ManageMessagesSaidKey, "1");
            await NoticeAsync(discord, thread, DiscordText.Notice(
                "Saved",
                "Messages you type here are saved, then marked with a ✅. To have them cleared away once they're saved, give the bot the Manage Messages permission.",
                CardTone.Finished));
        }
    }
}
