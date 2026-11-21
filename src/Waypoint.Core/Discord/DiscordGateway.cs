using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Waypoint.Core.Background;

namespace Waypoint.Core.Discord;

/// <summary>What to do after one payload from Discord. Each part is optional.</summary>
public sealed record GatewayStep(object? Send = null, TimeSpan? HeartbeatEvery = null, bool Reconnect = false, string? Event = null, JsonElement? Data = null);

/// <summary>
/// The gateway's rules, with no socket, so they can be tested. It remembers the session so a dropped
/// connection picks up where it left off, and Discord replays what was missed in between.
/// </summary>
public sealed class GatewaySession(string token)
{
    public const int Hello = 10;
    public const int HeartbeatAck = 11;
    public const int Heartbeat = 1;
    public const int Identify = 2;
    public const int Resume = 6;
    public const int Reconnect = 7;
    public const int InvalidSession = 9;
    public const int Dispatch = 0;

    /// <summary>
    /// Guilds (for forum posts being made and deleted), server messages (which include messages in posts), reactions,
    /// and what messages say. The last one has to be turned on in the developer portal.
    /// </summary>
    public const int MessageIntents = (1 << 0) | (1 << 9) | (1 << 10) | (1 << 15);

    private const string DefaultUrl = "wss://gateway.discord.gg/?v=10&encoding=json";
    private bool _acked = true;

    public string? SessionId { get; private set; }

    public string? ResumeUrl { get; private set; }

    public long? Sequence { get; private set; }

    /// <summary>Starts with messages included, and drops to buttons only if Discord won't allow that.</summary>
    public int Intents { get; private set; } = MessageIntents;

    public bool CanResume => SessionId is not null && Sequence is not null;

    /// <summary>Where to connect next: the resume address while there's a session to resume.</summary>
    public string Url => CanResume && ResumeUrl is { } url ? $"{url.TrimEnd('/')}/?v=10&encoding=json" : DefaultUrl;

    public GatewayStep Read(JsonElement root)
    {
        if (root.TryGetProperty("s", out var number) && number.ValueKind == JsonValueKind.Number)
        {
            Sequence = number.GetInt64();
        }

        var op = root.TryGetProperty("op", out var code) && code.ValueKind == JsonValueKind.Number ? code.GetInt32() : -1;
        switch (op)
        {
            case Hello:
                _acked = true;
                var interval = TimeSpan.FromMilliseconds(root.GetProperty("d").GetProperty("heartbeat_interval").GetDouble());
                return new GatewayStep(CanResume ? ResumePayload() : IdentifyPayload(), interval);
            case HeartbeatAck:
                _acked = true;
                return new GatewayStep();
            case Heartbeat:
                return new GatewayStep(HeartbeatPayload());
            case Reconnect:
                return new GatewayStep(Reconnect: true);
            case InvalidSession:
                // False means the session can't be picked up again, so the next connection starts fresh.
                if (!(root.TryGetProperty("d", out var resumable) && resumable.ValueKind == JsonValueKind.True))
                {
                    Forget();
                }

                return new GatewayStep(Reconnect: true);
            case Dispatch:
                var type = root.TryGetProperty("t", out var name) ? name.GetString() : null;
                var data = root.TryGetProperty("d", out var body) ? body.Clone() : (JsonElement?)null;
                if (type == "READY" && data is { } ready)
                {
                    SessionId = ready.TryGetProperty("session_id", out var id) ? id.GetString() : null;
                    ResumeUrl = ready.TryGetProperty("resume_gateway_url", out var url) ? url.GetString() : null;
                }

                return new GatewayStep(Event: type, Data: data);
            default:
                return new GatewayStep();
        }
    }

    /// <summary>The next heartbeat, or null when the last one was never answered, which means the connection is dead.</summary>
    public object? NextHeartbeat()
    {
        if (!_acked)
        {
            return null;
        }

        _acked = false;
        return HeartbeatPayload();
    }

    /// <summary>What a close code means. Null to reconnect, or the reason to stop for good.</summary>
    public string? Closed(int? code)
    {
        switch (code)
        {
            case 4004:
                return "Discord rejected the bot token.";
            case 4010 or 4011 or 4012:
                return $"Discord closed the connection ({code}).";
            case 4013 or 4014 when Intents != 0:
                // Message Content Intent is off in the portal. Buttons and commands still work without it.
                Intents = 0;
                Forget();
                return null;
            case 4013 or 4014:
                return "Discord won't allow the bot's connection. Check the bot's intents in the developer portal.";
            case 4007 or 4009:
                Forget();
                return null;
            default:
                return null;
        }
    }

    private void Forget()
    {
        SessionId = null;
        Sequence = null;
        ResumeUrl = null;
    }

    private object HeartbeatPayload() => new { op = Heartbeat, d = Sequence };

    private object IdentifyPayload() => new
    {
        op = Identify,
        d = new { token, intents = Intents, properties = new { os = "windows", browser = "waypoint", device = "waypoint" } },
    };

    private object ResumePayload() => new { op = Resume, d = new { token, session_id = SessionId, seq = Sequence } };
}

/// <summary>
/// The live connection to Discord. It carries button presses, slash commands, and new messages as they're sent,
/// so captures don't wait for a poll. It resumes when dropped, backing off between tries, and stops if the token is rejected.
/// Once it stops, the events already read are finished before <see cref="RunAsync"/> returns.
/// </summary>
/// <param name="arrived">Told about each event as it's read, before it waits its turn, so a press can be answered
/// in time even while a slow one ahead of it is still being handled.</param>
public sealed class DiscordGateway(string token, Func<string, JsonElement, Task> onEvent, Action<Exception>? failed = null, Action<string, JsonElement>? arrived = null)
{
    /// <summary>How long stopping waits for events already read, such as a capture being saved.</summary>
    public static readonly TimeSpan EventDrainLimit = TimeSpan.FromSeconds(5);

    // A code of our own, so Discord keeps the session for a resume. 1000 would end it.
    private const WebSocketCloseStatus KeepSession = (WebSocketCloseStatus)4000;

    private static readonly TimeSpan MinWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(2);

    private readonly GatewaySession _session = new(token);

    // A handler that fails only loses that one event, and the next still runs. The failure is logged.
    private readonly OrderedWork _events = new(failed ?? (_ => { }));

    /// <summary>True from Discord's welcome until the connection drops.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>False when Discord refused the message intents, so new messages come by polling only.</summary>
    public bool HasMessages => _session.Intents != 0;

    public string? LastError { get; private set; }

    /// <summary>What LastError says when Discord never said hello on a new connection.</summary>
    public const string NoHello = "Discord didn't answer when connecting, so the connection is being tried again.";

    /// <summary>How long a new connection waits for Discord's hello before it's dropped and tried again. Tests shorten it.</summary>
    internal TimeSpan HelloWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Where to connect instead of Discord, for tests.</summary>
    internal string? Address { get; init; }

    /// <summary>Stays connected until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await StayConnectedAsync(cancellationToken);
        }
        finally
        {
            // Events already read are captures, edits, and presses, so they're finished rather than dropped.
            await _events.StopAsync(EventDrainLimit);
        }
    }

    private async Task StayConnectedAsync(CancellationToken cancellationToken)
    {
        var wait = MinWait;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            var welcomed = false;

            // Connecting and Discord's hello have a time limit, so a connection that never answers is tried again.
            using var hello = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            hello.CancelAfter(HelloWait);
            try
            {
                await socket.ConnectAsync(new Uri(Address ?? _session.Url), hello.Token);
                welcomed = await ListenAsync(socket, hello.Token, cancellationToken);
            }
            catch (OperationCanceledException) when (hello.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                LastError = NoHello;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LastError = exception.Message;
            }

            IsConnected = false;
            if (_session.Closed((int?)socket.CloseStatus) is { } stop)
            {
                LastError = stop;
                return;
            }

            if (!HasMessages)
            {
                LastError = "Message Content Intent is off for the bot in the Discord developer portal, so captures can't be read. Buttons and commands still work.";
            }

            if (welcomed)
            {
                wait = MinWait;
            }

            // Discord asks for a short random pause before connecting again.
            await Task.Delay(wait + TimeSpan.FromMilliseconds(Random.Shared.Next(1000, 5000)), cancellationToken);
            wait = wait * 2 < MaxWait ? wait * 2 : MaxWait;
        }
    }

    /// <summary>True if the connection got as far as Discord's welcome.</summary>
    /// <param name="hello">Cancelled when the hello is late. Reading waits on it until the hello comes.</param>
    private async Task<bool> ListenAsync(ClientWebSocket socket, CancellationToken hello, CancellationToken cancellationToken)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var send = new SemaphoreSlim(1, 1);
        var welcomed = false;
        var greeted = false;

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                using var document = await ReceiveAsync(socket, greeted ? connection.Token : hello);
                if (document is null)
                {
                    return welcomed;
                }

                var step = _session.Read(document.RootElement);
                if (step.Send is { } payload)
                {
                    await SendAsync(socket, send, payload, connection.Token);
                }

                if (step.HeartbeatEvery is { } interval)
                {
                    greeted = true;
                    _ = BeatAsync(socket, send, interval, connection.Token);
                }

                if (step.Reconnect)
                {
                    await CloseAsync(socket);
                    return welcomed;
                }

                if (step.Event is not { } type || step.Data is not { } data)
                {
                    continue;
                }

                if (type is "READY" or "RESUMED")
                {
                    welcomed = true;
                    IsConnected = true;
                    LastError = HasMessages ? null : LastError;
                }

                Deliver(type, data);
            }

            return welcomed;
        }
        finally
        {
            await connection.CancelAsync();
        }
    }

    /// <summary>Hands one event on: told at once that it arrived, then handled in turn.</summary>
    internal void Deliver(string type, JsonElement data)
    {
        try
        {
            arrived?.Invoke(type, data);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            (failed ?? (_ => { }))(exception);
        }

        // Handled off the read loop, so a slow handler never holds up heartbeats, but one at a time in the
        // order Discord sent them, so a message closed and then reopened is saved that way round.
        _events.Post(() => onEvent(type, data));
    }

    private async Task BeatAsync(ClientWebSocket socket, SemaphoreSlim send, TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            // The first beat is at a random point in the interval, as Discord asks.
            await Task.Delay(interval * Random.Shared.NextDouble(), cancellationToken);
            while (socket.State == WebSocketState.Open)
            {
                if (_session.NextHeartbeat() is not { } beat)
                {
                    // No answer to the last beat, so the connection is dead even if the socket says otherwise.
                    socket.Abort();
                    return;
                }

                await SendAsync(socket, send, beat, cancellationToken);
                await Task.Delay(interval, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // The listening loop reconnects.
        }
    }

    private static async Task CloseAsync(ClientWebSocket socket)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.CloseOutputAsync(KeepSession, "reconnecting", limit.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
            socket.Abort();
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, SemaphoreSlim send, object payload, CancellationToken cancellationToken)
    {
        await send.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            send.Release();
        }
    }

    private static async Task<JsonDocument?> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return JsonDocument.Parse(Encoding.UTF8.GetString(message.ToArray()));
            }
        }
    }
}
