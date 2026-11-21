namespace Waypoint.Core.Tests;

/// <summary>A card whose try may have gone out is looked for however long ago it was, and never posted again just for its age.</summary>
public sealed partial class DiscordPhoneTests
{
    [Fact]
    public void A_send_left_unsettled_for_weeks_keeps_its_nonce_and_isnt_treated_as_new()
    {
        var first = _database.BeginDiscordSend("reminder:made-up", "alerts", _now);
        var later = _database.BeginDiscordSend("reminder:made-up", "alerts", _now.AddDays(30));

        Assert.True(first.Fresh);
        Assert.False(later.Fresh);
        Assert.Equal(first.Nonce, later.Nonce);
        Assert.Equal(first.StartedAt, later.StartedAt);
    }

    [Fact]
    public async Task A_reminder_card_posted_before_a_crash_a_week_ago_is_found_instead_of_posted_again()
    {
        var (task, key) = DueReminderFor("Made up passport renewal");
        var first = Worker(toDo: null, lists: null);
        FailPartway("BEFORE UPDATE OF message_id ON discord_sends");
        first.CheckAlerts();
        await Until(() => _discord.MadeMessages.Count > 0 && _database.GetDiscordSend(key) is not null);
        first.StopDiscord();
        StopFailingPartway();

        // Over a week later, with the reminder still due and the day's briefing already had.
        _now = _now.AddDays(8);
        _database.SetPreference("last_briefing", DateOnly.FromDateTime(_now).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        _discord.ForgetNonces();
        _discord.ListMade = true;
        var second = Worker(toDo: null, lists: null);
        second.CheckAlerts();
        await Until(() => Rows("sent_reminders") == 1);

        var card = Assert.Single(CardsAbout("Made up passport renewal"));
        Assert.Single(_discord.Of(HttpMethod.Post), request => request.Body.Contains("Made up passport renewal"));
        Assert.Equal(1, Rows("sent_reminders", $"message_id = '{card.Id}'"));
        AssertOneAction(second, card.Id, task);
    }
}
