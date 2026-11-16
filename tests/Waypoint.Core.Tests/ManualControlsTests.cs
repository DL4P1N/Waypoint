using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Tests;

public sealed class ManualControlsTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 11, 3);
    private static readonly DateTime Now = new(2026, 11, 3, 15, 0, 0);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public ManualControlsTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static StoredItem Item(long id, string title, ItemKind kind, DateOnly? due, string? parent = null, ItemSource source = ItemSource.Capture) =>
        new(id, kind, title, due, null, false, source, ParentRef: parent);

    private StoredItem Add(string title, ItemKind kind = ItemKind.Deadline, DateOnly? due = null, params string[] steps)
    {
        var plan = steps.Select(step => new PlannedStep(step, null)).ToList();
        _database.CompleteCapture(_database.AddCapture(title), [new SortedItem(kind, title, due ?? DateOnly.FromDateTime(DateTime.Now.AddDays(2)), null, Steps: plan.Count > 0 ? plan : null)]);
        return _database.GetOpenItems().First(item => item.Title == title);
    }

    // Energy picks.

    [Fact]
    public void Fifteen_minutes_picks_the_most_pressing_small_thing()
    {
        var open = new[]
        {
            Item(1, "laundry", ItemKind.Task, Today.AddDays(5)),
            Item(2, "email advisor", ItemKind.Task, Today),
            Item(3, "paper", ItemKind.Deadline, Today),
        };

        var pick = EnergyPicks.Pick(open, EnergyLevel.FifteenMinutes, Today);

        Assert.Equal("email advisor", pick?.Item.Title);
        Assert.Contains("due today", pick!.Reason);
    }

    [Fact]
    public void A_big_item_with_steps_offers_its_next_step()
    {
        var open = new[]
        {
            Item(1, "paper", ItemKind.Deadline, Today.AddDays(1)),
            Item(2, "write the intro", ItemKind.Task, null, parent: "item:1"),
            Item(3, "outline", ItemKind.Task, null, parent: "item:1"),
            Item(4, "laundry", ItemKind.Task, Today.AddDays(6)),
        };

        var pick = EnergyPicks.Pick(open, EnergyLevel.FifteenMinutes, Today);

        Assert.Equal("write the intro", pick?.Item.Title);
        Assert.Contains("paper", pick!.Reason);
    }

    [Fact]
    public void Low_energy_prefers_a_plain_task_over_a_step_of_bigger_work()
    {
        var open = new[]
        {
            Item(1, "paper", ItemKind.Deadline, Today),
            Item(2, "write the intro", ItemKind.Task, null, parent: "item:1"),
            Item(3, "water plants", ItemKind.Task, Today.AddDays(4)),
        };

        Assert.Equal("write the intro", EnergyPicks.Pick(open, EnergyLevel.FifteenMinutes, Today)?.Item.Title);
        Assert.Equal("water plants", EnergyPicks.Pick(open, EnergyLevel.LowEnergy, Today)?.Item.Title);
    }

    [Fact]
    public void Big_items_are_only_picked_when_nothing_small_is_waiting_and_only_if_soon()
    {
        var soon = new[] { Item(1, "paper", ItemKind.Deadline, Today.AddDays(2)), Item(2, "exam", ItemKind.Exam, Today.AddDays(30)) };
        var later = new[] { Item(2, "exam", ItemKind.Exam, Today.AddDays(30)) };

        var pick = EnergyPicks.Pick(soon, EnergyLevel.LowEnergy, Today);

        Assert.Equal("paper", pick?.Item.Title);
        Assert.Contains("just open it", pick!.Reason);
        Assert.Null(EnergyPicks.Pick(later, EnergyLevel.FifteenMinutes, Today));
    }

    [Fact]
    public void Recurring_scheduled_and_past_feed_items_are_never_picked()
    {
        var open = new[]
        {
            new StoredItem(1, ItemKind.Task, "water the plants", Today, null, false, Recurs: new Recurrence(RecurrenceUnit.Day, 1)),
            Item(2, "dentist", ItemKind.Reminder, Today),
            Item(3, "old quiz", ItemKind.Deadline, Today.AddDays(-2), source: ItemSource.Calendar),
            Item(4, "milk", ItemKind.Shopping, null),
        };

        Assert.Null(EnergyPicks.Pick(open, EnergyLevel.FifteenMinutes, Today));
        Assert.Null(EnergyPicks.Pick(open, EnergyLevel.LowEnergy, Today));
    }

    [Fact]
    public void The_same_items_always_give_the_same_pick()
    {
        var open = new[] { Item(5, "b", ItemKind.Task, null), Item(4, "a", ItemKind.Task, null) };

        Assert.Equal(4, EnergyPicks.Pick(open, EnergyLevel.FifteenMinutes, Today)?.Item.Id);
        Assert.Equal(4, EnergyPicks.Pick(open.Reverse().ToArray(), EnergyLevel.FifteenMinutes, Today)?.Item.Id);
    }

    [Fact]
    public void An_earlier_item_reads_plainly_as_overdue_with_no_count()
    {
        var pick = EnergyPicks.Pick([Item(1, "form", ItemKind.Task, Today.AddDays(-3))], EnergyLevel.LowEnergy, Today);

        Assert.Contains("overdue", pick!.Reason);
        Assert.DoesNotContain("3", pick.Reason);
        Assert.DoesNotContain("late", pick.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // Row menu.

    [Fact]
    public void Startable_rows_offer_a_smaller_step_and_past_items_count_as_behind()
    {
        var item = Item(1, "form", ItemKind.Task, Today.AddDays(-1));

        Assert.Contains(RowAction.SmallerStep, RowActions.For(item, Today, movedFromFeed: false));
        Assert.True(RowActions.IsBehind(item, Today));
        Assert.False(RowActions.IsBehind(item with { DueDate = Today }, Today));
        Assert.False(RowActions.IsBehind(item with { Done = true }, Today));
    }

    [Fact]
    public void Smaller_step_starts_a_session_with_a_fixed_action_in_front()
    {
        var paper = Add("paper");

        var reply = _database.StartSmaller(paper, "menu", Now);

        var session = _database.GetActiveSession(Now)!;
        Assert.Equal(Activation.Fallback(0), session.Action);
        Assert.Contains(Activation.Fallback(0), reply.Text);
        Assert.Equal("paper", session.TaskTitle);
    }

    [Fact]
    public void Smaller_step_on_an_item_with_steps_keeps_the_step_to_come_back_to()
    {
        var paper = Add("paper", steps: ["outline"]);

        _database.StartSmaller(paper, "menu", Now);

        var session = _database.GetActiveSession(Now)!;
        Assert.Equal(Activation.Fallback(0), session.Action);
        Assert.Equal(new[] { "outline" }, session.Queue);
    }

    // Save my spot.

    [Fact]
    public void Save_my_spot_form_uses_the_same_handoff_as_typing()
    {
        var reply = _database.SaveSpotByHand("Waypoint", "settings page", "write the tests", ["https://example.com/pr"], ["check the theme"], Now);

        Assert.Contains("Next time: write the tests", reply.Text);
        var shown = _database.DescribeHandoff("waypoint");
        Assert.Contains("Stopped: settings page", shown);
        Assert.Contains("Links: https://example.com/pr", shown);
        Assert.Contains("Remember: check the theme", shown);
        Assert.True(_database.HasHandoffSince(Now.AddMinutes(-1)));
        Assert.Empty(_database.GetPendingCaptures());
    }

    [Fact]
    public void Save_my_spot_with_no_next_step_asks_once_like_typing_does()
    {
        var reply = _database.SaveSpotByHand("Waypoint", "settings page", null, null, null, Now);

        Assert.True(reply.IsAnswer);
        Assert.Equal("Waypoint", _database.GetWaitingHandoff(Now));
    }

    [Fact]
    public void An_empty_save_my_spot_form_saves_nothing()
    {
        var reply = _database.SaveSpotByHand(" ", null, "", [], [" "], Now);

        Assert.True(reply.IsAnswer);
        Assert.Equal("No saved spots yet.", _database.DescribeHandoff(null));
    }

    // Work hours.

    [Fact]
    public void Work_hours_read_back_in_order_and_a_one_off_can_be_taken_away()
    {
        _database.SetWeeklyHours(["Friday", "Monday"], "09:00-17:00");
        _database.SetDayOff(new DateTime(2026, 11, 6));
        _database.AddWorkShift(new DateTime(2026, 11, 5, 12, 0, 0), new DateTime(2026, 11, 5, 16, 0, 0));

        Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Friday }, _database.GetWeeklyHours().Select(hours => hours.Day));
        var dates = _database.GetWorkDates(Today);
        Assert.Equal(new[] { new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 6) }, dates.Select(date => date.Date));
        Assert.True(dates[1].IsDayOff);
        Assert.Empty(_database.GetShifts(new DateOnly(2026, 11, 6)));

        _database.RemoveWorkDate(new DateOnly(2026, 11, 6));

        Assert.Single(_database.GetShifts(new DateOnly(2026, 11, 6)));
    }

    [Fact]
    public void Commute_reads_the_typed_value_before_the_settings_one()
    {
        _database.DefaultCommuteMinutes = 20;
        Assert.Equal(20, _database.GetCommuteMinutes());

        _database.SetCommute("35");

        Assert.Equal(35, _database.GetCommuteMinutes());
    }

    // Bedtime choices.

    [Fact]
    public void Bedtime_choices_lay_over_the_file_and_keep_what_they_dont_name()
    {
        var file = new BedtimeSettings(SleepHours: 7.5, EveningStepMinutes: null, RoutineSteps: ["Stretch"], HeadsUpMinutes: 120);

        var applied = new BedtimeChoices(SleepHours: 8.5, LatestBedtime: "00:30", EveningStep: true, WindDownStep: false).ApplyTo(file);

        Assert.Equal(8.5, applied.SleepHours);
        Assert.Equal(new TimeOnly(0, 30), applied.LatestBedtime);
        Assert.Equal(new BedtimeSettings().EveningStepMinutes, applied.EveningStepMinutes);
        Assert.Null(applied.WindDownStepMinutes);
        Assert.Equal(new[] { "Stretch" }, applied.RoutineSteps);
        Assert.Equal(120, applied.HeadsUpMinutes);
        Assert.Equal(file, BedtimeChoices.None.ApplyTo(file));
    }

    [Fact]
    public void Bedtime_choices_are_saved_in_preferences_and_bad_ones_are_turned_down()
    {
        Assert.NotNull(_database.SetBedtimeChoices(new BedtimeChoices(SleepHours: 2)));
        Assert.NotNull(_database.SetBedtimeChoices(new BedtimeChoices(LatestBedtime: "late")));
        Assert.Equal(BedtimeChoices.None, _database.GetBedtimeChoices());

        Assert.Null(_database.SetBedtimeChoices(new BedtimeChoices(ReadyMinutes: 30, RoutineSteps: BedtimeChoices.CleanSteps(["Stretch", " ", "stretch", "Read"]))));

        var saved = _database.GetBedtimeChoices();
        Assert.Equal(30, saved.ReadyMinutes);
        Assert.Equal(new[] { "Stretch", "Read" }, saved.RoutineSteps);
    }

    [Fact]
    public void An_unreadable_saved_choice_falls_back_to_the_file()
    {
        _database.SetPreference("bedtime_choices", "{not json");

        Assert.Equal(BedtimeChoices.None, _database.GetBedtimeChoices());
    }

    [Fact]
    public void A_changed_bedtime_setting_applies_to_the_running_schedule()
    {
        var schedule = new BedtimeSchedule(new BedtimeSettings(), null);

        schedule.Settings = new BedtimeChoices(WindDownStep: false).ApplyTo(schedule.Settings);

        Assert.DoesNotContain(BedtimeSettings.DefaultWindDownStepName, BedtimeRoutine.Steps(schedule.Settings));
    }
}
