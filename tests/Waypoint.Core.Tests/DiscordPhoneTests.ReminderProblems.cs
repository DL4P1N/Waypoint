namespace Waypoint.Core.Tests;

/// <summary>A reminder that hits a problem before its card goes out isn't left quiet until Waypoint starts again.</summary>
public sealed partial class DiscordPhoneTests
{
    [Fact]
    public async Task A_reminder_whose_send_fails_before_it_starts_goes_out_on_the_next_round()
    {
        var (task, _) = DueReminderFor("Made up gym class");
        var worker = Worker(toDo: null, lists: null);

        // Noting the card fails, so the send throws before anything goes out.
        FailPartway("BEFORE INSERT ON discord_sends");
        worker.CheckAlerts();
        Assert.Empty(CardsAbout("Made up gym class"));

        StopFailingPartway();
        worker.CheckAlerts();
        await Until(() => Rows("sent_reminders") == 1);

        var card = Assert.Single(CardsAbout("Made up gym class"));
        AssertOneAction(worker, card.Id, task);
    }

    [Fact]
    public async Task A_notice_that_hits_a_problem_is_logged_instead_of_thrown_at_whoever_started_it()
    {
        var logged = new List<Exception>();
        var before = PlainProblem.Log;
        PlainProblem.Log = exception =>
        {
            lock (logged)
            {
                logged.Add(exception);
            }
        };

        try
        {
            // Discord is down, and the pause before trying again fails.
            var worker = Worker(toDo: null, lists: null);
            worker.RetryWait = _ => Task.FromException(new InvalidOperationException("made up retry problem"));
            _discord.FailWith = "made up outage";
            await worker.NotifyAsync("Made up deposit", "Added");

            lock (logged)
            {
                Assert.Contains(logged, exception => exception.Message == "made up retry problem");
            }
        }
        finally
        {
            PlainProblem.Log = before;
        }
    }
}
