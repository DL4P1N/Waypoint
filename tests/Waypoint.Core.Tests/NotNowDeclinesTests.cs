using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

public sealed class NotNowDeclinesTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 10, 14, 0, 0);
    private static DateOnly Today => DateOnly.FromDateTime(Now);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public NotNowDeclinesTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private StoredItem AddTask(string title, int inDays, params string[] steps)
    {
        var plan = steps.Select(step => new PlannedStep(step, null)).ToList();
        _database.CompleteCapture(_database.AddCapture(title), [new SortedItem(ItemKind.Task, title, Today.AddDays(inDays), null, Steps: plan.Count > 0 ? plan : null)]);
        return _database.GetOpenItems().First(item => item.Title == title);
    }

    private IReadOnlySet<string> Declined(DateTime at) => NotNowDeclines.Read(_database.GetPreference(NotNowDeclines.Preference), at);

    [Fact]
    public void A_decline_lasts_one_snooze_window()
    {
        var stored = NotNowDeclines.Add(null, "item:1", Now);
        stored = NotNowDeclines.Add(stored, "item:2", Now.AddMinutes(10));

        Assert.Equal(["item:1", "item:2"], NotNowDeclines.Read(stored, Now.AddMinutes(14)).Order());
        Assert.Equal(["item:2"], NotNowDeclines.Read(stored, Now.AddMinutes(15)));
        Assert.Empty(NotNowDeclines.Read(stored, Now.AddMinutes(25)));
        Assert.Empty(NotNowDeclines.Read("not a list", Now));
    }

    [Fact]
    public void Not_now_on_the_session_card_keeps_low_energy_from_offering_it_again()
    {
        var first = AddTask("Email advisor", 0);
        AddTask("Water plants", 1);
        var open = _database.GetOpenItems();
        Assert.Equal(first.Id, EnergyPicks.Pick(open, EnergyLevel.LowEnergy, Today)!.Item.Id);

        _database.StartActivation(ItemRef.Format(first), null, null, "energy", Now);
        Assert.NotNull(_database.ApplySessionWord(Activation.ParseWord("not now")!.Value, Now));

        var declined = Declined(Now.AddMinutes(1));
        Assert.Equal("Water plants", EnergyPicks.Pick(open, EnergyLevel.LowEnergy, Today, declined)!.Item.Title);
        Assert.Equal("Water plants", EnergyPicks.Pick(open, EnergyLevel.FifteenMinutes, Today, declined)!.Item.Title);
        Assert.Equal("Water plants", NextAction.Pick(open, [], Now.AddMinutes(1), new HashSet<string>(), declined: declined)!.Item.Title);

        // Once the window is over it can come up again.
        Assert.Equal(first.Id, EnergyPicks.Pick(open, EnergyLevel.LowEnergy, Today, Declined(Now.AddMinutes(20)))!.Item.Id);
    }

    [Fact]
    public void A_declined_step_passes_over_its_task_and_a_reminders_not_now_counts_too()
    {
        var paper = AddTask("Paper", 0, "Outline", "Draft");
        var plants = AddTask("Water plants", 1);
        var open = _database.GetOpenItems();
        var outline = open.First(item => item.Title == "Outline");

        _database.StartActivation(ItemRef.Format(outline), null, null, "energy", Now);
        _database.ApplySessionWord(Activation.ParseWord("not now")!.Value, Now);
        var declined = Declined(Now);
        Assert.Equal(plants.Id, EnergyPicks.Pick(open, EnergyLevel.LowEnergy, Today, declined)!.Item.Id);
        Assert.Equal(plants.Id, NextAction.Pick(open, [], Now, new HashSet<string>(), declined: declined)!.Item.Id);

        _database.RecordReminderAnswer(plants, ReminderAnswer.NotNow, Now);
        declined = Declined(Now);
        Assert.Contains(ItemRef.Format(paper), NotNowDeclines.WithParents(declined, open));
        Assert.Null(EnergyPicks.Pick(open, EnergyLevel.LowEnergy, Today, declined));
    }
}
