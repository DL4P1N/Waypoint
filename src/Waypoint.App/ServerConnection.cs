using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Waypoint.Core.Recording;
using Waypoint.Core.Remote;

namespace Waypoint.App;

/// <summary>
/// Server mode on this PC: the client, the queue of captures waiting to go, and the To do data that goes through
/// them. The server runs reminders, Discord, and email, so none of that runs here while this is in use.
/// </summary>
internal sealed class ServerConnection
{
    public const string TokenFile = "server.token";
    private const string QueueFile = Waypoint.Core.Remote.PcMode.QueueFileName;
    private const string NotesFile = "server-notes.json";

    /// <summary>
    /// The pages' last reads, so each opens on its saved view after a restart with the server away. Kept encrypted to
    /// the Windows user, like the pairing token.
    /// </summary>
    private const string ReadsFile = "server-reads.json";
    private const string PcIdFile = "server-pc.id";

    public static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(15);

    private readonly ProtectedTokenStore _token;
    private readonly ReplyCursor _replies = new();

    /// <param name="marks">The moments marked while recording a note's lecture, which stay in this PC's recordings.</param>
    public ServerConnection(string address, string dataDirectory, Func<long, IReadOnlyList<RecordingMark>>? marks = null)
    {
        _token = new ProtectedTokenStore(Path.Combine(dataDirectory, TokenFile));
        var baseAddress = new Uri(address.TrimEnd('/') + "/");
        Client = new ServerClient(new HttpClient { BaseAddress = baseAddress }, _token.Load(), PcId(dataDirectory))
        {
            FileHttp = new HttpClient { BaseAddress = baseAddress, Timeout = ServerClient.FileTimeout },
        };
        Client.SaveReadsIn(Path.Combine(dataDirectory, ReadsFile), UserProtection.Instance);
        Queue = new CaptureQueue(Path.Combine(dataDirectory, QueueFile));
        Data = new ServerToDoData(Client);
        Budget = new ServerBudgetData(Client);
        Sessions = new ServerSessionData(Client);
        Lists = new ServerListsData(Client);
        History = new ServerHistoryData(Client);
        Settings = new ServerSettingsData(Client);
        Feedback = new ServerFeedbackData(Client);
        Backups = new ServerBackups(Client);
        Notes = new ServerNotesData(Client, new OfflineNotes(Path.Combine(dataDirectory, NotesFile)), marks);
    }

    public ServerClient Client { get; }

    public CaptureQueue Queue { get; }

    public IToDoData Data { get; }

    public ServerBudgetData Budget { get; }

    public ServerSessionData Sessions { get; }
    public IListsData Lists { get; }

    public IHistoryData History { get; }

    public ServerSettingsData Settings { get; }

    public IFeedbackData Feedback { get; }

    public ServerBackups Backups { get; }
    /// <summary>The Notes page's data. Typing in a note while the server is away waits in server-notes.json.</summary>
    public ServerNotesData Notes { get; }

    /// <summary>Captures still on this PC plus those the server hasn't sorted yet.</summary>
    public int Pending => Queue.Count + (Client.IsOnline ? Client.LastStatus?.PendingCaptures ?? 0 : 0);

    /// <summary>
    /// This PC's id for the server, made the first time and kept in the data folder. The server keeps what this
    /// PC's notes folder had by it, apart from other PCs and trial runs, which have data folders of their own. The
    /// data folder's place is part of it, so a copy of the data folder used somewhere else, such as for a trial
    /// run, is a PC of its own too.
    /// </summary>
    internal static string PcId(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, PcIdFile);
        string? id = null;
        try
        {
            if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } kept)
            {
                id = kept;
            }
        }
        catch (IOException)
        {
            // Made again below. A new id only means the notes folder's files are matched up again from scratch.
        }

        if (id is null)
        {
            id = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(path, id);
        }

        var place = Waypoint.Core.Notes.NoteFiles.Hash(Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant());
        return $"{id}-{place[..12]}";
    }

    /// <summary>This PC's id if it was made already, or null, so a Local mode PC doesn't make one just for idle reports.</summary>
    internal static string? ExistingPcId(string dataDirectory) =>
        File.Exists(Path.Combine(dataDirectory, PcIdFile)) ? PcId(dataDirectory) : null;

    /// <summary>Keeps a new pairing token, encrypted to this Windows user, and uses it straight away.</summary>
    public void Pair(string token)
    {
        _token.Save(token.Trim());
        Client.Pair(token);
    }

    /// <summary>Saved first and sent after, so it's kept even if the server is away or the app closes.</summary>
    public void Capture(string text, string? fileName = null) => Queue.Add(text, DateTimeOffset.Now, fileName);

    /// <summary>
    /// Checks the server, sends anything waiting, and returns replies to what was typed here. Replies from before
    /// this app started aren't shown again.
    /// </summary>
    public async Task<IReadOnlyList<ServerReply>> SyncAsync()
    {
        if (await Client.CheckAsync() is not { } status)
        {
            return [];
        }

        var after = _replies.After(status);
        await Queue.SendAsync(capture => Client.SendCaptureAsync(capture));
        var replies = await Client.GetRepliesAsync(after);
        _replies.Saw(replies);
        return replies;
    }
}
