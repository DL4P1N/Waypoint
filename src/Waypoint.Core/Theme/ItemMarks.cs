using Waypoint.Core.Capture;

namespace Waypoint.Core.Theme;

public enum ItemMark
{
    Urgent,
    Soon,
    School,
    Money,
    Personal,
}

/// <summary>
/// Picks the small color mark beside a row. The row's text already says the same thing, and anything
/// past its date keeps its plain category, because a list of red late items is only guilt.
/// </summary>
public static class ItemMarks
{
    private const int ExamSoonDays = 3;

    public static ItemMark For(StoredItem item, DateOnly today)
    {
        if (item.Done)
        {
            return ItemMark.Personal;
        }

        if (item.DueDate == today && item.Recurs is null && item.Kind is ItemKind.Deadline or ItemKind.Exam or ItemKind.Task)
        {
            return ItemMark.Urgent;
        }

        if (item.Kind == ItemKind.Exam && item.DueDate is { } date && date > today && date.DayNumber - today.DayNumber <= ExamSoonDays)
        {
            return ItemMark.Soon;
        }

        return item.Kind switch
        {
            ItemKind.Deadline or ItemKind.Exam => ItemMark.School,
            ItemKind.Shopping or ItemKind.Expense => ItemMark.Money,
            _ => ItemMark.Personal,
        };
    }

    /// <summary>What a mark means, for the tip on hover. Plain rows have no mark to explain.</summary>
    public static string? Describe(ItemMark mark) => mark switch
    {
        ItemMark.Urgent => "Due today",
        ItemMark.Soon => "Exam in the next 3 days",
        ItemMark.School => "Schoolwork",
        ItemMark.Money => "Shopping or money",
        _ => null,
    };
}
