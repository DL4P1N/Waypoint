using System.Globalization;
using System.Text.Json;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Background;

/// <summary>Waypoint's own notices in a post, noted before they're sent and cleared away later.</summary>
public sealed partial class WaypointWorker
{
    /// <summary>
    /// A notice in a post, such as a file that couldn't be added. It's cleared away after an hour, or sooner when a new
    /// part of the note has to go under it, so the post stays the note and not a trail of replies.
    /// </summary>
    /// <remarks>
    /// It's noted before it's sent, with Discord's nonce, so a crash before it's kept doesn't leave one that's never
    /// cleared away, and the same notice sent again finds the one already there instead of adding a second. What it
    /// says is kept with the note, so one whose try can't be checked is looked for again on a later round.
    /// </remarks>
    private async Task NoticeAsync(DiscordBridge discord, string thread, string title, string text, CardTone tone)
    {
        var notice = new WaitingNotice(title, text, tone);
        var key = NoticeSendPrefix(thread) + Hash(DiscordText.Notice(title, text, tone));
        var send = _database.BeginDiscordSend(key, thread, _clock());
        if (send.MessageId is null)
        {
            _database.SetDiscordSendCard(key, JsonSerializer.Serialize(notice));
        }

        await TryNoticeAsync(discord, thread, send, notice, raised: true);
    }

    /// <summary>
    /// A notice that hasn't gone out, whether or not it can be told, shows on this PC once it has waited as long as
    /// sending is tried. Its note stays, so a notice found in the post later is still cleared away.
    /// </summary>
    private void ShowHereOnceWaitedLong(DiscordSend send, WaitingNotice notice)
    {
        if (_clock() - send.StartedAt >= AlertRetry.Total && _database.MarkDiscordSendShownHere(send.Key))
        {
            _host.ShowNotice(notice.Title, $"{notice.Text}\n\nThis was meant for a post in Discord, which couldn't be checked.");
        }
    }

    /// <summary>
    /// One try at a notice, under the note it was given, with that note's key and nonce, so a try again later always
    /// settles the same note and never starts another, even when the notice is now laid out differently.
    /// </summary>
    /// <param name="raised">Raised just now, rather than tried again by a round.</param>
    private async Task TryNoticeAsync(DiscordBridge discord, string thread, DiscordSend send, WaitingNotice notice, bool raised = false)
    {
        var key = send.Key;
        var card = DiscordText.Notice(notice.Title, notice.Text, notice.Tone);
        var sent = send.MessageId;
        if (sent is null && !send.Fresh)
        {
            var search = await FindUnkeptAsync(discord, thread, send.Nonce, card);
            if (search.Result == SentLookup.Unknown)
            {
                // Whether the earlier try went out can't be told, so it's never sent again from here. It's looked for
                // again on later rounds, and shows on this PC once it has waited long enough.
                ShowHereOnceWaitedLong(send, notice);
                return;
            }

            sent = search.MessageId;
            if (sent is null && _database.WasDiscordSendShownHere(key))
            {
                // It was shown on this PC instead, and it isn't in the post. Tried again by a round, it's let go. The
                // same notice raised again is wanted in the post too, so it goes out, and could show on the PC again.
                if (!raised)
                {
                    _database.FinishDiscordSend(key);
                    return;
                }

                _database.ResetDiscordSendShownHere(key);
            }
        }

        if (sent is null)
        {
            (sent, var outcome) = await discord.SendInThreadAsync(thread, card, send.Nonce);
            if (sent is null)
            {
                // Turned away for sure, so there's nothing to look for. A try that failed some other way stays noted,
                // and is looked for and tried again on a later round, and shows on this PC once it has waited long enough.
                if (outcome is ThreadChange.Missing or ThreadChange.NotAllowed)
                {
                    _database.FinishDiscordSend(key);
                }
                else
                {
                    ShowHereOnceWaitedLong(send, notice);
                }

                return;
            }

            _database.SetDiscordSendMessage(key, sent);
        }

        KeepNotice(thread, key, sent, _clock());
    }

    /// <summary>What a notice says, kept with its note so it can be tried again.</summary>
    private sealed record WaitingNotice(string Title, string Text, CardTone Tone);

    /// <summary>The notice a note was kept for, or null when it was noted without one, such as by an older version.</summary>
    private WaitingNotice? WaitingNoticeFor(string key)
    {
        try
        {
            return _database.GetDiscordSendCard(key) is { } kept ? JsonSerializer.Deserialize<WaitingNotice>(kept) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NoticeSendPrefix(string thread) => $"notice:{thread}:";

    /// <summary>A notice is kept to be cleared away later, and its note goes, in one write.</summary>
    private void KeepNotice(string thread, string key, string message, DateTime sentAt) =>
        _database.InOneWrite(write =>
        {
            write.AddDiscordNotice(thread, message, sentAt);
            write.FinishDiscordSend(key);
        });

    /// <summary>
    /// A message of Waypoint's in a post, such as a notice or a part, that isn't one of the post's parts or a notice
    /// already kept. With <paramref name="button"/>, one not found by its words is looked for by a button whose action
    /// starts that way, for a part whose words may have changed since it was sent.
    /// </summary>
    private async Task<SentSearch> FindUnkeptAsync(DiscordBridge discord, string thread, string nonce, DiscordCard card, string? button = null)
    {
        // Read from the post's last part, not its last notice, since a notice can come after a part sent unheard.
        var parts = (_database.FindDiscordThread(thread)?.Parts.Select(part => part.Message) ?? []).Append(thread).ToList();
        var after = parts.MaxBy(id => ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0)!;
        var known = parts.Concat(_database.GetDiscordNotices(thread).Select(notice => notice.Message)).ToHashSet(StringComparer.Ordinal);
        return await discord.FindSentAfterAsync(thread, after, nonce, card, known, actionPrefix: button);
    }

    /// <summary>
    /// A notice whose try failed, or couldn't be checked, is looked for and tried again. The round holds the lock a
    /// post's own work can wait for, so it never waits for that post: one being read right now is tried on a later round.
    /// A round tries them only once the post's parts are all in place, so a notice never goes between them. Each waits
    /// longer between tries, from a minute up to an hour, so one that keeps failing isn't tried every round for a day.
    /// </summary>
    private async Task RetryNoticesAsync(DiscordBridge discord, string thread)
    {
        foreach (var send in _database.GetDiscordSends(NoticeSendPrefix(thread)))
        {
            if (send.MessageId is not null || _clock() - send.StartedAt >= NoticeGiveUp || WaitingNoticeFor(send.Key) is not { } waiting)
            {
                continue;
            }

            if (_noticeRetries.TryGetValue(send.Key, out var before) && _clock() < before.Next)
            {
                continue;
            }

            await NoticeAgainAsync(discord, thread, send, waiting);
            if (_database.GetDiscordSend(send.Key) is null)
            {
                _noticeRetries.TryRemove(send.Key, out _);
            }
            else
            {
                var wait = before.Wait == TimeSpan.Zero ? NoticeRetryFirst : TimeSpan.FromTicks(Math.Min(before.Wait.Ticks * 2, NoticeRetryLongest.Ticks));
                _noticeRetries[send.Key] = (_clock() + wait, wait);
            }
        }
    }

    /// <summary>When each waiting notice is next tried, and how long it waited last. Kept only while the app runs.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Next, TimeSpan Wait)> _noticeRetries = new(StringComparer.Ordinal);

    private static readonly TimeSpan NoticeRetryFirst = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan NoticeRetryLongest = TimeSpan.FromHours(1);

    /// <summary>One waiting notice tried again, unless the post is busy.</summary>
    private async Task NoticeAgainAsync(DiscordBridge discord, string thread, DiscordSend send, WaitingNotice waiting)
    {
        var gate = _postsRunning.GetOrAdd(thread, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0))
        {
            return;
        }

        try
        {
            await TryNoticeAsync(discord, thread, send, waiting);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ClearNoticesAsync(DiscordBridge discord, string thread, bool all)
    {
        // A notice sent just before a crash, before it was kept, is kept now so it's cleared away like the rest.
        foreach (var send in _database.GetDiscordSends(NoticeSendPrefix(thread)))
        {
            if (send.MessageId is { } sent)
            {
                KeepNotice(thread, send.Key, sent, send.StartedAt);
            }
            else if (_clock() - send.StartedAt >= NoticeGiveUp)
            {
                _database.FinishDiscordSend(send.Key);
            }
        }

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
}
