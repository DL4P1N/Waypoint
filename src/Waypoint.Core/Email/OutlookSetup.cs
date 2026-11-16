using System.Net.Mail;
using Waypoint.Core.Data;

namespace Waypoint.Core.Email;

/// <summary>
/// Alert email can be set up in settings.json or on the Settings page. What's saved on the page sits on top of the
/// file, the same way Discord's IDs do, so settings.json keeps working and is never rewritten. In server mode the
/// page saves to the server's database, so the server's file never needs editing for it. Neither value is a secret.
/// </summary>
public static class OutlookSetup
{
    public const string ClientIdKey = "outlook_client_id";
    public const string OwnAddressKey = "outlook_own_address";

    /// <summary>Null when there's no app ID, since nothing can sign in without one.</summary>
    public static OutlookSettings? Combine(OutlookSettings? file, WaypointDatabase database)
    {
        var clientId = Saved(database, ClientIdKey) ?? Clean(file?.ClientId);
        if (clientId is null)
        {
            return null;
        }

        return new OutlookSettings(clientId, Saved(database, OwnAddressKey) ?? Clean(file?.OwnAddress));
    }

    /// <summary>
    /// Saves the page's values, or says why not. The app ID is the Application (client) ID Azure shows, and the
    /// address is the one Tailspin Pay emails are forwarded from. A blank one falls back to settings.json.
    /// </summary>
    public static string? Save(WaypointDatabase database, string? clientId, string? ownAddress)
    {
        if (Clean(clientId) is { } id && !Guid.TryParse(id, out _))
        {
            return "The app ID looks like 12345678-abcd-1234-abcd-1234567890ab. Copy the Application (client) ID from the app's page in Azure.";
        }

        if (Clean(ownAddress) is { } address && !MailAddress.TryCreate(address, out _))
        {
            return "That doesn't look like an email address. Try one like you@example.com.";
        }

        database.SetPreference(ClientIdKey, Clean(clientId) ?? "");
        database.SetPreference(OwnAddressKey, Clean(ownAddress) ?? "");
        return null;
    }

    private static string? Saved(WaypointDatabase database, string key) => Clean(database.GetPreference(key));

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
