using Waypoint.Core.Data;

namespace Waypoint.Core.Calendar;

/// <summary>What the Settings page shows about the feed. The link itself is never sent back, only whether there is one.</summary>
public sealed record CalendarFeedForm(bool HasFeed, bool SavedHere);

/// <summary>
/// The calendar feed link typed on the Settings page. It's kept in preferences and used over calendarFeedUrl in
/// settings.json, which is never rewritten, the same way Discord's page values sit over its file values.
/// </summary>
public static class CalendarFeedSetup
{
    public const string Preference = "calendar_feed_url";

    public const string NotALink = "That doesn't look like a feed link. Copy the whole link, which starts with https:// and often ends in .ics.";

    /// <summary>The link saved on the page, or null when there isn't one.</summary>
    public static string? Saved(WaypointDatabase database) =>
        database.GetPreference(Preference) is { Length: > 0 } link ? link : null;

    /// <summary>
    /// Tidies a pasted link, or returns null when it isn't one. Calendar apps often hand out webcal:// links, which
    /// are the same feed over https.
    /// </summary>
    public static string? Clean(string? text)
    {
        var link = text?.Trim() ?? "";
        if (link.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
        {
            link = "https://" + link["webcal://".Length..];
        }

        return Uri.TryCreate(link, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && uri.Host.Length > 0
            ? uri.AbsoluteUri
            : null;
    }
}
