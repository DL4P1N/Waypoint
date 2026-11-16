using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Tests;

public sealed class WindDownTests : IDisposable
{
    // 2026-11-02 is a Monday. Tuesday's 9:30 class gives a 12:30 AM bedtime with the defaults.
    private static readonly ClassSchedule Schedule = new(
        new DateOnly(2026, 9, 14),
        new DateOnly(2026, 12, 18),
        [new ClassMeeting("CS 101", "ABC 100", [DayOfWeek.Tuesday], new TimeOnly(9, 30), new TimeOnly(10, 45), 15)],
        []);

    private static readonly DateTime Bedtime = new(2026, 11, 3, 0, 30, 0);
    private static readonly DateOnly Tuesday = new(2026, 11, 3);
    private static readonly DateOnly Wednesday = new(2026, 11, 4);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public WindDownTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private BedtimeSchedule Bedtimes(BedtimeSettings? settings = null, ClassSchedule? schedule = null) =>
        new(settings ?? new BedtimeSettings(), schedule, _database.GetFirstShift);

    private BedtimeSchedule WithClass(BedtimeSettings? settings = null) => Bedtimes(settings, Schedule);

    private sealed class FakeSorter(CaptureResult? result = null) : ICaptureSorter
    {
        public int Calls { get; private set; }

        public AssistantContext? Seen { get; private set; }

        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            Seen = context;
            return Task.FromResult(result ?? new CaptureResult([]));
        }
    }

    // Half an hour before Tuesday's bedtime, so the wind-down is open whatever the real time is.
    private static readonly DateTime Now = Bedtime.AddMinutes(-30);

    /// <summary>A schedule whose wind-down is open at <see cref="Now"/>.</summary>
    private BedtimeSchedule OpenWindDown(BedtimeSettings? settings = null) => WithClass(settings);

    private async Task<CaptureOutcome> Say(BedtimeSchedule? bedtime, string text, ICaptureSorter? sorter = null)
    {
        CaptureOutcome? outcome = null;
        var service = new CaptureService(_database, sorter ?? new FakeSorter(), null, bedtime, () => Now);
        service.Sorted += result => outcome = result;
        service.Capture(text);
        await service.ProcessPendingAsync();
        return outcome!;
    }

    private static List<BedtimeTask> Walk(BedtimeReminders reminders, DateTime from, DateTime to)
    {
        var sent = new List<BedtimeTask>();
        for (var time = from; time <= to; time = time.AddMinutes(1))
        {
            if (reminders.Check(time) is { } reminder)
            {
                sent.Add(reminder.Task);
            }
        }

        return sent;
    }

    [Fact]
    public void Sends_every_stage_in_order_once_each()
    {
        var sent = Walk(new BedtimeReminders(WithClass(), _database), Bedtime.AddHours(-3), Bedtime.AddHours(1));

        Assert.Equal(
            [BedtimeTask.HeadsUp, BedtimeTask.EveningStep, BedtimeTask.SaveSpot, BedtimeTask.Routine, BedtimeTask.WindDownStep, BedtimeTask.WrapUp, BedtimeTask.Bed],
            sent);
    }

    [Fact]
    public void A_stage_set_to_null_is_left_out()
    {
        var settings = new BedtimeSettings(HeadsUpMinutes: null, WrapUpMinutes: null, EveningStepMinutes: null);

        var sent = Walk(new BedtimeReminders(WithClass(settings), _database), Bedtime.AddHours(-3), Bedtime.AddHours(1));

        Assert.Equal([BedtimeTask.SaveSpot, BedtimeTask.Routine, BedtimeTask.WindDownStep, BedtimeTask.Bed], sent);
    }

    [Fact]
    public void Stage_offsets_read_from_settings_and_null_turns_one_off()
    {
        var settings = WaypointSettings.Parse("""{ "bedtime": { "headsUpMinutes": 120, "wrapUpMinutes": null, "quietAfterWrapUp": true, "routineSteps": ["Pack bag"] }, "workCommuteMinutes": 25 }""");

        Assert.Equal(120, settings.Bedtime!.HeadsUpMinutes);
        Assert.Null(settings.Bedtime.WrapUpMinutes);
        Assert.Equal(60, settings.Bedtime.SaveSpotMinutes);
        Assert.True(settings.Bedtime.QuietAfterWrapUp);
        Assert.Equal(25, settings.WorkCommuteMinutes);
    }

    [Fact]
    public void A_restart_does_not_send_a_stage_again()
    {
        var at = Bedtime.AddMinutes(-90);
        Assert.Equal(BedtimeTask.HeadsUp, new BedtimeReminders(WithClass(), _database).Check(at)!.Task);

        Assert.Null(new BedtimeReminders(WithClass(), new WaypointDatabase(_path)).Check(at.AddMinutes(1)));
    }

    [Fact]
    public void A_late_wake_sends_only_the_latest_stage_and_carries_the_routine_along()
    {
        var reminders = new BedtimeReminders(WithClass(), _database);
        var late = Bedtime.AddMinutes(-20);

        var reminder = reminders.Check(late)!;

        Assert.Equal(BedtimeTask.WindDownStep, reminder.Task);
        Assert.Equal([BedtimeTask.EveningStep], reminder.Also!);
        Assert.Null(reminders.Check(late));
        Assert.Contains("Also: evening step", BedtimeRoutine.Describe(reminder).Text);
    }

    [Fact]
    public void Nothing_goes_out_long_after_bedtime()
    {
        Assert.Null(new BedtimeReminders(WithClass(), _database).Check(Bedtime.AddHours(2)));
    }

    [Fact]
    public void Snoozing_brings_bedtime_back_once()
    {
        var bedtimes = WithClass();
        var reminders = new BedtimeReminders(bedtimes, _database);
        Assert.Equal(BedtimeTask.Bed, reminders.Check(Bedtime)!.Task);

        BedtimeRoutine.Snooze(_database, bedtimes, Bedtime);

        Assert.Null(reminders.Check(Bedtime.AddMinutes(14)));
        Assert.Equal(BedtimeTask.Bed, reminders.Check(Bedtime.AddMinutes(15))!.Task);
        Assert.Null(reminders.Check(Bedtime.AddMinutes(16)));
    }

    [Fact]
    public void A_snooze_left_from_an_old_night_is_dropped()
    {
        var reminders = new BedtimeReminders(WithClass(), _database);
        _database.BedtimeSnoozedUntil = Bedtime.AddHours(-30);

        Assert.Null(reminders.Check(Bedtime.AddHours(-3)));
        Assert.Null(_database.BedtimeSnoozedUntil);
    }

    [Fact]
    public void Skipping_tonight_stops_the_rest_and_not_the_next_night()
    {
        var bedtimes = Bedtimes();
        var evening = new DateOnly(2026, 11, 2);
        _database.AddWorkShift(Tuesday.ToDateTime(new TimeOnly(9, 0)), Tuesday.ToDateTime(new TimeOnly(17, 0)));
        _database.AddWorkShift(Wednesday.ToDateTime(new TimeOnly(9, 0)), Wednesday.ToDateTime(new TimeOnly(17, 0)));
        var reminders = new BedtimeReminders(bedtimes, _database);

        BedtimeRoutine.Dismiss(_database, bedtimes, evening.ToDateTime(new TimeOnly(20, 0)));

        Assert.Empty(Walk(reminders, evening.ToDateTime(new TimeOnly(20, 0)), Tuesday.ToDateTime(new TimeOnly(2, 0))));
        Assert.NotEmpty(Walk(reminders, Tuesday.ToDateTime(new TimeOnly(20, 0)), Wednesday.ToDateTime(new TimeOnly(2, 0))));
    }

    [Fact]
    public void Nights_with_no_morning_get_nothing()
    {
        var evening = new DateTime(2026, 11, 3, 18, 0, 0);

        Assert.Empty(Walk(new BedtimeReminders(WithClass(), _database), evening, evening.AddHours(9)));
        Assert.Null(WithClass().GetWindDown(evening.AddHours(5)));
    }

    [Fact]
    public void A_morning_shift_sets_bedtime_and_commute_is_counted()
    {
        _database.AddWorkShift(Wednesday.ToDateTime(new TimeOnly(8, 0)), Wednesday.ToDateTime(new TimeOnly(14, 0)));

        var plan = WithClass().GetPlan(new DateTime(2026, 11, 3, 20, 0, 0));

        // 7:45 leave, 7:00 up, 8 hours of sleep.
        Assert.Equal(new DateTime(2026, 11, 3, 23, 0, 0), plan.Bedtime);
        Assert.True(plan.IsSchoolNight);
        Assert.Equal("work", plan.Commitment);
    }

    [Fact]
    public void The_earlier_of_class_and_shift_wins()
    {
        _database.AddWorkShift(Tuesday.ToDateTime(new TimeOnly(11, 0)), Tuesday.ToDateTime(new TimeOnly(15, 0)));
        Assert.Equal("CS 101", WithClass().GetPlan(new DateTime(2026, 11, 2, 20, 0, 0)).Commitment);

        _database.AddWorkShift(Tuesday.ToDateTime(new TimeOnly(7, 0)), Tuesday.ToDateTime(new TimeOnly(15, 0)));
        var plan = WithClass().GetPlan(new DateTime(2026, 11, 2, 20, 0, 0));

        Assert.Equal("work", plan.Commitment);
        Assert.Equal(new DateTime(2026, 11, 2, 22, 0, 0), plan.Bedtime);
    }

    [Fact]
    public void An_afternoon_shift_is_not_an_early_morning()
    {
        _database.AddWorkShift(Wednesday.ToDateTime(new TimeOnly(13, 0)), Wednesday.ToDateTime(new TimeOnly(18, 0)));

        Assert.False(WithClass().GetPlan(new DateTime(2026, 11, 3, 20, 0, 0)).IsSchoolNight);
    }

    [Fact]
    public void Weekly_hours_repeat_and_a_day_off_or_one_off_takes_their_place()
    {
        _database.SetWeeklyHours(["Mondays", "Wednesday"], "09:00-17:00");
        var monday = new DateOnly(2026, 11, 2);
        var nextWednesday = Wednesday.AddDays(7);

        Assert.Equal(new TimeOnly(9, 0), _database.GetFirstShift(monday)!.Start);
        Assert.Null(_database.GetFirstShift(Tuesday));

        _database.SetDayOff(Wednesday.ToDateTime(TimeOnly.MinValue));
        Assert.Null(_database.GetFirstShift(Wednesday));
        Assert.NotNull(_database.GetFirstShift(nextWednesday));

        _database.AddWorkShift(nextWednesday.ToDateTime(new TimeOnly(10, 0)), nextWednesday.ToDateTime(new TimeOnly(12, 0)));
        Assert.Equal(new TimeOnly(10, 0), _database.GetFirstShift(nextWednesday)!.Start);
    }

    [Fact]
    public void Removing_a_day_keeps_the_others()
    {
        _database.SetWeeklyHours(["Monday", "Wednesday"], "09:00-17:00");

        _database.RemoveWeeklyHours(["Mondays"]);

        Assert.Null(_database.GetFirstShift(new DateOnly(2026, 11, 2)));
        Assert.NotNull(_database.GetFirstShift(Wednesday));
    }

    [Theory]
    [InlineData("Someday", "09:00-17:00")]
    [InlineData("Tuesday", "mornings")]
    [InlineData("Tuesday", "09:00-09:00")]
    [InlineData("Tuesday", null)]
    public void Unclear_hours_ask_and_save_nothing(string day, string? hours)
    {
        var reply = _database.SetWeeklyHours([day], hours);

        Assert.True(reply.IsAnswer);
        Assert.Contains("?", reply.Text);
        Assert.Null(_database.GetFirstShift(Tuesday));
    }

    [Fact]
    public void Half_read_days_save_nothing()
    {
        _database.SetWeeklyHours(["Tuesday", "whenever"], "09:00-17:00");

        Assert.Null(_database.GetFirstShift(Tuesday));
    }

    [Fact]
    public void Commute_comes_from_typing_then_settings()
    {
        _database.DefaultCommuteMinutes = 25;
        _database.SetWeeklyHours(["Tuesday"], "09:00-17:00");
        Assert.Equal(25, _database.GetFirstShift(Tuesday)!.CommuteMinutes);

        _database.SetCommute("40");
        Assert.Equal(40, _database.GetFirstShift(Tuesday)!.CommuteMinutes);

        Assert.True(_database.SetCommute("a while").IsAnswer);
    }

    [Fact]
    public void Showing_work_lists_regular_hours_and_exceptions()
    {
        _database.SetWeeklyHours(["Tuesday"], "09:00-17:00");
        _database.SetDayOff(new DateTime(2026, 11, 3));

        var text = _database.DescribeWork(new DateOnly(2026, 11, 1));

        Assert.Contains("Tuesday: 9:00 AM to 5:00 PM", text);
        Assert.Contains("Tue, Nov 3: off", text);
    }

    [Fact]
    public void Work_ops_go_through_the_sorter_path()
    {
        var reply = AssistantOps.Apply(new AssistantOp(AssistantOpKind.WorkHoursSet, Text: "09:00-17:00", Entries: ["Tuesday"]), _database, 0, DateTime.Now);

        Assert.StartsWith("Work saved: Tuesday", reply.Text);
        Assert.Contains("\"work_hours_set\"", ClaudeCliSorter.Schema);
        Assert.Contains("\"bedtime_snooze\"", ClaudeCliSorter.Schema);
    }

    [Fact]
    public void The_next_shift_reaches_the_sorter()
    {
        var shift = new WorkShift(Tuesday, new TimeOnly(9, 0), new TimeOnly(17, 0), 20);

        var prompt = ClaudeCliSorter.BuildPrompt("what do I do now", DateTime.Now, new AssistantContext(DateTime.Now, [], [], NextShift: shift));

        Assert.Contains("Next work shift: Tuesday, 2026-11-03 09:00 to 17:00, leave by 08:40", prompt);
    }

    [Fact]
    public void A_work_from_home_shift_reaches_the_sorter_with_a_start_time_and_no_leave_time()
    {
        var shift = new WorkShift(Tuesday, new TimeOnly(9, 0), new TimeOnly(17, 0), 0);

        var prompt = ClaudeCliSorter.BuildPrompt("what do I do now", DateTime.Now, new AssistantContext(DateTime.Now, [], [], NextShift: shift));

        Assert.Contains("works from home", prompt);
        Assert.Contains("work starts at 09:00", prompt);
        Assert.DoesNotContain("Next work shift: Tuesday, 2026-11-03 09:00 to 17:00, leave by", prompt);
        Assert.Contains("work_commute", ClaudeCliSorter.SystemPrompt);
        Assert.Contains("I work from home", ClaudeCliSorter.SystemPrompt);
    }

    [Fact]
    public void Working_from_home_is_a_commute_of_zero_that_leaves_shifts_starting_at_their_start()
    {
        Assert.False(_database.SetCommute("0").IsAnswer);
        Assert.Equal(0, _database.GetCommuteMinutes());
        _database.SetWeeklyHours(["Tuesday"], "09:00-17:00");
        var shift = _database.GetFirstShift(Tuesday)!;

        Assert.True(shift.IsRemote);
        Assert.Equal(shift.Starts, shift.LeaveAt);
        Assert.Equal((shift.Starts, shift.Date.ToDateTime(shift.End)), StudyWindows.Busy(Tuesday, null, [shift], []).Single());
    }

    [Fact]
    public async Task The_service_gives_the_sorter_the_next_shift()
    {
        var soon = Now.AddDays(1);
        _database.AddWorkShift(soon, soon.AddHours(2));
        var sorter = new FakeSorter();

        await Say(null, "what do I do now", sorter);

        Assert.Equal(DateOnly.FromDateTime(soon), sorter.Seen!.NextShift!.Date);
    }

    // Acceptance test 6.
    [Fact]
    public void A_reminder_going_out_never_marks_a_wind_down_step()
    {
        var bedtimes = WithClass();
        var reminders = new BedtimeReminders(bedtimes, _database);

        Assert.Contains(BedtimeTask.WindDownStep, Walk(reminders, Bedtime.AddHours(-3), Bedtime));

        var status = BedtimeRoutine.GetStatus(_database, bedtimes, Bedtime);
        Assert.False(status!.Steps.Single(step => step.Label == BedtimeSettings.DefaultWindDownStepName).Done);
    }

    [Fact]
    public async Task Saying_so_marks_it_without_the_sorter()
    {
        var bedtimes = OpenWindDown();
        var sorter = new FakeSorter();

        var outcome = await Say(bedtimes, "Done wind-down step.", sorter);

        Assert.Equal(0, sorter.Calls);
        Assert.Contains("Checked off wind-down step", outcome.Answer);
        Assert.Contains("evening step", outcome.Answer);
        Assert.True(BedtimeRoutine.GetStatus(_database, bedtimes, Now)!.Steps.Single(step => step.Label == BedtimeSettings.DefaultWindDownStepName).Done);
    }

    [Theory]
    [InlineData("didn't do the wind-down step")]
    [InlineData("should I do the wind-down step")]
    [InlineData("wind-down step tomorrow")]
    public async Task Other_mentions_of_a_step_mark_nothing(string text)
    {
        var bedtimes = OpenWindDown();
        var sorter = new FakeSorter();

        await Say(bedtimes, text, sorter);

        Assert.Equal(1, sorter.Calls);
        Assert.False(BedtimeRoutine.GetStatus(_database, bedtimes, Now)!.Steps.Single(step => step.Label == BedtimeSettings.DefaultWindDownStepName).Done);
    }

    [Fact]
    public async Task Outside_the_wind_down_the_phrase_goes_to_the_sorter()
    {
        var sorter = new FakeSorter();

        await Say(Bedtimes(), "done evening step", sorter);

        Assert.Equal(1, sorter.Calls);
    }

    [Fact]
    public void Status_leaves_a_step_out_when_its_reminder_is_off_and_adds_custom_steps()
    {
        var steps = BedtimeRoutine.Steps(new BedtimeSettings(WindDownStepMinutes: null, RoutineSteps: ["Pack bag"]));

        Assert.DoesNotContain(BedtimeSettings.DefaultWindDownStepName, steps);
        Assert.Contains(BedtimeRoutine.Teeth, steps);
        Assert.Contains("Pack bag", steps);
    }

    [Fact]
    public void Named_steps_show_their_names_and_take_done_phrases()
    {
        var settings = new BedtimeSettings(EveningStepName: " Stretch ", WindDownStepName: " ");

        Assert.Equal(["Stretch", BedtimeSettings.DefaultWindDownStepName], BedtimeRoutine.WindDownSteps(settings));
        Assert.Equal("Stretch", BedtimeRoutine.ParseDoneWord("Stretch done.", settings));
        Assert.Equal("Stretch", BedtimeRoutine.ParseDoneWord("done: stretch", settings));
        Assert.Equal(BedtimeSettings.DefaultWindDownStepName, BedtimeRoutine.ParseDoneWord(BedtimeRoutine.DoneText(settings.WindDownStepLabel()), settings));
        Assert.Null(BedtimeRoutine.ParseDoneWord("didn't stretch", settings));

        var reminder = new BedtimeReminder(BedtimeTask.WindDownStep, WithClass(settings).GetPlan(Bedtime), [BedtimeTask.EveningStep]);
        var (headline, text) = BedtimeRoutine.Describe(reminder, settings: settings);
        Assert.Equal("Time for wind-down step", headline);
        Assert.Contains("Also: stretch", text);
    }

    [Fact]
    public void Status_only_shows_during_the_wind_down()
    {
        var bedtimes = WithClass();

        Assert.Null(BedtimeRoutine.GetStatus(_database, bedtimes, Bedtime.AddMinutes(-91)));
        Assert.NotNull(BedtimeRoutine.GetStatus(_database, bedtimes, Bedtime.AddMinutes(-90)));
        Assert.NotNull(BedtimeRoutine.GetStatus(_database, bedtimes, Bedtime.AddMinutes(59)));
        Assert.Null(BedtimeRoutine.GetStatus(_database, bedtimes, Bedtime.AddMinutes(60)));
    }

    [Fact]
    public void Marks_belong_to_one_night()
    {
        var bedtimes = OpenWindDown();
        var now = Now;

        BedtimeRoutine.Mark(_database, bedtimes, BedtimeSettings.DefaultEveningStepName, true, now);
        Assert.Contains(BedtimeSettings.DefaultEveningStepName, _database.GetRoutineMarks(bedtimes.GetPlan(now).Morning));

        Assert.Empty(_database.GetRoutineMarks(bedtimes.GetPlan(now).Morning.AddDays(1)));

        BedtimeRoutine.Mark(_database, bedtimes, "evening step", false, now);
        Assert.Empty(_database.GetRoutineMarks(bedtimes.GetPlan(now).Morning));
    }

    [Fact]
    public void Saving_a_handoff_checks_off_saving_the_project()
    {
        var bedtimes = OpenWindDown();
        var now = Now;
        var capture = _database.AddCapture("save my spot");

        _database.SaveHandoff("Waypoint", "Bedtime card", "Wire the button", [], capture, now);

        Assert.True(BedtimeRoutine.GetStatus(_database, bedtimes, now)!.Steps.Single(step => step.Label == BedtimeRoutine.SaveProject).Done);
    }

    [Fact]
    public void An_unknown_step_asks()
    {
        var reply = BedtimeRoutine.Mark(_database, OpenWindDown(), "the laundry", true, Now);

        Assert.True(reply.IsAnswer);
        Assert.StartsWith("Which one?", reply.Text);
    }

    [Fact]
    public void Quiet_runs_from_wrap_up_to_morning_only_when_turned_on()
    {
        var quiet = WithClass(new BedtimeSettings(QuietAfterWrapUp: true));

        Assert.False(quiet.IsQuiet(Bedtime.AddMinutes(-16)));
        Assert.True(quiet.IsQuiet(Bedtime.AddMinutes(-15)));
        Assert.True(quiet.IsQuiet(Bedtime.AddHours(7)));
        Assert.False(quiet.IsQuiet(Bedtime.AddHours(8)));
        Assert.False(WithClass().IsQuiet(Bedtime));
    }

    [Fact]
    public void Held_notices_come_back_once_in_order()
    {
        _database.HoldNotice("$20 at Target", "Card: Contoso Card", CardTone.Money, true);
        _database.HoldNotice("Deposit", "Added", CardTone.Money, false);

        var held = _database.TakeHeldNotices();

        Assert.Equal(["$20 at Target", "Deposit"], held.Select(notice => notice.Title));
        Assert.True(held[0].ToBudget);
        Assert.Empty(_database.TakeHeldNotices());
    }

    [Fact]
    public void Stage_text_stays_gentle_and_gives_no_advice()
    {
        var plan = WithClass().GetPlan(new DateTime(2026, 11, 2, 20, 0, 0));
        var words = Enum.GetValues<BedtimeTask>().Select(task => BedtimeRoutine.Describe(new BedtimeReminder(task, plan))).Select(text => $"{text.Headline} {text.Text}").ToList();

        Assert.All(words, text => Assert.DoesNotMatch(new Regex(@"\b(must|late|failed|overdue)\b", RegexOptions.IgnoreCase), text));
    }

    // Acceptance test 7.
    [Fact]
    public void Nothing_in_the_bedtime_code_can_close_or_block_anything()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Waypoint.Core", "Schedule");
        var code = string.Join("\n", Directory.GetFiles(source, "*.cs").Select(File.ReadAllText));

        Assert.DoesNotMatch(new Regex(@"Process\.|\.Kill\(|LockWorkStation|ExitWindows|Shutdown"), code);
    }
}
