using System.Globalization;
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
    /// cleared away, and the same notice sent again finds the one already there instead of adding a second.
    /// </remarks>
    private async Task NoticeAsync(DiscordBridge discord, string thread, DiscordCard card)
    {
        var key = NoticeSendPrefix(thread) + Hash(card);
        var send = _database.BeginDiscordSend(key, thread, _clock());
        var sent = send.MessageId;
        if (sent is null && !send.Fresh)
        {
            sent = await FindNoticeAsync(discord, thread, send.Nonce, card);
        }

        if (sent is null)
        {
            (sent, var outcome) = await discord.SendInThreadAsync(thread, card, send.Nonce);
            if (sent is null)
            {
                // Turned away for sure, so there's nothing to look for. A try that failed some other way stays noted.
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

    private static string NoticeSendPrefix(string thread) => $"notice:{thread}:";

    /// <summary>A notice is kept to be cleared away later, and its note goes, in one write.</summary>
    private void KeepNotice(string thread, string key, string message, DateTime sentAt) =>
        _database.InOneWrite(() =>
        {
            _database.AddDiscordNotice(thread, message, sentAt);
            _database.FinishDiscordSend(key);
            return 0;
        });

    /// <summary>A notice of Waypoint's in a post that isn't one of the post's parts or a notice already kept.</summary>
    private async Task<string?> FindNoticeAsync(DiscordBridge discord, string thread, string nonce, DiscordCard card)
    {
        var known = (_database.FindDiscordThread(thread)?.Parts.Select(part => part.Message) ?? [])
            .Concat(_database.GetDiscordNotices(thread).Select(notice => notice.Message))
            .Append(thread)
            .ToHashSet(StringComparer.Ordinal);
        var after = known.MaxBy(id => ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0)!;
        return (await discord.FindSentAfterAsync(thread, after, nonce, card, known)).MessageId;
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
