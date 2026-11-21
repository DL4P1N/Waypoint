using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
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

        // When nothing the posts show has changed since a round found every post just as it should be, and no notice
        // is waiting to be cleared away, this round would change nothing, so it isn't made.
        var stamp = NotePostsStamp(forum, tags);
        if (stamp is not null && stamp == _notePostsSettled && !_database.HasDiscordNotices(NoticeSends))
        {
            return;
        }

        // Oldest first, so the newest note is made last and sits at the top of the forum.
        var notes = _database.GetNotes().Reverse().ToList();
        var rows = Live(WaypointDatabase.ThreadNote);
        var sources = _database.CountNoteSources();
        var settled = stamp is not null;
        foreach (var note in notes)
        {
            var key = note.Id.ToString(CultureInfo.InvariantCulture);
            IReadOnlyList<string>? tag = tags is null ? null : note.FolderId is { } folder && tags.TryGetValue(folder, out var found) ? [found] : [];
            var wanted = new WantedPost(WaypointDatabase.ThreadNote, key, DiscordBridge.ThreadName(NoteTitle(note)), tag, NoteParts(note, sources.GetValueOrDefault(note.Id)));
            var row = rows.GetValueOrDefault(key);
            settled &= Shows(row, forum, wanted);
            await SyncPostAsync(discord, forum, wanted, row);
        }

        var live = notes.Select(note => note.Id.ToString(CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows.Values.Where(row => !live.Contains(row.Key)))
        {
            settled = false;
            await RetirePostAsync(discord, row, "This note was deleted on the PC");
        }

        _notePostsSettled = settled ? stamp : null;
    }

    /// <summary>
    /// What the note posts are made from: the notes and everything about them, the posts kept for them, the forum,
    /// and its tags. Null when that can't be told, such as while the tags can't be read, so every round is made.
    /// </summary>
    private string? NotePostsStamp(string forum, Dictionary<long, string>? tags) =>
        tags is not null && _database.NotesChangeStamp() is { } notes
            ? $"{notes}|{forum}|{string.Join(",", tags.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key.ToString(CultureInfo.InvariantCulture)}={pair.Value}"))}"
            : null;

    /// <summary>How every notice's send is noted, whatever the post. See <see cref="NoticeSendPrefix"/>.</summary>
    private const string NoticeSends = "notice:";

    /// <summary>Set by a round that found every note post just as it should be, to what they were made from.</summary>
    private string? _notePostsSettled;

    /// <summary>Whether the last round found every note post as it should be, so the next is skipped if nothing changed. For tests.</summary>
    internal bool NotePostsSettled => _notePostsSettled is not null;

    /// <summary>Whether a post already shows just what it should, so <see cref="SyncPostAsync"/> has nothing to do for it.</summary>
    private static bool Shows(DiscordThreadRow? row, string forum, WantedPost wanted) =>
        row is not null
        && row.Forum == forum
        && (wanted.Tags is not { } tags || row.MetaHash == Meta(wanted.Name, tags))
        && row.Parts.Count == wanted.Parts.Count
        && row.Parts.Zip(wanted.Parts).All(pair => pair.First.Hash == Hash(pair.Second));

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

    /// <summary>
    /// A note's messages. Each holds one stretch of the note's Markdown, laid out for Discord, with an Edit button that
    /// opens that stretch in a form. Sources and transcripts stay on the PC and are only counted at the end.
    /// </summary>
    private IReadOnlyList<DiscordCard> NoteParts(NoteDetail note, int sources)
    {
        var chunks = SplitMarkdown(note.Body, PartMarkdown);
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

    /// <summary>A post was deleted on Discord. It's made again on the next round, which comes now.</summary>
    private void OnThreadGone(string threadId) => ViewsChanged();

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    private static string Hash(DiscordCard card) => Hash(JsonSerializer.Serialize(card));
}
