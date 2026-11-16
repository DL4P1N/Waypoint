using System.Text.Json;
using System.Text.Json.Serialization;
using Waypoint.Core.Discord;
using Waypoint.Core.Email;
using Waypoint.Core.Schedule;

namespace Waypoint.Core;

/// <summary>Personal settings, stored beside the database so they stay out of the code.</summary>
public sealed record WaypointSettings(
    string? CalendarFeedUrl = null,
    ClassSchedule? Schedule = null,
    BedtimeSettings? Bedtime = null,
    TimeOnly? BriefingTime = null,
    TimeOnly? WeeklyResetTime = null,
    DiscordSettings? Discord = null,
    OutlookSettings? Outlook = null,
    int? WorkCommuteMinutes = null,
    Remote.ServerSettings? Server = null)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static WaypointSettings Load(string path) =>
        File.Exists(path) ? Parse(File.ReadAllText(path)) : new WaypointSettings();

    public static WaypointSettings Parse(string json) =>
        JsonSerializer.Deserialize<WaypointSettings>(json, Options) ?? new WaypointSettings();
}
