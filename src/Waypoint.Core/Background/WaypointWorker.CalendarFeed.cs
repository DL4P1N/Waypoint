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

        _database.SetPreference(CalendarFeedSetup.Preference, clean);
        StartCalendar();
        return SyncsCalendar ? "Saved. Reading the feed now, then every hour." : "Saved. The feed is read every hour while Waypoint runs.";
    }

    /// <summary>Forgets the page's link. Anything the feed already added stays where it is.</summary>
    public string RemoveCalendarFeed()
    {
        _database.SetPreference(CalendarFeedSetup.Preference, "");
        StartCalendar();
        return Calendar is null
            ? "Removed. Nothing more is read from the feed, and what it already added stays."
            : "Removed. The calendar link from the setup file is used instead.";
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
            _ = nudges.PlanAsync(_clock());
        };

        Calendar = calendar;
        Nudges = nudges;
        if (SyncsCalendar)
        {
            _ = calendar.SyncAsync();
        }
    }
}
