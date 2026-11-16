using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Tests;

public sealed class ReminderPlanTests : IDisposable
{
    private static readonly DateOnly ExamDay = new(2026, 12, 3);
    private static readonly TimeOnly NineAm = new(9, 0);
    private static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public ReminderPlanTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static SortedItem Exam(params PlannedReminder[] reminders) =>
        new(ItemKind.Exam, "midterm", ExamDay, null, NineAm, reminders);

    private static SortedItem DueNow(int? repeatMinutes = null) =>
        new(ItemKind.Reminder, "call", DateOnly.FromDateTime(DateTime.Now), null, new TimeOnly(0, 0), null, repeatMinutes);

    private void Store(SortedItem item) => _database.CompleteCapture(_database.AddCapture("x"), [item]);

    [Fact]
    public void Reminders_fire_at_their_time_even_if_already_missed()
    {
        var item = new SortedItem(ItemKind.Reminder, "call", new DateOnly(2026, 10, 1), null, NineAm);

        Assert.Equal(new DateTime(2026, 10, 1, 9, 0, 0), ReminderPlan.Build(item, Now).Single().At);
    }

    [Fact]
    public void A_reminder_with_no_time_defaults_to_the_morning()
    {
        var item = new SortedItem(ItemKind.Reminder, "call", ExamDay, null);

        Assert.Equal(new DateTime(2026, 12, 3, 9, 0, 0), ReminderPlan.Build(item, Now).Single().At);
    }

    [Fact]
    public void Exams_always_keep_their_fixed_alerts()
    {
        Assert.Equal(
            [new DateTime(2026, 12, 2, 9, 0, 0), new DateTime(2026, 12, 3, 8, 15, 0)],
            ReminderPlan.Build(Exam(), Now).Select(reminder => reminder.At));
    }

    [Fact]
    public void Planned_study_reminders_join_the_fixed_alerts_in_order()
    {
        var study = new PlannedReminder(new DateTime(2026, 11, 28, 18, 0, 0), "Start reviewing");

        var reminders = ReminderPlan.Build(Exam(study), Now);

        Assert.Equal(3, reminders.Count);
        Assert.Equal(study, reminders[0]);
    }

    [Fact]
    public void Planned_reminders_in_the_past_are_dropped()
    {
        var past = new PlannedReminder(Now.AddDays(-1), "Too late");

        Assert.DoesNotContain(past, ReminderPlan.Build(Exam(past), Now));
    }

    [Fact]
    public void A_runaway_plan_is_capped_but_fixed_alerts_stay()
    {
        var many = Enumerable.Range(1, 50)
            .Select(day => new PlannedReminder(Now.AddHours(day), "Study"))
            .ToArray();

        var reminders = ReminderPlan.Build(Exam(many), Now);

        Assert.Equal(16, reminders.Count);
        Assert.Contains(reminders, reminder => reminder.At == new DateTime(2026, 12, 3, 8, 15, 0));
    }

    [Fact]
    public void Plain_tasks_get_no_reminders_unless_planned()
    {
        Assert.Empty(ReminderPlan.Build(new SortedItem(ItemKind.Task, "laundry", ExamDay, null, NineAm), Now));
    }

    [Fact]
    public void A_repeating_task_with_no_date_still_gets_a_first_reminder()
    {
        var item = new SortedItem(ItemKind.Task, "laundry", null, null, null, null, 60);

        Assert.Equal(Now.AddMinutes(60), ReminderPlan.Build(item, Now).Single().At);
    }

    [Fact]
    public void Repeat_intervals_have_a_minimum()
    {
        var item = new SortedItem(ItemKind.Task, "laundry", null, null, null, null, 1);

        Assert.Equal(ReminderPlan.MinRepeatMinutes, ReminderPlan.RepeatMinutes(item));
    }

    [Fact]
    public void Parses_the_plan_from_the_sorter()
    {
        var item = SortedItemParser.Parse("""
            {"items": [{"kind": "exam", "title": "midterm", "due_date": "2026-12-03", "due_time": "09:00", "amount_cents": null,
                        "reminders": [{"at": "2026-11-28T18:00", "message": "Start reviewing"}, {"at": "soon", "message": "bad"}],
                        "repeat_minutes": 30}]}
            """).Single();

        Assert.Equal(new PlannedReminder(new DateTime(2026, 11, 28, 18, 0, 0), "Start reviewing"), item.Reminders!.Single());
        Assert.Equal(30, item.RepeatMinutes);
    }

    [Fact]
    public void Stored_reminder_comes_due_and_keeps_its_time()
    {
        Store(DueNow());

        var reminder = _database.GetDueReminders(DateTime.Now).Single();

        Assert.Equal("call", reminder.Item.Title);
        Assert.Equal(new TimeOnly(0, 0), TimeOnly.FromDateTime(reminder.Item.DueAt!.Value.DateTime));
    }

    [Fact]
    public void Snoozing_hides_a_reminder_until_the_new_time()
    {
        var now = DateTime.Now;
        Store(DueNow());

        _database.SnoozeReminder(_database.GetDueReminders(now).Single(), now.AddMinutes(15));

        Assert.Empty(_database.GetDueReminders(now));
        Assert.Single(_database.GetDueReminders(now.AddMinutes(16)));
    }

    [Fact]
    public void Dismissing_clears_one_reminder_but_keeps_the_rest_of_the_plan()
    {
        var future = new PlannedReminder(DateTime.Now.AddDays(2), "Later");
        Store(DueNow() with { Reminders = [future] });

        _database.DismissReminder(_database.GetDueReminders(DateTime.Now).Single());

        Assert.Empty(_database.GetDueReminders(DateTime.Now));
        Assert.Equal("Later", _database.GetDueReminders(DateTime.Now.AddDays(3)).Single().Message);
    }

    [Fact]
    public void Marking_an_item_done_stops_all_of_its_reminders()
    {
        Store(DueNow(repeatMinutes: 30) with { Reminders = [new PlannedReminder(DateTime.Now.AddDays(2), "Later")] });

        var reminder = _database.GetDueReminders(DateTime.Now).Single();
        Assert.Equal(30, reminder.RepeatMinutes);
        _database.SetDone(reminder.Item, true);

        Assert.Empty(_database.GetDueReminders(DateTime.Now.AddDays(3)));
    }

    [Fact]
    public void An_older_database_gains_the_new_columns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE items (
                    id INTEGER PRIMARY KEY, capture_id INTEGER NOT NULL, kind TEXT NOT NULL, title TEXT NOT NULL,
                    due_date TEXT, amount_cents INTEGER, done INTEGER NOT NULL DEFAULT 0);
                INSERT INTO items (capture_id, kind, title) VALUES (1, 'Task', 'old item');
                """;
            command.ExecuteNonQuery();
        }

        var database = new WaypointDatabase(path);

        Assert.Equal("old item", database.GetOpenItems().Single().Title);
        SqliteConnection.ClearAllPools();
        File.Delete(path);
    }
}
