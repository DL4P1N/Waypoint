namespace Waypoint.Core.Data;

/// <summary>What Discord needs to find the card an answer belongs to.</summary>
public sealed partial class WaypointDatabase
{
    /// <summary>
    /// The newest reminder card that can still be finished or snoozed, the same one an answer with no card in mind
    /// acts on, so that card can turn gray too. Null when there isn't one.
    /// </summary>
    public string? LatestSentReminderMessage()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT message_id FROM sent_reminders WHERE handled = 0 AND start_only = 0 ORDER BY rowid DESC LIMIT 1";
        return command.ExecuteScalar() as string;
    }
}
