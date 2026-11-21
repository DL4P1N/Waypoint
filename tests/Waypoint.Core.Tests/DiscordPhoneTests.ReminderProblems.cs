namespace Waypoint.Core.Tests;

/// <summary>
/// A reminder that hits a problem before its card goes out isn't left quiet until Waypoint starts again, and the same
/// problem again isn't logged again. These set the shared log hook, so they share a collection with the tests that do.
/// </summary>
[Collection(PlainProblemTests.LogHook)]
public sealed partial class DiscordPhoneTests
{
    [Fact]
    public async Task A_reminder_whose_send_fails_before_it_starts_is_logged_once_and_tried_again_a_minute_later()
    {
        var (task, _) = DueReminderFor("Made up gym class");
        var worker = Worker(toDo: null, lists: null);
        var before = PlainProblem.Log;
        var logged = LogProblems();
        try
        {
            // Noting the card fails the same way twice, so the send throws before anything goes out.
            FailPartwayWith("BEFORE INSERT ON discord_sends", "made up reminder problem");
            worker.CheckAlerts();
            _now = _now.AddMinutes(1);
            worker.CheckAlerts();
            Assert.Empty(CardsAbout("Made up gym class"));
            Assert.Single(Logged(logged), message => message.Contains("made up reminder problem"));

            // Straight after a failure it waits, then it goes out once the minute is up.
            StopFailingPartway();
            worker.CheckAlerts();
            Assert.Empty(CardsAbout("Made up gym class"));
            _now = _now.AddMinutes(1);
            worker.CheckAlerts();
            await Until(() => Rows("sent_reminders") == 1);
        }
        finally
        {
            PlainProblem.Log = before;
        }

        var card = Assert.Single(CardsAbout("Made up gym class"));
        AssertOneAction(worker, card.Id, task);
    }

    [Fact]
    public async Task A_notice_that_hits_the_same_problem_again_is_logged_once_instead_of_thrown()
    {
        var before = PlainProblem.Log;
        var logged = LogProblems();
        try
        {
            // Discord is down, and the pause before trying again fails, the same way twice.
            var worker = Worker(toDo: null, lists: null);
            worker.RetryWait = _ => Task.FromException(new InvalidOperationException("made up retry problem"));
            _discord.FailWith = "made up outage";
            await worker.NotifyAsync("Made up deposit", "Added");
            await worker.NotifyAsync("Made up deposit", "Added");

            Assert.Single(Logged(logged), message => message == "made up retry problem");
        }
        finally
        {
            PlainProblem.Log = before;
        }
    }

    /// <summary>Keeps what's logged from here on.</summary>
    private static List<string> LogProblems()
    {
        var logged = new List<string>();
        PlainProblem.Log = exception =>
        {
            lock (logged)
            {
                logged.Add(exception.Message);
            }
        };
        return logged;
    }

    private static List<string> Logged(List<string> logged)
    {
        lock (logged)
        {
            return logged.ToList();
        }
    }

    /// <summary>Like FailPartway, with words of its own so its problem is told apart in the log.</summary>
    private void FailPartwayWith(string when, string message)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TRIGGER fail_partway {when} BEGIN SELECT RAISE(ABORT, '{message}'); END";
        command.ExecuteNonQuery();
    }
}
