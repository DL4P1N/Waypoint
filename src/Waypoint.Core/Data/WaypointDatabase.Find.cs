using Waypoint.Core.Budget;

namespace Waypoint.Core.Data;

public sealed partial class WaypointDatabase
{
    /// <summary>
    /// Purchases from any month whose merchant or category has every word typed, newest first, for Find anywhere.
    /// It matches the way the Budget page's own search does, just not only in one month.
    /// </summary>
    public IReadOnlyList<SpendingRow> SearchPurchases(string? words, int limit)
    {
        var parts = (words ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || limit <= 0)
        {
            return [];
        }

        return GetSpending(DateOnly.MinValue, DateOnly.MaxValue)
            .Where(row => parts.All(part =>
                row.Title.Contains(part, StringComparison.OrdinalIgnoreCase)
                || row.Category.Contains(part, StringComparison.OrdinalIgnoreCase)
                || MerchantName.Clean(row.Title).Contains(part, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(row => row.Date)
            .ThenByDescending(row => row.Id)
            .Take(limit)
            .ToList();
    }
}
