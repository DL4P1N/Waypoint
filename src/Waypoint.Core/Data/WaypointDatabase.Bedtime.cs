using System.Globalization;
using Waypoint.Core.Capture;
using Waypoint.Core.Discord;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Data;

/// <summary>A notice held through the quiet part of the night, to go out once it's over.</summary>
public sealed record HeldNotice(long Id, string Title, string Text, CardTone Tone, bool ToBudget);

public sealed partial class WaypointDatabase : IBedtimeLog
{
    private const string SnoozeKey = "bedtime_snooze";
    private const string CommuteKey = "work_commute_minutes";
    private const int NightsKept = 14;
    private const int ShiftsLookAheadDays = 21;
    private const string WorkQuestion = "What days and hours do you work? For example, Mondays 9 to 5.";

    /// <summary>Used until a commute has been typed. Comes from settings.</summary>
    public int DefaultCommuteMinutes
    {
        get => _root._defaultCommuteMinutes;
        set => _root._defaultCommuteMinutes = value;
    }

    private int _defaultCommuteMinutes = WorkShift.DefaultCommuteMinutes;

    public DateTime? BedtimeSnoozedUntil
    {
        get => DateTime.TryParse(GetPreference(SnoozeKey), CultureInfo.InvariantCulture, out var until) ? until : null;
        set => SetPreference(SnoozeKey, value is { } until ? FormatReminder(until) : "");
    }

    private void CreateBedtimeTables() => Execute("""
        CREATE TABLE IF NOT EXISTS bedtime_sent (
            morning TEXT NOT NULL,
            task TEXT NOT NULL,
            PRIMARY KEY (morning, task)
        );
        CREATE TABLE IF NOT EXISTS bedtime_marks (
            morning TEXT NOT NULL,
            step TEXT NOT NULL,
            PRIMARY KEY (morning, step)
        );
        CREATE TABLE IF NOT EXISTS held_notices (
            id INTEGER PRIMARY KEY,
            title TEXT NOT NULL,
            text TEXT NOT NULL,
            tone TEXT NOT NULL,
            to_budget INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS work_hours (
            id INTEGER PRIMARY KEY,
            day TEXT NOT NULL,
            start_time TEXT NOT NULL,
            end_time TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS work_dates (
            id INTEGER PRIMARY KEY,
            work_date TEXT NOT NULL,
            start_time TEXT,
            end_time TEXT
        );
        """);

    /// <summary>
    /// Moves what was saved under a wind-down step's name, from before the steps had slots, to the slot whose name
    /// it matches: the Settings page's on and off switches, and sent stages, so a reminder already out isn't sent
    /// again. Run once settings.json is read. Anything stored under a stage's own name is never touched, so running
    /// it again, or after a step is renamed, changes nothing. Check-offs are kept by name and need nothing.
    /// </summary>
    public void UpgradeWindDownSteps(BedtimeSettings file)
    {
        if (WindDownUpgrade.UpgradeChoices(GetPreference(BedtimeChoicesKey), file) is { } choices)
        {
            SetPreference(BedtimeChoicesKey, choices);
        }

        var settings = GetBedtimeChoices().ApplyTo(file);
        var stages = Enum.GetNames<BedtimeTask>().ToHashSet(StringComparer.Ordinal);
        using var lease = Connect(out var connection);
        var earlier = new List<string>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT DISTINCT task FROM bedtime_sent";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                earlier.Add(reader.GetString(0));
            }
        }

        foreach (var task in earlier.Where(task => !stages.Contains(task)))
        {
            if (WindDownUpgrade.SlotFor(task, settings) is not { } slot)
            {
                continue;
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE OR IGNORE bedtime_sent SET task = $now WHERE task = $earlier;
                DELETE FROM bedtime_sent WHERE task = $earlier;
                """;
            command.Parameters.AddWithValue("$earlier", task);
            command.Parameters.AddWithValue("$now", slot.ToString());
            command.ExecuteNonQuery();
        }
    }

    public bool WasSent(DateOnly morning, BedtimeTask task)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM bedtime_sent WHERE morning = $morning AND task = $task";
        command.Parameters.AddWithValue("$morning", Day(morning));
        command.Parameters.AddWithValue("$task", task.ToString());
        return command.ExecuteScalar() is not null;
    }

    public void MarkSent(DateOnly morning, BedtimeTask task)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO bedtime_sent (morning, task) VALUES ($morning, $task);
            DELETE FROM bedtime_sent WHERE morning < $oldest;
            DELETE FROM bedtime_marks WHERE morning < $oldest;
            """;
        command.Parameters.AddWithValue("$morning", Day(morning));
        command.Parameters.AddWithValue("$task", task.ToString());
        command.Parameters.AddWithValue("$oldest", Day(morning.AddDays(-NightsKept)));
        command.ExecuteNonQuery();
    }

    public IReadOnlySet<string> GetRoutineMarks(DateOnly morning)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT step FROM bedtime_marks WHERE morning = $morning";
        command.Parameters.AddWithValue("$morning", Day(morning));

        var marks = new HashSet<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            marks.Add(reader.GetString(0));
        }

        return marks;
    }

    public void SetRoutineMark(DateOnly morning, string step, bool done)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = done
            ? "INSERT OR IGNORE INTO bedtime_marks (morning, step) VALUES ($morning, $step)"
            : "DELETE FROM bedtime_marks WHERE morning = $morning AND step = $step";
        command.Parameters.AddWithValue("$morning", Day(morning));
        command.Parameters.AddWithValue("$step", step);
        command.ExecuteNonQuery();
    }

    public bool HasHandoffSince(DateTime since) =>
        GetHandoffs("created_at >= $since LIMIT 1", ("$since", FormatReminder(since))).Count > 0;

    /// <summary>Keeps a notice that can wait until the quiet part of the night is over.</summary>
    public void HoldNotice(string title, string text, CardTone tone, bool toBudget)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO held_notices (title, text, tone, to_budget) VALUES ($title, $text, $tone, $toBudget)";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$tone", tone.ToString());
        command.Parameters.AddWithValue("$toBudget", toBudget ? 1 : 0);
        command.ExecuteNonQuery();
    }

    /// <summary>What's held, oldest first, each kept until it's gone out (see <see cref="DropHeldNotice"/>).</summary>
    public IReadOnlyList<HeldNotice> GetHeldNotices()
    {
        using var lease = Connect(out var connection);
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT id, title, text, tone, to_budget FROM held_notices ORDER BY id";
        using var reader = read.ExecuteReader();
        var notices = new List<HeldNotice>();
        while (reader.Read())
        {
            notices.Add(new HeldNotice(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), Enum.Parse<CardTone>(reader.GetString(3)), reader.GetInt64(4) != 0));
        }

        return notices;
    }

    /// <summary>A held notice has gone out, so it's forgotten.</summary>
    public void DropHeldNotice(long id)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM held_notices WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Hands back what was held and forgets it, so nothing goes out twice.</summary>
    public IReadOnlyList<(string Title, string Text, CardTone Tone, bool ToBudget)> TakeHeldNotices()
    {
        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        var notices = new List<(string, string, CardTone, bool)>();

        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT title, text, tone, to_budget FROM held_notices ORDER BY id";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                notices.Add((reader.GetString(0), reader.GetString(1), Enum.Parse<CardTone>(reader.GetString(2)), reader.GetInt64(3) != 0));
            }
        }

        // Their send notes go too, so a notice held later under one of the same ids isn't taken for one already sent.
        using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM held_notices; DELETE FROM discord_sends WHERE substr(send_key, 1, 5) = 'held:';";
            clear.ExecuteNonQuery();
        }

        transaction.Commit();
        return notices;
    }

    /// <summary>Sets the weekly hours for these days, replacing what they had. Anything unreadable asks instead of guessing.</summary>
    public OpReply SetWeeklyHours(IReadOnlyList<string> dayNames, string? hours)
    {
        if (ParseDays(dayNames) is not { Count: > 0 } days || ParseHours(hours) is not { } range)
        {
            return new OpReply(WorkQuestion, true);
        }

        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        foreach (var day in days)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM work_hours WHERE day = $day;
                INSERT INTO work_hours (day, start_time, end_time) VALUES ($day, $start, $end);
                """;
            command.Parameters.AddWithValue("$day", day.ToString());
            command.Parameters.AddWithValue("$start", Clock(range.Start));
            command.Parameters.AddWithValue("$end", Clock(range.End));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return new OpReply($"Work saved: {string.Join(", ", days)}, {Hours(range.Start, range.End)}.");
    }

    public OpReply RemoveWeeklyHours(IReadOnlyList<string> dayNames)
    {
        if (ParseDays(dayNames) is not { Count: > 0 } days)
        {
            return new OpReply("Which day should I take off your work hours?", true);
        }

        using var lease = Connect(out var connection);
        foreach (var day in days)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM work_hours WHERE day = $day";
            command.Parameters.AddWithValue("$day", day.ToString());
            command.ExecuteNonQuery();
        }

        return new OpReply($"Off your regular hours: {string.Join(", ", days)}.");
    }

    /// <summary>One shift on one date, which takes the place of that day's usual hours.</summary>
    public OpReply AddWorkShift(DateTime? start, DateTime? end)
    {
        if (start is not { } from || end is not { } to || from.TimeOfDay == to.TimeOfDay)
        {
            return new OpReply(WorkQuestion, true);
        }

        var date = DateOnly.FromDateTime(from);
        SetWorkDate(date, TimeOnly.FromDateTime(from), TimeOnly.FromDateTime(to));
        return new OpReply($"Work saved: {date.ToString("dddd, MMM d", CultureInfo.InvariantCulture)}, {Hours(TimeOnly.FromDateTime(from), TimeOnly.FromDateTime(to))}.");
    }

    public OpReply SetDayOff(DateTime? date)
    {
        if (date is not { } day)
        {
            return new OpReply("Which day are you off?", true);
        }

        var off = DateOnly.FromDateTime(day);
        SetWorkDate(off, null, null);
        return new OpReply($"Off work {off.ToString("dddd, MMM d", CultureInfo.InvariantCulture)}.");
    }

    public OpReply SetCommute(string? minutes)
    {
        if (!int.TryParse(minutes?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return new OpReply("How many minutes does it take to get to work?", true);
        }

        SetPreference(CommuteKey, parsed.ToString(CultureInfo.InvariantCulture));
        return new OpReply($"Commute saved: {parsed} minutes.");
    }

    public IReadOnlyList<WorkShift> GetShifts(DateOnly date)
    {
        var commute = GetCommuteMinutes();

        using var lease = Connect(out var connection);
        var dated = new List<(string? Start, string? End)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT start_time, end_time FROM work_dates WHERE work_date = $date";
            command.Parameters.AddWithValue("$date", Day(date));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                dated.Add((reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            }
        }

        var hours = dated;
        if (dated.Count == 0)
        {
            hours = [];
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT start_time, end_time FROM work_hours WHERE day = $day";
            command.Parameters.AddWithValue("$day", date.DayOfWeek.ToString());
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                hours.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        return hours
            .Where(shift => shift.Start is not null)
            .Select(shift => new WorkShift(date, TimeOnly.Parse(shift.Start!, CultureInfo.InvariantCulture), TimeOnly.Parse(shift.End!, CultureInfo.InvariantCulture), commute))
            .OrderBy(shift => shift.Start)
            .ToList();
    }

    public WorkShift? GetFirstShift(DateOnly date) => GetShifts(date).FirstOrDefault();

    /// <summary>The next shift that hasn't started, a few weeks out at most.</summary>
    public WorkShift? GetNextShift(DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        return Enumerable.Range(0, ShiftsLookAheadDays)
            .SelectMany(offset => GetShifts(today.AddDays(offset)))
            .FirstOrDefault(shift => shift.Starts > now);
    }

    public string DescribeWork(DateOnly today)
    {
        using var lease = Connect(out var connection);
        var lines = new List<string>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT day, start_time, end_time FROM work_hours";
            using var reader = command.ExecuteReader();
            var weekly = new List<(DayOfWeek Day, string Start, string End)>();
            while (reader.Read())
            {
                weekly.Add((Enum.Parse<DayOfWeek>(reader.GetString(0)), reader.GetString(1), reader.GetString(2)));
            }

            lines.AddRange(weekly
                .OrderBy(shift => ((int)shift.Day + 6) % 7)
                .Select(shift => $"{shift.Day}: {Hours(TimeOnly.Parse(shift.Start, CultureInfo.InvariantCulture), TimeOnly.Parse(shift.End, CultureInfo.InvariantCulture))}"));
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT work_date, start_time, end_time FROM work_dates WHERE work_date >= $today ORDER BY work_date";
            command.Parameters.AddWithValue("$today", Day(today));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var date = DateOnly.Parse(reader.GetString(0), CultureInfo.InvariantCulture).ToString("ddd, MMM d", CultureInfo.InvariantCulture);
                lines.Add(reader.IsDBNull(1)
                    ? $"{date}: off"
                    : $"{date}: {Hours(TimeOnly.Parse(reader.GetString(1), CultureInfo.InvariantCulture), TimeOnly.Parse(reader.GetString(2), CultureInfo.InvariantCulture))}");
            }
        }

        return lines.Count > 0 ? string.Join("\n", lines) : "No work hours saved yet.";
    }

    private void SetWorkDate(DateOnly date, TimeOnly? start, TimeOnly? end)
    {
        // The old day and the new one change in one write, so a failure between can't drop the day's hours.
        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM work_dates WHERE work_date = $date;
            INSERT INTO work_dates (work_date, start_time, end_time) VALUES ($date, $start, $end);
            """;
        command.Parameters.AddWithValue("$date", Day(date));
        command.Parameters.AddWithValue("$start", start is { } from ? Clock(from) : DBNull.Value);
        command.Parameters.AddWithValue("$end", end is { } to ? Clock(to) : DBNull.Value);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>Null if any name can't be read, so nothing gets saved for a day that was only half understood.</summary>
    private static List<DayOfWeek>? ParseDays(IReadOnlyList<string> names)
    {
        var days = new List<DayOfWeek>();
        foreach (var name in names)
        {
            if (!Enum.TryParse<DayOfWeek>(name.Trim().TrimEnd('s', 'S'), ignoreCase: true, out var day) || !Enum.IsDefined(day))
            {
                return null;
            }

            days.Add(day);
        }

        return days.Distinct().ToList();
    }

    private static (TimeOnly Start, TimeOnly End)? ParseHours(string? hours)
    {
        var parts = hours?.Split('-', StringSplitOptions.TrimEntries) ?? [];
        return parts.Length == 2
            && TimeOnly.TryParseExact(parts[0], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            && TimeOnly.TryParseExact(parts[1], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
            && start != end
                ? (start, end)
                : null;
    }

    private static string Day(DateOnly date) => date.ToString("O", CultureInfo.InvariantCulture);

    private static string Clock(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Hours(TimeOnly start, TimeOnly end) =>
        $"{start.ToString("h:mm tt", CultureInfo.InvariantCulture)} to {end.ToString("h:mm tt", CultureInfo.InvariantCulture)}";
}
