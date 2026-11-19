using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Platform;
using Waypoint.Core.Remote;
using Waypoint.Core.Schedule;

namespace Waypoint.Server.Tests;

/// <summary>
/// Lists, History, Settings, and backups through the server, run in memory against a database in a temporary
/// folder. Each page's data class is used the way the PC uses it, so these check the whole trip.
/// </summary>
public sealed class PagesApiTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-pages-{Guid.NewGuid()}");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _token;

    public PagesApiTests()
    {
        Directory.CreateDirectory(_folder);
        _token = ServerToken.Create(_folder);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Waypoint:DataDirectory", _folder);
            builder.UseSetting("Waypoint:RunBackground", "false");
            builder.UseSetting("Waypoint:RestartAfterRestore", "false");
            builder.ConfigureServices(services => services.AddSingleton(new WorkerParts(
                path => new FileTokenStore(path),
                Http: () => new HttpClient(new Unreachable()),
                CaptureSorter: new NeverSorts(),
                ConnectGateway: false)));
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    /// <summary>Nothing leaves the test, so Discord is never really reached.</summary>
    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No network in tests");
    }

    private sealed class NeverSorts : ICaptureSorter
    {
        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<CaptureResult?>(null);
    }

    private HttpClient Paired()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return client;
    }

    private ServerClient Client() => new(Paired(), _token);

    private WaypointDatabase Database => _factory.Services.GetRequiredService<WaypointServer>().Database;

    [Theory]
    [InlineData("GET", "/api/history")]
    [InlineData("POST", "/api/history/reopen")]
    [InlineData("POST", "/api/lists/entries")]
    [InlineData("POST", "/api/lists/rename")]
    [InlineData("POST", "/api/lists/start")]
    [InlineData("GET", "/api/budget/purchases?words=milk")]
    [InlineData("GET", "/api/settings/weekly")]
    [InlineData("GET", "/api/settings/discord")]
    [InlineData("PUT", "/api/settings/discord")]
    [InlineData("PUT", "/api/settings/bedtime")]
    [InlineData("GET", "/api/settings/calendar")]
    [InlineData("PUT", "/api/settings/calendar")]
    [InlineData("DELETE", "/api/settings/calendar")]
    [InlineData("GET", "/api/settings/classes")]
    [InlineData("PUT", "/api/settings/classes")]
    [InlineData("GET", "/api/backups")]
    [InlineData("POST", "/api/backups")]
    [InlineData("POST", "/api/backups/restore")]
    [InlineData("GET", "/api/export/notes")]
    public async Task Every_page_route_needs_the_token(string method, string path)
    {
        using var none = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) };
        using var wrong = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) };
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token");

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().SendAsync(none)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().SendAsync(wrong)).StatusCode);
        Assert.Empty(Database.GetBackups());
    }

    [Fact]
    public void The_lists_page_works_the_same_through_the_server()
    {
        IListsData lists = new ServerListsData(Client());

        lists.AddListEntry(WaypointDatabase.ShoppingList, "oat milk", DateTime.Now);
        var milk = Assert.Single(lists.GetListOverview().Single(list => list.Name == WaypointDatabase.ShoppingList).Entries);
        Assert.Equal("oat milk", milk.Text);

        lists.EditEntry(milk, "oat milk, 2");
        lists.SetEntryDone(milk, true, DateTime.Now);
        Assert.Empty(Database.GetListEntries(WaypointDatabase.ShoppingList).Where(entry => !entry.Done));
        lists.SetEntryDone(milk, false, DateTime.Now);
        Assert.Equal("oat milk, 2", Assert.Single(Database.GetListEntries(WaypointDatabase.ShoppingList)).Text);

        lists.DeleteEntry(milk, DateTime.Now);
        Assert.Empty(lists.GetListOverview().Single(list => list.Name == WaypointDatabase.ShoppingList).Entries);
        lists.RestoreEntry(milk, DateTime.Now);
        Assert.Single(lists.GetListOverview().Single(list => list.Name == WaypointDatabase.ShoppingList).Entries);

        Assert.Null(lists.CreateList("Packing", DateTime.Now).Problem);
        Assert.NotNull(lists.CreateList(" ", DateTime.Now).Problem);
        Assert.Equal("Trip", lists.RenameList("Packing", "Trip", DateTime.Now).Name);
        lists.AddListEntry("Trip", "charger", DateTime.Now);
        Assert.Equal(["charger"], Database.GetListEntries("Trip").Select(entry => entry.Text));

        // The order lists were dragged into is kept on the server.
        lists.CreateList("Books", DateTime.Now);
        lists.OrderLists(["Trip", "Books"]);
        Assert.Equal(["Shopping", "Trip", "Books"], lists.GetListOverview().Select(list => list.Name));
        lists.OrderLists(["Books", "Trip"]);
        Assert.Equal(["Shopping", "Books", "Trip"], lists.GetListOverview().Select(list => list.Name));

        // Start over unchecks the list on the server and says where to begin.
        lists.SetEntryDone(Database.GetListEntries("Trip").Single(), true, DateTime.Now);
        Assert.Equal("Trip is ready. Start with: charger", lists.StartList("Trip"));
        Assert.False(Database.GetListEntries("Trip").Single().Done);

        Assert.Equal("Removed Trip", lists.DeleteList("Trip", DateTime.Now));
        Assert.DoesNotContain(lists.GetListOverview(), list => list.Name == "Trip");
    }

    [Fact]
    public void A_saved_spot_waiting_for_its_next_step_is_answered_through_the_server()
    {
        var notes = new ServerNotesData(Client());
        Assert.Null(notes.GetWaitingSpot(DateTime.Now));

        Database.SaveHandoff("essay", "the intro", null, [], Database.AddCapture("save my spot"), DateTime.Now);
        Assert.Equal("essay", notes.GetWaitingSpot(DateTime.Now)?.Project);

        Assert.Equal("Saved. Next time on essay: outline part two", notes.SetSpotNext("outline part two", DateTime.Now).Text);
        Assert.Equal("outline part two", Database.GetSavedSpots().Single().Next);
        Assert.Null(notes.GetWaitingSpot(DateTime.Now));
    }

    [Fact]
    public void Find_anywhere_searches_every_page_through_the_server()
    {
        var client = Client();
        var sources = new FindSources(new ServerToDoData(client), new ServerListsData(client), new ServerNotesData(client), new ServerHistoryData(client), new ServerBudgetData(client));
        var today = DateOnly.FromDateTime(DateTime.Now);
        Database.AddByHand(new HandAddedItem(ItemKind.Task, "Call the dentist", today), DateTime.Now);
        Database.AddToList("Errands", ["dentist forms"], 0, DateTime.Now);
        Database.CreateNote("Dentist questions", "ask about the cleaning", DateTime.Now);
        Database.SetDone(Database.AddByHand(new HandAddedItem(ItemKind.Task, "Book dentist cleaning", today), DateTime.Now).Item!, true);
        Database.AddExpense("Smile Dentist", 4500, "Health", today.AddMonths(-2), DateTime.Now);

        var found = FindAnywhere.Search("dentist", sources);

        Assert.Equal([FoundPage.ToDo, FoundPage.Lists, FoundPage.Notes, FoundPage.History, FoundPage.Budget], found.Select(thing => thing.Page));
        Assert.Equal(4500, found[^1].Purchase!.AmountCents);
        Assert.Equal("Book dentist cleaning", found[3].History!.Title);
    }

    [Fact]
    public void The_open_list_is_kept_on_the_server()
    {
        IListsData lists = new ServerListsData(Client());
        Assert.Null(lists.GetPreference("lists_open"));

        lists.SetPreference("lists_open", "Packing");

        Assert.Equal("Packing", Database.GetPreference("lists_open"));
        Assert.Equal("Packing", new ServerListsData(Client()).GetPreference("lists_open"));
    }

    [Fact]
    public void The_history_page_works_the_same_through_the_server()
    {
        IToDoData toDo = new ServerToDoData(Client());
        IHistoryData history = new ServerHistoryData(Client());
        var item = toDo.AddByHand(new HandAddedItem(ItemKind.Task, "Return books", DateOnly.FromDateTime(DateTime.Now)), DateTime.Now).Item!;
        toDo.SetDone(item, true);

        var row = Assert.Single(history.SearchHistory("books", 100, HistoryFilter.All));
        Assert.True(row.CanReopen);
        Assert.Empty(history.SearchHistory("dentist", 100, HistoryFilter.All));
        Assert.Single(history.SearchHistory(null, 100, HistoryFilter.Completed));
        Assert.Empty(history.SearchHistory(null, 100, HistoryFilter.Canceled));

        Assert.True(history.ReopenEntry(row.Entry));
        Assert.Contains(Database.GetOpenItems(), found => found.Id == item.Id);
        Assert.Single(history.SearchHistory(null, 100, HistoryFilter.Reopened));

        toDo.SetDone(Database.GetOpenItems().Single(found => found.Id == item.Id), true);
        var closed = history.SearchHistory(null, 100, HistoryFilter.Completed).First();
        var deleted = history.DeleteHistoryItem(closed.Entry, DateTime.Now);
        Assert.NotNull(deleted);
        Assert.Empty(history.SearchHistory(null, 100, HistoryFilter.Completed));
        history.UndoDelete(deleted, DateTime.Now);
        Assert.Single(history.SearchHistory(null, 100, HistoryFilter.Completed));

        // The column's chosen view is kept with the rest of the server's preferences.
        history.SetPreference("history_view", "Completed");
        Assert.Equal("Completed", Database.GetPreference("history_view"));
    }

    [Fact]
    public void Work_hours_and_pay_are_saved_on_the_server()
    {
        ISettingsData settings = new ServerSettingsData(Client());
        var tomorrow = DateOnly.FromDateTime(DateTime.Now).AddDays(1);
        var day = tomorrow.ToDateTime(TimeOnly.MinValue);

        Assert.False(settings.SetWeeklyHours(["Monday", "Wednesday"], "09:00-17:00").IsAnswer);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Wednesday], settings.GetWeeklyHours().Select(hours => hours.Day));
        settings.RemoveWeeklyHours(["Monday"]);
        Assert.Equal(DayOfWeek.Wednesday, Assert.Single(Database.GetWeeklyHours()).Day);

        settings.AddWorkShift(day.AddHours(10), day.AddHours(14));
        var shift = Assert.Single(settings.GetWorkDates(DateOnly.FromDateTime(DateTime.Now)));
        Assert.Equal(new TimeOnly(10, 0), shift.Start);
        settings.RemoveWorkDate(tomorrow);
        settings.SetDayOff(day);
        Assert.True(Assert.Single(settings.GetWorkDates(DateOnly.FromDateTime(DateTime.Now))).IsDayOff);

        settings.SetCommute("25");
        Assert.Equal(25, settings.GetCommuteMinutes());
        settings.SetCommute("0");
        Assert.Equal(0, settings.GetCommuteMinutes());
        Assert.Equal(0, Database.GetCommuteMinutes());
        settings.SetHourlyRate(21.50m);
        Assert.Equal(21.50m, settings.GetHourlyRate());
        Assert.Equal(21.50m, Database.GetHourlyRate());
    }

    [Fact]
    public void Bedtime_choices_change_the_servers_reminders_straight_away()
    {
        ISettingsData settings = new ServerSettingsData(Client());

        Assert.Null(settings.SetBedtimeChoices(new BedtimeChoices(SleepHours: 7.5, ReadyMinutes: 30, EveningStep: false)));

        var server = _factory.Services.GetRequiredService<WaypointServer>();
        Assert.Equal(7.5, server.Worker.BedtimeSchedule.Settings.SleepHours);
        Assert.Equal(7.5, settings.GetBedtime().SleepHours);
        Assert.Null(settings.GetBedtime().EveningStepMinutes);
        Assert.NotEmpty(settings.DescribeTonight(DateTime.Now));

        Assert.NotNull(settings.SetBedtimeChoices(new BedtimeChoices(SleepHours: 30)));
        Assert.Equal(7.5, settings.GetBedtime().SleepHours);
    }

    [Fact]
    public async Task A_discord_token_sent_from_the_pc_is_kept_by_the_server_and_never_sent_back()
    {
        ISettingsData settings = new ServerSettingsData(Client());
        const string token = "bot-token-from-the-pc";

        var reply = settings.SaveDiscord(new DiscordForm("123456789012345678", null, null, null, false), token);

        Assert.Equal("Saved. Connecting to Discord now.", reply);
        Assert.Equal(token, File.ReadAllText(Path.Combine(_folder, Core.Discord.DiscordSetup.TokenFile)));
        var form = settings.GetDiscord();
        Assert.True(form.HasToken);
        Assert.Equal("123456789012345678", form.OwnerUserId);
        Assert.DoesNotContain(token, await Paired().GetStringAsync("/api/settings/discord"));
        Assert.DoesNotContain(token, await Paired().GetStringAsync("/api/status"));

        // A blank token keeps the saved one.
        settings.SaveDiscord(new DiscordForm("123456789012345678", null, "223456789012345678", null, false), "");
        Assert.Equal(token, File.ReadAllText(Path.Combine(_folder, Core.Discord.DiscordSetup.TokenFile)));
        Assert.Equal("223456789012345678", settings.GetDiscord().AlertsChannelId);

        // The test message is sent from the server, which can't reach Discord here, and says so.
        Assert.StartsWith("The test didn't send", await settings.TestDiscordAsync());
    }

    [Fact]
    public void The_notes_and_lists_forums_set_on_the_pc_are_kept_by_the_server()
    {
        ISettingsData settings = new ServerSettingsData(Client());

        settings.SaveDiscord(new DiscordForm("123456789012345678", null, null, null, false, "323456789012345678", "423456789012345678"), "bot-token");

        var form = settings.GetDiscord();
        Assert.Equal("323456789012345678", form.NotesForumChannelId);
        Assert.Equal("423456789012345678", form.ListsForumChannelId);
        Assert.Equal("The notes forum ID should be only digits. Turn on Developer Mode in Discord, then right-click and choose Copy ID.",
            settings.SaveDiscord(new DiscordForm("123456789012345678", null, null, null, false, "notes", null), ""));
    }

    [Fact]
    public void Classes_saved_from_the_pc_are_used_by_the_server_straight_away()
    {
        ISettingsData settings = new ServerSettingsData(Client());
        var worker = _factory.Services.GetRequiredService<WaypointServer>().Worker;
        var schedule = worker.Schedule;

        Assert.False(settings.GetClasses().SavedHere);
        Assert.Null(settings.GetClasses().TermStart);
        Assert.Empty(settings.GetClasses().Classes);

        var meeting = new ClassMeeting("CS 110", "Room 1", [DayOfWeek.Monday, DayOfWeek.Wednesday], new TimeOnly(9, 0), new TimeOnly(10, 15), 15);
        var broken = meeting with { End = new TimeOnly(8, 0) };
        Assert.Equal("CS 110 has to end after it starts.", settings.SaveClasses(new ClassesForm(null, null, [broken])));
        Assert.Empty(worker.Schedule.Meetings);

        var reply = settings.SaveClasses(new ClassesForm(new DateOnly(2026, 9, 14), new DateOnly(2026, 12, 18), [meeting]));

        Assert.Equal("Saved. Waypoint uses this class straight away.", reply);
        Assert.Same(schedule, worker.Schedule);
        var saved = Assert.Single(worker.Schedule.Meetings);
        Assert.Equal("CS 110", saved.Course);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Wednesday], saved.Days);
        Assert.Equal(new DateTime(2026, 11, 11, 9, 0, 0), worker.Schedule.GetNextClass(new DateTime(2026, 11, 10, 12, 0, 0))!.Start);

        var form = settings.GetClasses();
        Assert.True(form.SavedHere);
        Assert.Equal(new DateOnly(2026, 9, 14), form.TermStart);
        Assert.Equal(new DateOnly(2026, 12, 18), form.TermEnd);
        Assert.Equal("Room 1", Assert.Single(form.Classes).Room);
        Assert.Equal(15, form.Classes[0].TravelMinutes);
    }

    [Fact]
    public void A_pc_in_server_mode_follows_the_servers_classes_and_bedtime_and_keeps_them_while_it_is_away()
    {
        // Saved from another PC.
        ISettingsData other = new ServerSettingsData(Client());
        var meeting = new ClassMeeting("CS 110", "Room 1", [DayOfWeek.Wednesday], new TimeOnly(9, 0), new TimeOnly(10, 15), 15);
        other.SaveClasses(new ClassesForm(new DateOnly(2026, 9, 14), new DateOnly(2026, 12, 18), [meeting]));
        Assert.Null(other.SetBedtimeChoices(new BedtimeChoices(SleepHours: 7, LatestBedtime: "00:30")));

        // This PC starts on its own settings.json's classes and bedtime.
        var fileMeeting = new ClassMeeting("OLD 1000", "", [DayOfWeek.Monday], new TimeOnly(8, 0), new TimeOnly(9, 0));
        var here = new ClassSchedule(new DateOnly(2027, 1, 11), new DateOnly(2027, 5, 7), [fileMeeting], [new DateOnly(2026, 10, 16)]);
        var bedtime = new BedtimeSchedule(new BedtimeSettings(), here);
        var reads = Path.Combine(_folder, "server-reads.json");
        var online = Client();
        online.SaveReadsIn(reads);

        Assert.True(ServerSettingsFollow.Apply(ServerSettingsFollow.FindClasses(online), ServerSettingsFollow.FindBedtime(online), here, bedtime, Database));

        Assert.Equal("CS 110", Assert.Single(here.Meetings).Course);
        Assert.Equal(new DateOnly(2026, 12, 18), here.TermEnd);
        Assert.Equal([new DateOnly(2026, 10, 16)], here.NoClassDates);
        Assert.Equal(7, bedtime.Settings.SleepHours);
        Assert.Equal(new TimeOnly(0, 30), bedtime.Settings.LatestBedtime);

        // Reading again with nothing new changes nothing.
        Assert.False(ServerSettingsFollow.Apply(ServerSettingsFollow.FindClasses(online), ServerSettingsFollow.FindBedtime(online), here, bedtime, Database));

        // After a restart with the server away, the last answer kept in the saved reads is used.
        var away = new ServerClient(new HttpClient(new Unreachable()) { BaseAddress = new Uri("http://server/") }, _token);
        away.SaveReadsIn(reads);
        var restarted = new ClassSchedule(new DateOnly(2027, 1, 11), new DateOnly(2027, 5, 7), [fileMeeting], []);
        var restartedBedtime = new BedtimeSchedule(new BedtimeSettings(), restarted);
        Assert.True(ServerSettingsFollow.Apply(ServerSettingsFollow.FindClasses(away), ServerSettingsFollow.FindBedtime(away), restarted, restartedBedtime, Database));
        Assert.Equal("CS 110", Assert.Single(restarted.Meetings).Course);
        Assert.Equal(7, restartedBedtime.Settings.SleepHours);

        // With nothing kept and the server away, this PC keeps what it has rather than clearing it.
        var never = new ServerClient(new HttpClient(new Unreachable()) { BaseAddress = new Uri("http://server/") }, _token);
        Assert.Null(ServerSettingsFollow.FindClasses(never));
        Assert.Null(ServerSettingsFollow.FindBedtime(never));
        var untouched = new ClassSchedule(new DateOnly(2027, 1, 11), new DateOnly(2027, 5, 7), [fileMeeting], []);
        Assert.False(ServerSettingsFollow.Apply(null, null, untouched, null, Database));
        Assert.Equal("OLD 1000", Assert.Single(untouched.Meetings).Course);
    }

    [Fact]
    public async Task A_calendar_feed_saved_from_the_pc_is_kept_on_the_server_and_never_sent_back()
    {
        ISettingsData settings = new ServerSettingsData(Client());
        const string link = "https://example.test/private-feed-link.ics";

        Assert.Equal(new Core.Calendar.CalendarFeedForm(false, false), settings.GetCalendarFeed());
        Assert.Equal(Core.Calendar.CalendarFeedSetup.NotALink, settings.SaveCalendarFeed("my calendar"));

        Assert.StartsWith("Saved.", settings.SaveCalendarFeed(link));

        Assert.Equal(link, Database.GetPreference(Core.Calendar.CalendarFeedSetup.Preference));
        Assert.Equal(new Core.Calendar.CalendarFeedForm(true, true), settings.GetCalendarFeed());
        Assert.NotNull(_factory.Services.GetRequiredService<WaypointServer>().Worker.Calendar);
        Assert.DoesNotContain("private-feed-link", await Paired().GetStringAsync("/api/settings/calendar"));
        Assert.DoesNotContain("private-feed-link", await Paired().GetStringAsync("/api/status"));

        Assert.StartsWith("Removed.", settings.RemoveCalendarFeed());
        Assert.Equal(new Core.Calendar.CalendarFeedForm(false, false), settings.GetCalendarFeed());
        Assert.Null(_factory.Services.GetRequiredService<WaypointServer>().Worker.Calendar);
    }

    [Fact]
    public async Task Alert_email_set_up_from_the_pc_is_used_by_the_server_and_signs_in_there()
    {
        var settings = new ServerSettingsData(Client());
        var server = _factory.Services.GetRequiredService<WaypointServer>();

        // Before it's set up, the sign-in says where to add it, without pointing at a file.
        var notYet = settings.ConnectAlertEmail();
        Assert.Null(notYet.UserCode);
        Assert.Contains("Settings page", notYet.Problem);
        Assert.DoesNotContain("settings.json", notYet.Problem);

        var reply = settings.SaveAlertEmail(new AlertEmailForm("12345678-abcd-1234-abcd-1234567890ab", null));

        Assert.StartsWith("Saved.", reply);
        Assert.NotNull(server.Worker.Outlook);
        Assert.Equal("12345678-abcd-1234-abcd-1234567890ab", settings.GetAlertEmail().ClientId);
        var status = await Client().CheckAsync();
        Assert.StartsWith("Set up, but not signed in", status!.Connections.Single(connection => connection.Name == "Alert email").State);
        Assert.Contains("app ID", settings.SaveAlertEmail(new AlertEmailForm("not an id", null)));
    }

    [Fact]
    public void A_bad_discord_id_is_turned_down_the_same_as_on_the_pc()
    {
        ISettingsData settings = new ServerSettingsData(Client());

        var reply = settings.SaveDiscord(new DiscordForm("not an id", null, null, null, false), "token");

        Assert.DoesNotContain("Saved", reply);
        Assert.False(File.Exists(Path.Combine(_folder, Core.Discord.DiscordSetup.TokenFile)));
    }

    [Fact]
    public void Backups_are_made_and_restored_on_the_server()
    {
        var backups = new ServerBackups(Client());
        Database.AddListEntry(WaypointDatabase.ShoppingList, "kept", DateTime.Now);

        var name = backups.BackUpNow();
        var made = Assert.Single(backups.GetBackups());
        Assert.Equal(name, made.Name);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, made.Name);

        Database.AddListEntry(WaypointDatabase.ShoppingList, "added after", DateTime.Now);
        var result = backups.Restore(made);

        Assert.True(result.Restored);
        Assert.False(result.Restarting);
        Assert.NotNull(result.SafetyCopy);
        Assert.Equal(["kept"], Database.GetListEntries(WaypointDatabase.ShoppingList).Select(entry => entry.Text));
        Assert.Contains(backups.GetBackups(), backup => backup.BeforeRestore);
    }

    [Fact]
    public async Task A_change_sent_during_a_restore_is_turned_away_and_reads_still_answer()
    {
        var maintenance = _factory.Services.GetRequiredService<ServerMaintenance>();
        using var client = Paired();
        Assert.True(maintenance.TryStartRestore());

        using var refused = await client.PostAsJsonAsync("/api/backups", new { });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Contains(ServerMaintenance.Restoring, await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/backups")).StatusCode);
        Assert.Empty(Database.GetBackups());

        maintenance.EndRestore();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/backups", new { })).StatusCode);
    }

    [Fact]
    public async Task A_restore_waits_for_a_change_under_way_before_replacing_anything()
    {
        var maintenance = _factory.Services.GetRequiredService<ServerMaintenance>();
        var backups = new ServerBackups(Client());
        Database.AddListEntry(WaypointDatabase.ShoppingList, "kept", DateTime.Now);
        backups.BackUpNow();
        var made = Assert.Single(backups.GetBackups());

        var change = maintenance.TryStartChange();
        Assert.NotNull(change);
        var restoring = Task.Run(() => backups.Restore(made));
        await Task.Delay(500);

        Assert.False(restoring.IsCompleted);
        Database.AddListEntry(WaypointDatabase.ShoppingList, "saved while it waited", DateTime.Now);
        change.Dispose();

        var result = await restoring.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(result.Restored);
        Assert.Equal(["kept"], Database.GetListEntries(WaypointDatabase.ShoppingList).Select(entry => entry.Text));
    }

    [Fact]
    public async Task Writers_are_stopped_before_the_backup_goes_in_and_changes_stay_turned_away_after()
    {
        var maintenance = _factory.Services.GetRequiredService<ServerMaintenance>();
        var backups = new ServerBackups(Client());
        Database.AddListEntry(WaypointDatabase.ShoppingList, "kept", DateTime.Now);
        backups.BackUpNow();
        var made = Assert.Single(backups.GetBackups());
        Database.AddListEntry(WaypointDatabase.ShoppingList, "added after", DateTime.Now);

        IReadOnlyList<string>? seenWhenStopped = null;
        maintenance.AddWriter(_ =>
        {
            seenWhenStopped = [.. Database.GetListEntries(WaypointDatabase.ShoppingList).Select(entry => entry.Text)];
            return Task.FromResult(true);
        });

        var result = backups.Restore(made);

        Assert.True(result.Restored);
        Assert.Equal(["kept", "added after"], seenWhenStopped);
        Assert.Equal(["kept"], Database.GetListEntries(WaypointDatabase.ShoppingList).Select(entry => entry.Text));
        using var client = Paired();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/backups", new { })).StatusCode);
    }

    [Fact]
    public void A_damaged_backup_is_turned_down_without_stopping_anything()
    {
        var maintenance = _factory.Services.GetRequiredService<ServerMaintenance>();
        var backups = new ServerBackups(Client());
        backups.BackUpNow();
        var made = Assert.Single(backups.GetBackups());
        SqliteConnection.ClearAllPools();
        File.WriteAllText(Path.Combine(_folder, "backups", made.Name), "not a database");

        var result = backups.Restore(made);

        Assert.False(result.Restored);
        Assert.False(result.Restarting);
        Assert.False(maintenance.IsRestoring);
    }

    [Fact]
    public void Only_the_servers_own_backups_can_be_restored()
    {
        var backups = new ServerBackups(Client());
        Database.AddListEntry(WaypointDatabase.ShoppingList, "still here", DateTime.Now);

        var result = backups.Restore(new BackupChoice("../waypoint.db", DateTime.Now, false));

        Assert.False(result.Restored);
        Assert.Contains("isn't on the server", result.Problem);
        Assert.Single(Database.GetListEntries(WaypointDatabase.ShoppingList));
    }

    [Fact]
    public void The_notes_export_comes_down_as_a_zip_and_unpacks_into_a_new_folder()
    {
        Database.CreateNote("Lecture 1", "Recursion basics", DateTime.Now);
        Database.CreateNote("Lecture 2", "Base cases", DateTime.Now);
        var destination = Path.Combine(_folder, "exported", "Waypoint notes");

        var count = new ServerBackups(Client()).ExportNotes(destination);

        Assert.Equal(2, count);
        var files = Directory.GetFiles(destination, "*.md", SearchOption.AllDirectories);
        Assert.Equal(2, files.Length);
        Assert.Contains(files, file => File.ReadAllText(file).Contains("Recursion basics"));
    }
}
