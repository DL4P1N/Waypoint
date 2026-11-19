using System.Net;
using System.Text.Json;

namespace Waypoint.Core.Discord;

/// <summary>
/// Catching up by polling: reactions and replies on alerts, the owner's new messages in the capture and notes
/// channels, and messages handed over again after their work was cut off.
/// </summary>
public sealed partial class DiscordBridge
{
    /// <summary>One of the owner's messages in the notes channel, such as a file to make notes from.</summary>
    public Action<DiscordMessage>? NotesSent { get; set; }

    /// <summary>
    /// Reads reactions and the owner's replies in the alerts channel. Reactions are read from the
    /// recent messages each time, and typed replies only once, even if the live connection already took them.
    /// </summary>
    public async Task<IReadOnlyList<AlertAction>> ReadAlertActionsAsync(CancellationToken cancellationToken = default)
    {
        var actions = new List<AlertAction>();
        if (settings.AlertsChannelId is not { } channel)
        {
            return actions;
        }

        try
        {
            using var response = await SendWithLimitsAsync(() => NewRequest(HttpMethod.Get, $"{ApiBase}/channels/{channel}/messages?limit={MaxAlertsRead}"), cancellationToken);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var messages = document.RootElement.EnumerateArray()
                .OrderBy(message => ulong.Parse(message.GetProperty("id").GetString()!))
                .ToList();

            var last = database.GetPreference(LastAlertKey);
            foreach (var message in messages)
            {
                var id = message.GetProperty("id").GetString()!;
                actions.AddRange(Reactions(message).Select(kind => new AlertAction(id, kind)));

                if (last is not null && ulong.Parse(id) > ulong.Parse(last) && IsFromOwner(message) && ReplyKind(Text(message, "content")) is { } kind && FirstTime(id, channel))
                {
                    var target = message.TryGetProperty("message_reference", out var reference) ? Text(reference, "message_id") : null;
                    actions.Add(new AlertAction(target, kind, id));
                }
            }

            if (messages.Count > 0)
            {
                database.SetPreference(LastAlertKey, messages[^1].GetProperty("id").GetString()!);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LastError = exception.Message;
        }

        return actions;
    }

    private static IEnumerable<AlertActionKind> Reactions(JsonElement message)
    {
        if (!message.TryGetProperty("reactions", out var reactions) || reactions.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var reaction in reactions.EnumerateArray())
        {
            var name = reaction.TryGetProperty("emoji", out var emoji) ? Text(emoji, "name") : null;
            if (name is not null && DoneEmoji.Contains(name))
            {
                yield return AlertActionKind.Done;
            }
            else if (name is not null && SnoozeEmoji.Contains(name))
            {
                yield return AlertActionKind.Snooze;
            }
        }
    }

    private static AlertActionKind? ReplyKind(string? text) => text?.Trim().TrimEnd('.', '!').ToLowerInvariant() switch
    {
        "done" or "finished" or "complete" or "completed" or "did it" => AlertActionKind.Done,
        "snooze" or "later" => AlertActionKind.Snooze,
        _ => null,
    };

    /// <summary>
    /// Hands over the owner's new messages, oldest first, from the capture channel and then the notes channel, so
    /// one sent while Waypoint was off is read once it's back. The first run in each only marks where to start.
    /// Messages the live connection already handed over are skipped. A poll still running keeps the next one from
    /// reading the same messages. Messages handed over before whose work was cut off are read again first, from
    /// whichever channel they came in.
    /// </summary>
    public async Task PollAsync(Action<DiscordMessage>? onMessage = null, CancellationToken cancellationToken = default)
    {
        onMessage ??= Captured;
        if (settings.CaptureChannelId is not { } channel || !await _polling.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            await RetryUnfinishedAsync(onMessage, cancellationToken);
            await CatchUpAsync(channel, LastMessageKey, onMessage, cancellationToken);
            // A notes channel that's also the capture or alerts channel is read there already.
            if (PinnedViews.ChannelFor(database, settings, DiscordViews.Notes) is { } notes && notes != channel && notes != settings.AlertsChannelId)
            {
                await CatchUpAsync(notes, LastNotesMessageKey, NotesSent, cancellationToken, placeHasChannel: true);
            }

            LastError = null;
            LastPolled = DateTimeOffset.Now;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LastError = exception.Message;
        }
        finally
        {
            _polling.Release();
        }
    }

    /// <summary>
    /// Reads one channel's messages since the last one read there, and hands over each of the owner's that hasn't
    /// been claimed yet. The place moves on past each, so it's read once however often this runs. With
    /// <paramref name="placeHasChannel"/>, the place is kept with its channel, and one from another channel, or none,
    /// is a first run.
    /// </summary>
    private async Task CatchUpAsync(string channel, string lastKey, Action<DiscordMessage>? handOver, CancellationToken cancellationToken, bool placeHasChannel = false)
    {
        var saved = database.GetPreference(lastKey);
        var last = !placeHasChannel ? saved
            : saved is not null && saved.StartsWith(channel + ":", StringComparison.Ordinal) ? saved[(channel.Length + 1)..]
            : null;
        string Place(string id) => placeHasChannel ? $"{channel}:{id}" : id;
        var query = last is null ? "limit=1" : $"limit={MaxFetched}&after={last}";

        using var response = await SendWithLimitsAsync(() => NewRequest(HttpMethod.Get, $"{ApiBase}/channels/{channel}/messages?{query}"), cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var messages = document.RootElement.EnumerateArray()
            .OrderBy(message => ulong.Parse(message.GetProperty("id").GetString()!))
            .ToList();

        // An empty channel still marks the place, or its first message would only be marked too.
        if (last is null && messages.Count == 0)
        {
            database.SetPreference(lastKey, Place("0"));
        }

        foreach (var message in messages)
        {
            var id = message.GetProperty("id").GetString()!;
            if (last is not null && IsFromOwner(message) && ReadMessage(message) is { } read && FirstTime(id, channel))
            {
                handOver?.Invoke(read);
            }

            database.SetPreference(lastKey, Place(id));
        }
    }

    /// <summary>
    /// True the first time a message is seen, from either the live connection or a poll, and again once its work
    /// has gone <see cref="UnfinishedAfter"/> without being finished. The claim is saved before it's handed over.
    /// </summary>
    private bool FirstTime(string id, string? channel) => database.ClaimDiscordMessage(id, channel ?? "", Clock(), UnfinishedAfter);

    /// <summary>
    /// Reads again each message whose work was cut off and hands it over the same way as when it came in. One
    /// deleted since, or that no longer asks for anything, such as after an edit, has nothing left to do. One that
    /// can't be read right now counts as a try and is tried on a later poll, so it's given up on in the end, and
    /// the owner is told.
    /// </summary>
    private async Task RetryUnfinishedAsync(Action<DiscordMessage>? onCapture, CancellationToken cancellationToken)
    {
        foreach (var (id, channel) in database.GetUnfinishedDiscordMessages(Clock(), UnfinishedAfter))
        {
            if (StillWorking?.Invoke(id) == true)
            {
                database.KeepDiscordClaim(id, Clock());
                continue;
            }

            try
            {
                using var response = await SendWithLimitsAsync(() => NewRequest(HttpMethod.Get, $"{ApiBase}/channels/{channel}/messages/{id}"), cancellationToken);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    database.FinishDiscordMessage(id, Clock());
                    continue;
                }

                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (document.RootElement.ValueKind != JsonValueKind.Object || Text(document.RootElement, "id") != id)
                {
                    database.CountDiscordTry(id, Clock());
                    continue;
                }

                // The claim knows the channel, which a message read back on its own may leave out.
                var message = Text(document.RootElement, "channel_id") is null ? WithChannel(document.RootElement, channel) : document.RootElement.Clone();
                if (await HandleMessageAsync(message, onCapture) is null)
                {
                    database.FinishDiscordMessage(id, Clock());
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                LastError = exception.Message;
                database.CountDiscordTry(id, Clock());
            }
        }

        // Tried as many times as it may be, so the owner is told under it, once, rather than it going quiet. It
        // stays given up on and untold until the notice goes through, so one lost to an outage is sent again.
        foreach (var (id, channel) in database.GetGivenUpDiscordMessages(Clock(), UnfinishedAfter))
        {
            // Its own nonce, so a try whose answer was lost and is sent again soon after isn't posted twice.
            if ((await TrySendAsync(channel, DiscordText.Notice("Not finished", GaveUp, CardTone.Attention), id, nonce: "g" + id)).Sent)
            {
                database.FinishGivenUpDiscordMessage(id, Clock());
            }
        }
    }

    /// <summary>Said under a message whose work never finished however many times it was tried.</summary>
    public const string GaveUp = "Couldn't finish this, so send it again.";
}
