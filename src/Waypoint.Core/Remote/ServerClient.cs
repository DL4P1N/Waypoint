using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Waypoint.Core.Remote;

/// <summary>Whether the server answered the last time it was asked.</summary>
public enum ServerState
{
    Unknown,
    Online,
    Offline,
    NotPaired,
}

/// <summary>
/// Talks to the server for the PC app. Reads are kept, so when the server can't be reached the pages show what
/// was there at the last refresh. Changes need the server, and say so when it's away. Once it's away, reads stop
/// trying until <see cref="CheckAsync"/> finds it again, so the window never waits on a dead connection.
/// </summary>
public sealed partial class ServerClient
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);

    private readonly HttpClient _http;
    private readonly OrderedDictionary<string, string> _lastReads = [];
    private readonly object _gate = new();

    public const string ClientHeader = "X-Waypoint-Client";

    /// <param name="clientId">This PC's id, kept in its data folder. Without one, a new one is made each time.</param>
    public ServerClient(HttpClient http, string? token, string? clientId = null)
    {
        _http = http;
        ClientId = string.IsNullOrWhiteSpace(clientId) ? Guid.NewGuid().ToString("N") : clientId.Trim();
        http.DefaultRequestHeaders.Remove(ClientHeader);
        http.DefaultRequestHeaders.Add(ClientHeader, ClientId);
        if (http.Timeout > RequestTimeout)
        {
            http.Timeout = RequestTimeout;
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        HasToken = !string.IsNullOrWhiteSpace(token);
    }

    public bool HasToken { get; private set; }

    /// <summary>Uses a new pairing token from now on, and tries the server again with it.</summary>
    public void Pair(string token)
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        HasToken = true;
        SetState(ServerState.Unknown);
    }

    public ServerState State { get; private set; } = ServerState.Unknown;

    /// <summary>When the server last answered, for "showing what was there at 3:15 PM".</summary>
    public DateTime? LastContact { get; private set; }

    public ServerStatus? LastStatus { get; private set; }

    /// <summary>Raised when <see cref="State"/> changes, possibly on a background thread.</summary>
    public event Action? StateChanged;

    private int _stateChanges;

    /// <summary>
    /// How many times <see cref="State"/> has changed. Something learned about the server, such as what it can do,
    /// holds only while this stays the same, since the server may have been changed for another while it was away.
    /// </summary>
    public int StateChanges => Volatile.Read(ref _stateChanges);

    public bool IsOnline => State is ServerState.Online or ServerState.Unknown;

    /// <summary>One line for the status: empty when all is well.</summary>
    public string? Problem => State switch
    {
        ServerState.Offline => LastContact is { } at
            ? $"Can't reach the server. Showing what was there at {at:h:mm tt}, and anything typed is sent when it's back."
            : "Can't reach the server. Anything typed is sent when it's back.",
        ServerState.NotPaired => "The server didn't accept this PC. Pair it again from the tray menu.",
        _ when !HasToken => "This PC isn't paired with the server yet. Use Pair with server in the tray menu.",
        _ => LastStatus?.Warnings.FirstOrDefault(),
    };

    /// <summary>Asks for the server's status. This is the one call that runs while offline, so it's how the app notices it's back.</summary>
    public async Task<ServerStatus?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync("api/status", cancellationToken);
            if (!Settle(response))
            {
                return null;
            }

            LastStatus = await response.Content.ReadFromJsonAsync<ServerStatus>(RemoteJson.Options, cancellationToken);
            return LastStatus;
        }
        catch (Exception exception) when (IsConnectionProblem(exception, cancellationToken))
        {
            MarkOffline();
            return null;
        }
    }

    /// <summary>
    /// Tells the server a notice it couldn't send has been shown here instead. True only for the first to say so,
    /// so it's shown once. False too while the server is away, and it's tried on the next round.
    /// </summary>
    public async Task<bool> ClaimNoticeAsync(long id, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.PostAsync($"api/notices/{id}/delivered", null, cancellationToken);
            return Settle(response) && (await response.Content.ReadFromJsonAsync<FlagValue>(RemoteJson.Options, cancellationToken))?.Value == true;
        }
        catch (Exception exception) when (IsConnectionProblem(exception, cancellationToken))
        {
            MarkOffline();
            return false;
        }
    }

    /// <summary>The notices from the last status that are this PC's to show, each claimed so it's shown just once.</summary>
    public async Task<IReadOnlyList<Data.UndeliveredNotice>> TakeNoticesAsync(CancellationToken cancellationToken = default)
    {
        var taken = new List<Data.UndeliveredNotice>();
        foreach (var notice in LastStatus?.Notices ?? [])
        {
            if (await ClaimNoticeAsync(notice.Id, cancellationToken))
            {
                taken.Add(notice);
            }
        }

        return taken;
    }

    /// <summary>
    /// Sends one capture. False if it should stay in the queue and be tried again. Throws
    /// <see cref="CaptureRefusedException"/> when the server turns it down for good.
    /// </summary>
    public async Task<bool> SendCaptureAsync(CaptureRequest capture, CancellationToken cancellationToken = default)
    {
        using var change = Changing();
        try
        {
            using var response = await _http.PostAsJsonAsync("api/captures", capture, RemoteJson.Options, cancellationToken);

            // Busy or slow is only for now, so it waits for the next try.
            if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
            {
                return false;
            }

            try
            {
                return Settle(response);
            }
            catch (InvalidOperationException exception)
            {
                throw new CaptureRefusedException(exception.Message);
            }
        }
        catch (Exception exception) when (IsConnectionProblem(exception, cancellationToken))
        {
            MarkOffline();
            return false;
        }
    }

    /// <summary>Replies to captures typed on the PC, newer than <paramref name="after"/>.</summary>
    public async Task<IReadOnlyList<ServerReply>> GetRepliesAsync(long after, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync($"api/replies?after={after}", cancellationToken);
            return Settle(response)
                ? await response.Content.ReadFromJsonAsync<List<ServerReply>>(RemoteJson.Options, cancellationToken) ?? []
                : [];
        }
        catch (Exception exception) when (IsConnectionProblem(exception, cancellationToken))
        {
            MarkOffline();
            return [];
        }
    }

    /// <summary>A read that falls back to the last answer while the server is away.</summary>
    public T Get<T>(string path) => Get<T>(path, out _);

    /// <summary>A read that falls back to the last answer. Fresh is false when the answer is that kept one.</summary>
    public T Get<T>(string path, out bool fresh)
    {
        fresh = false;
        Asked(path);
        if (TakeRecent(path) is { } recent)
        {
            fresh = true;
            return Parse<T>(recent);
        }

        if (IsOnline && ReadNow(path) is { } json)
        {
            fresh = true;
            return Parse<T>(json);
        }

        lock (_gate)
        {
            if (_lastReads.TryGetValue(path, out var kept))
            {
                return Parse<T>(kept);
            }
        }

        throw Unavailable();
    }

    /// <summary>
    /// A read for a page. With nothing kept yet and no server, it gives <paramref name="empty"/> so the window can
    /// still open, and the status line says why it's empty.
    /// </summary>
    public T GetOr<T>(string path, T empty) => GetOr(path, empty, out _);

    public T GetOr<T>(string path, T empty, out bool fresh)
    {
        try
        {
            return Get<T>(path, out fresh);
        }
        catch (ServerUnavailableException)
        {
            fresh = false;
            return empty;
        }
    }

    /// <summary>
    /// Changes the answers kept for reads, so a change that went through is what shows while the server is away,
    /// rather than what was read before it. Each kept answer whose path matches is changed.
    /// </summary>
    public void Amend<T>(Func<string, bool> matches, Func<T, T> change)
    {
        lock (_gate)
        {
            foreach (var path in _lastReads.Keys.Where(matches).ToList())
            {
                _readAt.Remove(path);
                Remember(path, JsonSerializer.Serialize(change(Parse<T>(_lastReads[path])), RemoteJson.Options));
            }
        }
    }

    /// <summary>Keeps an answer for a read that hasn't been made, such as a note just made here.</summary>
    public void Keep<T>(string path, T value)
    {
        lock (_gate)
        {
            _readAt.Remove(path);
            Remember(path, JsonSerializer.Serialize(value, RemoteJson.Options));
        }
    }

    /// <summary>How many reads the saved view keeps, the most recent ones, so dated reads don't pile up.</summary>
    public const int MaxSavedReads = 256;

    private string? _savedReadsPath;

    private IFileProtection? _savedReadsProtection;

    /// <summary>
    /// Keeps the reads in a file as well, so every page, Budget as much as To do, opens on its saved view after a
    /// restart with the server away, and the status line says when that view is from. Reads already saved there
    /// are loaded now. With <paramref name="protection"/> the file is kept encrypted with it: a file from before,
    /// still plain, is read once and written again encrypted, and one that can't be read counts as empty.
    /// </summary>
    public void SaveReadsIn(string path, IFileProtection? protection = null)
    {
        lock (_gate)
        {
            _savedReadsPath = path;
            _savedReadsProtection = protection;
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                var stored = File.ReadAllBytes(path);
                var plain = protection is not null && IsPlainJson(stored);
                var text = protection is null || plain ? Encoding.UTF8.GetString(stored) : Encoding.UTF8.GetString(protection.Unprotect(stored));
                if (JsonSerializer.Deserialize<SavedReads>(text, RemoteJson.Options) is { } saved)
                {
                    foreach (var (read, json) in saved.Reads)
                    {
                        _lastReads.TryAdd(read, json);
                    }

                    LastContact ??= saved.Contact;
                }

                if (plain)
                {
                    WriteSavedReads();
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                // A saved view that can't be read is only a missing one. The server fills it in again.
            }
        }
    }

    /// <summary>A file saved before it was encrypted: JSON text, which an encrypted one never starts like.</summary>
    private static bool IsPlainJson(byte[] stored)
    {
        var text = Encoding.UTF8.GetString(stored.AsSpan(0, Math.Min(stored.Length, 64))).TrimStart('﻿').TrimStart();
        return text.StartsWith('{');
    }

    /// <summary>Called under the gate. The newest read goes last, so the oldest are the ones dropped.</summary>
    private void Remember(string path, string json)
    {
        if (_lastReads.TryGetValue(path, out var kept) && kept == json)
        {
            return;
        }

        _lastReads.Remove(path);
        _lastReads[path] = json;
        while (_lastReads.Count > MaxSavedReads)
        {
            _readAt.Remove(_lastReads.GetAt(0).Key);
            _lastReads.RemoveAt(0);
        }

        WriteSavedReads();
    }

    /// <summary>Called under the gate. Written whole to a temporary file first, so a crash never leaves half of it.</summary>
    private void WriteSavedReads()
    {
        if (_savedReadsPath is not { } file)
        {
            return;
        }

        try
        {
            var temp = file + ".tmp";
            var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new SavedReads(LastContact, new Dictionary<string, string>(_lastReads)), RemoteJson.Options));
            File.WriteAllBytes(temp, _savedReadsProtection is { } protection ? protection.Protect(json) : json);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // Only the saved view falls behind. What's on screen and in memory is unaffected.
        }
    }

    private sealed record SavedReads(DateTime? Contact, Dictionary<string, string> Reads);

    /// <summary>A change. It needs the server, so while it's away this throws instead of pretending.</summary>
    public T Send<T>(HttpMethod method, string path, object? body = null)
    {
        var json = SendRaw(method, path, body);
        return Parse<T>(json);
    }

    public void Send(HttpMethod method, string path, object? body = null) => SendRaw(method, path, body);

    /// <summary>A change with a large body, such as a lecture's transcript, sent with the longer wait files get.</summary>
    public T SendLong<T>(HttpMethod method, string path, object? body = null) => Parse<T>(SendRaw(method, path, body, slow: true));

    /// <summary>
    /// This PC's own id, so what it sends can be told apart from what other PCs send, and sending the same again
    /// after a lost answer can be recognized. A new one each time unless it's kept, as server mode does. It goes
    /// on every request.
    /// </summary>
    public string ClientId { get; }

    /// <summary>
    /// A file made on the server, such as the notes export. Like a change, it needs the server. A large one can take
    /// longer than a page read waits, so it uses the long-wait client files use.
    /// </summary>
    public byte[] Download(string path)
    {
        if (!IsOnline)
        {
            throw Unavailable();
        }

        try
        {
            using var request = FileRequest(HttpMethod.Get, path);
            using var response = SendNow(request, FileHttp ?? _http);
            if (!Settle(response))
            {
                throw Unavailable();
            }

            return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (IsConnectionProblem(exception, CancellationToken.None))
        {
            MarkOffline();
            throw Unavailable(exception);
        }
    }

    /// <summary>
    /// A change the server can take a while over, such as a restore or reaching Discord, so it uses the long-wait
    /// client. Running out of time doesn't mean the server is away or that nothing happened, so it throws
    /// <see cref="ServerTimedOutException"/> saying to check <paramref name="check"/> before trying again.
    /// </summary>
    public T SendLong<T>(HttpMethod method, string path, object? body, string check) =>
        Parse<T>(SendRaw(method, path, body, FileHttp ?? _http, check));

    /// <summary>A request on the long-wait client when <paramref name="slow"/>, such as a large transcript.</summary>
    private string SendRaw(HttpMethod method, string path, object? body, bool slow) =>
        SendRaw(method, path, body, slow ? FileHttp : null);

    private string SendRaw(HttpMethod method, string path, object? body, HttpClient? client = null, string? check = null)
    {
        if (!IsOnline)
        {
            throw Unavailable();
        }

        using var change = method == HttpMethod.Get ? null : Changing();
        try
        {
            using var request = client is null ? new HttpRequestMessage(method, path) : FileRequest(method, path);
            if (body is not null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), RemoteJson.Options), Encoding.UTF8, "application/json");
            }

            using var response = SendNow(request, client);
            if (!Settle(response))
            {
                throw Unavailable();
            }

            return ReadText(response);
        }
        catch (Exception exception) when (check is not null && exception is TaskCanceledException or OperationCanceledException)
        {
            throw new ServerTimedOutException($"The server didn't answer in time. It may have gone through. Check {check} before trying again.", exception);
        }
        catch (Exception exception) when (IsConnectionProblem(exception, CancellationToken.None))
        {
            MarkOffline();
            throw Unavailable(exception);
        }
    }

    private ServerUnavailableException Unavailable(Exception? inner = null) =>
        new(State == ServerState.NotPaired
            ? "The server didn't accept this PC, so nothing can change until it's paired again."
            : "Can't reach the server right now, so that can't change yet. Anything typed is saved and sent when it's back.", inner);

    /// <summary>
    /// True if the server answered and did what was asked. A refused token stops everything until paired again.
    /// Any other error from the server throws, since that's a problem with the request rather than the connection.
    /// </summary>
    private bool Settle(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            SetState(ServerState.NotPaired);
            return false;
        }

        if ((int)response.StatusCode >= 500)
        {
            MarkOffline();
            return false;
        }

        LastContact = DateTime.Now;
        SetState(ServerState.Online);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new ServerBusyException("The server is busy with other work right now.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ServerRefusedException((int)response.StatusCode, $"The server turned that down ({(int)response.StatusCode}).");
        }

        return true;
    }

    private void MarkOffline()
    {
        if (State != ServerState.NotPaired)
        {
            SetState(ServerState.Offline);
        }
    }

    private void SetState(ServerState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        Interlocked.Increment(ref _stateChanges);
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Waits for one request. It runs off the calling thread, so waiting from the window's thread can't deadlock,
    /// and the short timeout keeps the wait small.
    /// </summary>
    private HttpResponseMessage SendNow(HttpRequestMessage request, HttpClient? client = null) =>
        Task.Run(async () =>
        {
            var response = await (client ?? _http).SendAsync(request);
            await response.Content.LoadIntoBufferAsync();
            return response;
        }).GetAwaiter().GetResult();

    private static bool IsConnectionProblem(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or IOException
        || (exception is TaskCanceledException or OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static string ReadText(HttpResponseMessage response)
    {
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, RemoteJson.Options)!;
}
