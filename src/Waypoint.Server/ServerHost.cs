using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Server;

/// <summary>
/// The worker's fallbacks on a machine with no screen. A notice that couldn't reach Discord is kept and tried
/// again, and the PC shows it meanwhile. A reminder is let go after a pause so it's tried again while it's still due.
/// </summary>
public sealed class ServerHost(ILogger<ServerHost> logger) : IWorkerHost, IAsyncDisposable
{
    public static readonly TimeSpan ReminderRetryPause = TimeSpan.FromMinutes(1);

    private readonly OrderedWork _work = new(
        exception => logger.LogError(exception, "Background work failed"),
        warning => logger.LogWarning("Background work: {Warning}", warning));
    private readonly object _gate = new();
    private string? _undelivered;

    /// <summary>The last thing that couldn't be sent, with when, for the status.</summary>
    public string? Undelivered
    {
        get
        {
            lock (_gate)
            {
                return _undelivered;
            }
        }
    }

    /// <summary>The pause before a reminder that couldn't be sent is tried again. Tests shorten it.</summary>
    public TimeSpan RetryPause { get; set; } = ReminderRetryPause;

    /// <summary>Where notices that couldn't be sent are kept. Set once the database is open, before any work starts.</summary>
    public WaypointDatabase? Database { get; set; }

    /// <summary>
    /// Kept in the database rather than only logged, so nothing is lost or written over. It's tried again in
    /// Discord, and the PC shows it if it gets there first.
    /// </summary>
    public void ShowNotice(string title, string text)
    {
        logger.LogWarning("Couldn't send to Discord: {Title}. It's kept to try again.", title);
        Database?.AddUndeliveredNotice(title, text, DateTime.Now);
    }

    /// <summary>Sends kept notices again, oldest first, and stops at the first that still doesn't go so the order holds.</summary>
    public async Task RetryNoticesAsync(Func<string, string, Task<bool>> send)
    {
        foreach (var notice in Database?.GetUndeliveredNotices() ?? [])
        {
            if (!await send(notice.Title, notice.Text))
            {
                return;
            }

            Database!.MarkNoticeDelivered(notice.Id);
        }
    }

    public void ShowReminder(DueReminder reminder, Action closed)
    {
        logger.LogWarning("Couldn't send the reminder for item {Id} to Discord. Trying again later.", reminder.Item.Id);
        SetUndelivered($"Couldn't send the reminder \"{reminder.Item.Title}\" to Discord. It's tried again while it's due.");
        _ = Task.Delay(RetryPause).ContinueWith(_ => closed(), TaskScheduler.Default);
    }

    public void ShowWeeklyReset(string text)
    {
        // It also goes out as a notification, which is all a server can do with it.
    }

    public void Changed()
    {
    }

    /// <summary>
    /// There's no window thread here, so the work runs out of the caller's way. It still runs one at a time in the
    /// order it came, as it would on a window thread.
    /// </summary>
    public void Post(Action action) => _work.Post(action);

    /// <summary>
    /// Takes no more work and finishes what's queued, up to <paramref name="limit"/>. What's left, and anything
    /// handed in afterwards, is logged as a warning. Gives the number left undone.
    /// </summary>
    public Task<int> StopAsync(TimeSpan limit) => _work.StopAsync(limit);

    public ValueTask DisposeAsync() => _work.DisposeAsync();

    private void SetUndelivered(string? text)
    {
        lock (_gate)
        {
            _undelivered = text is null ? null : $"{DateTime.Now:h:mm tt}: {text}";
        }
    }
}
