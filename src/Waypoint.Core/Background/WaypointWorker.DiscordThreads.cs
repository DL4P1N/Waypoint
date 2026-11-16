using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Files;
using Waypoint.Core.Notes;

namespace Waypoint.Core.Background;

/// <summary>
/// Notes and lists as forum posts on the phone. Each note is one post in the notes forum, named after it and tagged
/// with its folder, with its text in one or more of Waypoint's messages, each with an Edit button. Each list is one
/// post in the lists forum, with a menu to check things off. Typing in a post adds to that note or list, and a new
/// post makes a new one.
/// Discord can never delete a note or a list entry, or change one without saying so: everything typed there goes
/// through the same saves the PC uses, a save never writes over a change made meanwhile, and a deleted post is only
/// made again. Messages are only taken away once what they said is saved, and how far each post has been read is kept
/// with the post and only moves once a message is dealt with, so a restart or a failure never skips one.
/// Each post is read or started one piece of work at a time, and a round of updates never overlaps the few quick
/// changes that make a post's note or list. Anything slow, such as downloading a file or writing notes from it, and
/// an Edit being saved, waits for no round, since every save checks the note didn't change meanwhile.
/// </summary>
public sealed partial class WaypointWorker
{
    /// <summary>A part's Edit button: the note, which part, and a hash of that part's text when it was shown.</summary>
    public const string NoteEdit = "nedit:";

    /// <summary>Start over on a list's post, for running it again as a routine.</summary>
    public const string ListStart = "liststart:";

    /// <summary>The line an edit is added under when the part changed while the form was open.</summary>
    public const string FromDiscord = "From Discord";

    /// <summary>
    /// How much of a note's Markdown goes in one message, well under the 4096 a card holds and the 4000 a form box
    /// takes, so an Edit has room to add to it.
    /// </summary>
    public const int PartMarkdown = 3000;

    private const int MaxEditText = 4000;
    private const int MaxDescription = 4000;
    private const int MaxTags = 20;
    private const int MaxTagName = 20;
    private const int MaxPagesPerRead = 10;
    private const int SaveTries = 5;

    /// <summary>New posts made in one round at most, so a first sync of many notes is spread out and stays inside rate limits.</summary>
    public const int MaxNewPostsPerRound = 10;

    private const string ManageMessagesSaidKey = "discord_thread_manage_messages_said";
    private static readonly TimeSpan CatchUpEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TagsRetryAfter = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TagsCheckEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LongestBackoff = TimeSpan.FromHours(6);

    /// <summary>How long a post's title has to stay the same before the post is renamed, since each rename leaves a line in it.</summary>
    private static readonly TimeSpan RenameSettle = TimeSpan.FromMinutes(2);

    /// <summary>How long one of Waypoint's notices stays in a post, and how long it's tried before it's let go.</summary>
    private static readonly TimeSpan NoticeKeep = TimeSpan.FromHours(1);
    private static readonly TimeSpan NoticeGiveUp = TimeSpan.FromDays(1);

    /// <summary>A notice younger than this stays even when a new part goes under it, so there's time to read it.</summary>
    private static readonly TimeSpan NoticeFresh = TimeSpan.FromMinutes(1);

    /// <summary>The line under a note's last part that says what typing in its post does.</summary>
    public const string TypeToAdd = "Type here to add to the end.";

    private const int MaxOpenForms = 100;

    // A round of updates, and the quick changes that make a post's note or list, so a round never sees one half made.
    private readonly SemaphoreSlim _threadsRunning = new(1, 1);

    // One piece of work at a time for each post, so a message is never dealt with twice at once.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _postsRunning = new(StringComparer.Ordinal);

    // A title each post is waiting to take, and since when, so a title still being typed isn't renamed every round.
    private readonly ConcurrentDictionary<string, (string Name, DateTime Since)> _titlesWaiting = new(StringComparer.Ordinal);

    // The text each open Edit form was filled with, so a clash adds only what was changed. Kept only while the app runs.
    private readonly ConcurrentDictionary<string, string> _formsOpened = new(StringComparer.Ordinal);
    private readonly List<Task> _threadWork = [];
    private readonly Dictionary<string, (DateTime Until, TimeSpan Wait)> _postBackoff = [];
    private int _newPostsThisRound;
    private DateTime? _caughtUpAt;
    private string? _guildId;
    private (string Hash, Dictionary<long, string> Tags, DateTime At, bool Worked)? _forumTags;

    /// <summary>Something about the forums the user should know, such as folders that couldn't get a tag. Shown in Settings.</summary>
    public string? ForumNotice { get; private set; }

    private INoteFileStore NoteStore => (INoteFileStore?)NoteFiles ?? _database;

    /// <summary>
    /// Brings the forum posts up to date: makes posts for new notes and lists, edits only the messages whose text
    /// changed, renames and retags posts, makes deleted posts again, and takes away posts for notes and lists
    /// deleted on the PC. Every few minutes it also reads what was typed in the posts while Waypoint was away. Each
    /// forum, and the reading, go on separately, so a problem with one never stops the others.
    /// </summary>
    public async Task SyncDiscordThreadsAsync()
    {
        if (Discord is not { } discord || DiscordSettings is not { } settings || (settings.NotesForumChannelId is null && settings.ListsForumChannelId is null))
        {
            return;
        }

        var catchUp = false;
        await _threadsRunning.WaitAsync();
        try
        {
            _newPostsThisRound = 0;
            if (settings.ListsForumChannelId is { } listsForum)
            {
                await Separately(() => SyncListPostsAsync(discord, listsForum));
            }

            if (settings.NotesForumChannelId is { } notesForum)
            {
                await Separately(() => SyncNotePostsAsync(discord, notesForum));
            }

            if (_caughtUpAt is not { } caught || _clock() - caught >= CatchUpEvery)
            {
                _caughtUpAt = _clock();
                catchUp = true;
            }
        }
        finally
        {
            _threadsRunning.Release();
        }

        // Reading the posts goes one post at a time, aside from the round, since files read there can take a while.
        if (catchUp)
        {
            await Separately(() => CatchUpThreadsAsync(discord, settings));
        }
    }

    /// <summary>Work for one post, waiting for any other work for that same post to finish first.</summary>
    private async Task OnePostAsync(string thread, Func<Task> work)
    {
        var gate = _postsRunning.GetOrAdd(thread, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await work();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A quick change that a round shouldn't see half done, such as making a note and marking its post as showing it.</summary>
    private async Task<T> BetweenRoundsAsync<T>(Func<Task<T>> work)
    {
        await _threadsRunning.WaitAsync();
        try
        {
            return await work();
        }
        finally
        {
            _threadsRunning.Release();
        }
    }

    private async Task Separately(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logError(exception);
        }
    }

    /// <summary>Runs work for a post aside from the live connection, so a slow one never holds up a button press, and keeps track of it.</summary>
    private void Track(string thread, Func<Task> work)
    {
        var task = Task.Run(() => OnePostAsync(thread, () => Separately(work)));
        lock (_threadWork)
        {
            _threadWork.RemoveAll(done => done.IsCompleted);
            _threadWork.Add(task);
        }
    }

    /// <summary>Waits until every post being read or started has been dealt with. For tests and for closing down.</summary>
    public async Task ThreadsSettledAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_threadWork)
            {
                pending = _threadWork.Where(task => !task.IsCompleted).ToArray();
            }

            if (pending.Length == 0)
            {
                return;
            }

            await Task.WhenAll(pending);
        }
    }

    /// <summary>
    /// One post as it should look: its key, name, tags, and the cards for its messages in order. Tags is null when
    /// the forum's tags couldn't be read, so the post's name and tags are left as they are this round.
    /// </summary>
    private sealed record WantedPost(string Kind, string Key, string Name, IReadOnlyList<string>? Tags, IReadOnlyList<DiscordCard> Parts);

    private async Task SyncNotePostsAsync(DiscordBridge discord, string forum)
    {
        var tags = await ForumTagsAsync(discord, forum);

        // Oldest first, so the newest note is made last and sits at the top of the forum.
        var notes = _database.GetNotes().Reverse().ToList();
        var rows = Live(WaypointDatabase.ThreadNote);
        foreach (var note in notes)
        {
            var key = note.Id.ToString(CultureInfo.InvariantCulture);
            IReadOnlyList<string>? tag = tags is null ? null : note.FolderId is { } folder && tags.TryGetValue(folder, out var found) ? [found] : [];
            var wanted = new WantedPost(WaypointDatabase.ThreadNote, key, DiscordBridge.ThreadName(NoteTitle(note)), tag, NoteParts(note));
            await SyncPostAsync(discord, forum, wanted, rows.GetValueOrDefault(key));
        }

        var live = notes.Select(note => note.Id.ToString(CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows.Values.Where(row => !live.Contains(row.Key)))
        {
            await RetirePostAsync(discord, row, "This note was deleted on the PC");
        }
    }

    private async Task SyncListPostsAsync(DiscordBridge discord, string forum)
    {
        // Shopping comes first in the overview, so its post is made first.
        var lists = _database.GetListOverview(includeEmptyShopping: true);
        var rows = Live(WaypointDatabase.ThreadList);
        foreach (var list in lists)
        {
            var wanted = new WantedPost(WaypointDatabase.ThreadList, list.Name, DiscordBridge.ThreadName(list.Name), [], [ListPostCard(list)]);
            await SyncPostAsync(discord, forum, wanted, rows.GetValueOrDefault(list.Name));
        }

        var live = lists.Select(list => list.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Values.Where(row => !live.Contains(row.Key)))
        {
            await RetirePostAsync(discord, row, "This list was deleted on the PC");
        }
    }

    /// <summary>The live posts of a kind by key. Lists match without regard to case, and if two ever share a key, the newer wins.</summary>
    private Dictionary<string, DiscordThreadRow> Live(string kind)
    {
        var comparer = kind == WaypointDatabase.ThreadList ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var rows = new Dictionary<string, DiscordThreadRow>(comparer);
        foreach (var row in _database.GetDiscordThreads(kind))
        {
            rows[row.Key] = row;
        }

        return rows;
    }

    /// <summary>
    /// Makes one post match what it should show, with as few calls as it takes. A message is only edited when its
    /// card changed. A post that's gone is made again, and the note or list it shows is never touched.
    /// </summary>
    private async Task SyncPostAsync(DiscordBridge discord, string forum, WantedPost wanted, DiscordThreadRow? row)
    {
        if (row is null || row.Forum != forum)
        {
            await MakePostAsync(discord, forum, wanted);
            return;
        }

        await ClearNoticesAsync(discord, row.Thread, all: false);
        if (wanted.Tags is { } tags && row.MetaHash != Meta(wanted.Name, tags))
        {
            // Tags change at once. A new name waits until it has stayed the same for a while, since each rename
            // leaves a "changed the post title" line in the post.
            var (nameWas, tagsWere) = SplitMeta(row.MetaHash);
            var rename = nameWas != Hash(wanted.Name) && TitleSettled(row.Thread, wanted.Name);
            var retag = tagsWere != Hash(TagList(tags));
            if (rename || retag)
            {
                var changed = await discord.EditThreadAsync(row.Thread, rename ? wanted.Name : null, retag ? tags : null);
                if (changed == ThreadChange.Missing)
                {
                    _database.MarkDiscordThreadGone(row.Thread);
                    await MakePostAsync(discord, forum, wanted);
                    return;
                }

                if (changed is ThreadChange.Done or ThreadChange.NotAllowed)
                {
                    _database.SetDiscordThreadMeta(row.Thread, (rename ? Hash(wanted.Name) : nameWas) + ":" + Hash(TagList(tags)));
                }
            }
        }

        var parts = row.Parts.ToList();
        for (var index = 0; index < wanted.Parts.Count; index++)
        {
            var card = wanted.Parts[index];
            var hash = Hash(card);
            if (index < parts.Count)
            {
                if (parts[index].Hash == hash)
                {
                    continue;
                }

                var edited = await discord.EditInThreadAsync(row.Thread, parts[index].Message, card);
                if (edited is EditOutcome.Edited or EditOutcome.Refused)
                {
                    parts[index] = parts[index] with { Hash = hash };
                    _database.SetDiscordThreadParts(row.Thread, parts);
                    continue;
                }

                if (edited == EditOutcome.Failed)
                {
                    return;
                }

                // A message of Waypoint's is gone. Waypoint's own first message going means the post can't show the
                // note properly any more, so a new post is made and this one points to it. Any other is sent again,
                // with the ones after it, to keep their order.
                if (index == 0 && !row.UserStarter)
                {
                    await MakePostAsync(discord, forum, wanted);
                    return;
                }

                foreach (var later in parts.Skip(index + 1))
                {
                    await discord.DeleteAsync(row.Thread, later.Message);
                }

                parts.RemoveRange(index, parts.Count - index);
                _database.SetDiscordThreadParts(row.Thread, parts);
            }

            // A new part goes at the end, so notices are cleared first to keep the note's parts together.
            await ClearNoticesAsync(discord, row.Thread, all: true);
            var (sent, outcome) = await discord.SendInThreadAsync(row.Thread, card);
            if (outcome == ThreadChange.Missing)
            {
                _database.MarkDiscordThreadGone(row.Thread);
                await MakePostAsync(discord, forum, wanted);
                return;
            }

            if (sent is null)
            {
                return;
            }

            parts.Add(new DiscordThreadPart(sent, hash));
            _database.SetDiscordThreadParts(row.Thread, parts);
        }

        foreach (var extra in parts.Skip(wanted.Parts.Count).ToList())
        {
            if (!await discord.DeleteAsync(row.Thread, extra.Message))
            {
                return;
            }

            parts.Remove(extra);
            _database.SetDiscordThreadParts(row.Thread, parts);
        }
    }

    /// <summary>What a post was last named and tagged, as a hash of each, so a change to one is told from the other.</summary>
    private static string Meta(string name, IReadOnlyList<string> tags) => Hash(name) + ":" + Hash(TagList(tags));

    private static string TagList(IReadOnlyList<string> tags) => string.Join(",", tags);

    private static (string? Name, string? Tags) SplitMeta(string? meta) =>
        meta?.Split(':') is [var name, var tags] ? (name, tags) : (null, null);

    /// <summary>Whether a post's new title has stayed the same long enough to rename the post to it.</summary>
    private bool TitleSettled(string thread, string name)
    {
        if (_titlesWaiting.TryGetValue(thread, out var waiting) && waiting.Name == name)
        {
            if (_clock() - waiting.Since < RenameSettle)
            {
                return false;
            }

            _titlesWaiting.TryRemove(thread, out _);
            return true;
        }

        _titlesWaiting[thread] = (name, _clock());
        return false;
    }

    /// <summary>
    /// A notice in a post, such as a file that couldn't be added. It's cleared away after an hour, or sooner when a new
    /// part of the note has to go under it, so the post stays the note and not a trail of replies.
    /// </summary>
    private async Task NoticeAsync(DiscordBridge discord, string thread, DiscordCard card)
    {
        var (sent, _) = await discord.SendInThreadAsync(thread, card);
        if (sent is not null)
        {
            _database.AddDiscordNotice(thread, sent, _clock());
        }
    }

    private async Task ClearNoticesAsync(DiscordBridge discord, string thread, bool all)
    {
        foreach (var (message, sentAt) in _database.GetDiscordNotices(thread))
        {
            var age = _clock() - sentAt;
            if (age < (all ? NoticeFresh : NoticeKeep))
            {
                continue;
            }

            if (await discord.DeleteAsync(thread, message) || age >= NoticeGiveUp)
            {
                _database.RemoveDiscordNotice(message);
            }
        }
    }

    /// <summary>
    /// A new post of Waypoint's, its first part as the post's first message and the rest after it. Only posts that
    /// were made count toward the round's limit, and one Discord turns down waits longer each time, so it never holds
    /// up the others. A post it replaces points to this one and is archived.
    /// </summary>
    private async Task MakePostAsync(DiscordBridge discord, string forum, WantedPost wanted)
    {
        var key = wanted.Kind + ":" + wanted.Key;
        if (wanted.Tags is not { } tags
            || _newPostsThisRound >= MaxNewPostsPerRound
            || (_postBackoff.TryGetValue(key, out var waiting) && _clock() < waiting.Until))
        {
            return;
        }

        var made = await discord.CreateForumPostAsync(forum, wanted.Name, tags, wanted.Parts[0]);
        if (made.Post is not { } post)
        {
            var wait = _postBackoff.TryGetValue(key, out var before) ? TimeSpan.FromTicks(Math.Min(before.Wait.Ticks * 2, LongestBackoff.Ticks)) : FirstBackoff;
            _postBackoff[key] = (_clock() + wait, wait);
            if (made.TurnedDown)
            {
                // A tag may have been deleted by hand, so the forum's tags are read again next time.
                _forumTags = null;
            }

            return;
        }

        _newPostsThisRound++;
        _postBackoff.Remove(key);
        var row = new DiscordThreadRow(post.Thread, wanted.Kind, wanted.Key, forum, WaypointDatabase.PostLive, false, Meta(wanted.Name, tags), [new DiscordThreadPart(post.Message, Hash(wanted.Parts[0]))], post.Message);
        foreach (var old in _database.AddDiscordThread(row))
        {
            await PointToNewPostAsync(discord, old, post.Thread, wanted.Kind);
        }

        await SyncPostAsync(discord, forum, wanted, row);
    }

    /// <summary>An older post that a new one took over from says where things are now, and is archived.</summary>
    private async Task PointToNewPostAsync(DiscordBridge discord, DiscordThreadRow old, string moved, string kind)
    {
        var what = kind == WaypointDatabase.ThreadNote ? "This note" : "This list";
        var (_, outcome) = await discord.SendInThreadAsync(old.Thread, DiscordText.Notice("Moved", $"{what} has a new post now: <#{moved}>. Anything typed here still goes to it.", CardTone.Reminder));
        if (outcome == ThreadChange.Missing)
        {
            _database.MarkDiscordThreadGone(old.Thread);
            return;
        }

        await discord.EditThreadAsync(old.Thread, archived: true);
    }

    /// <summary>
    /// A note or list deleted in Waypoint: its post is deleted, since the note can still be brought back from History
    /// on the PC and its post then comes back. If anything the owner typed there hasn't been read yet, the post is
    /// archived instead, so those words aren't lost, and says why. Its record is kept either way, retired, so it's
    /// never taken for a new post, even when Discord won't let it be deleted or archived.
    /// </summary>
    private async Task RetirePostAsync(DiscordBridge discord, DiscordThreadRow row, string why)
    {
        // The round holds the lock a post's own work can wait for, so it never waits for that post: one being read or
        // started right now is retired on a later round instead.
        var gate = _postsRunning.GetOrAdd(row.Thread, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0))
        {
            return;
        }

        try
        {
            if (_database.FindDiscordThread(row.Thread) is { Live: true } now)
            {
                await RetirePostNowAsync(discord, now, why);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RetirePostNowAsync(DiscordBridge discord, DiscordThreadRow row, string why)
    {
        if (await ReadAllAfterAsync(discord, row.Thread, row.Cursor) is not { } after)
        {
            return;
        }

        var unread = after.Any(entry => entry.FromOwner && entry.Message is not null);
        if (!unread)
        {
            var deleted = await discord.DeleteThreadAsync(row.Thread);
            if (deleted is ThreadChange.Done or ThreadChange.Missing)
            {
                _database.SetDiscordThreadState(row.Thread, WaypointDatabase.PostGone);
                return;
            }

            if (deleted == ThreadChange.Failed)
            {
                return;
            }
        }

        string? last = after.LastOrDefault()?.Id;
        if (unread)
        {
            var (said, _) = await discord.SendInThreadAsync(row.Thread, DiscordText.Notice("Not added", $"{why}, so what was typed here wasn't added to it. It stays in this post.", CardTone.Attention));
            last = said ?? last;
        }

        var archived = await discord.EditThreadAsync(row.Thread, archived: true);
        if (archived == ThreadChange.Failed)
        {
            return;
        }

        if (last is not null)
        {
            _database.AdvanceDiscordThread(row.Thread, last);
        }

        _database.SetDiscordThreadState(row.Thread, archived == ThreadChange.Missing ? WaypointDatabase.PostGone : WaypointDatabase.PostRetired);
    }

    /// <summary>Every message in a post after a point, a page at a time, or null when it can't be read.</summary>
    private static async Task<List<ThreadEntry>?> ReadAllAfterAsync(DiscordBridge discord, string thread, string? after)
    {
        var all = new List<ThreadEntry>();
        for (var page = 0; page < MaxPagesPerRead; page++)
        {
            if (await discord.ReadThreadPageAsync(thread, after) is not { } read)
            {
                return null;
            }

            all.AddRange(read);
            if (read.Count < DiscordBridge.ThreadPage)
            {
                break;
            }

            after = read[^1].Id;
        }

        return all;
    }

    /// <summary>
    /// A note's messages. Each holds one stretch of the note's Markdown, laid out for Discord, with an Edit button that
    /// opens that stretch in a form. Sources and transcripts stay on the PC and are only counted at the end.
    /// </summary>
    private IReadOnlyList<DiscordCard> NoteParts(NoteDetail note)
    {
        var chunks = SplitMarkdown(note.Body, PartMarkdown);
        var sources = _database.GetNoteSources(note.Id).Count;
        var title = Shorten(DiscordText.Escape(NoteTitle(note)), 200);
        var cards = new List<DiscordCard>();
        for (var index = 0; index < chunks.Count; index++)
        {
            var text = DiscordNoteText.Format(chunks[index]);
            if (text.Length == 0)
            {
                text = chunks.Count == 1 ? "This note is empty. Type here to add to it." : "​";
            }

            text = Shorten(text, MaxDescription - 100);
            if (index == chunks.Count - 1)
            {
                if (sources > 0)
                {
                    text += "\n\n*" + SourcesLine(sources) + "*";
                }

                if (!text.Contains("Type here", StringComparison.Ordinal))
                {
                    text += "\n\n" + TypeToAdd;
                }
            }

            var id = $"{NoteEdit}{note.Id.ToString(CultureInfo.InvariantCulture)}:{index.ToString(CultureInfo.InvariantCulture)}:{Hash(chunks[index])}";
            cards.Add(new DiscordCard(DiscordPages.Titled(title, index, chunks.Count), text, [], CardTone.Reminder)
            {
                Actions = [new CardAction("Edit", id, ButtonRole.Other)],
            });
        }

        return cards;
    }

    public static string SourcesLine(int count) =>
        count == 1 ? "Sources: 1 file, kept on the PC" : $"Sources: {count.ToString(CultureInfo.InvariantCulture)} files, kept on the PC";

    /// <summary>A list's post: what's left and what's done, a menu to check one off, and Start over once something's done.</summary>
    private DiscordCard ListPostCard(ListOverview list, int page = 0)
    {
        var card = DiscordViews.ListCard(list, page);
        var actions = !string.Equals(list.Name, WaypointDatabase.ShoppingList, StringComparison.OrdinalIgnoreCase) && list.Entries.Any(entry => entry.Done)
            ? new List<CardAction> { new("Start over", ListStart + DiscordViews.ListTarget(list.Name), ButtonRole.Other) }
            : [];
        return card with
        {
            Description = card.Description + "\n\nType here to add to this list, one thing per line or with commas between.",
            Actions = actions,
        };
    }

    /// <summary>
    /// Splits Markdown into stretches that put back together exactly as it was, cut at line ends, and inside a line
    /// only when one line alone is too long. There's always at least one, even for an empty note.
    /// </summary>
    public static IReadOnlyList<string> SplitMarkdown(string body, int limit)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        var start = 0;
        while (start < body.Length)
        {
            var end = body.IndexOf('\n', start);
            var line = end < 0 ? body[start..] : body[start..(end + 1)];
            start += line.Length;
            while (line.Length > limit)
            {
                if (current.Length > 0)
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                }

                var cut = line.LastIndexOf(' ', limit - 1);
                cut = cut <= limit / 2 ? limit : cut + 1;
                chunks.Add(line[..cut]);
                line = line[cut..];
            }

            if (current.Length + line.Length > limit && current.Length > 0)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }

            current.Append(line);
        }

        if (current.Length > 0 || chunks.Count == 0)
        {
            chunks.Add(current.ToString());
        }

        return chunks;
    }

    /// <summary>
    /// Adds text at the end of a note as a new paragraph, with the note's own line endings. Nothing already there
    /// changes but blank lines at the very end.
    /// </summary>
    public static string AppendParagraph(string body, string text)
    {
        var newline = body.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var added = text.Replace("\r\n", "\n").Trim('\n').Replace("\n", newline);
        var kept = body.TrimEnd('\r', '\n');
        return kept.Trim().Length == 0 ? added : kept + newline + newline + added;
    }

    /// <summary>
    /// Adds a paragraph at the end of a note without ever writing over a change saved meanwhile: if the note changed
    /// between reading and saving, it's read again and tried again. False when the note is gone or kept changing. With
    /// <paramref name="readTo"/>, the post it came from is marked read to that message in the same save.
    /// </summary>
    private bool AppendToNote(long noteId, string text, (string Thread, string Message)? readTo = null)
    {
        for (var attempt = 0; attempt < SaveTries; attempt++)
        {
            if (_database.GetNote(noteId) is not { } note)
            {
                return false;
            }

            if (_database.SaveNoteIfUnchanged(noteId, note.Title, AppendParagraph(note.Body, text), note.Body, _clock(), readTo))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Folder IDs and the forum tag each one has, made to match the folders when they've changed. Null when the
    /// forum can't be read and nothing is known yet, so nothing about tags is changed until it can be.
    /// </summary>
    private async Task<Dictionary<long, string>?> ForumTagsAsync(DiscordBridge discord, string forum)
    {
        var (names, untagged) = FolderTagNames();
        var hash = Hash(forum + "|" + string.Join("|", names.Select(pair => $"{pair.Key}={pair.Value}")));
        if (_forumTags is { } cached && cached.Hash == hash && _clock() - cached.At < (cached.Worked ? TagsCheckEvery : TagsRetryAfter))
        {
            return cached.Tags;
        }

        ForumNotice = untagged > 0
            ? $"The notes forum can only have {MaxTags} tags, so notes in {untagged} {(untagged == 1 ? "folder have" : "folders have")} no tag. Their posts still show the notes."
            : null;
        if (await discord.GetForumAsync(forum) is not { } info)
        {
            return _forumTags?.Tags;
        }

        _guildId ??= info.GuildId;
        var have = info.Tags.GroupBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);
        var wantedNames = names.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        IReadOnlyList<ForumTag> now = info.Tags;
        var worked = true;
        var same = wantedNames.Count == info.Tags.Count && wantedNames.All(have.ContainsKey);
        if (!same)
        {
            var list = wantedNames.Select(name => (have.TryGetValue(name, out var id) ? id : null, name)).ToList();
            if (await discord.SetForumTagsAsync(forum, list) is { } set)
            {
                now = set;
            }
            else
            {
                worked = false;
            }
        }

        var byName = now.GroupBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);
        var tags = names.Where(pair => byName.ContainsKey(pair.Value)).ToDictionary(pair => pair.Key, pair => byName[pair.Value]);
        _forumTags = (hash, tags, _clock(), worked);
        return tags;
    }

    /// <summary>
    /// The tag name for each folder, in folder order: its own name at the top, "Parent / Child" inside another when
    /// that fits in 20 characters, else its own name. Only the first 20 get one; the count of the rest comes back too.
    /// </summary>
    private (Dictionary<long, string> Names, int Untagged) FolderTagNames()
    {
        var names = new Dictionary<long, string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var untagged = 0;

        void Add(IEnumerable<NoteFolder> folders, string? parent)
        {
            foreach (var folder in folders)
            {
                var nested = parent is null ? folder.Name : $"{parent} / {folder.Name}";
                var name = (nested.Length <= MaxTagName ? nested : folder.Name).Trim();
                name = name.Length > MaxTagName ? name[..MaxTagName].TrimEnd() : name;
                if (names.Count < MaxTags && name.Length > 0 && used.Add(name))
                {
                    names[folder.Id] = name;
                }
                else
                {
                    untagged++;
                }

                Add(folder.Children, folder.Name);
            }
        }

        Add(_database.GetNoteFolders(), null);
        return (names, untagged);
    }

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
                    var (dealt, said) = await DealWithAsync(discord, row, message, told);
                    told |= said;
                    if (!dealt)
                    {
                        return;
                    }
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
    private sealed record ThreadFiles(IReadOnlyList<FileText> Files, IReadOnlyList<string> Problems);

    private async Task<ThreadFiles> ReadThreadFilesAsync(DiscordBridge discord, DiscordMessage message)
    {
        var files = new List<FileText>();
        var problems = new List<string>();
        foreach (var attachment in message.Attachments)
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
                files.Add(file.File!);
            }
        }

        return new ThreadFiles(files, problems);
    }

    /// <summary>Each file joins the note's Sources, the same as dropping it on that note on the PC. False, after saying why, when any couldn't.</summary>
    private async Task<bool> AttachThreadFilesAsync(DiscordBridge discord, string thread, long noteId, ThreadFiles read)
    {
        var problems = read.Problems.ToList();
        foreach (var file in read.Files)
        {
            var attached = NoteFiles is { CanMakeNotes: true } notes
                ? notes.AttachFile(noteId, file, _clock())
                : new StartedNote(noteId, "", null, null, "Files can't be added to notes right now.");
            if (attached.Problem is { } problem)
            {
                problems.Add(problem);
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

    /// <summary>A post the owner started, as Discord described it.</summary>
    private sealed record StartedPost(string Id, string? Parent, string Name, IReadOnlyList<string> Tags);

    /// <summary>The owner started a post in one of the forums. It's dealt with aside, like a message in a post.</summary>
    public Task OnThreadStartedAsync(JsonElement thread)
    {
        if (DiscordBridge.ThreadText(thread, "id") is { } id)
        {
            var started = new StartedPost(id, DiscordBridge.ThreadText(thread, "parent_id"), DiscordBridge.ThreadText(thread, "name")?.Trim() ?? "", DiscordBridge.AppliedTags(thread));
            Track(id, async () =>
            {
                if (Discord is { } discord)
                {
                    await StartFromPostAsync(discord, started);
                }
            });
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// A post the owner started becomes a note or a list. In the notes forum its name is the title, its first tag
    /// picks the folder, its first message is the text, and its files become the note's sources. In the lists forum
    /// it becomes a list, its lines the entries. It's written down as pending first, so it's never taken for new again,
    /// and stays pending, to be tried again, until its first message can be read and, with a tag, the forum's tags
    /// are known. The first message is only taken away once everything in it is saved; otherwise it stays. Anything
    /// typed after it, even before Waypoint knew of the post, is read straight after.
    /// </summary>
    private async Task StartFromPostAsync(DiscordBridge discord, StartedPost post)
    {
        if (DiscordSettings is not { } settings)
        {
            return;
        }

        var kind = post.Parent == settings.NotesForumChannelId ? WaypointDatabase.ThreadNote
            : post.Parent == settings.ListsForumChannelId ? WaypointDatabase.ThreadList
            : null;
        if (kind is null)
        {
            return;
        }

        var known = _database.FindDiscordThread(post.Id);
        if (known is { State: not WaypointDatabase.PostPending })
        {
            return;
        }

        if (known is null)
        {
            _database.AddDiscordThread(new DiscordThreadRow(post.Id, kind, PendingKey + post.Id, post.Parent!, WaypointDatabase.PostPending, true, null, [], post.Id));
        }

        var starter = await discord.GetStarterAsync(post.Id);
        if (!starter.Read)
        {
            return;
        }

        var kept = kind == WaypointDatabase.ThreadNote
            ? await NoteFromPostAsync(discord, settings, post, starter.Message)
            : await ListFromPostAsync(discord, post, starter.Message);
        if (kept is null)
        {
            return;
        }

        if (kept == true && starter.Message is not null && starter.FromOwner)
        {
            // Without Manage Messages it stays, and Waypoint's messages go under it.
            await discord.RemoveMessageAsync(post.Id, post.Id);
        }

        _host.Changed();
        ViewsChanged();
        if (_database.FindDiscordThread(post.Id) is { } row && (row.Live || row.MovedTo is not null))
        {
            await ReadPostAsync(discord, row);
        }
    }

    /// <summary>
    /// The note a post makes. Null when it has to wait, true once everything in the first message is kept, and false
    /// when something wasn't, so the first message stays.
    /// </summary>
    private async Task<bool?> NoteFromPostAsync(DiscordBridge discord, DiscordSettings settings, StartedPost post, DiscordMessage? starter)
    {
        // Files are downloaded and read first, aside from any round, since that can take a while.
        var files = starter is { Attachments.Count: > 0 } ? await ReadThreadFilesAsync(discord, starter) : null;
        var text = starter?.Text.Trim() ?? "";
        var made = await BetweenRoundsAsync(async () =>
        {
            var tags = await ForumTagsAsync(discord, settings.NotesForumChannelId!);
            if (post.Tags.Count > 0 && tags is null)
            {
                return (long?)null;
            }

            long? folder = post.Tags.Count > 0 && tags!.FirstOrDefault(pair => pair.Value == post.Tags[0]) is { Value: not null } picked ? picked.Key : null;
            var id = NoteStore.CreateNote(post.Name, text, _clock(), folder);
            _database.SetDiscordThreadKey(post.Id, id.ToString(CultureInfo.InvariantCulture), WaypointDatabase.PostLive);
            _database.SetDiscordThreadMeta(post.Id, Meta(DiscordBridge.ThreadName(post.Name), post.Tags.Take(1).ToList()));
            return id;
        });
        if (made is not { } noteId)
        {
            return null;
        }

        if (files is null)
        {
            return true;
        }

        // The files are kept before the first message goes, since Discord takes a deleted message's files with it.
        var all = await AttachThreadFilesAsync(discord, post.Id, noteId, files);
        if (text.Length == 0 && NoteFiles is { CanMakeNotes: true } notes && _database.GetNoteSources(noteId).Count > 0)
        {
            var written = await notes.WriteNotesAsync(noteId, null, false, _clock());
            if (written.Status != NoteWriteStatus.Written)
            {
                await NoticeAsync(discord, post.Id, DiscordText.Notice("Notes not written", written.Message, CardTone.Attention));
            }
        }

        return all;
    }

    /// <summary>The list a post makes, or adds to when it already has a post. Null to wait, false to keep the first message.</summary>
    private async Task<bool?> ListFromPostAsync(DiscordBridge discord, StartedPost post, DiscordMessage? starter)
    {
        var (problem, name, existing) = await BetweenRoundsAsync(() =>
        {
            var made = _database.CreateList(post.Name, _clock());
            if (made.Problem is { } turnedDown)
            {
                // Nothing was saved, so the first message stays, and the post is set aside so it isn't tried again.
                _database.SetDiscordThreadState(post.Id, WaypointDatabase.PostRetired);
                return Task.FromResult<(string?, string, string?)>((turnedDown, "", null));
            }

            var name = ListNamed(made.Name) ?? made.Name;
            _database.AddListEntries(name, ListLines(starter?.Text ?? ""), _clock());
            if (_database.GetDiscordThread(WaypointDatabase.ThreadList, name) is { } other)
            {
                // The list keeps its one post. This one is set aside, pointing there.
                _database.AddDiscordThread(new DiscordThreadRow(post.Id, WaypointDatabase.ThreadList, name, post.Parent!, WaypointDatabase.PostRetired, true, null, [], post.Id, other.Thread));
                return Task.FromResult<(string?, string, string?)>((null, name, other.Thread));
            }

            _database.SetDiscordThreadKey(post.Id, name, WaypointDatabase.PostLive);
            _database.SetDiscordThreadMeta(post.Id, Meta(DiscordBridge.ThreadName(post.Name), []));
            return Task.FromResult<(string?, string, string?)>((null, name, null));
        });

        if (problem is not null)
        {
            await discord.SendInThreadAsync(post.Id, DiscordText.Notice("No list made", problem, CardTone.Attention));
            return false;
        }

        if (existing is not null)
        {
            await discord.SendInThreadAsync(post.Id, DiscordText.Notice($"Added to {name}", $"That list already has its own post, <#{existing}>, so what was typed here went on it there.", CardTone.Finished));
            await discord.EditThreadAsync(post.Id, archived: true);
        }

        return true;
    }

    /// <summary>
    /// Reads what was typed in the posts while Waypoint was away, and posts started meanwhile or still waiting, from
    /// the server's list of active posts. A post that gets a message is active again, so archived ones are covered too.
    /// </summary>
    private async Task CatchUpThreadsAsync(DiscordBridge discord, DiscordSettings settings)
    {
        if (_guildId is null && (settings.NotesForumChannelId ?? settings.ListsForumChannelId) is { } forum)
        {
            _guildId = (await discord.GetForumAsync(forum))?.GuildId;
        }

        if (_guildId is not { } guild || await discord.ActiveThreadsAsync(guild) is not { } threads)
        {
            _caughtUpAt = null;
            return;
        }

        foreach (var thread in threads.Where(thread => discord.IsForum(thread.ParentId)))
        {
            await OnePostAsync(thread.Id, () => Separately(async () =>
            {
                var row = _database.FindDiscordThread(thread.Id);
                if (row is null || row.State == WaypointDatabase.PostPending)
                {
                    if (thread.OwnerId == discord.OwnerUserId)
                    {
                        await StartFromPostAsync(discord, new StartedPost(thread.Id, thread.ParentId, thread.Name.Trim(), thread.Tags));
                    }

                    return;
                }

                if (thread.LastMessageId is { } last && Later(last, row.Cursor))
                {
                    await ReadPostAsync(discord, row);
                }
            }));
        }
    }

    private static bool Later(string id, string? than) =>
        than is null || (ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var a) && ulong.TryParse(than, NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? a > b : string.CompareOrdinal(id, than) > 0);

    /// <summary>A post was deleted on Discord. It's made again on the next round, which comes now.</summary>
    private void OnThreadGone(string threadId) => ViewsChanged();

    /// <summary>The Edit button on a note's part, and Start over on a list's post. Null when it isn't one of them.</summary>
    private ButtonResult? OnThreadAction(string action)
    {
        if (action.StartsWith(NoteEdit, StringComparison.Ordinal))
        {
            return EditPartForm(action[NoteEdit.Length..]);
        }

        if (action.StartsWith(ListStart, StringComparison.Ordinal))
        {
            if (ListForTarget(action[ListStart.Length..]) is not { } list)
            {
                return new ButtonResult(Outcome: "That list is gone now.");
            }

            _database.StartList(list.Name);
            var started = _database.GetListOverview(includeEmptyShopping: true).First(found => found.Name == list.Name);
            return new ButtonResult(ListPostCard(started));
        }

        return null;
    }

    private static bool TryReadPart(string rest, out long noteId, out int part, out string hash)
    {
        noteId = 0;
        part = 0;
        hash = "";
        var pieces = rest.Split(':');
        if (pieces.Length != 3
            || !long.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out noteId)
            || !int.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out part))
        {
            return false;
        }

        hash = pieces[2];
        return true;
    }

    /// <summary>
    /// The form for one part of a note, filled in with that part's Markdown as the note has it now, carrying a hash
    /// of it so saving can tell whether it changed meanwhile. A part too long for a form box says to use the PC.
    /// </summary>
    private ButtonResult EditPartForm(string rest)
    {
        static ButtonResult Private(string title, string text) =>
            new(DiscordText.Notice(title, text, CardTone.Attention) with { Private = true });

        if (!TryReadPart(rest, out var noteId, out var part, out _) || _database.GetNote(noteId) is not { } note)
        {
            return Private("That note is gone", "It was deleted on the PC.");
        }

        // A part is at most 3000 characters of Markdown, so it always fits the 4000 a form box holds, with room to add.
        var chunks = SplitMarkdown(note.Body, PartMarkdown);
        part = Math.Clamp(part, 0, chunks.Count - 1);
        var chunk = chunks[part];
        var shown = chunk.Replace("\r\n", "\n").TrimEnd('\n');
        var id = $"{NoteEdit}{noteId.ToString(CultureInfo.InvariantCulture)}:{part.ToString(CultureInfo.InvariantCulture)}:{Hash(chunk)}";
        if (_formsOpened.Count >= MaxOpenForms)
        {
            _formsOpened.Clear();
        }

        _formsOpened[id] = shown;
        var title = chunks.Count == 1 ? "Edit note" : $"Edit page {(part + 1).ToString(CultureInfo.InvariantCulture)} of {chunks.Count.ToString(CultureInfo.InvariantCulture)}";
        return new ButtonResult
        {
            Form = new CardForm(id, title, [new FormBox("text", "Note text, in Markdown", Long: true, Required: true, Value: shown, MaxLength: MaxEditText)]),
        };
    }

    /// <summary>
    /// A part's form was saved. If that part is still what the form was filled with, it's replaced with what was
    /// typed and nothing else changes. If it changed meanwhile, nothing is replaced: what was typed goes at the end
    /// of the note under a "From Discord" line, and the reply says so. It never waits for a round of updates, since
    /// each save checks the note didn't change meanwhile, and if it can't be saved, the reply holds what was typed.
    /// </summary>
    private CommandReply SavePartEdit(string rest, IReadOnlyDictionary<string, string> values)
    {
        static CommandReply Reply(string title, string text, CardTone tone) => new(DiscordText.Notice(title, text, tone), true);

        var typed = values.GetValueOrDefault("text") ?? "";
        var opened = _formsOpened.TryRemove(WaypointWorker.NoteEdit + rest, out var was) ? was : null;

        // Nothing typed is ever lost: when it can't be saved, the private reply holds it, ready to copy.
        CommandReply NotSaved(string why) => new(new DiscordCard("Not saved", typed.Trim().Length == 0 ? DiscordText.Escape(why) : $"{DiscordText.Escape(why)} Here's what you wrote, to copy:\n```\n{DiscordText.Shorten(typed.Replace("```", "`\u200b``"), MaxDescription - why.Length - 80)}\n```", [], CardTone.Attention), true);

        if (!TryReadPart(rest, out var noteId, out var part, out var hash) || _database.GetNote(noteId) is not { } note)
        {
            return NotSaved("That note was deleted on the PC. Bring it back from History there, then try again.");
        }

        if (typed.Trim().Length == 0)
        {
            return Reply("Not saved", "The text was empty, so the note stayed as it was.", CardTone.Attention);
        }

        var chunks = SplitMarkdown(note.Body, PartMarkdown).ToList();
        if (part < chunks.Count && Hash(chunks[part]) == hash)
        {
            chunks[part] = Replacement(chunks[part], typed);
            if (_database.SaveNoteIfUnchanged(noteId, note.Title, string.Concat(chunks), note.Body, _clock()))
            {
                _host.Changed();
                ViewsChanged();
                return Reply("Saved", "", CardTone.Finished);
            }
        }

        // The PC's change stays as it is. Only the lines changed or added in the form go at the end, so the rest
        // doesn't show up twice.
        var added = opened is null ? typed.Replace("\r\n", "\n").Trim('\n') : ChangedLines(opened, typed);
        if (added.Trim().Length == 0)
        {
            return NotSaved("That part changed on the PC since the form was opened, so nothing was replaced, and the form only took lines away. Make that change on the PC.");
        }

        if (!AppendToNote(noteId, FromDiscord + "\n\n" + added))
        {
            return NotSaved("The note kept changing on the PC, so it wasn't changed. Try again in a moment.");
        }

        _host.Changed();
        ViewsChanged();
        return Reply("Added at the end instead", $"That part changed since the form was opened, so nothing was replaced. The lines you changed or added were added at the end of the note under a \"{FromDiscord}\" line.", CardTone.Attention);
    }

    /// <summary>The lines in what was typed that weren't in the form as it opened, in order, found by the longest run they share.</summary>
    public static string ChangedLines(string opened, string typed)
    {
        var before = opened.Replace("\r\n", "\n").Split('\n');
        var after = typed.Replace("\r\n", "\n").Split('\n');
        var shared = new int[before.Length + 1, after.Length + 1];
        for (var i = before.Length - 1; i >= 0; i--)
        {
            for (var j = after.Length - 1; j >= 0; j--)
            {
                shared[i, j] = before[i] == after[j] ? shared[i + 1, j + 1] + 1 : Math.Max(shared[i + 1, j], shared[i, j + 1]);
            }
        }

        // Changed lines that sit apart in the form, with unchanged ones between, stay apart as their own paragraphs.
        var changed = new List<string>();
        int x = 0, y = 0, last = -2;
        while (y < after.Length)
        {
            if (x < before.Length && before[x] == after[y])
            {
                x++;
                y++;
            }
            else if (x < before.Length && shared[x + 1, y] >= shared[x, y + 1])
            {
                x++;
            }
            else
            {
                if (changed.Count > 0 && last != y - 1 && changed[^1].Trim().Length > 0)
                {
                    changed.Add("");
                }

                changed.Add(after[y]);
                last = y;
                y++;
            }
        }

        return System.Text.RegularExpressions.Regex.Replace(string.Join("\n", changed), @"\n{3,}", "\n\n").Trim('\n');
    }

    /// <summary>The typed text in place of a part, with the part's own line endings and the line breaks it ended with.</summary>
    private static string Replacement(string chunk, string typed)
    {
        var newline = chunk.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var trailing = chunk[chunk.TrimEnd('\r', '\n').Length..];
        return typed.Replace("\r\n", "\n").TrimEnd('\n').Replace("\n", newline) + trailing;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    private static string Hash(DiscordCard card) => Hash(JsonSerializer.Serialize(card));
}
