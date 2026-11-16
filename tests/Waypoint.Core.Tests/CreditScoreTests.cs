using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

/// <summary>The credit score log, kept only as typed.</summary>
public sealed class CreditScoreTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-{Guid.NewGuid():N}.db");
    private readonly WaypointDatabase _database;

    public CreditScoreTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private OpReply Say(string? score, string? source = null, DateTime? at = null, string? note = null) =>
        AssistantOps.Apply(new AssistantOp(AssistantOpKind.CreditScoreAdd, source, score, note is null ? null : [note], At: at), _database, 0, Now);

    [Fact]
    public void A_typed_score_is_logged_with_its_source_and_today()
    {
        var reply = Say("720", "ScoreCheck");

        Assert.Equal("Logged a credit score of 720 from ScoreCheck on Nov 9", reply.Text);
        var entry = Assert.Single(_database.GetCreditScores());
        Assert.Equal((new DateOnly(2026, 11, 9), 720, "ScoreCheck", (string?)null), (entry.On, entry.Score, entry.Source, entry.Note));
    }

    [Fact]
    public void A_later_score_says_how_it_moved_since_the_one_before()
    {
        Say("700", "Contoso Card", new DateTime(2026, 10, 12));

        var reply = Say("720", "ScoreCheck", note: "after paying Contoso Card down");

        Assert.EndsWith("up 20 since Oct 12", reply.Text);
        Assert.Equal([720, 700], _database.GetCreditScores().Select(entry => entry.Score));
        Assert.Equal("after paying Contoso Card down", _database.GetCreditScores()[0].Note);
    }

    [Fact]
    public void A_score_has_to_be_a_real_score_on_a_day_that_has_happened()
    {
        Assert.True(Say("seven").IsAnswer);
        Assert.True(Say("1200").IsAnswer);
        Assert.True(Say("250").IsAnswer);
        Assert.Contains("hasn't come yet", Say("720", at: Now.AddDays(2)).Text);
        Assert.Empty(_database.GetCreditScores());
    }

    [Fact]
    public void The_log_shows_newest_first_on_the_balances_section_and_in_the_chat()
    {
        Say("680", "CreditView", new DateTime(2026, 9, 1));
        Say("720", "ScoreCheck", note: "after paying Contoso Card down");

        var section = _database.GetCreditScoreSection();
        Assert.Equal("CREDIT SCORE NOTES", section.Title);
        Assert.Equal(
            [("Nov 9  ·  ScoreCheck  ·  after paying Contoso Card down", "720"), ("Sep 1  ·  CreditView", "680")],
            section.Rows.Select(row => (row.Label, row.Value)));

        var shown = AssistantOps.Apply(new AssistantOp(AssistantOpKind.CreditScoreShow), _database, 0, Now);
        Assert.True(shown.IsAnswer);
        Assert.Equal("Nov 9: 720 from ScoreCheck, after paying Contoso Card down\nSep 1: 680 from CreditView", shown.Text);
    }

    [Fact]
    public void Only_the_last_few_are_shown_and_none_says_how_to_add_one()
    {
        Assert.Contains("No credit scores logged yet", _database.DescribeCreditScores());

        for (var day = 1; day <= 8; day++)
        {
            Say((700 + day).ToString(System.Globalization.CultureInfo.InvariantCulture), at: new DateTime(2026, 10, day));
        }

        Assert.Equal(WaypointDatabase.CreditScoresShown, _database.GetCreditScoreSection().Rows.Count);
        Assert.Equal(708, _database.GetCreditScores()[0].Score);
    }

    [Fact]
    public void The_sorter_knows_the_score_ops()
    {
        Assert.Contains("credit_score_add", ClaudeCliSorter.Schema);
        Assert.True(AssistantOps.TryParseKind("credit_score_show", out var kind));
        Assert.Equal(AssistantOpKind.CreditScoreShow, kind);
    }
}
