using Microsoft.Data.Sqlite;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Tests;

/// <summary>
/// Buttons in Discord that save several things save them whole or not at all, and a card's buttons still work when
/// Waypoint stopped right after sending it. A failing trigger stands in for a crash partway. No network.
/// </summary>
public sealed partial class DiscordPhoneTests
{
    [Fact]
    public async Task A_start_that_fails_partway_starts_nothing_and_leaves_the_card_to_press_again()
    {
        var task = AddTask("Made up reading", null);
        _database.TrackSentReminder("card-1", task.Id);
        var worker = await Started(toDo: null, lists: null);
        var press = new ButtonPress("waypoint:start", AlertActionKind.Start, "card-1", "alerts");

        FailPartway("BEFORE INSERT ON activation_sessions");
        await Assert.ThrowsAnyAsync<SqliteException>(() => worker.OnButtonActedAsync(press));
        Assert.Equal(0, Rows("reminder_answers"));
        Assert.Equal(0, Rows("sent_reminders", "handled = 1"));
        Assert.Null(_database.GetActiveSession(_now));

        StopFailingPartway();
        var card = (await worker.OnButtonActedAsync(press))?.Card;

        Assert.True(card?.HasSessionButtons);
        Assert.Equal("Made up reading", _database.GetActiveSession(_now)?.TaskTitle);
        Assert.Equal(1, Rows("reminder_answers"));
        Assert.Null(await worker.OnButtonActedAsync(press));
        Assert.Equal(1, Rows("activation_sessions"));
    }

    [Fact]
    public async Task A_list_started_from_its_button_is_made_with_its_entries_or_not_at_all()
    {
        var worker = Worker(toDo: null, lists: null);
        var offer = worker.OnDiscordCommand(new SlashCommand("listadd", Options(("list", "Made up chores"), ("entry", "dishes, laundry"))));

        FailPartway("BEFORE INSERT ON list_entries WHEN NEW.text = 'laundry'");
        await Assert.ThrowsAnyAsync<SqliteException>(() => worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + Assert.Single(offer.Card.Actions).Id, null, "m1", "c")));
        Assert.DoesNotContain("Made up chores", _database.GetListNames());

        StopFailingPartway();
        offer = worker.OnDiscordCommand(new SlashCommand("listadd", Options(("list", "Made up chores"), ("entry", "dishes, laundry"))));
        var made = await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + Assert.Single(offer.Card.Actions).Id, null, "m2", "c"));

        Assert.Contains("Started a list called Made up chores", made!.Outcome);
        Assert.Equal(["dishes", "laundry"], _database.GetListEntries("Made up chores").Select(entry => entry.Text));
    }

    private void FailPartway(string when)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TRIGGER fail_partway {when} BEGIN SELECT RAISE(ABORT, 'made to fail partway'); END";
        command.ExecuteNonQuery();
    }

    private void StopFailingPartway()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TRIGGER fail_partway";
        command.ExecuteNonQuery();
    }

    private long Rows(string table, string where = "1 = 1")
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {where}";
        return (long)command.ExecuteScalar()!;
    }
}
