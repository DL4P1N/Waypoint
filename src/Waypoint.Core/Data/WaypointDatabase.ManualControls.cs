using System.Globalization;
using Waypoint.Core.Capture;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Data;

/// <summary>One regular weekly shift, as the Settings page lists it.</summary>
public sealed record WeeklyHours(DayOfWeek Day, TimeOnly Start, TimeOnly End);

/// <summary>A one-off date. Start and End are null for a day off.</summary>
public sealed record WorkDate(DateOnly Date, TimeOnly? Start, TimeOnly? End)
{
    public bool IsDayOff => Start is null;
}

/// <summary>
/// What the buttons and forms need beyond the ops. Each one ends in the same method typing uses,
/// so a manual action and a typed one can't drift apart, and none of them calls the sorter.
/// </summary>
public sealed partial class WaypointDatabase
{
    private const string BedtimeChoicesKey = "bedtime_choices";

    public BedtimeChoices GetBedtimeChoices() => BedtimeChoices.Parse(GetPreference(BedtimeChoicesKey));

    /// <summary>Returns why they weren't saved, or null once they are.</summary>
    public string? SetBedtimeChoices(BedtimeChoices choices)
    {
        if (choices.Problem() is { } problem)
        {
            return problem;
        }

        SetPreference(BedtimeChoicesKey, choices.Serialize());
        return null;
    }

    /// <summary>The typed commute, else the one from settings.</summary>
    public int GetCommuteMinutes() =>
        int.TryParse(GetPreference(CommuteKey), NumberStyles.None, CultureInfo.InvariantCulture, out var typed) ? typed : DefaultCommuteMinutes;

    /// <summary>The regular week, Monday first.</summary>
    public IReadOnlyList<WeeklyHours> GetWeeklyHours()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT day, start_time, end_time FROM work_hours";
        using var reader = command.ExecuteReader();
        var hours = new List<WeeklyHours>();
        while (reader.Read())
        {
            hours.Add(new WeeklyHours(Enum.Parse<DayOfWeek>(reader.GetString(0)), ParseClock(reader.GetString(1)), ParseClock(reader.GetString(2))));
        }

        return hours.OrderBy(shift => ((int)shift.Day + 6) % 7).ThenBy(shift => shift.Start).ToList();
    }

    /// <summary>One-off shifts and days off from this day on, soonest first.</summary>
    public IReadOnlyList<WorkDate> GetWorkDates(DateOnly from)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT work_date, start_time, end_time FROM work_dates WHERE work_date >= $from ORDER BY work_date, start_time";
        command.Parameters.AddWithValue("$from", Day(from));
        using var reader = command.ExecuteReader();
        var dates = new List<WorkDate>();
        while (reader.Read())
        {
            dates.Add(new WorkDate(
                DateOnly.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                reader.IsDBNull(1) ? null : ParseClock(reader.GetString(1)),
                reader.IsDBNull(2) ? null : ParseClock(reader.GetString(2))));
        }

        return dates;
    }

    /// <summary>Takes a one-off shift or day off away, so that day goes back to its usual hours.</summary>
    public OpReply RemoveWorkDate(DateOnly date)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM work_dates WHERE work_date = $date";
        command.Parameters.AddWithValue("$date", Day(date));
        return command.ExecuteNonQuery() > 0
            ? new OpReply($"{date.ToString("dddd, MMM d", CultureInfo.InvariantCulture)} is back to the usual hours.")
            : new OpReply("Nothing was set for that day.");
    }

    /// <summary>
    /// The Save my spot form. It gets a capture that's already sorted, the same as anything typed into a page,
    /// then saves through <see cref="SaveHandoff"/>, the method the typed "save my spot" op uses.
    /// </summary>
    public OpReply SaveSpotByHand(string? project, string? stopped, string? next, IEnumerable<string>? links, IEnumerable<string>? thoughts, DateTime now)
    {
        static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        var entries = (links ?? []).Concat(thoughts ?? []).Select(entry => entry.Trim()).Where(entry => entry.Length > 0).ToList();
        var cleanStopped = Clean(stopped);
        var cleanNext = Clean(next);
        if (Clean(project) is null && cleanStopped is null && cleanNext is null && entries.Count == 0)
        {
            return new OpReply("Write where you stopped or what's next, then save.", true);
        }

        var parts = new[]
        {
            Clean(project),
            cleanStopped is null ? null : $"stopped at {cleanStopped}",
            cleanNext is null ? null : $"next {cleanNext}",
        };
        var text = $"save my spot: {string.Join(", ", parts.OfType<string>().DefaultIfEmpty("saved by hand"))}";
        return SaveHandoff(Clean(project), cleanStopped, cleanNext, entries, AddHandTyped(text, now), now);
    }

    /// <summary>Starts on an item and goes one smaller straight away, using the fixed actions, for "smaller step" on a row.</summary>
    public OpReply StartSmaller(StoredItem item, string method, DateTime now)
    {
        var started = StartActivation(ItemRef.Format(item), null, null, method, now);
        return GetActiveSession(now) is null ? started : MakeSmaller(null, now);
    }

    private static TimeOnly ParseClock(string text) => TimeOnly.Parse(text, CultureInfo.InvariantCulture);
}
