using System.Globalization;
using System.Text.Json;

namespace Waypoint.Core.Schedule;

/// <summary>
/// The bedtime values the Settings page can change. They're kept in preferences and laid over
/// <c>bedtime</c> in settings.json, so the file is never rewritten. A null field keeps the file's value.
/// </summary>
public sealed record BedtimeChoices(
    double? SleepHours = null,
    int? ReadyMinutes = null,
    string? LatestBedtime = null,
    bool? EveningStep = null,
    bool? WindDownStep = null,
    string[]? RoutineSteps = null,
    bool? QuietAfterWrapUp = null,
    bool? LateCheckIns = null,
    string? EveningStepName = null,
    string? WindDownStepName = null)
{
    public const double MinSleepHours = 5;
    public const double MaxSleepHours = 12;
    public const int MaxReadyMinutes = 240;
    public const int MaxRoutineSteps = 10;
    public const int MaxStepName = 40;

    private static readonly BedtimeSettings Defaults = new();

    public static BedtimeChoices None { get; } = new();

    /// <summary>The file's settings with these choices on top. Turning a wind-down step back on uses the file's timing, or the default.</summary>
    public BedtimeSettings ApplyTo(BedtimeSettings file) => file with
    {
        SleepHours = SleepHours ?? file.SleepHours,
        ReadyMinutes = ReadyMinutes ?? file.ReadyMinutes,
        LatestBedtime = ParseTime(LatestBedtime) ?? file.LatestBedtime,
        EveningStepMinutes = EveningStep switch
        {
            true => file.EveningStepMinutes ?? Defaults.EveningStepMinutes,
            false => null,
            null => file.EveningStepMinutes,
        },
        WindDownStepMinutes = WindDownStep switch
        {
            true => file.WindDownStepMinutes ?? Defaults.WindDownStepMinutes,
            false => null,
            null => file.WindDownStepMinutes,
        },
        EveningStepName = CleanName(EveningStepName) ?? file.EveningStepName,
        WindDownStepName = CleanName(WindDownStepName) ?? file.WindDownStepName,
        RoutineSteps = RoutineSteps ?? file.RoutineSteps,
        QuietAfterWrapUp = QuietAfterWrapUp ?? file.QuietAfterWrapUp,
        LateCheckIns = LateCheckIns ?? file.LateCheckIns,
    };

    /// <summary>Why these can't be saved, or null if they're fine.</summary>
    public string? Problem()
    {
        if (SleepHours is { } hours && (hours < MinSleepHours || hours > MaxSleepHours))
        {
            return $"Sleep has to be between {MinSleepHours} and {MaxSleepHours} hours.";
        }

        if (ReadyMinutes is { } ready && (ready < 0 || ready > MaxReadyMinutes))
        {
            return $"Getting ready has to be between 0 and {MaxReadyMinutes} minutes.";
        }

        if (LatestBedtime is not null && ParseTime(LatestBedtime) is null)
        {
            return "Try a latest bedtime like 1:00 AM.";
        }

        if (RoutineSteps is { Length: > MaxRoutineSteps })
        {
            return $"Keep the routine to {MaxRoutineSteps} steps or fewer.";
        }

        if (CleanName(EveningStepName) is { Length: > MaxStepName } || CleanName(WindDownStepName) is { Length: > MaxStepName })
        {
            return $"Keep each wind-down step's name to {MaxStepName} characters or fewer.";
        }

        return null;
    }

    /// <summary>Extra routine steps from typed lines, trimmed, with blanks and repeats dropped.</summary>
    public static string[] CleanSteps(IEnumerable<string> lines) =>
        lines.Select(line => line.Trim()).Where(line => line.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>A typed step name, trimmed, or null when it's blank so the file's name is kept.</summary>
    public static string? CleanName(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    public string Serialize() => JsonSerializer.Serialize(this);

    /// <summary>Anything unreadable counts as no choices, so a bad value can't stop bedtime working.</summary>
    public static BedtimeChoices Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return None;
        }

        try
        {
            return JsonSerializer.Deserialize<BedtimeChoices>(json) ?? None;
        }
        catch (JsonException)
        {
            return None;
        }
    }

    /// <summary>Stored as HH:mm so it reads back the same on any machine.</summary>
    public static string FormatTime(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static TimeOnly? ParseTime(string? text) =>
        TimeOnly.TryParseExact(text?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;
}
