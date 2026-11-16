using Microsoft.Data.Sqlite;
using Waypoint.Core.Data;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Tests;

/// <summary>State saved under a step's name, from before the steps had slots, moves to the slot with that name.</summary>
public sealed class WindDownUpgradeTests : IDisposable
{
    private const string ChoicesKey = "bedtime_choices";
    private static readonly DateOnly Morning = new(2026, 11, 3);
    private static readonly BedtimeSettings Named = new(EveningStepName: "Stretch", WindDownStepName: "Tea");

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public WindDownUpgradeTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private void AddSent(string task)
    {
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO bedtime_sent (morning, task) VALUES ('2026-11-03', $task)";
        command.Parameters.AddWithValue("$task", task);
        command.ExecuteNonQuery();
    }

    private List<string> SentTasks()
    {
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT task FROM bedtime_sent ORDER BY task";
        using var reader = command.ExecuteReader();
        var tasks = new List<string>();
        while (reader.Read())
        {
            tasks.Add(reader.GetString(0));
        }

        return tasks;
    }

    [Fact]
    public void Switches_and_sent_stages_under_a_steps_name_move_to_its_slot_ignoring_case()
    {
        _database.SetPreference(ChoicesKey, """{"SleepHours":7.5,"stretch":false,"TEA":true,"Nap":true}""");
        AddSent("Stretch");
        AddSent("Nap");
        _database.SetRoutineMark(Morning, "Tea", true);

        _database.UpgradeWindDownSteps(Named);

        var choices = _database.GetBedtimeChoices();
        Assert.Equal(7.5, choices.SleepHours);
        Assert.False(choices.EveningStep);
        Assert.True(choices.WindDownStep);
        Assert.Contains("\"Nap\":true", _database.GetPreference(ChoicesKey));
        Assert.True(_database.WasSent(Morning, BedtimeTask.EveningStep));
        Assert.False(_database.WasSent(Morning, BedtimeTask.WindDownStep));
        Assert.Equal(["EveningStep", "Nap"], SentTasks());
        Assert.Contains("Tea", _database.GetRoutineMarks(Morning));
    }

    [Fact]
    public void Running_again_or_after_a_rename_moves_nothing_more()
    {
        _database.SetPreference(ChoicesKey, """{"Stretch":false}""");
        AddSent("Tea");
        _database.UpgradeWindDownSteps(Named);
        var once = _database.GetPreference(ChoicesKey);

        _database.UpgradeWindDownSteps(Named);
        _database.UpgradeWindDownSteps(new BedtimeSettings(EveningStepName: "WindDownStep", WindDownStepName: "Walk"));

        Assert.Equal(once, _database.GetPreference(ChoicesKey));
        Assert.Equal(["WindDownStep"], SentTasks());
        Assert.False(_database.GetBedtimeChoices().EveningStep);
    }

    [Fact]
    public void A_name_both_steps_share_or_neither_has_is_left_alone()
    {
        _database.SetPreference(ChoicesKey, """{"Tea":true,"Stretch":false}""");
        AddSent("Tea");

        _database.UpgradeWindDownSteps(new BedtimeSettings(EveningStepName: "Tea", WindDownStepName: "tea"));

        Assert.Equal("""{"Tea":true,"Stretch":false}""", _database.GetPreference(ChoicesKey));
        Assert.Equal(["Tea"], SentTasks());
        Assert.Null(WindDownUpgrade.SlotFor("Stretch", Named with { EveningStepName = "Walk" }));
    }

    [Fact]
    public void A_switch_already_in_its_slot_wins_and_a_non_switch_value_stays()
    {
        Assert.Equal("""{"EveningStep":true}""", WindDownUpgrade.UpgradeChoices("""{"EveningStep":true,"Stretch":false}""", Named));
        Assert.Null(WindDownUpgrade.UpgradeChoices("""{"Stretch":"yes"}""", Named));
        Assert.Null(WindDownUpgrade.UpgradeChoices("not json", Named));
        Assert.Null(WindDownUpgrade.UpgradeChoices(null, Named));
    }
}
