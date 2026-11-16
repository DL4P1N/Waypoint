using System.Globalization;

namespace Waypoint.Core.Data;

/// <summary>A notification that couldn't reach Discord, kept until it's sent or shown somewhere.</summary>
public sealed record UndeliveredNotice(long Id, string Title, string Text, DateTime At);

/// <summary>
/// Notifications a machine with no screen couldn't get to Discord, such as "Time to leave" or a briefing. They're
/// kept here, so a restart doesn't lose them, until Discord takes them or the PC shows them instead.
/// </summary>
public sealed partial class WaypointDatabase
{
    private void CreateUndeliveredTables() => Execute("""
        CREATE TABLE IF NOT EXISTS undelivered_notices (
            id INTEGER PRIMARY KEY,
            title TEXT NOT NULL,
            text TEXT NOT NULL,
            at TEXT NOT NULL
        );
        """);

    public void AddUndeliveredNotice(string title, string text, DateTime at)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO undelivered_notices (title, text, at) VALUES ($title, $text, $at)";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$at", FormatReminder(at));
        command.ExecuteNonQuery();
    }

    /// <summary>Oldest first, the order they were meant to arrive in.</summary>
    public IReadOnlyList<UndeliveredNotice> GetUndeliveredNotices()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, text, at FROM undelivered_notices ORDER BY id";
        var notices = new List<UndeliveredNotice>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            notices.Add(new UndeliveredNotice(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
        }

        return notices;
    }

    /// <summary>Forgets a notice once it's been delivered. Only the first to ask gets true, so it's never shown twice.</summary>
    public bool MarkNoticeDelivered(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM undelivered_notices WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() == 1;
    }
}
