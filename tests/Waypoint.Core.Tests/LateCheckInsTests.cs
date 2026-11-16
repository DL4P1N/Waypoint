using Waypoint.Core.Remote;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Tests;

public class LateCheckInsTests
{
    // A Tuesday 9:30 class, leaving at 9:15. Up at 8:30, so bedtime Monday night is 12:30 AM.
    private static readonly ClassSchedule Schedule = new(
        new DateOnly(2026, 9, 14),
        new DateOnly(2026, 12, 18),
        [new ClassMeeting("CS 101", "ABC 100", [DayOfWeek.Tuesday], new TimeOnly(9, 30), new TimeOnly(10, 45), 15)],
        []);

    private static readonly DateTime Bedtime = new(2026, 11, 3, 0, 30, 0);
    private static readonly DateOnly Morning = new(2026, 11, 3);

    private readonly FakeIdle _idle = new();
    private readonly MemoryLog _log = new();

    private sealed class FakeIdle : IUserIdle
    {
        public TimeSpan? Idle { get; set; } = TimeSpan.FromMinutes(1);

        public int Reads { get; private set; }

        public TimeSpan? IdleFor(DateTime now)
        {
            Reads++;
            return Idle;
        }
    }

    private sealed class MemoryLog : ILateCheckInLog
    {
        public HashSet<(DateOnly, BedtimeTask)> Sent { get; } = [];

        public DateTime? BedtimeSnoozedUntil { get; set; }

        public DateTime? LastCheckIn { get; set; }

        public DateTime? CheckInStopAt { get; set; }

        public bool WasSent(DateOnly morning, BedtimeTask task) => Sent.Contains((morning, task));

        public void MarkSent(DateOnly morning, BedtimeTask task) => Sent.Add((morning, task));
    }

    private static BedtimeSchedule Bedtimes(BedtimeSettings? settings = null, ClassSchedule? schedule = null) =>
        new(settings ?? new BedtimeSettings(), schedule ?? Schedule);

    private LateCheckIns CheckIns(BedtimeSchedule? schedule = null) => new(schedule ?? Bedtimes(), _log, _idle);

    [Fact]
    public void Nothing_before_bedtime_and_the_idle_time_isnt_read_then()
    {
        var checkIns = CheckIns();

        Assert.Null(checkIns.Check(Bedtime.AddMinutes(-10)));
        Assert.Equal(0, _idle.Reads);
    }

    [Fact]
    public void While_the_PC_is_in_use_after_bedtime_one_goes_out_about_every_30_minutes()
    {
        var checkIns = CheckIns();

        Assert.NotNull(checkIns.Check(Bedtime));
        Assert.Null(checkIns.Check(Bedtime.AddMinutes(10)));
        Assert.Null(checkIns.Check(Bedtime.AddMinutes(29)));
        Assert.NotNull(checkIns.Check(Bedtime.AddMinutes(30)));
        Assert.NotNull(checkIns.Check(Bedtime.AddMinutes(61)));
    }

    [Fact]
    public void After_the_bedtime_reminder_the_first_comes_30_minutes_later()
    {
        _log.MarkSent(DateOnly.FromDateTime(Bedtime), BedtimeTask.Bed);
        var checkIns = CheckIns();

        Assert.Null(checkIns.Check(Bedtime));
        Assert.NotNull(checkIns.Check(Bedtime.AddMinutes(30)));
    }

    [Fact]
    public void Fifteen_minutes_with_no_input_ends_them_for_the_night()
    {
        var checkIns = CheckIns();
        Assert.NotNull(checkIns.Check(Bedtime));

        _idle.Idle = TimeSpan.FromMinutes(15);
        Assert.Null(checkIns.Check(Bedtime.AddMinutes(30)));

        _idle.Idle = TimeSpan.FromSeconds(5);
        var reads = _idle.Reads;
        Assert.Null(checkIns.Check(Bedtime.AddMinutes(60)));
        Assert.Null(checkIns.Check(Bedtime.AddMinutes(120)));
        Assert.Equal(reads, _idle.Reads);
    }

    [Fact]
    public void A_server_checking_before_the_PCs_first_report_waits_for_it()
    {
        var checkIns = CheckIns();
        _idle.Idle = null;

        Assert.Null(checkIns.Check(Bedtime));
        Assert.Null(checkIns.Check(Bedtime.AddMinutes(2)));
        Assert.False(_log.WasSent(Morning, BedtimeTask.LateCheckIns));

        _idle.Idle = TimeSpan.FromSeconds(20);
        Assert.NotNull(checkIns.Check(Bedtime.AddMinutes(2.5)));
    }

    [Fact]
    public void A_server_restart_in_the_night_waits_for_the_next_report()
    {
        Assert.NotNull(CheckIns().Check(Bedtime));

        // A fresh start has heard nothing yet.
        _idle.Idle = null;
        var restarted = CheckIns();
        Assert.Null(restarted.Check(Bedtime.AddMinutes(40)));
        Assert.Null(restarted.Check(Bedtime.AddMinutes(42)));

        _idle.Idle = TimeSpan.FromMinutes(1);
        Assert.NotNull(restarted.Check(Bedtime.AddMinutes(43)));
    }

    [Fact]
    public void A_PC_that_never_reports_counts_as_gone_for_the_night()
    {
        var checkIns = CheckIns();
        _idle.Idle = null;

        Assert.Null(checkIns.Check(Bedtime));
        Assert.Null(checkIns.Check(Bedtime.AddMinutes(14)));
        Assert.False(_log.WasSent(Morning, BedtimeTask.LateCheckIns));
        Assert.Null(checkIns.Check(Bedtime.AddMinutes(15)));
        Assert.True(_log.WasSent(Morning, BedtimeTask.LateCheckIns));

        _idle.Idle = TimeSpan.FromMinutes(1);
        Assert.Null(checkIns.Check(Bedtime.AddMinutes(30)));
        Assert.Null(new LateCheckIns(Bedtimes(), _log, null).Check(Bedtime.AddMinutes(60)));
    }

    [Fact]
    public void Only_nights_with_a_wind_down_get_them()
    {
        var noStages = new BedtimeSettings(EveningStepMinutes: null, WindDownStepMinutes: null, HeadsUpMinutes: null, SaveSpotMinutes: null, RoutineMinutes: null, WrapUpMinutes: null, BedMinutes: null);

        Assert.Null(CheckIns(Bedtimes(noStages)).Check(Bedtime.AddMinutes(5)));
        Assert.Equal(0, _idle.Reads);
    }

    [Fact]
    public void The_spacing_is_kept_in_UTC_and_forgotten_when_the_night_ends()
    {
        var checkIns = CheckIns();
        Assert.NotNull(checkIns.Check(Bedtime));
        Assert.Equal(DateTimeKind.Utc, _log.LastCheckIn!.Value.Kind);

        checkIns.StopAt(Morning, Bedtime.AddMinutes(20), Bedtime.AddMinutes(1));
        Assert.Equal(DateTimeKind.Utc, _log.CheckInStopAt!.Value.Kind);
        checkIns.StopTonight(Morning, Bedtime.AddMinutes(2));

        Assert.Null(_log.LastCheckIn);
        Assert.Null(_log.CheckInStopAt);
    }

    [Fact]
    public void An_old_cards_buttons_cant_end_the_next_night()
    {
        var checkIns = CheckIns();

        // A week later, the next Tuesday's night, pressing Not tonight on last week's card.
        var nextWeek = Bedtime.AddDays(7);
        Assert.Equal(LateCheckIns.RunOut, checkIns.StopTonight(Morning, nextWeek));
        Assert.Equal(LateCheckIns.RunOut, checkIns.StopAt(Morning, nextWeek.AddMinutes(30), nextWeek));
        Assert.NotNull(checkIns.Check(nextWeek));
        Assert.False(_log.WasSent(Morning.AddDays(7), BedtimeTask.LateCheckIns));
    }

    [Fact]
    public void Wind_down_steps_left_unchecked_lead_the_card()
    {
        var checkIns = CheckIns();
        var check = new LateCheckIn(Bedtimes().GetPlan(Bedtime), Bedtime.AddMinutes(30));

        Assert.Equal([BedtimeSettings.DefaultEveningStepName, BedtimeSettings.DefaultWindDownStepName], checkIns.Unmarked(new HashSet<string>()));
        Assert.Equal([BedtimeSettings.DefaultEveningStepName], checkIns.Unmarked(new HashSet<string> { BedtimeSettings.DefaultWindDownStepName }));
        Assert.Empty(CheckIns(Bedtimes(new BedtimeSettings(EveningStepMinutes: null, WindDownStepMinutes: null))).Unmarked(new HashSet<string>()));

        var (_, text) = checkIns.Describe(check, checkIns.Unmarked(new HashSet<string>()));
        Assert.StartsWith("Not checked off yet: evening step, wind-down step", text);
        Assert.DoesNotContain("Not checked off", checkIns.Describe(check, []).Text);
    }

    [Fact]
    public void Nights_without_an_early_morning_get_none()
    {
        var late = Schedule with { Meetings = [new ClassMeeting("Late", "X", [DayOfWeek.Tuesday], new TimeOnly(15, 0), new TimeOnly(16, 0))] };

        Assert.Null(CheckIns(Bedtimes(schedule: late)).Check(new DateTime(2026, 11, 3, 1, 30, 0)));
        Assert.Equal(0, _idle.Reads);
    }

    [Fact]
    public void Turned_off_sends_none_and_reads_nothing()
    {
        Assert.Null(CheckIns(Bedtimes(new BedtimeSettings(LateCheckIns: false))).Check(Bedtime.AddMinutes(5)));
        Assert.Equal(0, _idle.Reads);
    }

    [Fact]
    public void No_more_bedtime_reminders_tonight_ends_them_too()
    {
        var schedule = Bedtimes();
        var database = new MemoryLog();
        foreach (var task in Enum.GetValues<BedtimeTask>())
        {
            database.MarkSent(DateOnly.FromDateTime(Bedtime), task);
        }

        Assert.Null(new LateCheckIns(schedule, database, _idle).Check(Bedtime.AddMinutes(45)));
        Assert.True(Enum.IsDefined(BedtimeTask.LateCheckIns));
    }

    [Fact]
    public void Not_tonight_ends_them_and_the_next_night_starts_as_usual()
    {
        var checkIns = CheckIns();
        Assert.NotNull(checkIns.Check(Bedtime));

        Assert.Equal("No more check-ins tonight.", checkIns.StopTonight(Morning, Bedtime.AddMinutes(5)));
        Assert.Null(checkIns.Check(Bedtime.AddMinutes(40)));

        // The next Tuesday's class, a week later.
        Assert.NotNull(checkIns.Check(Bedtime.AddDays(7)));
    }

    [Fact]
    public void Stop_at_holds_them_until_then_sends_one_and_ends()
    {
        var checkIns = CheckIns();
        Assert.NotNull(checkIns.Check(Bedtime));

        var at = Bedtime.AddMinutes(45);
        Assert.Contains("1:15 AM", checkIns.StopAt(Morning, at, Bedtime.AddMinutes(1)));
        Assert.Null(checkIns.Check(Bedtime.AddMinutes(31)));

        var stop = checkIns.Check(at);
        Assert.NotNull(stop);
        Assert.True(stop.StopTime);
        Assert.Null(checkIns.Check(at.AddMinutes(30)));
    }

    [Fact]
    public void Going_to_bed_before_the_stop_time_sends_nothing()
    {
        var checkIns = CheckIns();
        checkIns.StopAt(Morning, Bedtime.AddMinutes(45), Bedtime);

        _idle.Idle = TimeSpan.FromMinutes(20);
        Assert.Null(checkIns.Check(Bedtime.AddMinutes(45)));
        _idle.Idle = TimeSpan.FromMinutes(1);
        Assert.Null(checkIns.Check(Bedtime.AddMinutes(50)));
    }

    [Fact]
    public void A_past_time_isnt_taken()
    {
        Assert.Equal("That time has passed.", CheckIns().StopAt(Morning, Bedtime, Bedtime.AddMinutes(5)));
        Assert.Null(_log.CheckInStopAt);
    }

    [Fact]
    public void The_card_says_the_time_the_class_the_leave_time_and_the_sleep_left()
    {
        var checkIns = CheckIns();
        var (headline, text) = checkIns.Describe(new LateCheckIn(Bedtimes().GetPlan(Bedtime), new DateTime(2026, 11, 3, 1, 0, 0)));

        Assert.Equal("It's 1:00 AM", headline);
        Assert.Contains("Tomorrow: CS 101 at 9:30 AM", text);
        Assert.Contains("Leave by: 9:15 AM", text);
        Assert.Contains("Sleep if you stop now: 7 hours 30 minutes", text);
        Assert.DoesNotContain("should", (headline + text).ToLowerInvariant());
        Assert.DoesNotContain("—", headline + text);
    }

    [Fact]
    public void A_work_from_home_shift_says_when_work_starts_not_when_to_leave()
    {
        var shift = new WorkShift(new DateOnly(2026, 11, 4), new TimeOnly(9, 0), new TimeOnly(17, 0), 0);
        var schedule = new BedtimeSchedule(new BedtimeSettings(), null, day => day == shift.Date ? shift : null);
        var plan = schedule.GetPlan(new DateTime(2026, 11, 3, 23, 0, 0));

        var (_, text) = new LateCheckIns(schedule, _log, _idle).Describe(new LateCheckIn(plan, new DateTime(2026, 11, 3, 23, 30, 0)));

        Assert.Contains("Work starts: 9:00 AM", text);
        Assert.DoesNotContain("Leave by", text);
        Assert.Contains("Sleep if you stop now: 8 hours 45 minutes", text);
    }

    [Fact]
    public void A_shift_morning_names_work()
    {
        var shift = new WorkShift(new DateOnly(2026, 11, 4), new TimeOnly(8, 0), new TimeOnly(12, 0), 15);
        var schedule = new BedtimeSchedule(new BedtimeSettings(), null, day => day == shift.Date ? shift : null);
        var plan = schedule.GetPlan(new DateTime(2026, 11, 3, 23, 0, 0));

        var (_, text) = new LateCheckIns(schedule, _log, _idle).Describe(new LateCheckIn(plan, new DateTime(2026, 11, 3, 23, 30, 0)));

        Assert.Contains("Tomorrow: work at 8:00 AM", text);
        Assert.Contains("Leave by: 7:45 AM", text);
        Assert.Contains("Sleep if you stop now: 7 hours 30 minutes", text);
    }

    [Fact]
    public void Stop_at_offers_four_times_on_the_clock()
    {
        var choices = LateCheckIns.StopAtChoices(new DateTime(2026, 11, 3, 0, 52, 30));

        Assert.Equal([new DateTime(2026, 11, 3, 1, 5, 0), new DateTime(2026, 11, 3, 1, 20, 0), new DateTime(2026, 11, 3, 1, 35, 0), new DateTime(2026, 11, 3, 1, 50, 0)], choices);
    }

    [Fact]
    public void The_setting_is_kept_with_the_other_bedtime_choices()
    {
        var off = BedtimeChoices.Parse(new BedtimeChoices(LateCheckIns: false).Serialize()).ApplyTo(new BedtimeSettings());

        Assert.False(off.LateCheckIns);
        Assert.True(BedtimeChoices.None.ApplyTo(new BedtimeSettings()).LateCheckIns);
    }

    [Fact]
    public void The_PC_reports_its_idle_time_only_in_the_window_and_about_once_a_minute()
    {
        var reporter = new IdleReporter(Bedtimes(), _idle);

        Assert.Null(reporter.Due(Bedtime.AddMinutes(-6)));
        Assert.Equal(0, _idle.Reads);

        // A few minutes early, so the server has a reading by bedtime.
        Assert.Equal(new IdleReport(60), reporter.Due(Bedtime.AddMinutes(-5)));
        Assert.Null(reporter.Due(Bedtime.AddMinutes(-4.5)));
        Assert.NotNull(reporter.Due(Bedtime.AddMinutes(-4)));
        Assert.Null(new IdleReporter(Bedtimes(new BedtimeSettings(LateCheckIns: false)), _idle).Due(Bedtime));
    }

    [Fact]
    public void The_server_forgets_a_report_after_a_few_minutes()
    {
        var reported = new ReportedIdle();
        Assert.Null(reported.IdleFor(Bedtime));

        reported.Report(new IdleReport(30), Bedtime);
        Assert.Equal(TimeSpan.FromSeconds(90), reported.IdleFor(Bedtime.AddMinutes(1)));
        Assert.Null(reported.IdleFor(Bedtime.AddMinutes(3)));

        // So a server with a quiet PC sends nothing, and soon counts it as gone.
        var checkIns = new LateCheckIns(Bedtimes(), _log, reported);
        Assert.Null(checkIns.Check(Bedtime.AddMinutes(5)));
        Assert.Null(checkIns.Check(Bedtime.AddMinutes(19)));
        Assert.False(_log.WasSent(Morning, BedtimeTask.LateCheckIns));
        Assert.Null(checkIns.Check(Bedtime.AddMinutes(20)));
        Assert.True(_log.WasSent(Morning, BedtimeTask.LateCheckIns));
    }

    [Fact]
    public void A_short_gap_in_readings_isnt_taken_as_bed()
    {
        var checkIns = CheckIns();
        Assert.NotNull(checkIns.Check(Bedtime));

        // The last reading showed input a minute before bedtime, so only 15 minutes from then counts as idle.
        _idle.Idle = null;
        Assert.Null(checkIns.Check(Bedtime.AddMinutes(6)));
        Assert.Null(checkIns.Check(Bedtime.AddMinutes(13)));
        Assert.False(_log.WasSent(Morning, BedtimeTask.LateCheckIns));
        Assert.Null(checkIns.Check(Bedtime.AddMinutes(14)));
        Assert.True(_log.WasSent(Morning, BedtimeTask.LateCheckIns));
    }

    [Fact]
    public void Times_left_from_a_night_the_PC_was_shut_down_in_are_cleared()
    {
        var checkIns = CheckIns();
        Assert.NotNull(checkIns.Check(Bedtime));
        checkIns.StopAt(Morning, Bedtime.AddMinutes(45), Bedtime.AddMinutes(1));

        // The PC shut down right after, so the night never ended. The next start, the next afternoon, clears them.
        var nextStart = CheckIns();
        Assert.Null(nextStart.Check(new DateTime(2026, 11, 3, 15, 0, 0)));
        Assert.Null(_log.LastCheckIn);
        Assert.Null(_log.CheckInStopAt);
    }

    [Fact]
    public void Times_from_an_earlier_night_are_cleared_when_the_next_window_opens()
    {
        _log.LastCheckIn = Bedtime.AddDays(-7).ToUniversalTime();
        _log.CheckInStopAt = Bedtime.AddDays(-7).AddMinutes(30).ToUniversalTime();

        Assert.NotNull(CheckIns().Check(Bedtime));
        Assert.Null(_log.CheckInStopAt);
        Assert.Equal(Bedtime.ToUniversalTime(), _log.LastCheckIn);
    }

    [Fact]
    public void Leaving_the_app_clears_the_times_only_outside_a_night()
    {
        var checkIns = CheckIns();
        Assert.NotNull(checkIns.Check(Bedtime));

        checkIns.Forget(Bedtime.AddMinutes(10));
        Assert.NotNull(_log.LastCheckIn);

        checkIns.Forget(new DateTime(2026, 11, 3, 12, 0, 0));
        Assert.Null(_log.LastCheckIn);
    }

    [Fact]
    public void Controller_input_counts_when_its_packet_number_changes()
    {
        uint? packet = 5;
        var controller = new ControllerIdle(index => index == 0 ? packet : null);

        Assert.Null(controller.IdleFor(Bedtime));
        Assert.Null(controller.IdleFor(Bedtime.AddMinutes(1)));

        packet = 9;
        Assert.Equal(TimeSpan.Zero, controller.IdleFor(Bedtime.AddMinutes(2)));
        Assert.Equal(TimeSpan.FromMinutes(5), controller.IdleFor(Bedtime.AddMinutes(7)));
    }

    [Fact]
    public void Idle_is_the_shortest_of_the_keyboard_and_mouse_and_the_controller()
    {
        var mouse = new FakeIdle { Idle = TimeSpan.FromMinutes(40) };
        var pad = new FakeIdle { Idle = TimeSpan.FromMinutes(2) };

        Assert.Equal(TimeSpan.FromMinutes(2), new MinimumIdle(mouse, pad).IdleFor(Bedtime));

        pad.Idle = null;
        Assert.Equal(TimeSpan.FromMinutes(40), new MinimumIdle(mouse, pad).IdleFor(Bedtime));

        mouse.Idle = null;
        Assert.Null(new MinimumIdle(mouse, pad).IdleFor(Bedtime));
    }
}
