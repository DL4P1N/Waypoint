using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Tests;

/// <summary>
/// A card is noted before it's posted, so a crash between posting it and saving what it's for leaves exactly one card,
/// and its button still does its one thing. A failing trigger stands in for the crash. No network.
/// </summary>
public sealed partial class DiscordPhoneTests
{
    [Fact]
    public async Task A_reminder_card_posted_just_before_a_crash_is_saved_on_the_next_start_and_not_posted_again()
    {
        var (task, key) = DueReminderFor("Made up call to the dentist");
        var first = Worker(toDo: null, lists: null);

        // The card goes out and its message is noted, then Waypoint stops before the card is saved.
        FailPartway("BEFORE INSERT ON sent_reminders");
        first.CheckAlerts();
        await Until(() => _database.GetDiscordSend(key)?.MessageId is not null);
        first.StopDiscord();
        StopFailingPartway();
        Assert.Equal(0, Rows("sent_reminders"));

        var second = Worker(toDo: null, lists: null);
        second.CheckAlerts();
        await Until(() => Rows("sent_reminders") == 1);

        var card = Assert.Single(CardsAbout("Made up call to the dentist"));
        Assert.Equal(1, Rows("sent_reminders", $"message_id = '{card.Id}'"));
        Assert.Equal(0, Rows("discord_sends"));
        AssertOneAction(second, card.Id, task);
    }

    [Fact]
    public async Task A_reminder_card_made_but_never_heard_back_about_is_found_in_the_channel_instead_of_posted_again()
    {
        var (task, key) = DueReminderFor("Made up library return");
        var first = Worker(toDo: null, lists: null);

        // Discord makes the card, then Waypoint stops before it hears which message it is.
        FailPartway("BEFORE UPDATE OF message_id ON discord_sends");
        first.CheckAlerts();
        await Until(() => _discord.MadeMessages.Count > 0 && _database.GetDiscordSend(key) is not null);
        first.StopDiscord();
        StopFailingPartway();
        Assert.Null(_database.GetDiscordSend(key)!.MessageId);

        // Long enough later that Discord no longer keeps to the nonce, so only looking in the channel finds it.
        _discord.ForgetNonces();
        _discord.ListMade = true;
        var second = Worker(toDo: null, lists: null);
        second.CheckAlerts();
        await Until(() => Rows("sent_reminders") == 1);

        var card = Assert.Single(CardsAbout("Made up library return"));
        Assert.Single(_discord.Of(HttpMethod.Post), request => request.Body.Contains("Made up library return"));
        Assert.Equal(1, Rows("sent_reminders", $"message_id = '{card.Id}'"));
        AssertOneAction(second, card.Id, task);
    }

    [Fact]
    public async Task A_reminder_card_tried_again_soon_after_carries_the_same_nonce_so_discord_makes_it_once()
    {
        var (task, key) = DueReminderFor("Made up form to sign");
        var first = Worker(toDo: null, lists: null);
        FailPartway("BEFORE UPDATE OF message_id ON discord_sends");
        first.CheckAlerts();
        await Until(() => _discord.MadeMessages.Count > 0 && _database.GetDiscordSend(key) is not null);
        first.StopDiscord();
        StopFailingPartway();

        // The channel can't be searched this time, so the card is sent again with its nonce, within Discord's minutes.
        var second = Worker(toDo: null, lists: null);
        second.CheckAlerts();
        await Until(() => Rows("sent_reminders") == 1);

        var posts = _discord.Of(HttpMethod.Post).Where(request => request.Body.Contains("Made up form to sign")).ToList();
        Assert.Equal(2, posts.Count);
        Assert.All(posts, post => Assert.Contains("\"enforce_nonce\":true", post.Body));
        Assert.Equal(posts[0].Body, posts[1].Body);
        var card = Assert.Single(CardsAbout("Made up form to sign"));
        AssertOneAction(second, card.Id, task);
    }

    /// <summary>A made up task with a reminder already due.</summary>
    private (StoredItem Task, string Key) DueReminderFor(string title)
    {
        // The morning briefing would name the task too, so it's had already today.
        _database.SetPreference("last_briefing", Today.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        var task = AddTask(title, Today);
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO reminders (item_id, remind_at) VALUES ($item, $at)";
        command.Parameters.AddWithValue("$item", task.Id);
        command.Parameters.AddWithValue("$at", _now.AddMinutes(-1).ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        command.CommandText = "SELECT last_insert_rowid()";
        return (task, $"reminder:item:{command.ExecuteScalar()}");
    }

    private List<(string Channel, string Id, string Body)> CardsAbout(string title)
    {
        lock (_discord.MadeMessages)
        {
            return _discord.MadeMessages.Where(made => made.Channel == "alerts" && made.Body.Contains(title)).ToList();
        }
    }

    /// <summary>Done on the card finishes the task, and pressing it again does nothing more.</summary>
    private void AssertOneAction(Background.WaypointWorker worker, string card, StoredItem task)
    {
        var before = Rows("item_history", $"item_id = {task.Id}");
        Assert.NotNull(worker.OnButtonPressed(card, AlertActionKind.Done));
        Assert.DoesNotContain(_database.GetOpenItems(), item => item.Id == task.Id);
        Assert.Equal(1, Rows("sent_reminders", "handled = 1"));
        Assert.Equal(before + 1, Rows("item_history", $"item_id = {task.Id}"));

        worker.OnButtonPressed(card, AlertActionKind.Done);
        Assert.Equal(1, Rows("sent_reminders", "handled = 1"));
        Assert.Equal(before + 1, Rows("item_history", $"item_id = {task.Id}"));
    }

    private static async Task Until(Func<bool> done)
    {
        for (var waited = 0; waited < 400 && !Try(done); waited++)
        {
            await Task.Delay(25);
        }

        Assert.True(Try(done));
    }

    /// <summary>A check that hits a write still going counts as not yet.</summary>
    private static bool Try(Func<bool> check)
    {
        try
        {
            return check();
        }
        catch (SqliteException)
        {
            return false;
        }
    }
}
