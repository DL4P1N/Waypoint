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
    public async Task A_reminder_card_whose_nonce_has_lapsed_is_not_posted_again_while_the_channel_cant_be_read()
    {
        var (task, key) = DueReminderFor("Made up parcel pickup");
        var first = Worker(toDo: null, lists: null);
        FailPartway("BEFORE UPDATE OF message_id ON discord_sends");
        first.CheckAlerts();
        await Until(() => _discord.MadeMessages.Count > 0 && _database.GetDiscordSend(key) is not null);
        first.StopDiscord();
        StopFailingPartway();

        // Discord no longer keeps to the nonce, and the channel can't be read, so whether the card is there isn't known.
        _discord.ForgetNonces();
        _discord.ListFails = true;
        var second = Worker(toDo: null, lists: null);
        bool Looked() => _discord.All.Count(request => request.Method == HttpMethod.Get && request.Url.Contains("/channels/alerts/messages?")) >= 3;
        await Until(() =>
        {
            second.CheckAlerts();
            return Looked();
        });
        Assert.Single(CardsAbout("Made up parcel pickup"));
        Assert.Equal(0, Rows("sent_reminders"));
        Assert.NotNull(_database.GetDiscordSend(key));

        // Once the channel can be read, the card already there is found and kept.
        _discord.ListFails = false;
        _discord.ListMade = true;
        await Until(() =>
        {
            second.CheckAlerts();
            return Rows("sent_reminders") == 1;
        });

        var card = Assert.Single(CardsAbout("Made up parcel pickup"));
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

    [Fact]
    public async Task A_view_posted_just_before_a_crash_is_kept_on_the_next_start_and_not_posted_again()
    {
        AddTask("Made up reading", Today);

        // The views go out and are noted, then Waypoint stops before where they are is kept.
        FailPartway("BEFORE INSERT ON preferences WHEN NEW.key = 'discord_pinned_views'");
        var first = Worker(lists: null);
        await Record.ExceptionAsync(() => first.RefreshDiscordViewsAsync());
        await first.ViewsSettledAsync();
        first.StopDiscord();
        StopFailingPartway();
        var noted = _database.GetDiscordSends("view:");
        Assert.NotEmpty(noted);
        Assert.All(noted, send => Assert.NotNull(send.MessageId));
        var second = Worker(lists: null);
        await second.RefreshDiscordViewsAsync();
        await second.ViewsSettledAsync();
        AssertEachViewMadeOnce();
        Assert.Empty(_database.GetDiscordSends("view:"));

        // The view kept is the one edited when the list changes.
        AddTask("Made up essay", Today);
        _now = _now.AddMinutes(2);
        await second.RefreshDiscordViewsAsync();
        var edit = Assert.Single(_discord.Of(HttpMethod.Patch), patch => patch.Body.Contains("Made up essay"));
        Assert.Contains(noted, send => edit.Url.EndsWith("/messages/" + send.MessageId, StringComparison.Ordinal));
        AssertEachViewMadeOnce();
    }

    [Fact]
    public async Task A_view_made_but_never_heard_back_about_is_found_in_its_channel_and_brought_up_to_date()
    {
        AddTask("Made up reading", Today);
        FailPartway("BEFORE UPDATE OF message_id ON discord_sends");
        var first = Worker(lists: null);
        await Record.ExceptionAsync(() => first.RefreshDiscordViewsAsync());
        await first.ViewsSettledAsync();
        first.StopDiscord();
        StopFailingPartway();
        Assert.NotEmpty(ViewsMade());

        _discord.ForgetNonces();
        _discord.ListMade = true;
        var second = Worker(lists: null);
        await second.RefreshDiscordViewsAsync();
        await second.ViewsSettledAsync();

        AssertEachViewMadeOnce();
        Assert.Empty(_database.GetDiscordSends("view:"));

        // What it shows can't be told from the channel, so the next round draws it again in place.
        _now = _now.AddMinutes(2);
        await second.RefreshDiscordViewsAsync();
        Assert.Contains(_discord.Of(HttpMethod.Patch), patch => patch.Url.Contains("/channels/todo-ch/messages/") && patch.Body.Contains("Made up reading"));
        AssertEachViewMadeOnce();
    }

    [Fact]
    public async Task A_held_notice_sent_just_before_a_crash_goes_out_once_and_is_never_lost()
    {
        _database.HoldNotice("Made up deposit", "Added: $12.00", CardTone.Money, false);

        // It goes out and is noted, then Waypoint stops before it's let go.
        FailPartway("BEFORE DELETE ON held_notices");
        var first = Worker(toDo: null, lists: null);
        first.CheckAlerts();
        await Until(() => _database.GetDiscordSends("held:").SingleOrDefault()?.MessageId is not null);
        first.StopDiscord();
        StopFailingPartway();
        Assert.Single(_database.GetHeldNotices());

        var second = Worker(toDo: null, lists: null);
        second.CheckAlerts();
        await Until(() => _database.GetHeldNotices().Count == 0);

        // A round after it's let go starts no send of its own, and the one that sent it is waited out.
        second.CheckAlerts();
        await Until(() => !second.SendingHeld);

        Assert.Single(CardsAbout("Made up deposit"));
        Assert.Single(_discord.Of(HttpMethod.Post), request => request.Body.Contains("Made up deposit"));
        Assert.Empty(_database.GetDiscordSends("held:"));
    }

    [Fact]
    public async Task A_new_reminder_given_the_row_id_of_one_that_went_after_its_card_was_posted_still_gets_its_own_card()
    {
        var (_, firstKey) = DueReminderFor("Made up first reminder");
        var first = Worker(toDo: null, lists: null);
        FailPartway("BEFORE INSERT ON sent_reminders");
        first.CheckAlerts();
        await Until(() => _database.GetDiscordSend(firstKey)?.MessageId is not null);
        first.StopDiscord();
        StopFailingPartway();

        // The reminder is dealt with elsewhere before the next start, and a new one takes its row id.
        using (var connection = _database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM reminders";
            command.ExecuteNonQuery();
        }

        var (_, secondKey) = DueReminderFor("Made up second reminder");
        Assert.Equal(firstKey.Split(':')[2], secondKey.Split(':')[2]);

        var second = Worker(toDo: null, lists: null);
        second.CheckAlerts();
        await Until(() => Rows("sent_reminders") == 1);

        Assert.Single(CardsAbout("Made up second reminder"));
        Assert.Null(_database.GetDiscordSend(firstKey));
        Assert.Empty(_database.GetDiscordSends("reminder:"));
    }

    [Fact]
    public async Task A_held_notice_checked_on_again_and_again_while_it_goes_out_is_sent_once()
    {
        _database.HoldNotice("Made up refund", "Added: $7.00", CardTone.Money, false);
        var worker = Worker(toDo: null, lists: null);

        // Rounds keep coming while the send finishes, so one may land right as the notice is let go.
        for (var round = 0; round < 400 && Try(() => _database.GetHeldNotices().Count > 0); round++)
        {
            worker.CheckAlerts();
            await Task.Yield();
        }

        await Until(() => _database.GetHeldNotices().Count == 0);
        worker.CheckAlerts();
        await Until(() => !worker.SendingHeld);

        Assert.Single(_discord.Of(HttpMethod.Post), request => request.Body.Contains("Made up refund"));
        Assert.Empty(_database.GetDiscordSends("held:"));
    }

    [Fact]
    public async Task A_held_notice_given_the_id_of_one_whose_note_was_left_behind_still_goes_out()
    {
        _database.HoldNotice("Made up first deposit", "Added: $3.00", CardTone.Money, false);
        FailPartway("BEFORE DELETE ON held_notices");
        var first = Worker(toDo: null, lists: null);
        first.CheckAlerts();
        await Until(() => _database.GetDiscordSends("held:").SingleOrDefault()?.MessageId is not null);
        first.StopDiscord();
        StopFailingPartway();

        // The held notice goes some other way, and the next one held takes its id.
        var gone = Assert.Single(_database.GetHeldNotices());
        _database.TakeHeldNotices();
        _database.HoldNotice("Made up second deposit", "Added: $4.00", CardTone.Money, false);
        Assert.Equal(gone.Id, Assert.Single(_database.GetHeldNotices()).Id);

        var second = Worker(toDo: null, lists: null);
        second.CheckAlerts();
        await Until(() => _database.GetHeldNotices().Count == 0);

        Assert.Single(CardsAbout("Made up second deposit"));
        Assert.Empty(_database.GetDiscordSends("held:"));
    }

    [Fact]
    public async Task A_reminder_card_looked_for_after_a_crash_never_takes_a_card_already_kept_for_another_reminder()
    {
        DueReminderFor("Made up phone call");
        var worker = Worker(toDo: null, lists: null);
        worker.CheckAlerts();
        await Until(() => Rows("sent_reminders") == 1);

        // A second reminder that looks the same was noted, then Waypoint stopped before its card was made.
        var (_, key) = DueReminderFor("Made up phone call");
        _database.BeginDiscordSend(key, "alerts", _now);
        _discord.ListMade = true;
        var second = Worker(toDo: null, lists: null);
        second.CheckAlerts();
        await Until(() => Rows("sent_reminders") == 2);

        var cards = CardsAbout("Made up phone call");
        Assert.Equal(2, cards.Select(card => card.Id).Distinct().Count());
        Assert.All(cards, card => Assert.Equal(1, Rows("sent_reminders", $"message_id = '{card.Id}'")));
    }

    /// <summary>The messages Discord made in the views channel, by their title.</summary>
    private List<string> ViewsMade()
    {
        lock (_discord.MadeMessages)
        {
            return _discord.MadeMessages.Where(made => made.Channel == "todo-ch")
                .Select(made => System.Text.Json.JsonDocument.Parse(made.Body).RootElement.GetProperty("embeds")[0].GetProperty("title").GetString()!)
                .ToList();
        }
    }

    /// <summary>To do and Waiting on each went out as one message, however many times they were tried.</summary>
    private void AssertEachViewMadeOnce() => Assert.Equal(["To do", "Waiting on"], ViewsMade().Order());

    /// <summary>A made up task with a reminder already due.</summary>
    private (StoredItem Task, string Key) DueReminderFor(string title)
    {
        // The morning briefing would name the task too, so it's had already today.
        _database.SetPreference("last_briefing", Today.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        var task = AddTask(title, Today);
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO reminders (item_id, remind_at) VALUES ($item, $at)";
        var at = _now.AddMinutes(-1).ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue("$item", task.Id);
        command.Parameters.AddWithValue("$at", at);
        command.ExecuteNonQuery();
        command.CommandText = "SELECT last_insert_rowid()";
        return (task, $"reminder:item:{command.ExecuteScalar()}:{task.Id}:{at}");
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
