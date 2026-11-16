using Microsoft.Data.Sqlite;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Email;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Tests;

/// <summary>Classes saved on the Settings page, laid over the ones in settings.json and used without a restart.</summary>
public sealed class ClassSetupTests : IDisposable
{
    private static readonly ClassMeeting FileClass = new("CS 220", "Room 1", [DayOfWeek.Tuesday, DayOfWeek.Thursday], new TimeOnly(9, 0), new TimeOnly(10, 20), 10);
    private static readonly ClassMeeting PageClass = new("MATH 140", "Room 2", [DayOfWeek.Monday, DayOfWeek.Wednesday], new TimeOnly(13, 0), new TimeOnly(14, 20), 15);

    private static readonly ClassSchedule FileSchedule = new(
        new DateOnly(2026, 9, 14),
        new DateOnly(2026, 12, 18),
        [FileClass],
        [new DateOnly(2026, 10, 16)],
        20);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-classes-{Guid.NewGuid()}");
    private readonly WaypointDatabase _database;

    public ClassSetupTests()
    {
        Directory.CreateDirectory(_folder);
        _database = new WaypointDatabase(Path.Combine(_folder, "waypoint.db"));
    }

    public void Dispose()
    {
        for (var attempt = 0; ; attempt++)
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_folder, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(100);
            }
        }
    }

    private sealed class NeverSorts : ICaptureSorter
    {
        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<CaptureResult?>(null);
    }

    private sealed class NoPlans : INudgePlanner
    {
        public Task<IReadOnlyList<EventNudges>?> PlanAsync(IReadOnlyList<StoredItem> events, DateTime now, ClassSchedule? schedule, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EventNudges>?>([]);
    }

    private sealed class NoTokens : ITokenStore
    {
        public string? Load() => null;

        public void Save(string token)
        {
        }
    }

    private sealed class CountingHost : IWorkerHost
    {
        public int Changes { get; private set; }

        public void ShowNotice(string title, string text)
        {
        }

        public void ShowReminder(DueReminder reminder, Action closed)
        {
        }

        public void ShowWeeklyReset(string text)
        {
        }

        public void Changed() => Changes++;

        public void Post(Action action) => action();
    }

    private WaypointWorker Worker(ClassSchedule? file, CountingHost? host = null) =>
        new(_database, new WaypointSettings(Schedule: file), _folder, host ?? new CountingHost(), exception => throw exception, new WorkerParts(
            _ => new NoTokens(),
            Http: () => throw new InvalidOperationException("No network in tests"),
            CaptureSorter: new NeverSorts(),
            NudgePlanner: new NoPlans(),
            ConnectGateway: false));

    [Fact]
    public void With_nothing_saved_the_files_classes_are_used()
    {
        var worker = Worker(FileSchedule);

        Assert.Equal([FileClass], worker.Schedule.Meetings);
        var form = worker.GetClasses();
        Assert.False(form.SavedHere);
        Assert.Equal(new DateOnly(2026, 9, 14), form.TermStart);
        Assert.Equal(new DateOnly(2026, 12, 18), form.TermEnd);
        Assert.Equal(["CS 220"], _database.ScheduleCourses);
    }

    [Fact]
    public void With_no_classes_anywhere_there_is_still_a_schedule_to_change()
    {
        var worker = Worker(null);

        Assert.Empty(worker.Schedule.Meetings);
        Assert.Null(worker.Schedule.GetNextClass(new DateTime(2026, 11, 10, 8, 0, 0)));
        Assert.Equal(new ClassesForm(null, null, worker.Schedule.Meetings, false), worker.GetClasses());
    }

    [Fact]
    public void Saved_classes_are_used_in_place_straight_away_and_after_a_restart()
    {
        var host = new CountingHost();
        var worker = Worker(FileSchedule, host);
        var running = worker.Schedule;

        var reply = worker.SaveClasses(new ClassesForm(null, new DateOnly(2026, 12, 18), [FileClass, PageClass]));

        Assert.Equal("Saved. Waypoint uses these 2 classes straight away.", reply);
        Assert.Same(running, worker.Schedule);
        Assert.Equal(["CS 220", "MATH 140"], running.Meetings.Select(meeting => meeting.Course));
        Assert.Equal(new DateOnly(2026, 9, 14), running.TermStart);
        Assert.Equal(new DateOnly(2026, 12, 18), running.TermEnd);
        Assert.Equal([new DateOnly(2026, 10, 16)], running.NoClassDates);
        Assert.Equal(20, running.FirstWarningMinutes);
        Assert.Equal(new DateTime(2026, 11, 11, 13, 0, 0), running.GetNextClass(new DateTime(2026, 11, 10, 12, 0, 0))!.Start);
        Assert.Contains("MATH 140", _database.ScheduleCourses);
        Assert.Equal(1, host.Changes);

        var restarted = Worker(FileSchedule);
        Assert.Equal(["CS 220", "MATH 140"], restarted.Schedule.Meetings.Select(meeting => meeting.Course));
        Assert.True(restarted.GetClasses().SavedHere);
    }

    [Fact]
    public void Removing_every_class_on_the_page_keeps_them_removed_over_the_file()
    {
        var worker = Worker(FileSchedule);

        Assert.Equal("Saved. There are no classes now.", worker.SaveClasses(new ClassesForm(null, null, [])));

        Assert.Empty(worker.Schedule.Meetings);
        Assert.Empty(Worker(FileSchedule).Schedule.Meetings);
    }

    [Fact]
    public void A_class_with_no_term_dates_runs_until_some_are_set()
    {
        var worker = Worker(null);

        worker.SaveClasses(new ClassesForm(null, null, [PageClass]));

        Assert.Equal(new DateTime(2026, 11, 11, 13, 0, 0), worker.Schedule.GetNextClass(new DateTime(2026, 11, 10, 12, 0, 0))!.Start);
        Assert.Single(worker.Schedule.GetClasses(new DateOnly(2027, 3, 1)));
        var form = worker.GetClasses();
        Assert.Null(form.TermStart);
        Assert.Null(form.TermEnd);
    }

    [Theory]
    [InlineData("", "Give the class a name, like CS 101.")]
    [InlineData("no days", "Pick the days no days meets.")]
    [InlineData("backwards", "backwards has to end after it starts.")]
    [InlineData("far", "Minutes to get there has to be between 0 and 240.")]
    public void A_class_that_cant_be_used_is_turned_down_and_nothing_changes(string name, string problem)
    {
        var worker = Worker(FileSchedule);
        var meeting = name switch
        {
            "no days" => PageClass with { Course = name, Days = [] },
            "backwards" => PageClass with { Course = name, End = new TimeOnly(12, 0) },
            "far" => PageClass with { Course = name, TravelMinutes = 500 },
            _ => PageClass with { Course = name },
        };

        Assert.Equal(problem, worker.SaveClasses(new ClassesForm(null, null, [meeting])));

        Assert.Equal([FileClass], worker.Schedule.Meetings);
        Assert.Null(ClassSetup.Read(_database));
    }

    [Fact]
    public void A_term_that_ends_before_it_starts_is_turned_down()
    {
        var worker = Worker(FileSchedule);

        Assert.Equal(
            "The term's last day has to come after its first day.",
            worker.SaveClasses(new ClassesForm(new DateOnly(2026, 12, 1), new DateOnly(2026, 8, 1), [FileClass])));
    }

    [Fact]
    public void Saved_classes_are_tidied()
    {
        var worker = Worker(null);

        worker.SaveClasses(new ClassesForm(null, null, [PageClass with { Course = "  MATH 140 ", Room = " Room 2 ", Days = [DayOfWeek.Wednesday, DayOfWeek.Monday, DayOfWeek.Monday] }]));

        var saved = Assert.Single(worker.Schedule.Meetings);
        Assert.Equal("MATH 140", saved.Course);
        Assert.Equal("Room 2", saved.Room);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Wednesday], saved.Days);
    }

    [Fact]
    public void A_pc_in_server_mode_takes_the_servers_classes_and_keeps_its_own_days_off()
    {
        var here = FileSchedule with { };

        here.Use(ClassSetup.FromForm(new ClassesForm(new DateOnly(2027, 1, 6), null, [PageClass]), here));

        Assert.Equal([PageClass], here.Meetings);
        Assert.Equal(new DateOnly(2027, 1, 6), here.TermStart);
        Assert.Equal(DateOnly.MaxValue, here.TermEnd);
        Assert.Equal([new DateOnly(2026, 10, 16)], here.NoClassDates);
        Assert.Equal(20, here.FirstWarningMinutes);
    }

    [Fact]
    public void Settings_json_still_loads_and_with_still_copies()
    {
        var settings = WaypointSettings.Parse("""
            { "schedule": { "termStart": "2026-09-14", "termEnd": "2026-12-18", "firstWarningMinutes": 5,
              "meetings": [ { "course": "CS 220", "room": "Room 1", "days": ["Tuesday"], "start": "09:00", "end": "10:20" } ] } }
            """);

        var schedule = settings.Schedule!;
        Assert.Equal(5, schedule.FirstWarningMinutes);
        Assert.Empty(schedule.NoClassDates);
        var holiday = schedule with { NoClassDates = [new DateOnly(2026, 11, 3)] };
        Assert.Empty(holiday.GetClasses(new DateOnly(2026, 11, 3)));
        Assert.Single(schedule.GetClasses(new DateOnly(2026, 11, 3)));
    }
}
