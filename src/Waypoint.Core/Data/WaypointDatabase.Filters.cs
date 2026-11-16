using System.Globalization;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

/// <summary>Where a To do filter came from, which decides what it matches and whether it can be removed.</summary>
public enum FilterSource
{
    Class,
    Personal,
    Custom,
}

/// <summary>
/// One entry in the To do column. Name is only what's shown. A class filter matches items by Course,
/// the short course code, so renaming it never breaks the match with the feed.
/// </summary>
public sealed record ToDoFilter(long Id, string Name, FilterSource Source, string? Course, int Position);

/// <summary>A filter after creating or renaming it. Problem says why nothing changed.</summary>
public sealed record FilterEdit(ToDoFilter? Filter, string? Problem = null);

/// <summary>What the items table knows about an item beyond the usual columns.</summary>
public sealed record ItemPlacement(string? Course, long? FilterId, TimeOnly? EndTime);

public sealed partial class WaypointDatabase
{
    private const string PersonalFilter = "Personal";

    /// <summary>Course names from the class schedule in settings, so a class has a filter before anything is due in it.</summary>
    public IReadOnlyList<string> ScheduleCourses { get; set; } = [];

    // Rows are never deleted, so a renamed class keeps its name through terms and feed gaps.
    private void CreateFilterTables()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS todo_filters (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                source TEXT NOT NULL,
                course TEXT,
                position INTEGER NOT NULL DEFAULT 0,
                removed_at TEXT
            );
            """);
        AddColumnIfMissing("items", "course", "TEXT");
        AddColumnIfMissing("items", "filter_id", "INTEGER");
        AddColumnIfMissing("items", "end_time", "TEXT");
    }

    /// <summary>
    /// The To do column's filters in order: classes, then Personal, then the user's own. Any class seen in
    /// the feed, on an item, or in the schedule gets a filter first. Classes with nothing in them are left out.
    /// </summary>
    public IReadOnlyList<ToDoFilter> GetToDoFilters()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var active = ActiveCourses(connection);
        var filters = ReadFilters(connection);

        var next = filters.Count == 0 ? 0 : filters.Max(filter => filter.Position) + 1;
        // New classes start in name order, and after that the order is the user's.
        foreach (var course in active.Order(StringComparer.OrdinalIgnoreCase).Where(course => !filters.Any(filter => filter.Source == FilterSource.Class && filter.Course == course)))
        {
            InsertFilter(connection, course, FilterSource.Class, course, next++);
        }

        if (!filters.Any(filter => filter.Source == FilterSource.Personal))
        {
            InsertFilter(connection, PersonalFilter, FilterSource.Personal, null, next);
        }

        filters = ReadFilters(connection);
        transaction.Commit();

        return filters
            .Where(filter => filter.Source != FilterSource.Class || active.Contains(filter.Course!))
            .OrderBy(filter => filter.Source)
            .ThenBy(filter => filter.Position)
            .ThenBy(filter => filter.Id)
            .ToList();
    }

    /// <summary>Adds a filter of the user's own, such as Work, after the others.</summary>
    public FilterEdit CreateToDoFilter(string name)
    {
        var clean = name.Trim();
        if (clean.Length == 0)
        {
            return new FilterEdit(null, "A filter needs a name.");
        }

        var filters = GetToDoFilters();
        if (filters.Any(filter => string.Equals(filter.Name, clean, StringComparison.OrdinalIgnoreCase)))
        {
            return new FilterEdit(null, $"The name {clean} is already used.");
        }

        using var connection = Open();
        var position = filters.Count == 0 ? 0 : filters.Max(filter => filter.Position) + 1;
        var id = InsertFilter(connection, clean, FilterSource.Custom, null, position);
        return new FilterEdit(new ToDoFilter(id, clean, FilterSource.Custom, null, position));
    }

    /// <summary>Changes only the name shown, so a class filter still matches its course.</summary>
    public FilterEdit RenameToDoFilter(long id, string name)
    {
        var clean = name.Trim();
        var filters = GetToDoFilters();
        if (filters.FirstOrDefault(filter => filter.Id == id) is not { } found)
        {
            return new FilterEdit(null, "That filter is gone.");
        }

        if (clean.Length == 0)
        {
            return new FilterEdit(found, "A filter needs a name.");
        }

        if (filters.Any(filter => filter.Id != id && string.Equals(filter.Name, clean, StringComparison.OrdinalIgnoreCase)))
        {
            return new FilterEdit(found, $"The name {clean} is already used.");
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE todo_filters SET name = $name WHERE id = $id";
        command.Parameters.AddWithValue("$name", clean);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        return new FilterEdit(found with { Name = clean });
    }

    /// <summary>Saves the order filters were dragged into. Ids that aren't given keep their place.</summary>
    public void SetToDoFilterOrder(IReadOnlyList<long> ids)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        for (var position = 0; position < ids.Count; position++)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE todo_filters SET position = $position WHERE id = $id";
            command.Parameters.AddWithValue("$position", position);
            command.Parameters.AddWithValue("$id", ids[position]);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Takes away one of the user's own filters. Its items show under Personal, and keep their place
    /// in it, so <see cref="RestoreToDoFilter"/> puts everything back.
    /// </summary>
    public bool RemoveToDoFilter(long id, DateTime now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE todo_filters SET removed_at = $now WHERE id = $id AND source = 'Custom' AND removed_at IS NULL";
        command.Parameters.AddWithValue("$now", FormatReminder(now));
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    public void RestoreToDoFilter(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE todo_filters SET removed_at = NULL WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Files an item with no class under one of the user's filters, or back under Personal with null.
    /// Feed items and items with a class already have their place, so they're left alone.
    /// </summary>
    public bool AssignToFilter(StoredItem item, long? filterId)
    {
        if (item.Source != ItemSource.Capture || item.Course is not null)
        {
            return false;
        }

        using var connection = Open();
        if (filterId is { } id && ReadFilters(connection).FirstOrDefault(filter => filter.Id == id) is not { Source: FilterSource.Custom })
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE items SET filter_id = $filter WHERE id = $id AND (course IS NULL OR course = '')";
        command.Parameters.AddWithValue("$filter", (object?)filterId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", item.Id);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>Course, filter, and end time for items that have any of them, by item id. Feed items aren't in it.</summary>
    public IReadOnlyDictionary<long, ItemPlacement> GetItemPlacements()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.id, i.course, CASE WHEN f.removed_at IS NULL THEN i.filter_id END, i.end_time
            FROM items i LEFT JOIN todo_filters f ON f.id = i.filter_id
            WHERE i.course IS NOT NULL OR i.filter_id IS NOT NULL OR i.end_time IS NOT NULL
            """;

        var placements = new Dictionary<long, ItemPlacement>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            placements[reader.GetInt64(0)] = new ItemPlacement(
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : TimeOnly.Parse(reader.GetString(3), CultureInfo.InvariantCulture));
        }

        return placements;
    }

    /// <summary>Keeps an event's length when its start moves.</summary>
    private static void ShiftEndTime(SqliteConnection connection, StoredItem before, TimeOnly? start)
    {
        if (start is not { } newStart || before.DueAt is not { } oldStart)
        {
            return;
        }

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT end_time FROM items WHERE id = $id";
        read.Parameters.AddWithValue("$id", before.Id);
        if (read.ExecuteScalar() is not string stored)
        {
            return;
        }

        var length = TimeOnly.Parse(stored, CultureInfo.InvariantCulture) - TimeOnly.FromDateTime(oldStart.DateTime);
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE items SET end_time = $end WHERE id = $id";
        update.Parameters.AddWithValue("$end", newStart.Add(length).ToString("HH:mm", CultureInfo.InvariantCulture));
        update.Parameters.AddWithValue("$id", before.Id);
        update.ExecuteNonQuery();
    }

    /// <summary>Short course codes with anything behind them right now: open feed events, items, or the schedule.</summary>
    private HashSet<string> ActiveCourses(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT course FROM calendar_events WHERE course IS NOT NULL AND {EventDueDate} >= $today
            UNION SELECT course FROM items WHERE course IS NOT NULL AND done = 0
            """;
        command.Parameters.AddWithValue("$today", DateOnly.FromDateTime(DateTime.Now).ToString("O", CultureInfo.InvariantCulture));

        var courses = new HashSet<string>(ScheduleCourses.Where(course => !string.IsNullOrWhiteSpace(course)).Select(course => ItemGroups.ShortCourse(course.Trim())));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(0).Trim() is { Length: > 0 } course)
            {
                courses.Add(ItemGroups.ShortCourse(course));
            }
        }

        return courses;
    }

    private static List<ToDoFilter> ReadFilters(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, source, course, position FROM todo_filters WHERE removed_at IS NULL";

        var filters = new List<ToDoFilter>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            filters.Add(new ToDoFilter(
                reader.GetInt64(0),
                reader.GetString(1),
                Enum.Parse<FilterSource>(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4)));
        }

        return filters;
    }

    private static long InsertFilter(SqliteConnection connection, string name, FilterSource source, string? course, int position)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO todo_filters (name, source, course, position) VALUES ($name, $source, $course, $position) RETURNING id";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$source", source.ToString());
        command.Parameters.AddWithValue("$course", (object?)course ?? DBNull.Value);
        command.Parameters.AddWithValue("$position", position);
        return (long)command.ExecuteScalar()!;
    }
}
