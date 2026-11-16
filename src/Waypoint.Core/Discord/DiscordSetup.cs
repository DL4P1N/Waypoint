using Waypoint.Core.Data;

namespace Waypoint.Core.Discord;

/// <summary>
/// Discord can be set up in settings.json or on the Settings page. What's saved on the page sits on top of the file,
/// the same way bedtime choices do, so settings.json keeps working and is never rewritten. The token entered on the
/// page is kept encrypted beside the database, never in preferences.
/// </summary>
public static class DiscordSetup
{
    public const string OwnerKey = "discord_owner_id";
    public const string CaptureKey = "discord_capture_channel";
    public const string AlertsKey = "discord_alerts_channel";
    public const string BudgetKey = "discord_budget_channel";
    public const string NotesForumKey = "discord_notes_forum";
    public const string ListsForumKey = "discord_lists_forum";
    public const string TokenFile = "discord.token";

    /// <summary>Null when there's no token or owner, since nothing would work without both.</summary>
    public static DiscordSettings? Combine(DiscordSettings? file, string? savedToken, WaypointDatabase database)
    {
        var token = Clean(savedToken) ?? Clean(file?.BotToken);
        var owner = Saved(database, OwnerKey) ?? Clean(file?.OwnerUserId);
        if (token is null || owner is null)
        {
            return null;
        }

        return new DiscordSettings(
            token,
            owner,
            Saved(database, CaptureKey) ?? Clean(file?.CaptureChannelId),
            Saved(database, AlertsKey) ?? Clean(file?.AlertsChannelId),
            Saved(database, BudgetKey) ?? Clean(file?.BudgetChannelId),
            Clean(file?.ToDoChannelId),
            Clean(file?.ListsChannelId),
            Clean(file?.NotesChannelId),
            Clean(file?.HistoryChannelId),
            Saved(database, NotesForumKey) ?? Clean(file?.NotesForumChannelId),
            Saved(database, ListsForumKey) ?? Clean(file?.ListsForumChannelId));
    }

    /// <summary>Saves the page's IDs. Each must be a Discord ID, which is all digits. A blank one falls back to settings.json.</summary>
    public static string? SaveIds(WaypointDatabase database, string? owner, string? capture, string? alerts, string? budget, string? notesForum = null, string? listsForum = null)
    {
        var values = new[]
        {
            (OwnerKey, owner, "Your user ID"),
            (CaptureKey, capture, "The capture channel ID"),
            (AlertsKey, alerts, "The alerts channel ID"),
            (BudgetKey, budget, "The budget channel ID"),
            (NotesForumKey, notesForum, "The notes forum ID"),
            (ListsForumKey, listsForum, "The lists forum ID"),
        };
        foreach (var (_, value, label) in values)
        {
            if (Clean(value) is { } id && !IsId(id))
            {
                return $"{label} should be only digits. Turn on Developer Mode in Discord, then right-click and choose Copy ID.";
            }
        }

        foreach (var (key, value, _) in values)
        {
            database.SetPreference(key, Clean(value) ?? "");
        }

        return null;
    }

    public static bool IsId(string text) => text.Length is >= 15 and <= 22 && text.All(char.IsAsciiDigit);

    private static string? Saved(WaypointDatabase database, string key) => Clean(database.GetPreference(key));

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
