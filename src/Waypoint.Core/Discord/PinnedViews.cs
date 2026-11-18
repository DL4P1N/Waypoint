using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Waypoint.Core.Data;

namespace Waypoint.Core.Discord;

/// <summary>One view as it should look now, and the channel it's pinned in.</summary>
public sealed record PinnedView(string Key, string Channel, DiscordCard Card);

/// <summary>
/// Keeps each pinned view as one message that's edited in place, never sent again. A view is only edited when
/// what it shows has changed, so an unchanged day costs no calls at all. A view deleted by hand is sent again,
/// and one that's no longer wanted, such as a removed list, has its message taken away.
/// </summary>
public sealed class PinnedViews(WaypointDatabase database)
{
    /// <summary>A view turned off with /unpin, which also keeps settings.json from turning it back on.</summary>
    public const string Off = "off";

    public const string ListPrefix = "list:";

    private const string StateKey = "discord_pinned_views";
    private const string ChannelKeyPrefix = "discord_view_";

    private readonly object _lock = new();
    private Dictionary<string, Placed>? _placed;

    /// <summary>Refused is the content Discord last turned down, so it isn't sent again every round.</summary>
    private sealed record Placed(string Channel, string Message, string Hash, string? Refused = null);

    /// <summary>Where a view goes: the channel /pinhere chose, or else the one in settings.json, or nowhere.</summary>
    public static string? ChannelFor(WaypointDatabase database, DiscordSettings settings, string view)
    {
        var saved = database.GetPreference(ChannelKeyPrefix + view);
        if (saved == Off)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(saved))
        {
            return saved;
        }

        return view switch
        {
            DiscordViews.ToDo or DiscordViews.Waiting => settings.ToDoChannelId,
            DiscordViews.Lists => settings.ListsChannelId,
            DiscordViews.Notes => settings.NotesChannelId,
            DiscordViews.History => settings.HistoryChannelId,
            _ => null,
        };
    }

    /// <summary>Pins a view in a channel, or turns it off when the channel is null.</summary>
    public static void Choose(WaypointDatabase database, string view, string? channel) =>
        database.SetPreference(ChannelKeyPrefix + view, channel ?? Off);

    /// <summary>Which view a message is, so a menu used on it knows what to change.</summary>
    public string? ViewFor(string messageId)
    {
        lock (_lock)
        {
            return Load().FirstOrDefault(pair => pair.Value.Message == messageId).Key;
        }
    }

    /// <summary>Notes that a view was just drawn some other way, such as in answer to its menu, so it isn't edited again for nothing.</summary>
    public void Drawn(string key, DiscordCard card)
    {
        lock (_lock)
        {
            if (Load().TryGetValue(key, out var placed))
            {
                _placed![key] = placed with { Hash = Hash(card) };
                Save();
            }
        }
    }

    /// <summary>
    /// Brings every view up to date, one call at a time so rate limits are waited out in order. A call that fails is
    /// left for the next round, since the stored state only changes once Discord has taken the change.
    /// </summary>
    public async Task SyncAsync(DiscordBridge bridge, IReadOnlyList<PinnedView> wanted)
    {
        foreach (var view in wanted)
        {
            var hash = Hash(view.Card);
            Placed? placed;
            lock (_lock)
            {
                placed = Load().GetValueOrDefault(view.Key);
            }

            if (placed is not null && placed.Channel == view.Channel)
            {
                if (placed.Hash == hash || placed.Refused == hash)
                {
                    continue;
                }

                var edited = await bridge.EditAsync(view.Channel, placed.Message, view.Card);
                if (edited == EditOutcome.Edited)
                {
                    Store(view.Key, placed with { Hash = hash, Refused = null });
                    continue;
                }

                if (edited == EditOutcome.Refused)
                {
                    Store(view.Key, placed with { Refused = hash });
                    continue;
                }

                if (edited == EditOutcome.Failed)
                {
                    continue;
                }
            }
            else if (placed is not null)
            {
                // Moved to another channel, so the old message goes.
                await bridge.DeleteAsync(placed.Channel, placed.Message);
            }

            if (await PostOnceAsync(bridge, view, hash) is { } message)
            {
                await bridge.PinAsync(view.Channel, message);
            }
            else if (placed is not null)
            {
                Forget(view.Key);
            }
        }

        List<KeyValuePair<string, Placed>> unwanted;
        lock (_lock)
        {
            var keys = wanted.Select(view => view.Key).ToHashSet(StringComparer.Ordinal);
            unwanted = Load().Where(pair => !keys.Contains(pair.Key)).ToList();
        }

        foreach (var (key, placed) in unwanted)
        {
            if (await bridge.DeleteAsync(placed.Channel, placed.Message))
            {
                Forget(key);
            }
        }
    }

    /// <summary>
    /// Posts a view and keeps where it is. It's noted before it goes, with Discord's nonce, so a crash before it's
    /// kept finds the view already posted instead of posting a second. One found that way may show something older,
    /// so it's kept as needing an edit. Null when it couldn't be posted.
    /// </summary>
    private async Task<string?> PostOnceAsync(DiscordBridge bridge, PinnedView view, string hash)
    {
        var key = $"view:{view.Key}:{view.Channel}";
        var send = database.BeginDiscordSend(key, view.Channel, bridge.Clock());
        var message = send.MessageId;
        var shown = hash;
        if (message is null && !send.Fresh)
        {
            var known = database.GetTrackedMessages(key);
            known.UnionWith(PlacedMessages());
            message = (await bridge.FindSentAsync(view.Channel, send.StartedAt, send.Nonce, view.Card, sameTitleOnly: true, known: known)).MessageId;
        }

        if (message is null)
        {
            message = (await bridge.DeliverOnceAsync(view.Channel, view.Card, send.Nonce)).MessageId;
            if (message is null)
            {
                return null;
            }

            database.SetDiscordSendMessage(key, message);
        }
        else
        {
            shown = "";
        }

        database.InOneWrite(() =>
        {
            Store(view.Key, new Placed(view.Channel, message, shown));
            database.FinishDiscordSend(key);
            return 0;
        });
        return message;
    }

    /// <summary>Short and stable across restarts, unlike string hash codes.</summary>
    private static string Hash(DiscordCard card) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(card))))[..16];

    private void Store(string key, Placed placed)
    {
        lock (_lock)
        {
            Load()[key] = placed;
            Save();
        }
    }

    /// <summary>The messages of the views already placed, so none is taken for another.</summary>
    public IReadOnlyList<string> PlacedMessages()
    {
        lock (_lock)
        {
            return Load().Values.Select(placed => placed.Message).ToList();
        }
    }

    private void Forget(string key)
    {
        lock (_lock)
        {
            if (Load().Remove(key))
            {
                Save();
            }
        }
    }

    private Dictionary<string, Placed> Load()
    {
        if (_placed is not null)
        {
            return _placed;
        }

        try
        {
            _placed = database.GetPreference(StateKey) is { Length: > 0 } json
                ? JsonSerializer.Deserialize<Dictionary<string, Placed>>(json) ?? []
                : [];
        }
        catch (JsonException)
        {
            _placed = [];
        }

        return _placed;
    }

    private void Save() => database.SetPreference(StateKey, JsonSerializer.Serialize(_placed));
}
