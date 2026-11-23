using System.Globalization;
using System.Text.Json;
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
        var card = DiscordText.Notice(title, text, tone);
        var key = NoticeSendPrefix(thread) + Hash(card);
        var send = _database.BeginDiscordSend(key, thread, _clock());
        var sent = send.MessageId;
        if (sent is null)
        {
            _database.SetDiscordSendCard(key, JsonSerializer.Serialize(new WaitingNotice(title, text, tone)));
        }

        if (sent is null && !send.Fresh)
        {
            var search = await FindUnkeptAsync(discord, thread, send.Nonce, card);
            if (search.Result == SentLookup.Unknown)
            {
                // Whether the earlier try went out can't be told, so it's never sent again from here. It's looked for
                // again on later rounds. Once that's gone on as long as sending is tried, it shows on this PC, once,
                // and its note stays, so a notice found in the post later is still cleared away.
                if (_clock() - send.StartedAt >= AlertRetry.Total && _database.MarkDiscordSendShownHere(key))
                {
                    _host.ShowNotice(title, $"{text}\n\nThis was meant for a post in Discord, which couldn't be checked.");
                }

                return;
            }

            sent = search.MessageId;
            if (sent is null && _database.WasDiscordSendShownHere(key))
            {
                // It was shown on this PC instead, and it isn't in the post, so it's let go.
                _database.FinishDiscordSend(key);
                return;
            }
        }

        if (sent is null)
        {
            (sent, var outcome) = await discord.SendInThreadAsync(thread, card, send.Nonce);
            if (sent is null)
            {
                // Turned away for sure, so there's nothing to look for. A try that failed some other way stays noted,
                // and is looked for and tried again on a later round.
                if (outcome is ThreadChange.Missing or ThreadChange.NotAllowed)
                {
                    _database.FinishDiscordSend(key);
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

    /// <summary>A message of Waypoint's in a post, such as a notice or a part, that isn't one of the post's parts or a notice already kept.</summary>
    private async Task<SentSearch> FindUnkeptAsync(DiscordBridge discord, string thread, string nonce, DiscordCard card)
    {
        var known = (_database.FindDiscordThread(thread)?.Parts.Select(part => part.Message) ?? [])
            .Concat(_database.GetDiscordNotices(thread).Select(notice => notice.Message))
            .Append(thread)
            .ToHashSet(StringComparer.Ordinal);
        var after = known.MaxBy(id => ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0)!;
        return await discord.FindSentAfterAsync(thread, after, nonce, card, known);
    }

    /// <summary>
    /// A notice whose try failed, or couldn't be checked, is looked for and tried again. The round holds the lock a
    /// post's own work can wait for, so it never waits for that post: one being read right now is tried on a later round.
    /// </summary>
    private async Task NoticeAgainAsync(DiscordBridge discord, string thread, WaitingNotice waiting)
    {
        var gate = _postsRunning.GetOrAdd(thread, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0))
        {
            return;
        }

        try
        {
            await NoticeAsync(discord, thread, waiting.Title, waiting.Text, waiting.Tone);
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
            else if (WaitingNoticeFor(send.Key) is { } waiting)
            {
                await NoticeAgainAsync(discord, thread, waiting);
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
