using Waypoint.Core.Data;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Background;

/// <summary>Making a post, finding one a crash left unnoted, and retiring a post that's no longer wanted.</summary>
public sealed partial class WaypointWorker
{
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

        // The post is noted before it's made, so a crash before it's saved finds the post already made instead of
        // making a second. Discord takes no nonce for a new post, so it's looked for among the forum's posts.
        var sendKey = "post:" + key;
        var send = _database.BeginDiscordSend(sendKey, forum, _clock());
        var post = send.MessageId is { } known ? new ForumPost(known, known) : null;
        var recovered = post is not null;

        // An earlier try made its post with the name it had then, which the note's title may have changed from since.
        var madeAs = send.Fresh ? wanted.Name : _database.GetDiscordSendTriedName(sendKey) ?? wanted.Name;
        if (post is null && !send.Fresh)
        {
            var (lookup, found) = await FindMadePostAsync(discord, forum, madeAs);
            if (lookup == SentLookup.Unknown)
            {
                // The forum's posts couldn't be read, so an earlier try may have made it. It's looked for again later,
                // and the Discord status says why it's waiting, since it waits for as long as that lasts.
                discord.PostHeldBack(DiscordBridge.PostUnchecked);
                BackOff(key);
                return;
            }

            post = found;
            recovered = post is not null;
        }

        if (post is null)
        {
            madeAs = wanted.Name;
            _database.SetDiscordSendTriedName(sendKey, wanted.Name);
            var made = await discord.CreateForumPostAsync(forum, wanted.Name, tags, wanted.Parts[0]);
            if (made.Post is not { } fresh)
            {
                BackOff(key);
                if (made.TurnedDown)
                {
                    // A tag may have been deleted by hand, so the forum's tags are read again next time. Nothing was
                    // made, so there's nothing to look for.
                    _forumTags = null;
                    _database.FinishDiscordSend(sendKey);
                }

                return;
            }

            post = fresh;
            _database.SetDiscordSendMessage(sendKey, post.Thread);
        }

        _newPostsThisRound++;
        _postBackoff.Remove(key);
        // A post made by an earlier try may show an older name, first part, and tags, so it's kept as showing the
        // name it was made with and needing the rest drawn again, and the sync just below brings it up to date.
        var meta = recovered ? Hash(madeAs) + ":" : Meta(wanted.Name, tags);
        var row = new DiscordThreadRow(post.Thread, wanted.Kind, wanted.Key, forum, WaypointDatabase.PostLive, false, meta, [new DiscordThreadPart(post.Message, recovered ? "" : Hash(wanted.Parts[0]))], post.Message);
        var replaced = _database.InOneWrite(write =>
        {
            var older = write.AddDiscordThread(row);
            write.FinishDiscordSend(sendKey);
            return older;
        });
        foreach (var old in replaced)
        {
            await PointToNewPostAsync(discord, old, post.Thread, wanted.Kind);
        }

        await SyncPostAsync(discord, forum, wanted, row);
    }

    /// <summary>A post that wasn't made, or might have been, waits longer each time before it's tried again.</summary>
    private void BackOff(string key)
    {
        var wait = _postBackoff.TryGetValue(key, out var before) ? TimeSpan.FromTicks(Math.Min(before.Wait.Ticks * 2, LongestBackoff.Ticks)) : FirstBackoff;
        _postBackoff[key] = (_clock() + wait, wait);
    }

    /// <summary>
    /// A post of the bot's in this forum with this name that no record knows, made by a try that went through before
    /// it could be saved. Its first message has the post's own ID. Unknown when the posts can't be read, so it may be there.
    /// </summary>
    private async Task<(SentLookup Result, ForumPost? Post)> FindMadePostAsync(DiscordBridge discord, string forum, string name)
    {
        _guildId ??= (await discord.GetForumAsync(forum))?.GuildId;
        if (_guildId is not { } guild || await discord.ActiveThreadsAsync(guild) is not { } threads)
        {
            return (SentLookup.Unknown, null);
        }

        var shown = DiscordBridge.ThreadName(name);
        var found = threads
            .Where(thread => thread.ParentId == forum && IsBots(discord, thread.OwnerId) && thread.Name == shown && _database.FindDiscordThread(thread.Id) is null)
            .Select(thread => new ForumPost(thread.Id, thread.Id))
            .FirstOrDefault();
        return found is null ? (SentLookup.NotFound, null) : (SentLookup.Found, found);
    }

    /// <summary>Whether the bot made a post: by its own user ID once Discord has said it, or else as not the owner's.</summary>
    private static bool IsBots(DiscordBridge discord, string? owner) =>
        discord.BotUserId is { } bot ? owner == bot : owner != discord.OwnerUserId;

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
}
