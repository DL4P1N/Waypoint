namespace Waypoint.Core.Budget;

/// <summary>US federal bank holidays, worked out locally. One on a Sunday is observed on Monday, and a Saturday one isn't moved.</summary>
public static class BankHolidays
{
    public static bool IsHoliday(DateOnly date)
    {
        var observedFromSunday = date.DayOfWeek == DayOfWeek.Monday && IsFixed(date.AddDays(-1));
        return (date.DayOfWeek != DayOfWeek.Sunday && IsFixed(date)) || observedFromSunday || IsFloating(date);
    }

    private static bool IsFixed(DateOnly date) => (date.Month, date.Day) is (1, 1) or (6, 19) or (7, 4) or (11, 11) or (12, 25);

    private static bool IsFloating(DateOnly date)
    {
        var week = ((date.Day - 1) / 7) + 1;
        var isLastWeek = date.Day + 7 > DateTime.DaysInMonth(date.Year, date.Month);
        return (date.DayOfWeek, date.Month, week) switch
        {
            (DayOfWeek.Monday, 1, 3) => true,
            (DayOfWeek.Monday, 2, 3) => true,
            (DayOfWeek.Monday, 9, 1) => true,
            (DayOfWeek.Monday, 10, 2) => true,
            (DayOfWeek.Thursday, 11, 4) => true,
            (DayOfWeek.Monday, 5, _) => isLastWeek,
            _ => false,
        };
    }
}
