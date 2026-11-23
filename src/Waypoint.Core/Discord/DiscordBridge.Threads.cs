using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Waypoint.Core.Data;

namespace Waypoint.Core.Discord;

/// <summary>A tag a forum offers for its posts. Discord allows 20 per forum and 20 characters per name.</summary>
public sealed record ForumTag(string Id, string Name);

/// <summary>A forum and the server it's in, as Discord describes it.</summary>
public sealed record ForumInfo(string? GuildId, IReadOnlyList<ForumTag> Tags);

/// <summary>A post that's still active, from the server's list of them, so messages sent while away can be read.</summary>
public sealed record ActiveThread(string Id, string? ParentId, string? OwnerId, string? LastMessageId, string Name, IReadOnlyList<string> Tags);

/// <summary>A new forum post: its ID and its first message's, which Discord gives the same ID.</summary>
public sealed record ForumPost(string Thread, string Message);

/// <summary>
/// How making a post went. Turned down means Discord answered 400, such as a forum that needs a tag or a tag that
/// was deleted, so the same post would only be turned down again.
/// </summary>
public sealed record PostMade(ForumPost? Post, bool TurnedDown = false);

/// <summary>One message in a post, oldest first when read in a batch. Message is null when it has nothing to read.</summary>
public sealed record ThreadEntry(string Id, bool FromOwner, DiscordMessage? Message);

/// <summary>A post's first message, if it could be read. Read is false when Discord couldn't be asked, so it's tried later.</summary>
public sealed record StarterRead(bool Read, DiscordMessage? Message, bool FromOwner);

/// <summary>How a change to a post went. Missing means the post was deleted, and NotAllowed that a permission is missing.</summary>
public enum ThreadChange
{
    Done,
    Missing,
    NotAllowed,
    Failed,
}

/// <summary>
/// Forum posts, for notes and lists that each get one. The calls follow Discord's API v10 docs as read on 2026-11-15
/// (discord.com/developers/docs, resources/channel and topics/threads):
/// - A forum is channel type 15, and a post is a public thread made with POST /channels/{forum}/threads, a name of
///   1 to 100 characters, up to 5 applied tags, and a first message whose ID is the thread's own.
/// - A forum has at most 20 tags of up to 20 characters, set with PATCH on the forum, which needs Manage Channels.
/// - Messages can't be edited in an archived post (error 50083), so it's unarchived first. Sending a message there
///   unarchives it by itself.
/// - Deleting someone else's message needs Manage Messages (403, code 50013, without it). Renaming or deleting a post
///   Waypoint didn't start needs Manage Threads.
/// - THREAD_CREATE, THREAD_UPDATE and THREAD_DELETE come with the Guilds intent, and messages in posts come as
///   MESSAGE_CREATE with Guild Messages, with no need to join the post.
/// - Messages are sent with SUPPRESS_NOTIFICATIONS, which forum posts allow, so a long note doesn't buzz the phone.
/// </summary>
public sealed partial class DiscordBridge
{
    private const int SuppressNotifications = 1 << 12;
    private const int ArchivedThread = 50083;
    private const int MaxThreadName = 100;
    private const int MaxAppliedTags = 5;

    /// <summary>A post the owner just started in one of the forums. Given the thread as Discord sent it.</summary>
    public Func<JsonElement, Task>? ThreadStarted { get; set; }

    /// <summary>One of the owner's messages in a post that shows a note or a list, given the post's ID.</summary>
    public Func<string, DiscordMessage, Task>? ThreadMessage { get; set; }

    /// <summary>A post Waypoint keeps was deleted on Discord, so it's made again on the next round.</summary>
    public Action<string>? ThreadGone { get; set; }

    /// <summary>Why the last change to a forum post was turned down, until one goes through.</summary>
    public string? ThreadProblem { get; private set; }

    /// <summary>Says why a post is being held back, such as one that may already be made, so it shows with the others.</summary>
    public void PostHeldBack(string why) => ThreadProblem = why;

    /// <summary>A post that may already have been made waits until the forum's posts can be read to look for it.</summary>
    public const string PostUnchecked = "Discord's list of forum posts can't be read right now, so a note or list post that may already have been made waits until it can be checked, rather than being made twice.";

    public bool IsForum(string? channel) =>
        channel is not null && (channel == settings.NotesForumChannelId || channel == settings.ListsForumChannelId);

    public string OwnerUserId => settings.OwnerUserId;

    /// <summary>
    /// A thread made or opened. Only a new post started by the owner in one of the forums counts, and one Waypoint
    /// already knows, in any state, never counts again.
    /// </summary>
    private async Task HandleThreadCreateAsync(JsonElement thread)
    {
        if (Text(thread, "id") is not { } id
            || !IsForum(Text(thread, "parent_id"))
            || Text(thread, "owner_id") != settings.OwnerUserId
            || !(thread.TryGetProperty("newly_created", out var created) && created.ValueKind == JsonValueKind.True)
            || database.FindDiscordThread(id) is not null)
        {
            return;
        }

        if (ThreadStarted is { } started)
        {
            await started(thread);
        }
    }

    private void HandleThreadDelete(JsonElement thread)
    {
        if (Text(thread, "id") is { } id && database.MarkDiscordThreadGone(id))
        {
            ThreadGone?.Invoke(id);
        }
    }

    /// <summary>
    /// One of the owner's messages in a post Waypoint knows, other than a post's first message, which the post itself
    /// is made from. It's only a sign to read the post: what's read, and how far, is kept with the post, so a message
    /// is dealt with once whether it comes live or in a later read.
    /// </summary>
    private async Task<bool> HandleThreadMessageAsync(string channel, string id, JsonElement message)
    {
        if (id == channel || database.FindDiscordThread(channel) is null)
        {
            return false;
        }

        if (ReadMessage(message) is { } read && ThreadMessage is { } handle)
        {
            await handle(channel, read);
        }

        return true;
    }

    /// <summary>Makes a post. Its name is trimmed to Discord's 100 characters, and at most 5 tags go on.</summary>
    public async Task<PostMade> CreateForumPostAsync(string forum, string name, IReadOnlyList<string> tags, DiscordCard first)
    {
        try
        {
            var message = MessageBody(first, flags: SuppressNotifications);
            using var response = await SendWithLimitsAsync(() =>
            {
                var request = NewRequest(HttpMethod.Post, $"{ApiBase}/channels/{forum}/threads");
                request.Content = JsonContent.Create(new Dictionary<string, object>
                {
                    ["name"] = ThreadName(name),
                    ["auto_archive_duration"] = 10080,
                    ["applied_tags"] = tags.Take(MaxAppliedTags).ToArray(),
                    ["message"] = message,
                });
                return request;
            });
            if (await Refused(response, "make a post in the forum", "Send Messages") is { } refused)
            {
                ThreadProblem = refused;
                return new PostMade(null);
            }

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                ThreadProblem = "Discord turned down a new forum post. If the forum needs a tag on every post, turn that off in its settings.";
                return new PostMade(null, TurnedDown: true);
            }

            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            var thread = Text(root, "id");
            var starter = root.TryGetProperty("message", out var sent) ? Text(sent, "id") : null;
            return new PostMade(thread is null ? null : new ForumPost(thread, starter ?? thread));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            return new PostMade(null);
        }
    }

    /// <summary>
    /// Sends a message in a post. Missing comes back when the post itself is gone. A post noted beforehand carries its
    /// note's nonce, so trying again soon after never makes a second.
    /// </summary>
    public async Task<(string? Id, ThreadChange Outcome)> SendInThreadAsync(string thread, DiscordCard card, string? nonce = null)
    {
        try
        {
            using var response = await SendWithLimitsAsync(() =>
            {
                var request = NewRequest(HttpMethod.Post, $"{ApiBase}/channels/{thread}/messages");
                request.Content = JsonContent.Create(WithNonce(MessageBody(card, flags: SuppressNotifications), nonce));
                return request;
            });
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (null, ThreadChange.Missing);
            }

            if (await Refused(response, "write in a post", "Send Messages in Threads") is { } refused)
            {
                ThreadProblem = refused;
                return (null, ThreadChange.NotAllowed);
            }

            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return (Text(document.RootElement, "id"), ThreadChange.Done);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            return (null, ThreadChange.Failed);
        }
    }

    /// <summary>
    /// Changes one of Waypoint's messages in a post. Discord won't edit in an archived post, so when it says that's
    /// why, the post is unarchived and the edit tried once more.
    /// </summary>
    public async Task<EditOutcome> EditInThreadAsync(string thread, string messageId, DiscordCard card)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await SendWithLimitsAsync(() =>
                {
                    var request = NewRequest(HttpMethod.Patch, $"{ApiBase}/channels/{thread}/messages/{messageId}");
                    request.Content = JsonContent.Create(MessageBody(card));
                    return request;
                });
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return EditOutcome.Missing;
                }

                if (response.StatusCode == HttpStatusCode.BadRequest && attempt == 0 && await ErrorCode(response) == ArchivedThread)
                {
                    if (await EditThreadAsync(thread, archived: false) != ThreadChange.Done)
                    {
                        return EditOutcome.Failed;
                    }

                    continue;
                }

                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    ThreadProblem = "Discord turned down a change to a forum post, so it stays as it was until what it shows changes.";
                    return EditOutcome.Refused;
                }

                response.EnsureSuccessStatusCode();
                return EditOutcome.Edited;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LastError = exception.Message;
                return EditOutcome.Failed;
            }
        }
    }

    /// <summary>
    /// Takes away one of Waypoint's messages in a post. One already gone counts as taken away. Like an edit, when
    /// Discord says the post is archived, it's unarchived and the delete tried once more.
    /// </summary>
    public async Task<bool> DeleteInThreadAsync(string thread, string messageId)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await SendWithLimitsAsync(() => NewRequest(HttpMethod.Delete, $"{ApiBase}/channels/{thread}/messages/{messageId}"));
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
                {
                    return true;
                }

                if (response.StatusCode == HttpStatusCode.BadRequest && attempt == 0 && await ErrorCode(response) == ArchivedThread
                    && await EditThreadAsync(thread, archived: false) == ThreadChange.Done)
                {
                    continue;
                }

                return false;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LastError = exception.Message;
                return false;
            }
        }
    }

    /// <summary>Renames a post, sets its tags, or archives or unarchives it. Anything left null stays as it is.</summary>
    public async Task<ThreadChange> EditThreadAsync(string thread, string? name = null, IReadOnlyList<string>? tags = null, bool? archived = null)
    {
        var body = new Dictionary<string, object>();
        if (name is not null)
        {
            body["name"] = ThreadName(name);
        }

        if (tags is not null)
        {
            body["applied_tags"] = tags.Take(MaxAppliedTags).ToArray();
        }

        if (archived is { } archive)
        {
            body["archived"] = archive;
        }

        // Renaming or tagging an archived post would be turned down, so it comes back first in the same call.
        if (archived is null && (name is not null || tags is not null))
        {
            body["archived"] = false;
        }

        return await ChangeThreadAsync(HttpMethod.Patch, thread, body, "rename or tag a post", "Manage Threads");
    }

    /// <summary>Deletes a whole post. Needs Manage Threads.</summary>
    public Task<ThreadChange> DeleteThreadAsync(string thread) =>
        ChangeThreadAsync(HttpMethod.Delete, thread, null, "delete a post", "Manage Threads");

    private async Task<ThreadChange> ChangeThreadAsync(HttpMethod method, string thread, Dictionary<string, object>? body, string doing, string permission)
    {
        try
        {
            using var response = await SendWithLimitsAsync(() =>
            {
                var request = NewRequest(method, $"{ApiBase}/channels/{thread}");
                if (body is not null)
                {
                    request.Content = JsonContent.Create(body);
                }

                return request;
            });
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return ThreadChange.Missing;
            }

            if (await Refused(response, doing, permission) is { } refused)
            {
                ThreadProblem = refused;
                return ThreadChange.NotAllowed;
            }

            response.EnsureSuccessStatusCode();
            return ThreadChange.Done;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            return ThreadChange.Failed;
        }
    }

    /// <summary>
    /// Takes away one of the owner's messages once what it said is saved. Without Manage Messages that's refused,
    /// and NotAllowed comes back so it can be marked instead.
    /// </summary>
    public async Task<ThreadChange> RemoveMessageAsync(string channel, string messageId)
    {
        try
        {
            using var response = await SendWithLimitsAsync(() => NewRequest(HttpMethod.Delete, $"{ApiBase}/channels/{channel}/messages/{messageId}"));
            if (response.StatusCode == HttpStatusCode.NotFound || response.IsSuccessStatusCode)
            {
                return ThreadChange.Done;
            }

            return response.StatusCode == HttpStatusCode.Forbidden ? ThreadChange.NotAllowed : ThreadChange.Failed;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            return ThreadChange.Failed;
        }
    }

    /// <summary>Puts a reaction on a message, such as ✅ on one that was saved but couldn't be taken away.</summary>
    public async Task<bool> ReactAsync(string channel, string messageId, string emoji)
    {
        try
        {
            using var response = await SendWithLimitsAsync(() =>
                NewRequest(HttpMethod.Put, $"{ApiBase}/channels/{channel}/messages/{messageId}/reactions/{Uri.EscapeDataString(emoji)}/@me"));
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// A post's first message. A message that's gone counts as read with nothing in it, and anything else that goes
    /// wrong as not read, so it's asked for again later rather than taken as empty.
    /// </summary>
    public async Task<StarterRead> GetStarterAsync(string thread)
    {
        try
        {
            using var response = await SendWithLimitsAsync(() => NewRequest(HttpMethod.Get, $"{ApiBase}/channels/{thread}/messages/{thread}"));
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new StarterRead(true, null, false);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new StarterRead(false, null, false);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            return new StarterRead(true, ReadMessage(root), IsFromOwner(root));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            return new StarterRead(false, null, false);
        }
    }

    /// <summary>The most messages one read asks for, which is Discord's limit.</summary>
    public const int ThreadPage = 100;

    /// <summary>
    /// Up to 100 messages in a post right after <paramref name="after"/>, oldest first, everyone's, with which are the
    /// owner's. A post's first message is left out, since the post is made from it. Null when the post can't be read,
    /// and empty when it's gone.
    /// </summary>
    public async Task<IReadOnlyList<ThreadEntry>?> ReadThreadPageAsync(string thread, string? after)
    {
        try
        {
            var query = $"limit={ThreadPage}&after={after ?? thread}";
            using var response = await SendWithLimitsAsync(() => NewRequest(HttpMethod.Get, $"{ApiBase}/channels/{thread}/messages?{query}"));
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return [];
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return document.RootElement.EnumerateArray()
                .Where(message => Text(message, "id") is { } id && id != thread && ulong.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
                .OrderBy(message => ulong.Parse(Text(message, "id")!, System.Globalization.CultureInfo.InvariantCulture))
                .Select(message => IsFromOwner(message)
                    ? new ThreadEntry(Text(message, "id")!, true, ReadMessage(message))
                    : new ThreadEntry(Text(message, "id")!, false, null))
                .ToList();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            return null;
        }
    }

    /// <summary>A forum's tags and the server it's in, or null when it can't be read.</summary>
    public async Task<ForumInfo?> GetForumAsync(string forum)
    {
        try
        {
            using var response = await SendWithLimitsAsync(() => NewRequest(HttpMethod.Get, $"{ApiBase}/channels/{forum}"));
            if (!response.IsSuccessStatusCode)
            {
                ThreadProblem = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden
                    ? "A forum channel can't be found. Check the forum IDs in Settings and that the bot can see them."
                    : ThreadProblem;
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            var tags = new List<ForumTag>();
            if (root.TryGetProperty("available_tags", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                tags.AddRange(list.EnumerateArray()
                    .Where(tag => Text(tag, "id") is not null && Text(tag, "name") is not null)
                    .Select(tag => new ForumTag(Text(tag, "id")!, Text(tag, "name")!)));
            }

            return new ForumInfo(Text(root, "guild_id"), tags);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            return null;
        }
    }

    /// <summary>
    /// Sets a forum's whole list of tags. Tags kept carry their IDs, so posts that have them keep them. Returns the
    /// tags as Discord now has them, or null when that didn't work.
    /// </summary>
    public async Task<IReadOnlyList<ForumTag>?> SetForumTagsAsync(string forum, IReadOnlyList<(string? Id, string Name)> tags)
    {
        try
        {
            var list = tags.Select(tag => tag.Id is { } id ? (object)new { id, name = tag.Name } : new { name = tag.Name }).ToArray();
            using var response = await SendWithLimitsAsync(() =>
            {
                var request = NewRequest(HttpMethod.Patch, $"{ApiBase}/channels/{forum}");
                request.Content = JsonContent.Create(new { available_tags = list });
                return request;
            });
            if (await Refused(response, "add folder tags to the notes forum", "Manage Channels") is { } refused)
            {
                ThreadProblem = refused;
                return null;
            }

            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            return root.TryGetProperty("available_tags", out var now) && now.ValueKind == JsonValueKind.Array
                ? now.EnumerateArray().Where(tag => Text(tag, "id") is not null && Text(tag, "name") is not null).Select(tag => new ForumTag(Text(tag, "id")!, Text(tag, "name")!)).ToList()
                : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            return null;
        }
    }

    /// <summary>The active posts in the server, so messages and new posts made while Waypoint was off can be read.</summary>
    public async Task<IReadOnlyList<ActiveThread>?> ActiveThreadsAsync(string guild)
    {
        try
        {
            using var response = await SendWithLimitsAsync(() => NewRequest(HttpMethod.Get, $"{ApiBase}/guilds/{guild}/threads/active"));
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!document.RootElement.TryGetProperty("threads", out var threads) || threads.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return threads.EnumerateArray()
                .Where(thread => Text(thread, "id") is not null)
                .Select(thread => new ActiveThread(
                    Text(thread, "id")!,
                    Text(thread, "parent_id"),
                    Text(thread, "owner_id"),
                    Text(thread, "last_message_id"),
                    Text(thread, "name") ?? "",
                    AppliedTags(thread)))
                .ToList();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            return null;
        }
    }

    public static IReadOnlyList<string> AppliedTags(JsonElement thread) =>
        thread.TryGetProperty("applied_tags", out var tags) && tags.ValueKind == JsonValueKind.Array
            ? tags.EnumerateArray().Where(tag => tag.ValueKind == JsonValueKind.String).Select(tag => tag.GetString()!).ToList()
            : [];

    public static string? ThreadText(JsonElement thread, string name) => Text(thread, name);

    /// <summary>A post's name as Discord allows it: 1 to 100 characters on one line.</summary>
    public static string ThreadName(string name)
    {
        var line = string.Join(' ', name.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (line.Length == 0)
        {
            line = "Untitled";
        }

        if (line.Length <= MaxThreadName)
        {
            return line;
        }

        // Cut between whole characters, so an emoji or an accented letter is never split in half.
        var cut = new System.Text.StringBuilder();
        var characters = System.Globalization.StringInfo.GetTextElementEnumerator(line);
        while (characters.MoveNext() && cut.Length + characters.GetTextElement().Length <= MaxThreadName - 1)
        {
            cut.Append(characters.GetTextElement());
        }

        return cut.ToString().TrimEnd() + "…";
    }

    /// <summary>Says which permission is missing when Discord answers 403, or null when it wasn't that.</summary>
    private static Task<string?> Refused(HttpResponseMessage response, string doing, string permission) =>
        Task.FromResult(response.StatusCode == HttpStatusCode.Forbidden
            ? $"The bot isn't allowed to {doing}. Give it the {permission} permission in the server or channel settings."
            : (string?)null);

    private static async Task<int?> ErrorCode(HttpResponseMessage response)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number
                ? code.GetInt32()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
