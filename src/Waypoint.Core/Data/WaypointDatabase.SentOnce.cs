using System.Globalization;
using System.Text.Json;
using Waypoint.Core.Remote;

namespace Waypoint.Core.Data;

/// <summary>
/// Changes a PC sends with a key of its own, such as an expense or a dose taken, so the same one sent again after its
/// answer was lost is answered as before instead of done twice. Each key is kept with the answer it got, for a day,
/// which is far longer than a PC goes on sending the same again.
/// </summary>
public sealed partial class WaypointDatabase
{
    /// <summary>How long a key and its answer are kept.</summary>
    public static readonly TimeSpan SentOnceKept = TimeSpan.FromDays(1);

    private void CreateSentOnceTables() => Execute("""
        CREATE TABLE IF NOT EXISTS sent_once (
            key TEXT PRIMARY KEY,
            answer TEXT NOT NULL,
            at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS sent_once_at ON sent_once (at);
        """);

    /// <summary>
    /// Does <paramref name="work"/> on the write's database the first time <paramref name="key"/> is seen, and keeps
    /// what it answered under the key in the same write, so the change and its key land together or not at all. Seen
    /// again, it gives back that answer and does nothing.
    /// </summary>
    public T Once<T>(string key, DateTime now, Func<WaypointDatabase, T> work)
    {
        lock (_root._sentOnce)
        {
            return InOneWrite(write =>
            {
                if (write.SentOnceAnswer(key) is { } kept)
                {
                    return JsonSerializer.Deserialize<T>(kept, RemoteJson.Options)!;
                }

                var answer = work(write);
                write.KeepSentOnce(key, JsonSerializer.Serialize(answer, RemoteJson.Options), now);
                return answer;
            });
        }
    }

    /// <summary>The answer kept for <paramref name="key"/>, as JSON, or null if it hasn't been seen or was forgotten.</summary>
    public string? SentOnceAnswer(string key)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT answer FROM sent_once WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    /// <summary>Keeps the answer for a key, and forgets keys older than <see cref="SentOnceKept"/>.</summary>
    private void KeepSentOnce(string key, string answer, DateTime now)
    {
        var at = now.Kind == DateTimeKind.Local ? now.ToUniversalTime() : DateTime.SpecifyKind(now, DateTimeKind.Utc);
        using var lease = Connect(out var connection);
        using (var forget = connection.CreateCommand())
        {
            forget.CommandText = "DELETE FROM sent_once WHERE at < $before";
            forget.Parameters.AddWithValue("$before", (at - SentOnceKept).ToString("O", CultureInfo.InvariantCulture));
            forget.ExecuteNonQuery();
        }

        using var keep = connection.CreateCommand();
        keep.CommandText = "INSERT OR REPLACE INTO sent_once (key, answer, at) VALUES ($key, $answer, $at)";
        keep.Parameters.AddWithValue("$key", key);
        keep.Parameters.AddWithValue("$answer", answer);
        keep.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture));
        keep.ExecuteNonQuery();
    }
}
