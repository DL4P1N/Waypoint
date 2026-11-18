using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Waypoint.Core.Data;

/// <summary>What Discord needs to find the card an answer belongs to.</summary>
public sealed partial class WaypointDatabase
{
    /// <summary>
    /// The newest reminder card that can still be finished or snoozed, the same one an answer with no card in mind
    /// acts on, so that card can turn gray too. With <paramref name="before"/>, the typed answer's own message, only
    /// cards sent before it count, so an answer handed over again later can't land on a card that came after it.
    /// Null when there isn't one.
    /// </summary>
    public string? LatestSentReminderMessage(string? before = null)
    {
        using var connection = Open();
        return LatestSentReminder(connection, before);
    }

    private static string? LatestSentReminder(SqliteConnection connection, string? before)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT message_id FROM sent_reminders WHERE handled = 0 AND start_only = 0 ORDER BY rowid DESC";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var card = reader.GetString(0);
            if (SentBefore(card, before))
            {
                return card;
            }
        }

        return null;
    }

    /// <summary>Discord's IDs grow with time, so a smaller one was sent first. IDs that aren't Discord's are let through.</summary>
    private static bool SentBefore(string card, string? answer) =>
        answer is null
        || !ulong.TryParse(card, NumberStyles.None, CultureInfo.InvariantCulture, out var sent)
        || !ulong.TryParse(answer, NumberStyles.None, CultureInfo.InvariantCulture, out var typed)
        || sent < typed;
}
