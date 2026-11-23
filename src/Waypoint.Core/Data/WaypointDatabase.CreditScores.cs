using System.Globalization;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

/// <summary>A credit score the user typed in, with where they saw it. Nothing is looked up or tracked beyond these.</summary>
public sealed record CreditScoreNote(long Id, DateOnly On, int Score, string? Source, string? Note);

/// <summary>A plain log of credit scores, only as typed. Waypoint never checks a score itself.</summary>
public sealed partial class WaypointDatabase
{
    public const int LowestScore = 300;
    public const int HighestScore = 850;
    public const int CreditScoresShown = 6;

    private void CreateCreditScoreTables() => Execute("""
        CREATE TABLE IF NOT EXISTS credit_scores (
            id INTEGER PRIMARY KEY,
            on_date TEXT NOT NULL,
            score INTEGER NOT NULL,
            source TEXT,
            note TEXT,
            added_at TEXT NOT NULL
        );
        """);

    /// <summary>Logs a score. The date is today unless one is given, and the source and note are optional.</summary>
    public OpReply AddCreditScore(string? scoreText, string? source, DateTime? at, string? note, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        if (!int.TryParse(scoreText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var score) || score is < LowestScore or > HighestScore)
        {
            return new OpReply($"What was the score? It's a number from {LowestScore} to {HighestScore}.", true);
        }

        var on = at is { } given ? DateOnly.FromDateTime(given) : today;
        if (on > today)
        {
            return new OpReply("That date hasn't come yet. What day did you see the score?", true);
        }

        var earlier = GetCreditScores(50).FirstOrDefault(entry => entry.On <= on);
        var from = Trimmed(source);
        using (Connect(out var connection))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO credit_scores (on_date, score, source, note, added_at) VALUES ($on, $score, $source, $note, $now)";
            command.Parameters.AddWithValue("$on", on.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$score", score);
            command.Parameters.AddWithValue("$source", (object?)from ?? DBNull.Value);
            command.Parameters.AddWithValue("$note", (object?)Trimmed(note) ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        var change = earlier is null ? "" : $", {ScoreChange(score - earlier.Score)} since {ShortDate(earlier.On)}";
        var where = from is null ? "" : $" from {from}";
        return new OpReply($"Logged a credit score of {score}{where} on {ShortDate(on)}{change}");
    }

    /// <summary>The most recent scores, newest first.</summary>
    public IReadOnlyList<CreditScoreNote> GetCreditScores(int limit = CreditScoresShown)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, on_date, score, source, note FROM credit_scores ORDER BY on_date DESC, id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);

        var scores = new List<CreditScoreNote>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            scores.Add(new CreditScoreNote(
                reader.GetInt64(0),
                DateOnly.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return scores;
    }

    public string DescribeCreditScores()
    {
        var scores = GetCreditScores();
        return scores.Count == 0
            ? "No credit scores logged yet. Tell me one, such as \"credit score 720 on ScoreCheck\"."
            : string.Join("\n", scores.Select(entry => $"{ShortDate(entry.On)}: {entry.Score}{(entry.Source is { } source ? $" from {source}" : "")}{(entry.Note is { } note ? $", {note}" : "")}"));
    }

    /// <summary>The log for the Balances page, newest first.</summary>
    public ReportSection GetCreditScoreSection() =>
        new("CREDIT SCORE NOTES", GetCreditScores().Select(entry => new ReportRow(
            string.Join("  ·  ", new[] { ShortDate(entry.On), entry.Source, entry.Note }.Where(part => !string.IsNullOrEmpty(part))),
            entry.Score.ToString(CultureInfo.InvariantCulture))).ToList());

    private static string ScoreChange(int points) => points switch
    {
        > 0 => $"up {points}",
        < 0 => $"down {-points}",
        _ => "the same",
    };

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
