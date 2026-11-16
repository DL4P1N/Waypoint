using Waypoint.Core.Data;

namespace Waypoint.Core.Capture;

/// <summary>Which To do rows a filter and the search box let through. Steps go with their parent, so only top rows are asked about.</summary>
public static class ToDoFilters
{
    /// <summary>The short course code an item belongs to, from the feed or from the item itself.</summary>
    public static string? CourseOf(StoredItem item, ItemPlacement? placement) =>
        (item.Course ?? placement?.Course) is { Length: > 0 } course ? ItemGroups.ShortCourse(course.Trim()) : null;

    /// <summary>A null filter is All.</summary>
    public static bool Matches(ToDoFilter? filter, StoredItem item, ItemPlacement? placement)
    {
        if (filter is null)
        {
            return true;
        }

        var course = CourseOf(item, placement);
        return filter.Source switch
        {
            FilterSource.Class => course == filter.Course,
            FilterSource.Personal => course is null && placement?.FilterId is null,
            _ => course is null && placement?.FilterId == filter.Id,
        };
    }

    /// <summary>Every word typed has to be in the title or the class, in any order.</summary>
    public static bool MatchesSearch(string? search, StoredItem item, ItemPlacement? placement, IEnumerable<StoredItem>? steps = null)
    {
        var words = (search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return true;
        }

        var text = string.Join(' ', new[] { item.Title, item.Course, placement?.Course }.Concat((steps ?? []).Select(step => step.Title)).Where(part => part is not null));
        return words.All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}
