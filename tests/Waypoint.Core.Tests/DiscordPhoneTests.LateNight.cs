using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Tests;

/// <summary>Late-night check-ins go to Discord with their three buttons, and only while the PC is in use.</summary>
public sealed partial class DiscordPhoneTests
{
    // Work Wednesday at 8, leaving at 7:45, so bedtime Tuesday night is 11:00 PM.
    private static readonly DateTime LateBedtime = new(2026, 11, 10, 23, 0, 0);
    private const string LateNight = "20261111";

    private const string LateSavePress = """
        {"id":"9","token":"tok","type":3,"channel_id":"alerts","data":{"custom_id":"waypoint:act:late-save:20261111","component_type":2},"member":{"user":{"id":"owner"}},"message":{"id":"m3"}}
        """;

    private sealed class SetIdle : IUserIdle
    {
        public TimeSpan? Idle { get; set; } = TimeSpan.FromMinutes(1);

        public TimeSpan? IdleFor(DateTime now) => Idle;
    }

    private WaypointWorker LateWorker(SetIdle idle)
    {
        _database.SetWeeklyHours(["Wednesday"], "08:00-12:00");
        var settings = new WaypointSettings(Bedtime: new BedtimeSettings(LateCheckIns: true), Discord: new DiscordSettings("token", "owner", "capture", "alerts", "budget"));
        var worker = new WaypointWorker(_database, settings, _folder, new Host(), exception => throw exception, new WorkerParts(
            _ => new NoTokens(),
            Clock: () => _now,
            Http: () => new HttpClient(_discord),
            CaptureSorter: new NullSorter(),
            ConnectGateway: false,
            Idle: idle))
        {
            RetryWait = _ => Task.CompletedTask,
        };
        worker.StartDiscord();
        return worker;
    }

    private int CheckInsSent() => _discord.Of(HttpMethod.Post).Count(request => request.Body.Contains("waypoint:act:late-save"));

    [Fact]
    public async Task A_check_in_goes_to_Discord_half_an_hour_after_the_bedtime_reminder_with_its_buttons()
    {
        var worker = LateWorker(new SetIdle());

        _now = LateBedtime;
        worker.CheckAlerts();
        await WaitFor(() => _discord.Of(HttpMethod.Post).Any(request => request.Body.Contains("waypoint:snooze-bedtime")));
        Assert.Equal(0, CheckInsSent());

        _now = LateBedtime.AddMinutes(30);
        worker.CheckAlerts();
        await WaitFor(() => CheckInsSent() == 1);

        var card = _discord.Of(HttpMethod.Post).Single(request => request.Body.Contains("waypoint:act:late-save")).Body;
        Assert.Contains("11:30 PM", card);
        Assert.Contains("work at 8:00 AM", card);
        Assert.Contains("7:45 AM", card);
        Assert.Contains("7 hours 30 minutes", card);
        Assert.Contains(LateCheckIns.SaveAndStop, card);
        Assert.Contains(LateCheckIns.StopAtLabel, card);
        Assert.Contains(LateCheckIns.NotTonight, card);
        Assert.Contains("\"name\":\"Not checked off yet\",\"value\":\"evening step, wind-down step\"", card);
        Assert.Contains("waypoint:act:late-done:20261111", card);
        Assert.Contains("waypoint:act:late-evening-step:20261111", card);
    }

    [Fact]
    public async Task Done_on_a_check_in_checks_off_a_wind_down_step_and_it_drops_off_the_card()
    {
        var worker = LateWorker(new SetIdle());
        _now = LateBedtime.AddMinutes(30);

        var result = await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + WaypointWorker.LateWindDownStepDone + LateNight, null, "m1", "alerts"));

        Assert.Contains(BedtimeSettings.DefaultWindDownStepName, _database.GetRoutineMarks(DateOnly.FromDateTime(LateBedtime.AddDays(1))));
        var labels = result!.Card!.Actions.Select(action => action.Label).ToList();
        Assert.DoesNotContain("Done: Wind-down step", labels);
        Assert.Contains("Done: Evening step", labels);
        Assert.Contains(LateCheckIns.NotTonight, labels);

        _now = LateBedtime.AddMinutes(60);
        worker.CheckAlerts();
        await WaitFor(() => CheckInsSent() == 1);
        var card = _discord.Of(HttpMethod.Post).Single(request => request.Body.Contains("waypoint:act:late-save")).Body;
        Assert.DoesNotContain("late-done", card);
        Assert.Contains("\"name\":\"Not checked off yet\",\"value\":\"evening step\"", card);
    }

    [Fact]
    public async Task An_old_cards_Not_tonight_has_run_out_and_leaves_the_next_night_alone()
    {
        var worker = LateWorker(new SetIdle());
        _now = LateBedtime.AddDays(7).AddMinutes(30);

        var result = await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + WaypointWorker.LateNotTonight + LateNight, null, "m1", "alerts"));

        Assert.Equal(LateCheckIns.RunOut, result!.Outcome);
        Assert.False(_database.WasSent(DateOnly.FromDateTime(LateBedtime.AddDays(8)), BedtimeTask.LateCheckIns));
    }

    [Fact]
    public async Task Once_the_PC_goes_idle_nothing_more_goes_out_that_night()
    {
        var idle = new SetIdle();
        var worker = LateWorker(idle);
        _now = LateBedtime.AddMinutes(30);
        worker.CheckAlerts();
        await WaitFor(() => CheckInsSent() == 1);

        idle.Idle = TimeSpan.FromMinutes(16);
        _now = LateBedtime.AddMinutes(60);
        worker.CheckAlerts();
        idle.Idle = TimeSpan.FromMinutes(1);
        _now = LateBedtime.AddMinutes(90);
        worker.CheckAlerts();
        await Task.Delay(100);

        Assert.Equal(1, CheckInsSent());
    }

    [Fact]
    public async Task Save_my_spot_and_stop_opens_the_form_and_ends_the_check_ins()
    {
        var worker = LateWorker(new SetIdle());
        _now = LateBedtime.AddMinutes(30);

        await worker.Discord!.HandleInteractionAsync(Json(LateSavePress), (_, _) => null);

        var form = _discord.All.Single(request => request.Url.Contains("/callback")).Body;
        Assert.Contains("\"type\":9", form);
        Assert.Contains("waypoint:spot-form", form);
        Assert.True(_database.WasSent(DateOnly.FromDateTime(LateBedtime.AddDays(1)), BedtimeTask.LateCheckIns));
    }

    [Fact]
    public void Save_my_spot_and_stop_is_answered_at_once_since_it_opens_a_form()
    {
        var bridge = LateWorker(new SetIdle()).Discord!;

        Assert.Equal(PressReply.Form, bridge.PressWillReply!(DiscordBridge.ActionPrefix + WaypointWorker.LateSaveAndStop + LateNight, "m1", []));
        Assert.Equal(PressReply.Update, bridge.PressWillReply!(DiscordBridge.ActionPrefix + WaypointWorker.LateNotTonight + LateNight, "m1", []));
    }

    [Fact]
    public async Task Only_the_owner_can_press_the_check_in_buttons()
    {
        var worker = LateWorker(new SetIdle());
        _now = LateBedtime.AddMinutes(30);

        await worker.Discord!.HandleInteractionAsync(Json(LateSavePress.Replace("\"id\":\"owner\"", "\"id\":\"someone\"")), (_, _) => null);

        Assert.Contains("Only the owner", _discord.All.Single(request => request.Url.Contains("/callback")).Body);
        Assert.False(_database.WasSent(DateOnly.FromDateTime(LateBedtime.AddDays(1)), BedtimeTask.LateCheckIns));
    }

    [Fact]
    public async Task Stop_at_offers_times_and_one_reminder_comes_at_the_one_picked()
    {
        var worker = LateWorker(new SetIdle());
        _now = LateBedtime.AddMinutes(32);

        var picks = await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + WaypointWorker.LateStopAt + LateNight, null, "m1", "alerts"));
        var times = picks!.Card!.Actions.Select(action => action.Label).ToList();
        Assert.Equal(["11:45 PM", "12:00 AM", "12:15 AM", "12:30 AM", LateCheckIns.NotTonight], times);

        var midnight = picks.Card.Actions[1].Id;
        var picked = await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + midnight, null, "m1", "alerts"));
        Assert.Contains("12:00 AM", picked!.Outcome);

        _now = LateBedtime.AddMinutes(45);
        worker.CheckAlerts();
        await Task.Delay(100);
        Assert.Equal(0, CheckInsSent());

        _now = LateBedtime.AddMinutes(60);
        worker.CheckAlerts();
        await WaitFor(() => _discord.Of(HttpMethod.Post).Any(request => request.Body.Contains("the time you picked to stop")));

        _now = LateBedtime.AddMinutes(95);
        worker.CheckAlerts();
        await Task.Delay(100);
        Assert.Equal(0, CheckInsSent());
    }

    [Fact]
    public async Task Not_tonight_ends_the_check_ins()
    {
        var worker = LateWorker(new SetIdle());
        _now = LateBedtime.AddMinutes(30);

        var result = await worker.OnButtonActedAsync(new ButtonPress(DiscordBridge.ActionPrefix + WaypointWorker.LateNotTonight + LateNight, null, "m1", "alerts"));
        Assert.Equal("No more check-ins tonight.", result!.Outcome);

        worker.CheckAlerts();
        _now = LateBedtime.AddMinutes(70);
        worker.CheckAlerts();
        await Task.Delay(100);
        Assert.Equal(0, CheckInsSent());
    }

    private sealed class CountingHost : IWorkerHost
    {
        public List<string> Notices { get; } = [];

        public void ShowNotice(string title, string text) => Notices.Add(title);

        public void ShowReminder(DueReminder reminder, Action closed)
        {
        }

        public void ShowWeeklyReset(string text)
        {
        }

        public void Changed()
        {
        }

        public void Post(Action action) => action();
    }

    [Fact]
    public void Without_Discord_no_check_in_shows_on_the_PC()
    {
        _database.SetWeeklyHours(["Wednesday"], "08:00-12:00");
        var host = new CountingHost();
        var worker = new WaypointWorker(_database, new WaypointSettings(), _folder, host, exception => throw exception, new WorkerParts(
            _ => new NoTokens(),
            Clock: () => _now,
            CaptureSorter: new NullSorter(),
            ConnectGateway: false,
            Idle: new SetIdle()));

        _now = LateBedtime.AddMinutes(30);
        worker.CheckAlerts();
        _now = LateBedtime.AddMinutes(61);
        worker.CheckAlerts();

        Assert.DoesNotContain(host.Notices, title => title.StartsWith("It's", StringComparison.Ordinal));
    }
}
