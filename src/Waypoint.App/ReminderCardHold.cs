using System;
using System.Windows.Threading;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.App;

/// <summary>
/// "After this" for a PC reminder card with Start. Left unanswered for <see cref="AfterThis.Unanswered"/>, its item is
/// held once, dated from when the card showed, so starting it some other way meanwhile still counts. Answering the
/// card, or saying it didn't help, stops the wait and drops the hold.
/// </summary>
public sealed class ReminderCardHold
{
    private readonly WaypointDatabase _database;
    private readonly StoredItem _item;
    private readonly DateTime _shownAt;
    private readonly DispatcherTimer _timer;
    private bool _over;

    public ReminderCardHold(WaypointDatabase database, StoredItem item, DateTime shownAt)
    {
        _database = database;
        _item = item;
        _shownAt = shownAt;
        _timer = new DispatcherTimer { Interval = AfterThis.Unanswered };
        _timer.Tick += (_, _) => Elapsed();
    }

    /// <summary>Only a card with Start, and not a waiting-on nudge, can mean "after this".</summary>
    public static bool Applies(StoredItem item, bool waiting) => !waiting && Activation.CanStart(item);

    public void Start() => _timer.Start();

    /// <summary>The card went unanswered for long enough. Tests call this instead of waiting half an hour.</summary>
    public void Elapsed()
    {
        _timer.Stop();
        if (!_over)
        {
            _over = true;
            _database.HoldAfterThis(_item, _shownAt);
        }
    }

    /// <summary>The card was answered or closed, or marked didn't help, so nothing is held for it.</summary>
    public void Answered()
    {
        _timer.Stop();
        _over = true;
        _database.DropAfterThis(_item);
    }
}
