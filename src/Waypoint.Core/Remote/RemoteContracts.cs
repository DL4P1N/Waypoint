using System.Text.Json;
using System.Text.Json.Serialization;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Remote;

/// <summary>How the PC app and the server write to each other. Both ends use these options.</summary>
public static class RemoteJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>Where the PC app keeps its data: on this PC as before, or on the server.</summary>
public enum DataMode
{
    Local,
    Server,
}

/// <summary>
/// The server part of settings.json on the PC. Mode stays Local until it's changed by hand, so nothing moves before
/// the server is ready, and changing it back undoes the switch. The pairing token is kept apart, encrypted.
/// </summary>
public sealed record ServerSettings(string? Address = null, DataMode Mode = DataMode.Local);

/// <summary>
/// Something typed on the PC. Id is made on the PC, so a capture sent again after a dropped reply is only kept once.
/// </summary>
public sealed record CaptureRequest(string Id, string Text, DateTimeOffset WrittenAt, string? FileName = null);

public sealed record CaptureAccepted(bool Duplicate);

/// <summary>
/// What the PC app shows about the server: whether it's working, and anything it couldn't do. Boot is new each
/// time the server starts, which is when LastReply counts from 1 again. Notices are notifications that couldn't
/// reach Discord, for the PC to show instead.
/// </summary>
public sealed record ServerStatus(
    DateTime ServerTime,
    string TimeZone,
    int PendingCaptures,
    string? SortingError,
    string? CalendarError,
    string? NudgeError,
    string? ConnectionError,
    IReadOnlyList<ConnectionStatus> Connections,
    IReadOnlyList<string> Warnings,
    long LastReply,
    string? Boot = null,
    IReadOnlyList<Data.UndeliveredNotice>? Notices = null);

/// <summary>A reply to something typed on the PC, kept on the server until the PC asks for it.</summary>
public sealed record ServerReply(long Seq, string Text, IReadOnlyList<StoredItem> Made, bool StartedSession, bool OpensWindow);

public sealed record DoneRequest(StoredItem Item, bool Done);

public sealed record ItemRequest(StoredItem Item);

/// <summary>Marks a repeating reminder as a dose, or not.</summary>
public sealed record DoseRequest(StoredItem Item, bool Dose);

public sealed record RescheduleRequest(StoredItem Item, DateOnly Date, TimeOnly? Time);

public sealed record AssignRequest(StoredItem Item, long? FilterId);

public sealed record NameRequest(string Name);

public sealed record OrderRequest(IReadOnlyList<long> Ids);

public sealed record PreferenceValue(string? Value);

public sealed record FlagValue(bool Value);

public sealed record TextValue(string? Value);

/// <summary>A saved spot, or null when there's none.</summary>
public sealed record SpotValue(Handoff? Spot);

/// <summary>The money lines at the top of To do.</summary>
public sealed record SpendSummary(long? SafeToSpend, string? Attention, string? SafeToSpendReason = null);

public sealed record StartRequest(string? TaskRef, string Method, string? FirstAction = null);

public sealed record SmallerRequest(StoredItem Item, string Method);

public sealed record SpotRequest(string? Project, string? Stopped, string? Next, IReadOnlyList<string>? Links, IReadOnlyList<string>? Thoughts);

public sealed record RoutineMarkRequest(DateOnly Morning, string Step, bool Done);

public sealed record WaitingRequest(string Who, string? Topic, DateOnly? FollowUp, string? Email);

public sealed record MarkWaitingRequest(StoredItem Item, string Who, DateOnly? FollowUp, string? Email);

public sealed record StillWaitingRequest(long? ItemId, DateOnly? Until);

/// <summary>Items with their placements, which the dictionary type can't carry as JSON keys of its own.</summary>
public sealed record PlacementEntry(long ItemId, Data.ItemPlacement Placement);

/// <summary>The server couldn't be reached, or refused the pairing token. What's shown stays as it last was.</summary>
public sealed class ServerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A long change, such as a restore, ran out of time while the server was still working on it. It may have gone
/// through, so the message says what to check before trying again.
/// </summary>
public sealed class ServerTimedOutException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The server is doing as much of this as it will at once, so it said to try again later (429).</summary>
public sealed class ServerBusyException(string message) : InvalidOperationException(message);

/// <summary>
/// The server answered and turned the request down, such as a note too big to take (413). Sending the same again
/// gets the same answer, except a request that timed out there (408), which may go through later.
/// </summary>
public sealed class ServerRefusedException(int status, string message) : InvalidOperationException(message)
{
    public int Status { get; } = status;

    public bool IsLasting => Status != 408;
}

/// <summary>The server turned a capture down in a way sending it again won't change, such as a bad request.</summary>
public sealed class CaptureRefusedException(string message) : InvalidOperationException(message);
