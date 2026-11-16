using Waypoint.Core.Data;

namespace Waypoint.Core.Calendar;

/// <summary>Keeps stored events matched to the feed. A failed sync leaves them as they were.</summary>
public sealed class CalendarFeedSync(HttpClient http, WaypointDatabase database, string feedUrl)
{
    /// <summary>Raised after a sync finishes, possibly on a background thread.</summary>
    public event Action? Changed;

    public string? LastError { get; private set; }

    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var ics = await http.GetStringAsync(feedUrl, cancellationToken);
            database.ReplaceCalendarEvents(IcsParser.Parse(ics));
            LastError = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LastError = exception.Message;
        }

        Changed?.Invoke();
    }
}
