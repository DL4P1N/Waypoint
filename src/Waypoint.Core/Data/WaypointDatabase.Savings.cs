using System.Globalization;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

/// <summary>
/// The optional savings goal, kept as preferences. Progress counts money added to savings, cushion, and invested
/// accounts since the goal was set, so it only moves when those balances do.
/// </summary>
public sealed partial class WaypointDatabase
{
    private const string GoalCentsKey = "savings_goal_cents";
    private const string GoalByKey = "savings_goal_by";
    private const string GoalSetOnKey = "savings_goal_set_on";
    private const string GoalStartKey = "savings_goal_start_cents";
    private const long MaxGoalCents = 100_000_000;

    public SavingsGoal? GetSavingsGoal()
    {
        if (!long.TryParse(GetPreference(GoalCentsKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cents) || cents <= 0
            || !DateOnly.TryParse(GetPreference(GoalSetOnKey), CultureInfo.InvariantCulture, out var setOn))
        {
            return null;
        }

        var by = DateOnly.TryParse(GetPreference(GoalByKey), CultureInfo.InvariantCulture, out var date) ? date : (DateOnly?)null;
        var start = long.TryParse(GetPreference(GoalStartKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var held) ? held : 0;
        return new SavingsGoal(cents, by, setOn, start);
    }

    /// <summary>
    /// Sets the goal, or changes the amount or date of the one there is. Changing it keeps the day it started, so
    /// money already saved toward it still counts. The date is optional.
    /// </summary>
    public OpReply SetSavingsGoal(long? cents, DateTime? by, DateOnly today)
    {
        if (cents is not > 0 || cents > MaxGoalCents)
        {
            return new OpReply("How much do you want to save?", true);
        }

        var date = by is { } given ? DateOnly.FromDateTime(given) : (DateOnly?)null;
        if (date is { } end && end <= today)
        {
            return new OpReply("What date do you want to reach it by? It needs to be after today.", true);
        }

        var existing = GetSavingsGoal();
        var setOn = existing?.SetOn ?? today;
        var start = existing?.StartCents ?? SavingsPlan.SavedBalance(GetCurrentAccounts(today));
        SetPreference(GoalCentsKey, cents.Value.ToString(CultureInfo.InvariantCulture));
        SetPreference(GoalByKey, date?.ToString("O", CultureInfo.InvariantCulture) ?? "");
        SetPreference(GoalSetOnKey, setOn.ToString("O", CultureInfo.InvariantCulture));
        SetPreference(GoalStartKey, start.ToString(CultureInfo.InvariantCulture));
        return new OpReply(DescribeSavingsGoal(today));
    }

    public OpReply ClearSavingsGoal()
    {
        if (GetSavingsGoal() is null)
        {
            return new OpReply("There's no savings goal to remove.", true);
        }

        SetPreference(GoalCentsKey, "");
        SetPreference(GoalByKey, "");
        SetPreference(GoalSetOnKey, "");
        SetPreference(GoalStartKey, "");
        return new OpReply("Removed your savings goal");
    }

    public SavingsGoalStatus? GetSavingsGoalStatus(DateOnly today) =>
        GetSavingsGoal() is { } goal
            ? SavingsPlan.Status(goal, GetCurrentAccounts(today), today, GetForecastSchedule(today), GetPayDeposits())
            : null;

    public string DescribeSavingsGoal(DateOnly today) =>
        GetSavingsGoalStatus(today) is { } status
            ? SavingsPlan.Describe(status, today)
            : "There's no savings goal yet. Tell me one, such as \"I want to save $500 by March\".";
}
