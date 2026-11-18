using System.Globalization;
using System.Text.Json;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Notes;

namespace Waypoint.Core.Background;

/// <summary>Posts the owner starts on the phone, which make a new note or list, and catching up on posts after time away.</summary>
public sealed partial class WaypointWorker
{
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
            var written = await NoteJobText.GuardNotes(() => notes.WriteNotesAsync(noteId, null, false, _clock()));
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
}
