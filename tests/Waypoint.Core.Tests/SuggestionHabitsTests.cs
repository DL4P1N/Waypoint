using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Tests;

public sealed class SuggestionHabitsTests : IDisposable
{
    // A Monday morning, so every date below is fixed and nothing reads the real clock.
    private static readonly DateTime Now = new(2026, 11, 9, 10, 0, 0);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now);
    private static readonly string[] None = [];

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    private long _nextId = 1;

    public SuggestionHabitsTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static IEnumerable<PastSitting> Sittings(ItemKind kind, bool onSteps, params double[] minutes) =>
        minutes.Select((length, index) => new PastSitting(kind, onSteps, Now.AddDays(-1 - index), length));

    private static SuggestionHabits Learn(IEnumerable<PastSitting>? sittings = null, IEnumerable<PastFinish>? finishes = null, IEnumerable<PastAnswer>? answers = null) =>
        SuggestionHabits.Learn(sittings ?? [], finishes ?? [], answers ?? [], Now);

    private StoredItem Item(string title, ItemKind kind = ItemKind.Task, int? inDays = 0, string? parent = null)
    {
        DateOnly? date = inDays is { } days ? Today.AddDays(days) : null;
        return new StoredItem(_nextId++, kind, title, date, null, false, ItemSource.Capture, ParentRef: parent);
    }

    private StoredItem[] WithSteps(StoredItem parent, int count) =>
        [parent, .. Enumerable.Range(1, count).Select(index => Item($"{parent.Title} step {index}", parent.Kind, parent.DueDate!.Value.DayNumber - Today.DayNumber, ItemRef.Format(parent)))];

    // Learning the sizes.

    [Fact]
    public void With_no_history_every_size_is_the_starting_guess()
    {
        var habits = Learn();

        Assert.Equal(NextAction.StepMinutes, habits.StepMinutes);
        Assert.Equal(NextAction.TaskMinutes, habits.TaskMinutes);
        Assert.Equal(NextAction.SittingMinutes, habits.SittingMinutes);
        Assert.Equal(NextAction.DayMinutes, habits.DayMinutes);
        Assert.Equal(0, habits.LeadDays(ItemKind.Deadline));
        Assert.False(habits.IsHardTime(Now));
    }

    [Fact]
    public void Four_sessions_are_too_few_and_five_are_enough()
    {
        Assert.Null(Learn(Sittings(ItemKind.Task, false, 30, 30, 30, 30)).LearnedTaskMinutes);
        Assert.Equal(30, Learn(Sittings(ItemKind.Task, false, 30, 30, 30, 30, 30)).LearnedTaskMinutes);
    }

    [Fact]
    public void The_middle_session_counts_so_one_long_one_does_not_swing_it()
    {
        // The median of 22, 24, 26, 28, and 170 is 26, which rounds to 25.
        Assert.Equal(25, Learn(Sittings(ItemKind.Task, false, 22, 24, 170, 26, 28)).LearnedTaskMinutes);
    }

    [Fact]
    public void Learned_sizes_stay_inside_their_bounds()
    {
        var habits = Learn(Sittings(ItemKind.Task, false, 90, 90, 90, 90, 90)
            .Concat(Sittings(ItemKind.Deadline, false, 5, 5, 5, 5, 5))
            .Concat(Sittings(ItemKind.Deadline, true, 120, 120, 120, 120, 120)));

        Assert.Equal(SuggestionHabits.TaskBounds.High, habits.LearnedTaskMinutes);
        Assert.Equal(SuggestionHabits.SittingBounds.Low, habits.LearnedSittingMinutes);
        Assert.Equal(SuggestionHabits.StepBounds.High, habits.LearnedStepMinutes);
    }

    [Fact]
    public void Sessions_left_running_started_by_mistake_or_too_old_are_left_out()
    {
        var old = Enumerable.Range(0, 5).Select(index => new PastSitting(ItemKind.Task, false, Now.AddDays(-SuggestionHabits.LookBackDays - 1 - index), 30));
        var stray = Sittings(ItemKind.Task, false, 1, 2, 200, 240, 500);

        Assert.Null(Learn(old.Concat(stray)).LearnedTaskMinutes);
    }

    [Fact]
    public void A_day_holds_what_the_user_usually_gets_through_in_one()
    {
        // Two 45-minute sessions on each of five days is 90 minutes a day.
        var sittings = Enumerable.Range(1, 5).SelectMany(day => new[]
        {
            new PastSitting(ItemKind.Task, false, Now.Date.AddDays(-day).AddHours(14), 45),
            new PastSitting(ItemKind.Task, false, Now.Date.AddDays(-day).AddHours(19), 45),
        });

        Assert.Equal(90, Learn(sittings).DayMinutes);
    }

    [Fact]
    public void A_kind_usually_checked_off_late_gets_a_day_of_lead()
    {
        var late = Enumerable.Range(1, 3).Select(index => new PastFinish(ItemKind.Deadline, Today.AddDays(-10 - index), Today.AddDays(-8 - index)));
        var onTime = Enumerable.Range(1, 2).Select(index => new PastFinish(ItemKind.Deadline, Today.AddDays(-index), Today.AddDays(-index)));
        var tasks = Enumerable.Range(1, 5).Select(index => new PastFinish(ItemKind.Task, Today.AddDays(-index), Today.AddDays(-index - 1)));

        var habits = Learn(finishes: late.Concat(onTime).Concat(tasks));

        Assert.Equal(1, habits.LeadDays(ItemKind.Deadline));
        Assert.Equal(0, habits.LeadDays(ItemKind.Task));
    }

    [Fact]
    public void Half_late_is_not_enough_to_change_anything()
    {
        var finishes = Enumerable.Range(1, 6).Select(index => new PastFinish(ItemKind.Deadline, Today.AddDays(-index * 2), Today.AddDays(-index * 2 + (index % 2))));

        Assert.Equal(0, Learn(finishes: finishes).LeadDays(ItemKind.Deadline));
    }

    [Fact]
    public void A_time_of_day_is_hard_only_when_reminders_there_are_almost_always_passed_on()
    {
        // Mornings from 9 to noon: five Not nows. Evenings from 6 to 9: four Not nows and one Start.
        var mornings = Enumerable.Range(1, 5).Select(day => new PastAnswer(Now.Date.AddDays(-day).AddHours(10), ReminderAnswer.NotNow));
        var evenings = Enumerable.Range(1, 4).Select(day => new PastAnswer(Now.Date.AddDays(-day).AddHours(19), ReminderAnswer.NotNow))
            .Append(new PastAnswer(Now.Date.AddDays(-6).AddHours(20), ReminderAnswer.Started));
        var afternoons = Enumerable.Range(1, 3).Select(day => new PastAnswer(Now.Date.AddDays(-day).AddHours(13), ReminderAnswer.NotNow));

        var habits = Learn(answers: mornings.Concat(evenings).Concat(afternoons));

        Assert.True(habits.IsHardTime(Now));
        Assert.True(habits.IsHardTime(Now.Date.AddHours(18)));
        Assert.False(habits.IsHardTime(Now.Date.AddHours(13)));
    }

    [Fact]
    public void Three_of_five_passed_on_is_not_a_hard_time()
    {
        var answers = Enumerable.Range(1, 5).Select(day => new PastAnswer(Now.Date.AddDays(-day).AddHours(10), day <= 3 ? ReminderAnswer.NotNow : ReminderAnswer.Started));

        Assert.False(Learn(answers: answers).IsHardTime(Now));
    }

    // Using them in the suggestion.

    [Fact]
    public void Without_habits_the_pick_and_reason_are_unchanged()
    {
        var task = Item("Email advisor", inDays: 1);

        var plain = NextAction.Pick([task], [], Now, None);
        var defaults = NextAction.Pick([task], [], Now, None, SuggestionHabits.Defaults);

        Assert.Equal(plain, defaults);
        Assert.Equal("Due tomorrow", plain!.Reason);
    }

    [Fact]
    public void The_reason_says_how_long_it_usually_takes()
    {
        var habits = new SuggestionHabits(LearnedTaskMinutes: 30, LearnedStepMinutes: 25);
        var task = Item("Email advisor", inDays: 1);
        var paper = WithSteps(Item("Paper", ItemKind.Deadline, 3), 2);

        Assert.Equal("Due tomorrow, usually takes you about 30 min", NextAction.Pick([task], [], Now, None, habits)!.Reason);
        Assert.Contains("2 steps left, steps usually take you about 25 min", NextAction.Pick(paper, [], Now, None, habits)!.Reason);
    }

    [Fact]
    public void A_longer_learned_sitting_no_longer_fits_before_class()
    {
        // Fifty minutes until class leaves 45 free once the buffer is kept. A 15-minute task fits, a 60-minute one doesn't.
        var classAt = new Commitment(Now.AddMinutes(50), Now.AddMinutes(125), "class");
        var task = Item("Email advisor", inDays: 1);

        Assert.NotNull(NextAction.Pick([task], [classAt], Now, None));
        Assert.Null(NextAction.Pick([task], [classAt], Now, None, new SuggestionHabits(LearnedTaskMinutes: 45 + 5)));
    }

    [Fact]
    public void A_smaller_day_makes_a_big_item_more_pressing()
    {
        // Nine 20-minute steps are three hours: two days at the usual 120, three at 60. Due in 2 days, it then has none to spare.
        var big = WithSteps(Item("Lab report", ItemKind.Deadline, 2), 9);
        var soon = Item("Reading quiz", inDays: 1);

        Assert.Equal("Reading quiz", NextAction.Pick([soon, .. big], [], Now, None)!.Item.Title);
        Assert.Equal("Lab report", NextAction.Pick([soon, .. big], [], Now, None, new SuggestionHabits(LearnedDayMinutes: 60))!.Item.Title);
    }

    [Fact]
    public void A_kind_that_runs_late_comes_up_a_day_sooner()
    {
        // Both have a day to spare, so the one due sooner goes first, until the deadline loses its spare day.
        var deadline = WithSteps(Item("Lab report", ItemKind.Deadline, 2), 9);
        var task = Item("Email advisor", inDays: 1);

        Assert.Equal("Email advisor", NextAction.Pick([.. deadline, task], [], Now, None)!.Item.Title);
        Assert.Equal("Lab report", NextAction.Pick([.. deadline, task], [], Now, None, new SuggestionHabits(RunsLate: [ItemKind.Deadline]))!.Item.Title);
    }

    [Fact]
    public void At_a_hard_time_a_short_item_goes_first_unless_something_is_pressing()
    {
        var hard = new SuggestionHabits(HardBlocks: [Now.Hour / SuggestionHabits.BlockHours]);
        var paper = Item("Paper", ItemKind.Deadline, 3);
        var task = Item("Email advisor", inDays: 5);
        var dueToday = Item("Lab report", ItemKind.Deadline, 0);

        Assert.Equal("Paper", NextAction.Pick([paper, task], [], Now, None)!.Item.Title);
        var pick = NextAction.Pick([paper, task], [], Now, None, hard)!;
        Assert.Equal("Email advisor", pick.Item.Title);
        Assert.EndsWith("a short one for this time of day", pick.Reason);

        var pressing = NextAction.Pick([paper, task, dueToday], [], Now, None, hard)!;
        Assert.Equal("Lab report", pressing.Item.Title);
        Assert.DoesNotContain("short one", pressing.Reason);
    }

    // What the database learns from.

    private StoredItem AddItem(string title, ItemKind kind)
    {
        _database.CompleteCapture(_database.AddCapture(title), [new SortedItem(kind, title, Today.AddDays(2), null)]);
        return _database.GetOpenItems().First(item => item.Title == title);
    }

    private void AddSession(StoredItem item, DateTime started, int minutes, string status = "Finished")
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO activation_sessions (task_ref, task_title, action, status, started_at, ended_at, method)
            VALUES ($ref, $title, 'Open it', $status, $started, $ended, 'typed')
            """;
        command.Parameters.AddWithValue("$ref", ItemRef.Format(item));
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$started", started.ToString("yyyy-MM-ddTHH:mm:ss"));
        command.Parameters.AddWithValue("$ended", started.AddMinutes(minutes).ToString("yyyy-MM-ddTHH:mm:ss"));
        command.ExecuteNonQuery();
    }

    [Fact]
    public void The_database_learns_from_finished_sessions_by_the_kind_of_item()
    {
        var task = AddItem("Email advisor", ItemKind.Task);
        var paper = AddItem("Paper", ItemKind.Deadline);
        for (var day = 1; day <= 5; day++)
        {
            AddSession(task, Now.AddDays(-day), 35);
            AddSession(paper, Now.AddDays(-day).AddHours(3), 50);
            AddSession(paper, Now.AddDays(-day).AddHours(5), 5, "Canceled");
        }

        var habits = _database.GetSuggestionHabits(Now);

        Assert.Equal(35, habits.LearnedTaskMinutes);
        Assert.Equal(50, habits.LearnedSittingMinutes);
        Assert.Null(habits.LearnedStepMinutes);
        Assert.Equal(90, habits.LearnedDayMinutes);
    }

    [Fact]
    public void Not_now_from_Discord_is_kept_and_teaches_the_hard_times()
    {
        var task = AddItem("Email advisor", ItemKind.Task);
        for (var day = 1; day <= 4; day++)
        {
            _database.RecordReminderAnswer(task, ReminderAnswer.NotNow, Now.AddDays(-day));
        }

        Assert.False(_database.GetSuggestionHabits(Now).IsHardTime(Now));

        _database.TrackSentReminder("700", task.Id);
        Assert.NotNull(_database.ApplyTransitionAction("700", AlertActionKind.NotNowReminder, Now.AddDays(-5)));

        Assert.True(_database.GetSuggestionHabits(Now).IsHardTime(Now));
    }

    private void AddFinish(ItemKind kind, DateOnly due, DateTime doneAt, string action = "Done")
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO item_history (happened_at, action, source, item_id, kind, title, due_date)
            VALUES ($at, $action, 'Capture', 999, $kind, 'Old thing', $due)
            """;
        command.Parameters.AddWithValue("$at", doneAt.ToString("yyyy-MM-ddTHH:mm:ss"));
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$due", due.ToString("yyyy-MM-dd"));
        command.ExecuteNonQuery();
    }

    [Fact]
    public void The_database_learns_late_finishes_only_from_the_last_few_months()
    {
        // Five recent tasks checked off late teach a day of lead. Older ones, on time, and other actions don't count.
        for (var day = 1; day <= 5; day++)
        {
            AddFinish(ItemKind.Task, Today.AddDays(-day - 2), Now.AddDays(-day));
            AddFinish(ItemKind.Task, Today.AddDays(-day), Now.AddDays(-day), "Canceled");
        }

        for (var day = 1; day <= 20; day++)
        {
            AddFinish(ItemKind.Task, Today.AddDays(-SuggestionHabits.LookBackDays - day), Now.AddDays(-SuggestionHabits.LookBackDays - day));
        }

        Assert.Equal(1, _database.GetSuggestionHabits(Now).LeadDays(ItemKind.Task));
    }

    [Fact]
    public void A_session_that_went_stale_doesnt_teach_a_sitting_size()
    {
        var task = AddItem("Email advisor", ItemKind.Task);
        for (var day = 1; day <= 5; day++)
        {
            AddSession(task, Now.AddDays(-day), 35);
        }

        // Started and left going overnight, then finished the next time it came up.
        for (var day = 1; day <= 6; day++)
        {
            _database.StartActivation(ItemRef.Format(task), "Open it", [], Activation.TypedMethod, Now.AddDays(-day).AddHours(-20));
            _database.ApplySessionWord(SessionWord.Did, Now.AddDays(-day).AddHours(-20));
            Assert.Null(_database.GetActiveSession(Now.AddDays(-day)));
            _database.ResumeSession(Now.AddDays(-day));
            _database.ApplySessionWord(SessionWord.Did, Now.AddDays(-day));
        }

        AddSession(task, Now.AddDays(-7), 900);

        Assert.Equal(35, _database.GetSuggestionHabits(Now).LearnedTaskMinutes);
    }

    [Fact]
    public void Sessions_learn_their_kind_from_items_and_skip_ones_whose_item_is_gone()
    {
        var task = AddItem("Email advisor", ItemKind.Task);
        var gone = new StoredItem(12345, ItemKind.Deadline, "Gone", null, null, false, ItemSource.Capture);
        for (var day = 1; day <= 5; day++)
        {
            AddSession(task, Now.AddDays(-day), 35);
            AddSession(gone, Now.AddDays(-day).AddHours(3), 50);
            AddSession(task, Now.AddDays(-SuggestionHabits.LookBackDays - day), 20);
        }

        var habits = _database.GetSuggestionHabits(Now);

        Assert.Equal(35, habits.LearnedTaskMinutes);
        Assert.Null(habits.LearnedSittingMinutes);
    }

    [Fact]
    public void An_empty_database_gives_the_starting_guesses()
    {
        var habits = _database.GetSuggestionHabits(Now);

        Assert.Null(habits.LearnedStepMinutes);
        Assert.Null(habits.LearnedTaskMinutes);
        Assert.Null(habits.LearnedSittingMinutes);
        Assert.Null(habits.LearnedDayMinutes);
        Assert.Empty(habits.RunsLate!);
        Assert.Empty(habits.HardBlocks!);
    }
}
