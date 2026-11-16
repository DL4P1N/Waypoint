using System.Text.Json;
using System.Text.Json.Serialization;
using Waypoint.Core.Remote;

namespace Waypoint.Core.Schedule;

/// <summary>
/// The class schedule as the Settings page shows and saves it: the term's first and last days and the weekly
/// classes. <see cref="SavedHere"/> says whether the page's classes are in use rather than the setup file's.
/// </summary>
public sealed record ClassesForm(DateOnly? TermStart, DateOnly? TermEnd, IReadOnlyList<ClassMeeting> Classes, bool SavedHere = false);

/// <summary>
/// Classes saved on the Settings page. They're kept in preferences and laid over <c>schedule</c> in settings.json,
/// which is never rewritten, the same way the calendar feed and Discord's IDs are. Once the page saves its list it's
/// used instead of the file's (the page starts from the file's, so nothing is lost), and the term dates saved here
/// are used over the file's. Days with no class still come from the file.
/// </summary>
public static class ClassSetup
{
    public const string Preference = "class_schedule";
    public const int MaxClasses = 30;
    public const int MaxTravelMinutes = 240;
    public const int MaxNameLength = 80;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>What the page saved, or null when it hasn't saved any classes or what's kept can't be read.</summary>
    public static Saved? Read(IPreferences preferences)
    {
        if (preferences.GetPreference(Preference) is not { Length: > 0 } json)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Saved>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The schedule in use: the file's, with what the page saved laid over it. Never null.</summary>
    public static ClassSchedule Combine(ClassSchedule? file, IPreferences preferences)
    {
        var saved = Read(preferences);
        var start = saved?.TermStart ?? Known(file?.TermStart);
        var end = saved?.TermEnd ?? Known(file?.TermEnd);
        var meetings = saved?.Classes ?? file?.Meetings ?? [];
        if (meetings.Length == 0)
        {
            return file is null ? ClassSchedule.Empty() : file with { Meetings = [] };
        }

        // A term with no dates given runs on until one is set, rather than holding no classes at all.
        return new ClassSchedule(
            start ?? DateOnly.MinValue,
            end ?? DateOnly.MaxValue,
            meetings,
            file?.NoClassDates ?? [],
            file?.FirstWarningMinutes ?? 15);
    }

    /// <summary>What the page shows: the classes in use and the term dates, with dates that were never set left empty.</summary>
    public static ClassesForm Form(ClassSchedule schedule, IPreferences preferences) => new(
        Known(schedule.TermStart) is { } start && start != DateOnly.MaxValue ? start : null,
        Known(schedule.TermEnd) is { } end && end != DateOnly.MaxValue ? end : null,
        schedule.Meetings,
        Read(preferences)?.Classes is not null);

    /// <summary>
    /// A schedule from what the page shows, for a PC whose classes are kept on the server. Days with no class and the
    /// warning time stay as <paramref name="keep"/> has them, since the page doesn't change those.
    /// </summary>
    public static ClassSchedule FromForm(ClassesForm form, ClassSchedule? keep) =>
        form.Classes.Count == 0
            ? (keep ?? ClassSchedule.Empty()) with { Meetings = [] }
            : new ClassSchedule(
                form.TermStart ?? DateOnly.MinValue,
                form.TermEnd ?? DateOnly.MaxValue,
                form.Classes.ToArray(),
                keep?.NoClassDates ?? [],
                keep?.FirstWarningMinutes ?? 15);

    /// <summary>Why these can't be saved, or null when they're fine.</summary>
    public static string? Problem(ClassesForm form)
    {
        if (form.TermStart is { } start && form.TermEnd is { } end && end < start)
        {
            return "The term's last day has to come after its first day.";
        }

        if (form.Classes is null)
        {
            return "No classes came with that, so nothing changed.";
        }

        if (form.Classes.Count > MaxClasses)
        {
            return $"Keep it to {MaxClasses} classes or fewer.";
        }

        foreach (var meeting in form.Classes)
        {
            if (string.IsNullOrWhiteSpace(meeting.Course))
            {
                return "Give the class a name, like CS 101.";
            }

            if (meeting.Course.Trim().Length > MaxNameLength)
            {
                return $"Keep the class name under {MaxNameLength} characters.";
            }

            if (meeting.Days is not { Length: > 0 })
            {
                return $"Pick the days {meeting.Course.Trim()} meets.";
            }

            if (meeting.End <= meeting.Start)
            {
                return $"{meeting.Course.Trim()} has to end after it starts.";
            }

            if (meeting.TravelMinutes is < 0 or > MaxTravelMinutes)
            {
                return $"Minutes to get there has to be between 0 and {MaxTravelMinutes}.";
            }
        }

        return null;
    }

    /// <summary>Keeps the page's term dates and classes, tidied, or says why they weren't kept.</summary>
    public static string? Save(IPreferences preferences, ClassesForm form)
    {
        if (Problem(form) is { } problem)
        {
            return problem;
        }

        var classes = form.Classes
            .Select(meeting => meeting with
            {
                Course = meeting.Course.Trim(),
                Room = meeting.Room?.Trim() ?? "",
                Days = meeting.Days.Distinct().OrderBy(day => ((int)day + 6) % 7).ToArray(),
            })
            .ToArray();
        preferences.SetPreference(Preference, JsonSerializer.Serialize(new Saved(form.TermStart, form.TermEnd, classes), Options));
        return null;
    }

    /// <summary>A date from the file, or null when the file left it out.</summary>
    private static DateOnly? Known(DateOnly? date) => date is { } day && day != DateOnly.MinValue ? day : null;

    /// <summary>What's kept in preferences. Null classes means the page hasn't saved a list, so the file's is used.</summary>
    public sealed record Saved(DateOnly? TermStart, DateOnly? TermEnd, ClassMeeting[]? Classes);
}
