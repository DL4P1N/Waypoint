using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Background;

/// <summary>
/// The calendar feed, from the Settings page or calendarFeedUrl in settings.json, with the page's link used first.
/// Saving a link starts using it straight away, without a restart.
/// </summary>
public sealed partial class WaypointWorker
{
    /// <summary>
    /// Set by whatever runs the hourly sync, so a newly saved link is read straight away. A trial run never sets it,
    /// so saving there reads nothing.
    /// </summary>
    public bool SyncsCalendar { get; set; }

    /// <summary>The link in use: the Settings page's, or else the one in settings.json.</summary>
    private string? CalendarFeedUrl => CalendarFeedSetup.Saved(_database) ?? (string.IsNullOrWhiteSpace(_settings.CalendarFeedUrl) ? null : _settings.CalendarFeedUrl);

    public CalendarFeedForm GetCalendarFeed() => new(CalendarFeedUrl is not null, CalendarFeedSetup.Saved(_database) is not null);

    /// <summary>Saves a link from the Settings page and starts reading it. A blank or broken link changes nothing.</summary>
    public string SaveCalendarFeed(string? link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return "Paste the feed link first.";
        }

        if (CalendarFeedSetup.Clean(link) is not { } clean)
        {
            return CalendarFeedSetup.NotALink;
        }

        lock (_calendarFeedLock)
        {
            _database.SetPreference(CalendarFeedSetup.Preference, clean);
            StartCalendar();
        }

        return SyncsCalendar ? "Saved. Reading the feed now, then every hour." : "Saved. The feed is read every hour while Waypoint runs.";
    }

    /// <summary>
    /// The link the last Remove took away, kept only in memory so Undo can put it back. It's never sent out. A Remove
    /// that took nothing away leaves none, so Undo can't bring back an older link.
    /// </summary>
    private string? _removedCalendarFeed;

    private readonly object _calendarFeedLock = new();

    /// <summary>Forgets the page's link. Anything the feed already added stays where it is.</summary>
    public string RemoveCalendarFeed()
    {
        lock (_calendarFeedLock)
        {
            _removedCalendarFeed = CalendarFeedSetup.Saved(_database);
            _database.SetPreference(CalendarFeedSetup.Preference, "");
            StartCalendar();
        }

        return Calendar is null
            ? "Removed. Nothing more is read from the feed, and what it already added stays."
            : "Removed. The calendar link from the setup file is used instead.";
    }

    /// <summary>
    /// Undo for Remove: saves the link it took away again and starts reading it. A link saved since then is kept,
    /// so Undo never replaces a newer one.
    /// </summary>
    public string PutBackCalendarFeed()
    {
        lock (_calendarFeedLock)
        {
            var link = _removedCalendarFeed;
            _removedCalendarFeed = null;
            if (CalendarFeedSetup.Saved(_database) is not null)
            {
                return "A feed link is already saved, so it was kept.";
            }

            if (link is null)
            {
                return "There's no removed link to put back. Paste it again to use it.";
            }

            _database.SetPreference(CalendarFeedSetup.Preference, link);
            StartCalendar();
            return "Put the feed link back.";
        }
    }

    /// <summary>Builds the sync for the link in use, or none when there isn't one, and reads it now if the hourly sync runs.</summary>
    private void StartCalendar()
    {
        if (CalendarFeedUrl is not { } link)
        {
            Calendar = null;
            Nudges = null;
            return;
        }

        var calendar = new CalendarFeedSync(_http(), _database, link);
        var nudges = new AssignmentNudges(_database, _nudgePlanner, Schedule);
        nudges.Changed += _host.Changed;
        calendar.Changed += () =>
        {
            _host.Changed();
            _ = Tracked(() => nudges.PlanAsync(_clock(), _workStop.Token));
        };

        Calendar = calendar;
        Nudges = nudges;
        if (SyncsCalendar)
        {
            _ = Tracked(() => calendar.SyncAsync(_workStop.Token));
        }
    }
}
